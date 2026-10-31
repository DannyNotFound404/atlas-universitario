using System.Net;
using System.Net.Sockets;

namespace Miguel.Server.NetworkDiscovery;

public static class NetworkTargets
{
    public static string[] Addresses(NetworkScopeDefinition scope)
    {
        string[] parts = scope.Network.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0],out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1],out int prefix) || prefix is < 20 or > 32)
            throw new ArgumentException("invalid_network_scope");
        uint address = Number(ip), mask = prefix == 0 ? 0 : uint.MaxValue << (32-prefix);
        uint first = address & mask; ulong count = 1UL << (32-prefix);
        int skip = prefix < 31 ? 1 : 0;
        var exclusions = new HashSet<string>(scope.ExcludedAddresses ?? [],StringComparer.Ordinal);
        var result = new List<string>();
        for (ulong offset=(ulong)skip; offset<count-(ulong)skip;offset++)
        {
            uint value=first+(uint)offset;
            string text=new IPAddress(new byte[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value}).ToString();
            if(!exclusions.Contains(text)) result.Add(text);
        }
        if(result.Count==0) throw new ArgumentException("empty_network_scope");
        if(scope.ScanTargets is not null)
        {
            var allowed=result.ToHashSet(StringComparer.Ordinal);
            if(scope.ScanTargets.Length==0 || scope.ScanTargets.Any(a=>!allowed.Contains(a)) || scope.ScanTargets.Distinct().Count()!=scope.ScanTargets.Length)
                throw new ArgumentException("network_range_outside_scope");
            return scope.ScanTargets.OrderBy(a=>Number(IPAddress.Parse(a))).ToArray();
        }
        return result.ToArray();
    }
    public static string[] Range(NetworkScopeDefinition scope,string? start,string? end)
    {
        var allowed=Addresses(scope with {ScanTargets=null});
        if(string.IsNullOrWhiteSpace(start)&&string.IsNullOrWhiteSpace(end))return allowed;
        if(!IPAddress.TryParse(start,out var first)||!IPAddress.TryParse(end,out var last)||
            first.AddressFamily!=AddressFamily.InterNetwork||last.AddressFamily!=AddressFamily.InterNetwork||Number(first)>Number(last))
            throw new ArgumentException("invalid_network_range");
        var authorized=Addresses(scope with{ExcludedAddresses=[],ScanTargets=null}).ToHashSet(StringComparer.Ordinal);
        if(!authorized.Contains(first.ToString())||!authorized.Contains(last.ToString()))throw new ArgumentException("network_range_outside_scope");
        var selected=allowed.Where(a=>Number(IPAddress.Parse(a))>=Number(first)&&Number(IPAddress.Parse(a))<=Number(last)).ToArray();
        if(selected.Length==0)throw new ArgumentException("empty_network_range");
        return selected;
    }
    public static string[] NormalizeExclusions(NetworkScopeDefinition scope, string[]? addresses)
    {
        if(addresses?.Length>128) throw new ArgumentException("too_many_network_exclusions");
        var allowed=Addresses(scope with {ExcludedAddresses=[],ScanTargets=null}).ToHashSet(StringComparer.Ordinal);
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(string value in addresses ?? [])
        {
            if(!IPAddress.TryParse(value.Trim(),out var ip) || ip.AddressFamily!=AddressFamily.InterNetwork || !allowed.Contains(ip.ToString()))
                throw new ArgumentException("network_exclusion_outside_scope");
            result.Add(ip.ToString());
        }
        _=Addresses(scope with {ExcludedAddresses=result.ToArray()});
        return result.OrderBy(v=>Number(IPAddress.Parse(v))).ToArray();
    }
    private static uint Number(IPAddress ip){byte[] b=ip.GetAddressBytes();return (uint)b[0]<<24|(uint)b[1]<<16|(uint)b[2]<<8|b[3];}
}
