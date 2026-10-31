using Miguel.Contracts;

namespace Miguel.Server.Admin;

public static class AdminRoles
{
    public const string Administrator = "Administrator";
    public const string Auditor = "Auditor";
    public const string Operator = "Operator";
    public const string Installer = "Installer";
    public const string TechnologyManager = "TechnologyManager";

    public static bool IsValid(string role) =>
        role is Administrator or Auditor or Operator or Installer or
            TechnologyManager;
}

public sealed record AdminUserAccount(
    Guid Id,
    string Username,
    string NormalizedUsername,
    string DisplayName,
    string PasswordHash,
    string Role,
    bool Enabled,
    int FailedAttempts,
    DateTimeOffset? LockedUntilUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    int SecurityRevision = 1);

public sealed record AdminLoginRequest(string Username, string Password, string? Code = null);

public sealed record AdminDeviceRetirementRequest(string Reason);

public sealed record AdminSessionResponse(
    string Username,
    string DisplayName,
    string Role,
    DateTimeOffset ExpiresAtUtc);

public sealed record AdminDashboardSummary(
    DateTimeOffset GeneratedAtUtc,
    int TotalDevices,
    int OnlineDevices,
    int AttentionDevices,
    int OfflineDevices,
    int SecurityDetectedDevices,
    int SuccessfulBackups,
    int WarningBackups,
    int FailedBackups,
    int DevicesWithoutBackup,
    int Agencies);

public sealed record AdminSecurityDashboard(
    DateTimeOffset GeneratedAtUtc,
    AdminSecuritySummary Summary,
    AdminSecurityDevicePage Devices,
    AdminSecurityEventPage RecentEvents,
    IReadOnlyList<AdminSecurityDeviceOption> DeviceOptions,
    IReadOnlyList<AdminSecurityDailyView> DailyEvents);

public sealed record AdminNotificationView(
    Guid Id,
    string Category,
    string Severity,
    string Title,
    string Message,
    string TargetView,
    string? TargetEntityId,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsRead,
    DateTimeOffset? ReadAtUtc);

public sealed record AdminNotificationPage(
    DateTimeOffset GeneratedAtUtc,
    int Page,
    int PageSize,
    int Total,
    int UnreadCount,
    IReadOnlyList<AdminNotificationView> Items,
    IReadOnlyList<string>? MutedCategories = null,
    IReadOnlyList<AdminAttentionGroup>? AttentionGroups = null);

public sealed record AdminSecurityNotificationSignals(
    IReadOnlyList<AdminSecurityDeviceView> Devices,
    IReadOnlyList<AdminSecurityEventView> RecentEvents);

public sealed record AdminSecurityDeviceOption(
    Guid DeviceId,
    string MachineName,
    string AgencyCode);

public sealed record AdminSecurityDailyView(
    DateOnly Day,
    int TotalEvents,
    int CriticalEvents,
    int HighEvents,
    int FailedEvents);

public sealed record AdminSecurityDevicePage(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AdminSecurityDeviceView> Items);

public sealed record AdminSecurityEventPage(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AdminSecurityEventView> Items);

public sealed record AdminSecuritySummary(
    int TotalDevices,
    int ReportingDevices,
    int FirewallProtectedDevices,
    int BitLockerProtectedDevices,
    int SecureBootDevices,
    int TpmDevices,
    int Smb1EnabledDevices,
    int RemoteDesktopEnabledDevices,
    int PendingRebootDevices,
    int SuccessfulRemoteEventsLast24Hours,
    int FailedLogonsLast24Hours,
    int CriticalEventsLast24Hours,
    int HighEventsLast24Hours,
    int ObservedDevices = 0, int StaleDevices = 0, int MissingDevices = 0,
    int FreshnessHours = 24);

public sealed record AdminSecurityDeviceView(
    Guid DeviceId,
    string MachineName,
    string AgencyCode,
    DateTimeOffset? CapturedAtUtc,
    bool? FirewallEnabled,
    bool? SystemDriveProtected,
    bool? SecureBootEnabled,
    bool? TpmPresent,
    bool? Smb1Enabled,
    bool? RemoteDesktopEnabled,
    bool? PendingReboot,
    bool? SecurityEventLogReadable,
    int InstalledSoftwareCount,
    int InstalledUpdateCount,
    IReadOnlyList<string> CollectionWarnings,
    string ObservationState = "UNKNOWN", int FreshnessHours = 24,
    double? AgeHours = null);

