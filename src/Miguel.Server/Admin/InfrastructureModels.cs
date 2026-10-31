namespace Miguel.Server.Admin;

public static class BackupProviders
{
    public const string Atlas = "ATLAS";
    public const string Acronis = "ACRONIS";
    public const string External = "EXTERNAL";
    public const string SharedFolder = "SHARED_FOLDER";

    public static bool IsValid(string? value) =>
        value is Atlas or Acronis or External or SharedFolder;

    public static bool IsValidAgencyDefault(string? value) =>
        value is Atlas or Acronis or External;

    public static bool RequiresNas(string? value) => value is Atlas;
}

public static class NasProbeStatuses
{
    public const string Unknown = "UNKNOWN";
    public const string Online = "ONLINE";
    public const string Degraded = "DEGRADED";
    public const string Offline = "OFFLINE";
}

public sealed record AdminAgencyRequest(
    string Code,
    string DisplayName,
    string? Location,
    string? Responsible,
    string DefaultBackupProvider,
    bool Enabled = true);

public sealed record AdminAgencyDefinition(
    string Code,
    string DisplayName,
    string? Location,
    string? Responsible,
    string DefaultBackupProvider,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

public sealed record AdminNasRequest(
    string Code,
    string AgencyCode,
    string DisplayName,
    string Host,
    int SmbPort,
    string ShareName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    bool IsPrimary,
    bool Enabled = true);

public sealed record AdminNasMountRequest(
    string Protocol,
    string? Username,
    string? Password,
    string? Domain,
    string? SmbSecurityProfile,
    string? NfsExport);

public sealed record AdminNasView(
    Guid Id,
    string Code,
    string AgencyCode,
    string DisplayName,
    string Host,
    int SmbPort,
    string ShareName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    bool IsPrimary,
    bool Enabled,
    string MountPath,
    DateTimeOffset? LastProbeAtUtc,
    string LastProbeStatus,
    int? LastProbeLatencyMs,
    long? TotalBytes,
    long? FreeBytes,
    string? LastProbeError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    long LifecycleRevision = 0,
    DateTimeOffset? RetiredAtUtc = null,
    string? RetiredBy = null,
    string? RetirementReason = null);

public sealed record AdminNasDefinition(
    Guid Id,
    string Code,
    string AgencyCode,
    string DisplayName,
    string Host,
    int SmbPort,
    string ShareName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    bool IsPrimary,
    bool Enabled,
    DateTimeOffset? LastProbeAtUtc,
    string LastProbeStatus,
    int? LastProbeLatencyMs,
    long? TotalBytes,
    long? FreeBytes,
    string? LastProbeError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    long LifecycleRevision = 0,
    DateTimeOffset? RetiredAtUtc = null,
    string? RetiredBy = null,
    string? RetirementReason = null);

public sealed record AdminDeviceBackupPolicyRequest(
    string BackupProvider,
    Guid? NasApplianceId);

public sealed record AdminBackupCoverageView(
    Guid DeviceId,
    string MachineName,
    string AgencyCode,
    string BackupProvider,
    Guid? NasApplianceId,
    string? NasDisplayName,
    string? NasHost,
    string? NasShareName,
    DateTimeOffset? LastBackupAtUtc,
    string? LastBackupStatus,
    long? FilesCopied,
    long? FilesSkipped,
    long? BytesCopied,
    string? ReportedDestinationHost,
    string? ReportedDestinationShare,
    string Compliance,
    string Detail);

public sealed record AdminNasBrowseItem(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset LastModifiedAtUtc);

public sealed record AdminNasBrowsePage(
    Guid NasId,
    string NasName,
    string RelativePath,
    string? ParentPath,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AdminNasBrowseItem> Items);

public sealed record NasProbeResult(
    string Status,
    int? LatencyMs,
    long? TotalBytes,
    long? FreeBytes,
    string? ErrorCode);
