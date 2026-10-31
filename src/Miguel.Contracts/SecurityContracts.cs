namespace Miguel.Contracts;

public sealed record SecurityPostureSnapshot(
    int SchemaVersion,
    Guid SnapshotId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<FirewallProfileInventory> FirewallProfiles,
    IReadOnlyList<BitLockerVolumeInventory> BitLockerVolumes,
    bool? SecureBootEnabled,
    TpmInventory Tpm,
    bool? RemoteDesktopEnabled,
    bool? Smb1Enabled,
    bool? PendingReboot,
    bool SecurityEventLogReadable,
    IReadOnlyList<InstalledSoftwareInventory> InstalledSoftware,
    IReadOnlyList<InstalledUpdateInventory> InstalledUpdates,
    IReadOnlyList<string> CollectionWarnings);

public sealed record FirewallProfileInventory(
    string Name,
    bool? Enabled);

public sealed record BitLockerVolumeInventory(
    string DriveLetter,
    bool? ProtectionEnabled,
    int? ProtectionStatus,
    int? ConversionStatus);

public sealed record TpmInventory(
    bool? Present,
    bool? Enabled,
    bool? Activated,
    string? SpecificationVersion);

public sealed record InstalledSoftwareInventory(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate);

public sealed record InstalledUpdateInventory(
    string HotFixId,
    string? Description,
    DateTimeOffset? InstalledAtUtc);

public sealed record SecurityEventBatch(
    int SchemaVersion,
    Guid BatchId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<WindowsSecurityEvent> Events);

public sealed record WindowsSecurityEvent(
    string Channel,
    long RecordId,
    int EventCode,
    DateTimeOffset OccurredAtUtc,
    string Category,
    string Outcome,
    int? LogonType,
    string? UserName,
    string? UserDomain,
    string? TargetUserSid,
    string? SourceAddress,
    int? SourcePort,
    string? WorkstationName,
    string? AuthenticationPackage,
    string? SessionName,
    string? FailureReason,
    string? StatusCode,
    string? SubStatusCode,
    string Severity = SecurityEventSeverities.Information,
    string? Provider = null,
    string? SubjectUserName = null,
    string? SubjectUserDomain = null,
    string? SubjectUserSid = null,
    string? TargetName = null,
    string? ProcessName = null,
    string? CorrelationKey = null,
    string? Details = null,
    string? SessionId = null);

