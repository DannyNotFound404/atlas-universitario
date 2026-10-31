using System.Net;
using System.Text.RegularExpressions;
using Miguel.Contracts;

namespace Atlas.Nas.Provisioner;

public static partial class ProvisioningValidator
{
    public const int MaximumMessageBytes = 32 * 1024;

    public static bool TryNormalize(
        NasProvisioningRequest request,
        out NasProvisioningRequest normalized,
        out string errorCode)
    {
        normalized = request;
        errorCode = "INVALID_REQUEST";
        string action = request.Action?.Trim().ToUpperInvariant() ?? "";
        string code = request.Code?.Trim().ToUpperInvariant() ?? "";
        string host = request.Host?.Trim() ?? "";
        string share = request.ShareName?.Trim() ?? "";
        string protocol = request.Protocol?.Trim().ToUpperInvariant() ?? "";

        if (action != "PROVISION" || !CodePattern().IsMatch(code) ||
            !IsValidHost(host) || !IsSafeShare(share) ||
            request.SmbPort is < 1 or > 65535 ||
            protocol is not NasMountProtocols.Cifs and not NasMountProtocols.Nfs)
        {
            return false;
        }

        if (protocol == NasMountProtocols.Cifs)
        {
            string username = request.Username?.Trim() ?? "";
            string password = request.Password ?? "";
            string domain = request.Domain?.Trim() ?? "";
            string profile = request.SmbSecurityProfile?.Trim().ToUpperInvariant() ??
                SmbSecurityProfiles.Smb311Seal;
            if (!IsSafeCredentialValue(username, 256, allowEmpty: false) ||
                !IsSafeCredentialValue(password, 1024, allowEmpty: false) ||
                !IsSafeCredentialValue(domain, 256, allowEmpty: true) ||
                !IsSupportedSmbProfile(profile))
            {
                errorCode = "INVALID_CIFS_OPTIONS";
                return false;
            }

            normalized = request with
            {
                Action = action,
                Code = code,
                Host = host,
                ShareName = share,
                Protocol = protocol,
                Username = username,
                Password = password,
                Domain = domain.Length == 0 ? null : domain,
                SmbSecurityProfile = profile,
                NfsExport = null
            };
            return true;
        }

        string export = request.NfsExport?.Trim() ?? "";
        if (!IsSafeNfsExport(export))
        {
            errorCode = "INVALID_NFS_EXPORT";
            return false;
        }
        normalized = request with
        {
            Action = action,
            Code = code,
            Host = host,
            ShareName = share,
            Protocol = protocol,
            Username = null,
            Password = null,
            Domain = null,
            SmbSecurityProfile = null,
            NfsExport = export
        };
        return true;
    }

    public static bool IsSupportedSmbProfile(string value) => value is
        SmbSecurityProfiles.Smb311Seal or
        SmbSecurityProfiles.Smb30Seal or
        SmbSecurityProfiles.Smb30Sign or
        SmbSecurityProfiles.Smb21Sign;

    private static bool IsValidHost(string value)
    {
        if (value.Length is < 1 or > 255 || value.Any(char.IsControl) ||
            value.Contains('/') || value.Contains('\\') || value.Contains('%'))
        {
            return false;
        }
        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }
        return HostPattern().IsMatch(value) &&
            value.Split('.').All(label => label.Length is >= 1 and <= 63);
    }

    private static bool IsSafeShare(string value) =>
        value.Length is >= 1 and <= 128 &&
        value is not "." and not ".." &&
        !value.Any(character =>
            char.IsControl(character) || character is '/' or '\\' or ':');

    private static bool IsSafeCredentialValue(
        string value,
        int maximumLength,
        bool allowEmpty) =>
        (allowEmpty || value.Length > 0) &&
        value.Length <= maximumLength &&
        !value.Any(character => character is '\r' or '\n' or '\0');

    private static bool IsSafeNfsExport(string value)
    {
        if (value.Length is < 1 or > 512 || !value.StartsWith('/') ||
            value.Any(character => char.IsControl(character) || character == '\0'))
        {
            return false;
        }
        return !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(
        "^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,253}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
