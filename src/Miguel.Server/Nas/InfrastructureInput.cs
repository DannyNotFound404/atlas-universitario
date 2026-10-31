using System.Net;
using System.Text.RegularExpressions;
using Miguel.Contracts;
using Miguel.Server.Admin;

namespace Miguel.Server.Nas;

public static partial class InfrastructureInput
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static bool TryNormalizeAgency(
        AdminAgencyRequest request,
        out AdminAgencyRequest normalized)
    {
        string code = NormalizeCode(request.Code);
        string displayName = NormalizeText(request.DisplayName, 128);
        string? location = NormalizeOptionalText(request.Location, 192);
        string? responsible = NormalizeOptionalText(request.Responsible, 128);
        string provider = request.DefaultBackupProvider?.Trim().ToUpperInvariant() ?? "";
        normalized = request with
        {
            Code = code,
            DisplayName = displayName,
            Location = location,
            Responsible = responsible,
            DefaultBackupProvider = provider
        };
        return CodePattern().IsMatch(code) &&
               displayName.Length > 0 &&
               BackupProviders.IsValidAgencyDefault(provider) &&
               TextIsSafe(displayName) &&
               TextIsSafe(location) &&
               TextIsSafe(responsible);
    }

    public static bool TryNormalizeNas(
        AdminNasRequest request,
        out AdminNasRequest normalized)
    {
        string code = NormalizeCode(request.Code);
        string agencyCode = NormalizeCode(request.AgencyCode);
        string displayName = NormalizeText(request.DisplayName, 128);
        string host = request.Host?.Trim() ?? "";
        string shareName = request.ShareName?.Trim() ?? "";
        string? manufacturer = NormalizeOptionalText(request.Manufacturer, 96);
        string? model = NormalizeOptionalText(request.Model, 128);
        string? serial = NormalizeOptionalText(request.SerialNumber, 128);
        normalized = request with
        {
            Code = code,
            AgencyCode = agencyCode,
            DisplayName = displayName,
            Host = host,
            ShareName = shareName,
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = serial
        };
        return CodePattern().IsMatch(code) &&
               CodePattern().IsMatch(agencyCode) &&
               displayName.Length > 0 &&
               request.SmbPort is >= 1 and <= 65535 &&
               HostIsSafe(host) &&
               ShareIsSafe(shareName) &&
               TextIsSafe(displayName) &&
               TextIsSafe(manufacturer) &&
               TextIsSafe(model) &&
               TextIsSafe(serial);
    }

    public static bool TryNormalizeNasMount(
        AdminNasMountRequest request,
        out AdminNasMountRequest normalized)
    {
        string protocol = request.Protocol?.Trim().ToUpperInvariant() ?? "";
        string? username = NormalizeOptionalText(request.Username, 128);
        string? password = NormalizeOptionalSecret(request.Password, 512);
        string? domain = NormalizeOptionalText(request.Domain, 128);
        string? securityProfile = request.SmbSecurityProfile?.Trim().ToUpperInvariant();
        string? nfsExport = request.NfsExport?.Trim();

        normalized = request with
        {
            Protocol = protocol,
            Username = username,
            Password = password,
            Domain = domain,
            SmbSecurityProfile = securityProfile,
            NfsExport = nfsExport
        };

        if (protocol == NasMountProtocols.Cifs)
        {
            return username is { Length: > 0 } &&
                   password is { Length: > 0 } &&
                   SmbSecurityProfiles.IsSupported(securityProfile) &&
                   TextIsSafe(username) &&
                   TextIsSafe(domain) &&
                   SecretIsSafe(password);
        }

        return protocol == NasMountProtocols.Nfs &&
               NfsExportIsSafe(nfsExport) &&
               username is null &&
               password is null &&
               domain is null;
    }

    public static string NormalizeCode(string? value) =>
        value?.Trim().ToUpperInvariant() ?? "";

    private static string NormalizeText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Length <= maximumLength ? normalized : "";
    }

    private static string? NormalizeOptionalText(string? value, int maximumLength)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0)
        {
            return null;
        }
        return normalized.Length <= maximumLength ? normalized : "\0";
    }

    private static string? NormalizeOptionalSecret(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        return value.Length <= maximumLength ? value : "\0";
    }

    private static bool TextIsSafe(string? value) =>
        value is null || !value.Any(char.IsControl);

    private static bool SecretIsSafe(string? value) =>
        value is null ||
        (!value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0'));

    private static bool NfsExportIsSafe(string? value) =>
        value is { Length: >= 1 and <= 512 } &&
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\0') &&
        !value.Contains('\r') &&
        !value.Contains('\n') &&
        !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    private static bool HostIsSafe(string host)
    {
        if (host.Length is < 1 or > 255 ||
            host.Any(char.IsControl) ||
            host.Contains('/') ||
            host.Contains('\\') ||
            host.Any(char.IsWhiteSpace))
        {
            return false;
        }
        return IPAddress.TryParse(host, out _) ||
               Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static bool ShareIsSafe(string share) =>
        share.Length is >= 1 and <= 128 &&
        share is not "." and not ".." &&
        !share.Any(char.IsControl) &&
        !share.Contains('/') &&
        !share.Contains('\\') &&
        !share.Contains(':');
}