public sealed record AdminSecurityEventView(
    long Id,
    Guid DeviceId,
    string MachineName,
    string AgencyCode,
    DateTimeOffset OccurredAtUtc,
    int EventCode,
    string Category,
    string Outcome,
    int? LogonType,
    string? UserName,
    string? UserDomain,
    string? SourceAddress,
    string? WorkstationName,
    bool IsRemote,
    string Severity,
    string? Provider,
    string? SubjectUserName,
    string? SubjectUserDomain,
    string? TargetName,
    string? ProcessName,
    string? CorrelationKey,
    string? Details,
    string? AuthenticationPackage,
    string? SessionName,
    string? FailureReason);

public sealed record AdminAgencyView(
    string AgencyCode,
    string DisplayName,
    string? Location,
    string? Responsible,
    string DefaultBackupProvider,
    bool Enabled,
    int TotalDevices,
    int OnlineDevices,
    int AttentionDevices,
    int OfflineDevices,
    int SecurityDetectedDevices,
    int AtlasDevices,
    int AcronisDevices,
    int ExternalDevices,
    int SharedFolderDevices,
    int NasCount,
    int OnlineNas,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastBackupAtUtc);

public sealed record AdminDeviceView(
    Guid Id,
    string AgencyCode,
    string MachineName,
    bool Enabled,
    string Connectivity,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset EnrolledAtUtc,
    string AgentVersion,
    string? BackupClientVersion,
    bool BackupRunning,
    int PendingEvents,
    string? InteractiveUserAccount,
    string? InteractiveUserDisplayName,
    string? InteractiveUserSid,
    string? InteractiveUserSessionType,
    string? InteractiveUserResolutionSource,
    DateTimeOffset? InteractiveUserCheckedAtUtc,
    DateTimeOffset? InventoryCapturedAtUtc,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Processor,
    int LogicalProcessors,
    ulong MemoryBytes,
    long DiskTotalBytes,
    long DiskFreeBytes,
    int? DiskUsedPercent,
    string? OperatingSystem,
    IReadOnlyList<string> UserProfiles,
    IReadOnlyList<string> SecurityProducts,
    IReadOnlyList<NetworkInterfaceInventory> NetworkInterfaces,
    string? WindowsLicenseEdition,
    int? WindowsLicenseStatus,
    DateTimeOffset? LastBackupAtUtc,
    string? LastBackupStatus,
    long? LastBackupFilesCopied,
    long? LastBackupFilesSkipped,
    long? LastBackupBytesCopied,
    DateTimeOffset? RetiredAtUtc,
    string? RetiredBy,
    string? RetirementReason,
    string BackupProvider,
    Guid? NasApplianceId,
    string? NasDisplayName,
    string? NasHost,
    string? NasShareName,
    string? LastBackupDestinationHost,
    string? LastBackupDestinationShare,
    string UpdateRingCode,
    bool LastBackupAcceptable = false,
    DateTimeOffset? LastContactAtUtc = null);

public sealed record AdminDevicePage(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AdminDeviceView> Items);

public sealed record AdminDeviceDetail(
    AdminDeviceView Device,
    DeviceInventory? Inventory,
    BackupRunReport? LatestBackup);

internal sealed record AdminDeviceData(
    Guid Id,
    string AgencyCode,
    string MachineName,
    bool Enabled,
    string AgentVersion,
    string? BackupClientVersion,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? AgentStartedAtUtc,
    bool BackupRunning,
    int PendingEvents,
    string? InteractiveUserAccount,
    string? InteractiveUserDisplayName,
    string? InteractiveUserSid,
    string? InteractiveUserSessionType,
    string? InteractiveUserResolutionSource,
    DateTimeOffset? InteractiveUserCheckedAtUtc,
    DateTimeOffset? InventoryCapturedAtUtc,
    DeviceInventory? Inventory,
    DateTimeOffset? LastBackupAtUtc,
    BackupRunReport? LatestBackup,
    DateTimeOffset? RetiredAtUtc,
    string? RetiredBy,
    string? RetirementReason,
    string BackupProvider,
    Guid? NasApplianceId,
    string? NasDisplayName,
    string? NasHost,
    string? NasShareName,
    string UpdateRingCode,
    DateTimeOffset? LastContactAtUtc = null);

internal sealed record AdminDeviceRetirementResult(
    bool Retired,
    string MachineName,
    string AgencyCode,
    DateTimeOffset? RetiredAtUtc,
    string? RetiredBy,
    string? RetirementReason);

public enum AdminLoginFailure
{
    None = 0,
    InvalidCredentials = 1,
    Locked = 2,
    Disabled = 3
}

public sealed record AdminLoginResult(
    AdminUserAccount? Account,
    AdminLoginFailure Failure)
{
    public bool Succeeded => Account is not null && Failure == AdminLoginFailure.None;
}