public static class SecurityEventCategories
{
    public const string InteractiveLogon = "InteractiveLogon";
    public const string RemoteInteractiveLogon = "RemoteInteractiveLogon";
    public const string NetworkLogon = "NetworkLogon";
    public const string ExplicitCredentialLogon = "ExplicitCredentialLogon";
    public const string RemoteAuthentication = "RemoteAuthentication";
    public const string RemoteSessionLogon = "RemoteSessionLogon";
    public const string RemoteSessionLogoff = "RemoteSessionLogoff";
    public const string RemoteSessionDisconnected = "RemoteSessionDisconnected";
    public const string RemoteSessionReconnected = "RemoteSessionReconnected";
    public const string InteractiveSessionLogon = "InteractiveSessionLogon";
    public const string InteractiveSessionLogoff = "InteractiveSessionLogoff";
    public const string SessionShellStarted = "SessionShellStarted";
    public const string SessionDisconnected = "SessionDisconnected";
    public const string SessionReconnected = "SessionReconnected";
    public const string BatchLogon = "BatchLogon";
    public const string ServiceLogon = "ServiceLogon";
    public const string NetworkCleartextLogon = "NetworkCleartextLogon";
    public const string NewCredentialsLogon = "NewCredentialsLogon";
    public const string SessionLogoff = "SessionLogoff";
    public const string UserInitiatedLogoff = "UserInitiatedLogoff";
    public const string AuditLogCleared = "AuditLogCleared";
    public const string SystemTimeChanged = "SystemTimeChanged";
    public const string PrivilegedLogon = "PrivilegedLogon";
    public const string ServiceInstalled = "ServiceInstalled";
    public const string AuditPolicyChanged = "AuditPolicyChanged";
    public const string LocalAccountCreated = "LocalAccountCreated";
    public const string LocalAccountEnabled = "LocalAccountEnabled";
    public const string LocalAccountDisabled = "LocalAccountDisabled";
    public const string LocalAccountDeleted = "LocalAccountDeleted";
    public const string LocalGroupMemberAdded = "LocalGroupMemberAdded";
    public const string LocalGroupMemberRemoved = "LocalGroupMemberRemoved";
    public const string FirewallRuleAdded = "FirewallRuleAdded";
    public const string FirewallRuleChanged = "FirewallRuleChanged";
    public const string FirewallRuleRemoved = "FirewallRuleRemoved";
    public const string FirewallSettingChanged = "FirewallSettingChanged";
    public const string SystemStartup = "SystemStartup";
    public const string SystemShutdown = "SystemShutdown";
    public const string UnexpectedShutdown = "UnexpectedShutdown";
    public const string MalwareDetected = "MalwareDetected";
    public const string MalwareActionTaken = "MalwareActionTaken";
    public const string AttackSurfaceReductionBlocked = "AttackSurfaceReductionBlocked";
    public const string RealtimeProtectionDisabled = "RealtimeProtectionDisabled";
    public const string AntimalwareConfigurationChanged = "AntimalwareConfigurationChanged";
    public const string AntimalwareEngineFailure = "AntimalwareEngineFailure";
    public const string AntispywareDisabled = "AntispywareDisabled";
    public const string AntivirusDisabled = "AntivirusDisabled";
    public const string TamperProtectionBlocked = "TamperProtectionBlocked";

    public static bool IsKnown(string category) => category is
        InteractiveLogon or
        RemoteInteractiveLogon or
        NetworkLogon or
        ExplicitCredentialLogon or
        RemoteAuthentication or
        RemoteSessionLogon or
        RemoteSessionLogoff or
        RemoteSessionDisconnected or
        RemoteSessionReconnected or
        InteractiveSessionLogon or
        InteractiveSessionLogoff or
        SessionShellStarted or
        SessionDisconnected or
        SessionReconnected or
        BatchLogon or
        ServiceLogon or
        NetworkCleartextLogon or
        NewCredentialsLogon or
        SessionLogoff or
        UserInitiatedLogoff or
        AuditLogCleared or
        SystemTimeChanged or
        PrivilegedLogon or
        ServiceInstalled or
        AuditPolicyChanged or
        LocalAccountCreated or
        LocalAccountEnabled or
        LocalAccountDisabled or
        LocalAccountDeleted or
        LocalGroupMemberAdded or
        LocalGroupMemberRemoved or
        FirewallRuleAdded or
        FirewallRuleChanged or
        FirewallRuleRemoved or
        FirewallSettingChanged or
        SystemStartup or
        SystemShutdown or
        UnexpectedShutdown or
        MalwareDetected or
        MalwareActionTaken or
        AttackSurfaceReductionBlocked or
        RealtimeProtectionDisabled or
        AntimalwareConfigurationChanged or
        AntimalwareEngineFailure or
        AntispywareDisabled or
        AntivirusDisabled or
        TamperProtectionBlocked;
}

public static class SecurityEventOutcomes
{
    public const string Success = "Success";
    public const string Failure = "Failure";
    public const string Information = "Information";
    public const string Detected = "Detected";
    public const string Changed = "Changed";
    public const string Blocked = "Blocked";

    public static bool IsKnown(string outcome) => outcome is
        Success or Failure or Information or Detected or Changed or Blocked;
}

public static class SecurityEventSeverities
{
    public const string Critical = "Critical";
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
    public const string Information = "Information";

    public static bool IsKnown(string severity) => severity is
        Critical or High or Medium or Low or Information;
}
