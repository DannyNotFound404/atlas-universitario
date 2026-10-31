using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Options;

namespace Miguel.Server.NetworkDiscovery;

public sealed class NmapDiscoveryEngine(
    IOptions<ServerOptions> options,
    ILogger<NmapDiscoveryEngine> logger,
    INetworkProbeRunner runner,
    INetworkNameResolver? nameResolver = null) : INetworkProgressEngine
{
    private readonly ServerOptions _options = options.Value;

    public bool IsAvailable => runner.NmapAvailable || runner.IcmpAvailable;
    public bool IcmpAvailable => runner.IcmpAvailable;

    public async Task<NetworkScanResult> DiscoverAsync(NetworkScopeDefinition scope,IReadOnlySet<string> identifyTargets,CancellationToken ct)
        =>await DiscoverWithProgressAsync(scope,identifyTargets,(_,_)=>Task.CompletedTask,ct);
    public async Task<NetworkScanResult> DiscoverWithProgressAsync(NetworkScopeDefinition scope,IReadOnlySet<string> identifyTargets,
        Func<NetworkScanProgress,CancellationToken,Task> report,CancellationToken ct)
    {
        string[] targets=NetworkTargets.Addresses(scope);
        var progress=new NetworkProgressReporter(report,runner.IcmpAvailable?targets.Length:0);
        await progress.ReportAsync(ct,true);
        var allowed=targets.ToHashSet(StringComparer.Ordinal);
        var warnings=new List<NetworkScanWarning>();var methods=new List<string>();
        int rate=Math.Max(1,_options.NetworkDiscoveryMaxPacketsPerSecond/_options.NetworkDiscoveryWorkers);
        async Task<(NetworkObservation[] Items,bool Complete)> Tcp()
        {
            if(!runner.NmapAvailable)return([],false);
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.NetworkDiscoveryScopeTimeoutSeconds));
                string xml=await runner.NmapAsync(["--unprivileged","-sn","-n","-PS22,53,80,443,445,515,631,3389,5000,5001,8080,8291,9100","--max-retries","2","--host-timeout","20s","--max-rate",Math.Max(1,rate*3/5).ToString(),"-oX","-",..targets],timeout.Token);
                var parsed=ReadNative(xml,targets.Length);
                var items=parsed.Items.Where(i=>allowed.Contains(i.IpAddress)).Select(i=>i with{PresenceSources=["TCP"]}).ToArray();
                foreach(var item in items)progress.Observe(item);
                progress.TcpFinished=true;await progress.ReportAsync(ct,true);
                return(items,parsed.Complete);
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){logger.LogWarning(ex,"Fase TCP incompleta en {Scope}",scope.Code);progress.TcpFinished=true;return([],false);}
        }
        async Task<(NetworkObservation[] Items,bool Complete)> Icmp()
        {
            if(!runner.IcmpAvailable)return([],false);
            var found=new System.Collections.Concurrent.ConcurrentBag<NetworkObservation>();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.NetworkDiscoveryScopeTimeoutSeconds));
            using var spacing=new SemaphoreSlim(1,1);
            DateTimeOffset next=DateTimeOffset.MinValue;int delay=Math.Max(20,1000/Math.Max(1,rate*2/5));
            try
            {
                await Parallel.ForEachAsync(targets,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=timeout.Token},async(ip,token)=>
                {
                    await spacing.WaitAsync(token);
                    try{TimeSpan wait=next-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait,token);next=DateTimeOffset.UtcNow.AddMilliseconds(delay);}
                    finally{spacing.Release();}
                    var ping=runner is INetworkDetailedProbeRunner detailed?await detailed.PingDetailedAsync(ip,token):new NetworkPingResult(await runner.PingAsync(ip,token),null);
                    if(ping.Responded){var item=new NetworkObservation(ip,null,null,null,[],null,null,["ICMP"],ResponseTimeMs:ping.ResponseTimeMs);found.Add(item);progress.Observe(item);}
                    progress.CompleteIcmp();await progress.ReportAsync(token);
                });
                return(found.ToArray(),true);
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){logger.LogWarning(ex,"Fase ICMP incompleta en {Scope}",scope.Code);return(found.ToArray(),false);}
        }
        Task<(NetworkObservation[] Items,bool Complete)> tcp=Tcp(),icmp=Icmp();
        await Task.WhenAll(tcp,icmp);ct.ThrowIfCancellationRequested();
        if(runner.NmapAvailable)methods.Add("TCP");if(runner.IcmpAvailable)methods.Add("ICMP");
        if(!tcp.Result.Complete)warnings.Add(new("tcp_incomplete","DISCOVERY"));
        if(!icmp.Result.Complete)warnings.Add(new("icmp_incomplete","DISCOVERY"));
        var observed=tcp.Result.Items.Concat(icmp.Result.Items).GroupBy(i=>i.IpAddress,StringComparer.Ordinal)
            .Select(group=>group.First() with{PresenceSources=group.SelectMany(i=>i.PresenceSources??[]).Distinct().ToArray(),ResponseTimeMs=group.Select(i=>i.ResponseTimeMs).FirstOrDefault(v=>v.HasValue)}).ToArray();
        if(!tcp.Result.Complete&&!icmp.Result.Complete&&observed.Length==0)throw new InvalidOperationException("network_discovery_unavailable");
        var detailTargets=observed.Where(i=>identifyTargets.Contains(i.IpAddress)).Select(i=>i.IpAddress).ToArray();
        // New addresses are always identified; the repository supplies stale/changed known addresses.
        string identification=detailTargets.Length==0?"NOT_DUE":"COMPLETE";
        if(detailTargets.Length>0)
        {
            progress.Phase="IDENTIFICATION";progress.IdentificationTotal=detailTargets.Length;await progress.ReportAsync(ct,true);
            try
            {
                if(!runner.NmapAvailable)throw new InvalidOperationException("nmap_unavailable");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.NetworkDiscoveryScopeTimeoutSeconds));
                string xml=await runner.NmapAsync(["--unprivileged","-Pn","-sT","-sV","--version-light","-n","-p","22,23,53,80,443,445,515,548,554,631,3389,5000,5001,7443,8000,8080,8291,8443,8728,8729,9100","--max-retries","2","--host-timeout","45s","--max-rate",Math.Max(1,rate*3/5).ToString(),"-oX","-",..detailTargets],timeout.Token);
                var native=ReadNative(xml,detailTargets.Length);
                var details=native.Items.Where(i=>allowed.Contains(i.IpAddress)).ToArray();
                observed=Merge(observed,details).ToArray();
                foreach(var item in observed)progress.Observe(item);
                progress.IdentificationCompleted=details.Length;await progress.ReportAsync(ct,true);
                bool complete=native.Complete&&detailTargets.All(ip=>details.Any(d=>d.IpAddress==ip&&d.ServicesCheckStatus=="COMPLETE"));
                identification=complete?"COMPLETE":"PARTIAL";
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){logger.LogWarning(ex,"Identificación incompleta en {Scope}",scope.Code);identification="PARTIAL";}
            if(identification=="PARTIAL")warnings.Add(new("identification_incomplete","IDENTIFICATION"));
        }
        if(nameResolver is not null&&observed.Length>0)
        {
            progress.Phase="NAMES";await progress.ReportAsync(ct,true);
            var named=new System.Collections.Concurrent.ConcurrentDictionary<string,NetworkObservation>(observed.ToDictionary(i=>i.IpAddress));
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.NetworkDiscoveryScopeTimeoutSeconds));
            try
            {
                await Parallel.ForEachAsync(observed,new ParallelOptions{MaxDegreeOfParallelism=8,CancellationToken=timeout.Token},async(item,token)=>
                {
                    string? name=item.Hostname??await nameResolver.ResolveAsync(item.IpAddress,token);
                    var current=item with{Hostname=name,HostnameSource=name is null?null:item.Hostname is not null?"SERVICES":"DNS"};
                    named[item.IpAddress]=current;progress.Observe(current);await progress.ReportAsync(token);
                });
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){logger.LogWarning(ex,"Resolución de nombres incompleta en {Scope}",scope.Code);warnings.Add(new("names_incomplete","NAMES"));}
            observed=named.Values.ToArray();methods.Add("DNS");
        }
        observed=EnrichFromLinuxNeighborCache(observed).ToArray();
        foreach(var item in observed)progress.Observe(item);
        progress.Phase="FINALIZING";await progress.ReportAsync(ct,true);
        return new(observed,tcp.Result.Complete&&icmp.Result.Complete,identification,warnings,methods);
    }

    private static (NetworkObservation[] Items,bool Complete) ReadNative(string xml,int expectedTargets)
    {
        var items=ParseXml(xml).ToArray();
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=8_000_000};
        using var text=new StringReader(xml);using var reader=XmlReader.Create(text,settings);
        var document=new XmlDocument{XmlResolver=null};document.Load(reader);
        bool complete=document.SelectSingleNode("/nmaprun/runstats/finished")?.Attributes?["exit"]?.Value=="success"&&
            int.TryParse(document.SelectSingleNode("/nmaprun/runstats/hosts")?.Attributes?["total"]?.Value,out int total)&&total==expectedTargets;
        return(items,complete);
    }

    internal static IReadOnlyList<NetworkObservation> ParseXml(string xml)
    {
        List<NetworkObservation> observations = [];
        XmlReaderSettings settings = new()
        {
            // Nmap always emits a standard DOCTYPE in XML output. Ignore the
            // declaration while keeping external resolution disabled so the
            // parser accepts native Nmap output without loading any DTD.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 8_000_000
        };
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, settings);
        XmlDocument document = new() { XmlResolver = null };
        document.Load(reader);

        XmlNodeList? hosts =
            document.SelectNodes("/nmaprun/host[status/@state='up']");
        if (hosts is null)
        {
            return observations;
        }

        foreach (XmlNode host in hosts)
        {
            string? ip = host.SelectSingleNode("address[@addrtype='ipv4']")
                ?.Attributes?["addr"]?.Value;
            if (string.IsNullOrWhiteSpace(ip))
            {
                continue;
            }

            XmlNode? mac = host.SelectSingleNode("address[@addrtype='mac']");
            string? hostname = host.SelectSingleNode("hostnames/hostname")
                ?.Attributes?["name"]?.Value;
            List<NetworkServiceObservation> services = [];
            XmlNodeList? portNodes = host.SelectNodes(
                "ports/port[state/@state='open']");
            if (portNodes is not null)
            {
                foreach (XmlNode portNode in portNodes)
                {
                    if (!int.TryParse(
                            portNode.Attributes?["portid"]?.Value,
                            out int port))
                    {
                        continue;
                    }

                    XmlNode? service = portNode.SelectSingleNode("service");
                    List<string> cpes = [];
                    XmlNodeList? cpeNodes = service?.SelectNodes("cpe");
                    if (cpeNodes is not null)
                    {
                        foreach (XmlNode cpe in cpeNodes)
                        {
                            string? value = Clean(cpe.InnerText, 255);
                            if (value is not null)
                            {
                                cpes.Add(value);
                            }
                        }
                    }

                    services.Add(new NetworkServiceObservation(
                        port,
                        Clean(portNode.Attributes?["protocol"]?.Value, 8) ?? "tcp",
                        Clean(service?.Attributes?["name"]?.Value, 64) ?? "unknown",
                        Clean(service?.Attributes?["product"]?.Value, 255),
                        Clean(service?.Attributes?["version"]?.Value, 96),
                        Clean(service?.Attributes?["extrainfo"]?.Value, 255),
                        Clean(service?.Attributes?["hostname"]?.Value, 255),
                        Clean(service?.Attributes?["ostype"]?.Value, 96),
                        Clean(service?.Attributes?["devicetype"]?.Value, 96),
                        cpes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                }
            }

            string? operatingSystemHint = services
                .Select(item => item.OperatingSystem)
                .FirstOrDefault(value => value is not null) ??
                Clean(host.SelectSingleNode("os/osmatch")
                    ?.Attributes?["name"]?.Value, 255);
            string? deviceTypeHint = services
                .Select(item => item.DeviceType)
                .FirstOrDefault(value => value is not null) ??
                Clean(host.SelectSingleNode("os/osmatch/osclass")
                    ?.Attributes?["type"]?.Value, 96);
            bool portsPresent = host.SelectSingleNode("ports") is not null;
            bool uncertainPorts = host.SelectSingleNode("ports/port[state/@state='filtered' or state/@state='open|filtered'] | ports/extraports[@state='filtered' or @state='open|filtered']") is not null;
            observations.Add(new NetworkObservation(
                ip,
                Clean(hostname, 255),
                Clean(mac?.Attributes?["addr"]?.Value, 32),
                Clean(mac?.Attributes?["vendor"]?.Value, 255),
                services,
                operatingSystemHint,
                deviceTypeHint,
                null,
                portsPresent,
                portsPresent ? uncertainPorts ? "PARTIAL" : "COMPLETE" : "NOT_RUN"));
        }

        return observations
            .GroupBy(item => item.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<NetworkObservation> Merge(
        IReadOnlyList<NetworkObservation> discovered,
        IReadOnlyList<NetworkObservation> enriched)
    {
        Dictionary<string, NetworkObservation> details = enriched
            .ToDictionary(item => item.IpAddress, StringComparer.OrdinalIgnoreCase);
        return discovered.Select(item =>
        {
            if (!details.TryGetValue(item.IpAddress, out NetworkObservation? detail))
            {
                return item;
            }

            return item with
            {
                Hostname = item.Hostname ?? detail.Hostname ??
                    detail.Services.Select(service => service.Hostname)
                        .FirstOrDefault(value => value is not null),
                MacAddress = item.MacAddress ?? detail.MacAddress,
                Vendor = item.Vendor ?? detail.Vendor,
                Services = detail.Services,
                ServicesChecked = detail.ServicesChecked,
                ServicesCheckStatus = detail.ServicesCheckStatus,
                OperatingSystemHint = detail.OperatingSystemHint,
                DeviceTypeHint = detail.DeviceTypeHint
            };
        }).ToArray();
    }

    private static IReadOnlyList<NetworkObservation> EnrichFromLinuxNeighborCache(
        IReadOnlyList<NetworkObservation> observations)
    {
        const string arpPath = "/proc/net/arp";
        if (!OperatingSystem.IsLinux() || !File.Exists(arpPath))
        {
            return observations;
        }

        try
        {
            Dictionary<string, string> macByIp = File.ReadLines(arpPath)
                .Skip(1)
                .Select(line => line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length >= 4 && parts[2] != "0x0")
                .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First()[3],
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> vendors = ReadMacVendors();
            return observations.Select(observation =>
            {
                if (observation.MacAddress is not null ||
                    !macByIp.TryGetValue(observation.IpAddress, out string? mac))
                {
                    return observation;
                }

                string prefix = new(mac
                    .Where(char.IsAsciiHexDigit)
                    .Take(6)
                    .Select(char.ToUpperInvariant)
                    .ToArray());
                vendors.TryGetValue(prefix, out string? vendor);
                return observation with
                {
                    MacAddress = Clean(mac, 32),
                    Vendor = observation.Vendor ?? Clean(vendor, 255),
                    PresenceSources = (observation.PresenceSources ?? []).Append("ARP_CACHE").ToArray()
                };
            }).ToArray();
        }
        catch (IOException)
        {
            return observations;
        }
        catch (UnauthorizedAccessException)
        {
            return observations;
        }
    }

    private static Dictionary<string, string> ReadMacVendors()
    {
        const string path = "/usr/share/nmap/nmap-mac-prefixes";
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return File.ReadLines(path)
            .Where(line => line.Length > 7 && line[0] != '#')
            .Select(line => line.Split(
                (char[]?)null,
                2,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length == 6)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First()[1],
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? Clean(string? value, int maxLength)
    {
        string? clean = value?.Trim();
        return string.IsNullOrEmpty(clean)
            ? null
            : clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
