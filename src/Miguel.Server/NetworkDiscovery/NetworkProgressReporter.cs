using System.Collections.Concurrent;

namespace Miguel.Server.NetworkDiscovery;

internal sealed class NetworkProgressReporter(Func<NetworkScanProgress,CancellationToken,Task> report,int icmpTotal)
{
    private readonly ConcurrentDictionary<string,NetworkObservation> _observed=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate=new(1,1);
    private int _icmpCompleted;
    private DateTimeOffset _last=DateTimeOffset.MinValue;
    public string Phase {get;set;}="DISCOVERY";
    public bool TcpFinished {get;set;}
    public int IdentificationCompleted {get;set;}
    public int IdentificationTotal {get;set;}
    public void CompleteIcmp()=>Interlocked.Increment(ref _icmpCompleted);
    public void Observe(NetworkObservation item)=>_observed.AddOrUpdate(item.IpAddress,item,(_,old)=>item with
    {
        PresenceSources=(old.PresenceSources??[]).Concat(item.PresenceSources??[]).Distinct().ToArray(),
        ResponseTimeMs=item.ResponseTimeMs??old.ResponseTimeMs,
        Hostname=item.Hostname??old.Hostname,HostnameSource=item.HostnameSource??old.HostnameSource,
        MacAddress=item.MacAddress??old.MacAddress,Vendor=item.Vendor??old.Vendor,
        Services=item.ServicesChecked?item.Services:old.Services,
        ServicesChecked=item.ServicesChecked||old.ServicesChecked,
        ServicesCheckStatus=item.ServicesChecked?item.ServicesCheckStatus:old.ServicesCheckStatus
    });
    public async Task ReportAsync(CancellationToken ct,bool force=false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(!force&&DateTimeOffset.UtcNow-_last<TimeSpan.FromSeconds(1))return;
            await report(new(Phase,Volatile.Read(ref _icmpCompleted),icmpTotal,TcpFinished,IdentificationCompleted,IdentificationTotal,_observed.Values.OrderBy(i=>System.Net.IPAddress.Parse(i.IpAddress).GetAddressBytes(),ByteArrayComparer.Instance).ToArray()),ct);
            _last=DateTimeOffset.UtcNow;
        }
        finally{_gate.Release();}
    }
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance=new();
        public int Compare(byte[]? a,byte[]? b)=>a is null?-1:b is null?1:a.AsSpan().SequenceCompareTo(b);
    }
}
