using System.Net;
using System.Net.Sockets;

namespace Miguel.Server.NetworkDiscovery;

public interface INetworkNameResolver
{
    Task<string?> ResolveAsync(string address,CancellationToken ct);
}
public sealed class NetworkNameResolver : INetworkNameResolver
{
    public async Task<string?> ResolveAsync(string address,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var entry=await Dns.GetHostEntryAsync(IPAddress.Parse(address).ToString(),AddressFamily.InterNetwork,deadline.Token);
            var name=entry.HostName.Trim();
            return name.Length is >0 and <=255&&!IPAddress.TryParse(name,out _)&&entry.AddressList.Contains(IPAddress.Parse(address))?name:null;
        }
        catch(SocketException){return null;}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return null;}
    }
}
