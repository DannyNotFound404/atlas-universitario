namespace Miguel.Contracts;

public static class NasMountProtocols
{
    public const string Cifs = "CIFS";
    public const string Nfs = "NFS";
}

public static class SmbSecurityProfiles
{
    public const string Smb311Seal = "SMB311_SEAL";
    public const string Smb30Seal = "SMB30_SEAL";
    public const string Smb30Sign = "SMB30_SIGN";
    public const string Smb21Sign = "SMB21_SIGN";

    public static bool IsSupported(string? value) => value is
        Smb311Seal or Smb30Seal or Smb30Sign or Smb21Sign;
}

public sealed record NasProvisioningRequest(
    string Action,
    string Code,
    string Host,
    int SmbPort,
    string ShareName,
    string Protocol,
    string? Username,
    string? Password,
    string? Domain,
    string? SmbSecurityProfile,
    string? NfsExport);

public sealed record NasProvisioningResponse(
    bool Succeeded,
    string Code,
    string Status,
    string? ErrorCode,
    string? Detail,
    string? MountPath,
    string? Protocol);
