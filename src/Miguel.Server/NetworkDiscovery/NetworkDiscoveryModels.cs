namespace Miguel.Server.NetworkDiscovery;

public static class NetworkScanStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string TimedOut = "TIMED_OUT";
    public const string Skipped = "SKIPPED";
    public const string Partial = "PARTIAL";
    public const string Interrupted = "INTERRUPTED";
    public const string Cancelled = "CANCELLED";
}

public sealed record NetworkScopeDefinition(
    string Code,
    string AgencyCode,
    string DisplayName,
    string Network,
    string Gateway,
    int ScanOrder,
    bool AutomaticEnabled = true,
    int IntervalMinutes = 30,
    int IdentificationIntervalHours = 24,
    string[]? ExcludedAddresses = null,
    DateTimeOffset? NextScanAtUtc = null,
    int ConfigurationRevision = 1,
    string[]? ScanTargets = null,
    bool ForceIdentification = false);

public sealed record NetworkObservation(
    string IpAddress,
    string? Hostname,
    string? MacAddress,
    string? Vendor,
    IReadOnlyList<NetworkServiceObservation> Services,
    string? OperatingSystemHint,
    string? DeviceTypeHint,
    IReadOnlyList<string>? PresenceSources = null,
    bool ServicesChecked = false,
    string ServicesCheckStatus = "NOT_RUN",
    double? ResponseTimeMs = null,
    string? HostnameSource = null,
    string? MacSource = null,
    Guid? AssetId = null,
    string? DeviceKind = null,
    string? DeviceName = null,
    string? ComputerManufacturer = null,
    string? ComputerModel = null,
    DateTimeOffset? MacCheckedAtUtc = null,
    string? IdentificationBasis = null);

public sealed record NetworkServiceObservation(
    int Port,
    string Protocol,
    string Name,
    string? Product,
    string? Version,
    string? ExtraInfo,
    string? Hostname,
    string? OperatingSystem,
    string? DeviceType,
    IReadOnlyList<string> Cpes);

public sealed record NetworkScanRequest(string ScopeCode, Guid RunId);
public sealed record NetworkScanClaim(NetworkScopeDefinition Scope, Guid RunId, Guid Owner);
public sealed record NetworkScanWarning(string Code, string Phase);
public sealed record NetworkScanResult(IReadOnlyList<NetworkObservation> Observations,
    bool DiscoveryComplete, string IdentificationStatus, IReadOnlyList<NetworkScanWarning> Warnings,
    IReadOnlyList<string> Methods);
public sealed record NetworkScopeSettingsRequest(bool AutomaticEnabled, int IntervalMinutes,
    int IdentificationIntervalHours, string[]? ExcludedAddresses, int ExpectedRevision);
public sealed record NetworkScanRunView(Guid Id, string ScopeCode, string Status, string RequestedBy,
    DateTimeOffset RequestedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    int AddressesTargeted, int? HostsObserved, bool DiscoveryComplete, string IdentificationStatus,
    IReadOnlyList<NetworkScanWarning> Warnings, IReadOnlyList<string> Methods, string? ErrorCode);
public sealed record NetworkAssetEventView(Guid Id, string EventType, DateTimeOffset OccurredAtUtc, System.Text.Json.JsonElement Evidence);
public sealed record NetworkScanProgress(string Phase, int IcmpCompleted, int IcmpTotal,
    bool TcpFinished, int IdentificationCompleted, int IdentificationTotal,
    IReadOnlyList<NetworkObservation> Observations);
public sealed record NetworkScanLiveView(NetworkScanRunView Run, NetworkScanProgress? Progress, DateTimeOffset? UpdatedAtUtc);
public interface INetworkProgressEngine : INetworkDiscoveryEngine
{
    Task<NetworkScanResult> DiscoverWithProgressAsync(NetworkScopeDefinition scope, IReadOnlySet<string> identifyTargets,
        Func<NetworkScanProgress,CancellationToken,Task> report, CancellationToken ct);
}
public interface INetworkDiscoveryEngine
{
    bool IsAvailable { get; }
    bool IcmpAvailable { get; }
    Task<NetworkScanResult> DiscoverAsync(NetworkScopeDefinition scope, IReadOnlySet<string> identifyTargets, CancellationToken ct);
}

public sealed record QueueNetworkScanResponse(
    int Queued,
    IReadOnlyList<Guid> RunIds,
    int AlreadyQueued = 0);

public sealed record QueueNetworkScanRequest(string? ScopeCode, string? StartAddress = null,
    string? EndAddress = null, bool FullIdentification = true);

public sealed record NetworkScopeView(
    string Code,
    string AgencyCode,
    string DisplayName,
    string Network,
    string Gateway,
    int AddressesTargeted,
    int ObservedAssets,
    int NotObservedAddresses,
    DateTimeOffset? LastScanAtUtc,
    string LastScanStatus,
    int? LastHostsObserved,
    string? LastErrorCode,
    bool AutomaticEnabled = true,
    int IntervalMinutes = 30,
    int IdentificationIntervalHours = 24,
    IReadOnlyList<string>? ExcludedAddresses = null,
    DateTimeOffset? NextScanAtUtc = null,
    DateTimeOffset? LastCompleteScanAtUtc = null,
    int ConfigurationRevision = 1);

public sealed record NetworkAssetView(
    Guid Id,
    string ScopeCode,
    string AgencyCode,
    string AgencyName,
    string IpAddress,
    string? Hostname,
    string? MacAddress,
    string? Vendor,
    string DeviceKind,
    string? DisplayName,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    int ConsecutiveMisses,
    Guid? LinkedDeviceId,
    string? LinkedDeviceName,
    Guid? LinkedNasId,
    string? LinkedNasName,
    string ClassificationSource,
    IReadOnlyList<NetworkServiceObservation> Services,
    string? OperatingSystemHint,
    string? DeviceTypeHint,
    int IdentificationConfidence,
    string? ComputerManufacturer,
    string? ComputerModel,
    string? BaseBoardManufacturer,
    string? BaseBoardProduct,
    IReadOnlyList<string>? PresenceSources = null,
    string IdentificationBasis = "LEGACY",
    DateTimeOffset? ServicesCheckedAtUtc = null,
    string ServicesCheckStatus = "NOT_RUN",
    string? HistoricalDeviceName = null,
    bool HardwareHistorical = false,
    double? ResponseTimeMs = null,
    string? HostnameSource = null,
    string? MacSource = null,
    DateTimeOffset? MacCheckedAtUtc = null);

public sealed record NetworkAssetPage(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<NetworkAssetView> Items);

public sealed record NetworkDiscoveryDashboard(
    bool Enabled,
    bool AutomaticEnabled,
    bool EngineAvailable,
    bool ScanInProgress,
    string Engine,
    DateTimeOffset? NextAutomaticScanAtUtc,
    IReadOnlyList<NetworkScopeView> Scopes,
    NetworkAssetPage Assets,
    bool IcmpAvailable = false,
    IReadOnlyList<NetworkScanRunView>? RecentRuns = null);

public sealed record UpdateNetworkAssetRequest(
    string DeviceKind,
    string? DisplayName);
