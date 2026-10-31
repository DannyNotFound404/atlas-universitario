using System.Globalization;

namespace Miguel.Server.NetworkDiscovery;

internal static class NetworkMacVendors
{
    private static readonly Lazy<IReadOnlyDictionary<string,string>> Vendors=new(Read);
    public static string? Find(string? mac)
    {
        if(mac is null)return null;
        string key=mac.Replace(":","").Replace("-","").Replace(".","").ToUpperInvariant();
        if(key.Length!=12||key.Any(c=>!Uri.IsHexDigit(c))||!byte.TryParse(key[..2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out byte first)||(first&3)!=0)return null;
        return Vendors.Value.GetValueOrDefault(key[..6]);
    }
    internal static IReadOnlyDictionary<string,string> Parse(IEnumerable<string> lines)
    {
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string line in lines)
        {
            if(line.Length<8||line.StartsWith('#')||line[..6].Any(c=>!Uri.IsHexDigit(c))||!char.IsWhiteSpace(line[6]))continue;
            string vendor=line[7..].Trim();if(vendor.Length>0)values[line[..6]]=vendor[..Math.Min(255,vendor.Length)];
        }
        return values;
    }
    private static IReadOnlyDictionary<string,string> Read()
    {
        const string path="/usr/share/nmap/nmap-mac-prefixes";
        try{return File.Exists(path)&&new FileInfo(path).Length<=8_000_000?Parse(File.ReadLines(path)):new Dictionary<string,string>();}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return new Dictionary<string,string>();}
    }
}
