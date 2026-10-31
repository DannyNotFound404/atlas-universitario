using System.Diagnostics;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Miguel.Server.NetworkDiscovery;

public interface INetworkProbeRunner
{
    bool NmapAvailable { get; }
    bool IcmpAvailable { get; }
    Task<string> NmapAsync(IEnumerable<string> arguments,CancellationToken ct);
    Task<bool> PingAsync(string address,CancellationToken ct);
}

public sealed record NetworkPingResult(bool Responded,double? ResponseTimeMs);
public interface INetworkDetailedProbeRunner
{
    Task<NetworkPingResult> PingDetailedAsync(string address,CancellationToken ct);
}
public sealed class NetworkProbeRunner(IOptions<ServerOptions> options) : INetworkProbeRunner,INetworkDetailedProbeRunner
{
    private int _pingRejected;
    public bool NmapAvailable => OperatingSystem.IsLinux()&&File.Exists(options.Value.NetworkDiscoveryNmapPath);
    public bool IcmpAvailable => OperatingSystem.IsLinux()&&File.Exists(options.Value.NetworkDiscoveryPingPath)&&Volatile.Read(ref _pingRejected)==0;
    public async Task<string> NmapAsync(IEnumerable<string> arguments,CancellationToken ct)
    {
        var result=await RunAsync(options.Value.NetworkDiscoveryNmapPath,arguments,8_000_000,ct);
        if(result.Code!=0)throw new InvalidOperationException("nmap_process_failed");
        return result.Output;
    }
    public async Task<bool> PingAsync(string address,CancellationToken ct)
        =>(await PingDetailedAsync(address,ct)).Responded;
    public async Task<NetworkPingResult> PingDetailedAsync(string address,CancellationToken ct)
    {
        // Arguments are literal IPv4 addresses from the server-owned target set.
        var result=await RunAsync(options.Value.NetworkDiscoveryPingPath,["-4","-n","-q","-c","1","-W","2","-w","3","-s","0",address],4096,ct);
        if(result.Code>1){Interlocked.Exchange(ref _pingRejected,1);throw new InvalidOperationException("icmp_unavailable");}
        return new(result.Code==0,result.Code==0?ParsePingLatency(result.Output):null);
    }
    internal static double? ParsePingLatency(string output)
    {
        var match=Regex.Match(output,@"(?:rtt|round-trip)[^=]*=\s*[\d.]+/([\d.]+)/",RegexOptions.CultureInvariant);
        return match.Success&&double.TryParse(match.Groups[1].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var ms)&&double.IsFinite(ms)&&ms>=0?ms:null;
    }
    internal static async Task<(int Code,string Output)> RunAsync(string path,IEnumerable<string> arguments,int maxCharacters,CancellationToken ct)
    {
        var start=new ProcessStartInfo(path){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        start.Environment["LC_ALL"]="C";
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=new Process{StartInfo=start};
        if(!process.Start())throw new InvalidOperationException("network_process_start_failed");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<string> output=ReadBoundedAsync(process.StandardOutput,maxCharacters,lifetime.Token);
        Task<string> error=ReadBoundedAsync(process.StandardError,8192,lifetime.Token);
        Task exit=process.WaitForExitAsync(lifetime.Token);
        try
        {
            // An oversized stream must stop the producer before waiting for its exit.
            while(!exit.IsCompleted)
            {
                var pending=new List<Task>{exit};if(!output.IsCompleted)pending.Add(output);if(!error.IsCompleted)pending.Add(error);
                await Task.WhenAny(pending);
                if(output.IsFaulted)await output;if(error.IsFaulted)await error;
                ct.ThrowIfCancellationRequested();
            }
            await exit;string text=await output;await error;
            return(process.ExitCode,text);
        }
        finally
        {
            lifetime.Cancel();
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}
            try{await Task.WhenAll(output,error);}catch(Exception ex) when(ex is OperationCanceledException or IOException or InvalidDataException){}
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,int limit,CancellationToken ct)
    {
        var output=new StringBuilder();char[] buffer=new char[4096];int read;
        while((read=await reader.ReadAsync(buffer,ct))>0)
        {if(output.Length+read>limit)throw new InvalidDataException("network_output_too_large");output.Append(buffer,0,read);}
        return output.ToString();
    }
}
