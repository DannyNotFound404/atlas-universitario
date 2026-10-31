namespace Miguel.Contracts;

// Kept outside the legacy signed manifest: deployed 0.3.x clients reject unknown fields.
public sealed record UpdaterComponentVersion(string Component, string? Version);
public sealed record UpdaterCheckRequest(string UpdaterVersion, IReadOnlyList<UpdaterComponentVersion> Components);
public sealed record UpdaterPolicy(int WavePercent = 100, int MaxConcurrent = 8,
    int MaxConcurrentPerAgency = 2, int FailureStopCount = 3, int FailurePercent = 25,
    int MinimumSamples = 5, int WindowStartMinute = 0, int WindowEndMinute = 1440,
    string TimeZoneId = "America/Guatemala", int DownloadKiBPerSecond = 1024,
    bool RequireModernUpdater = true, bool Paused = false);
public sealed record UpdaterCheckResponse(string Decision, string Reason, Guid? ReleaseId,
    AtlasSignedReleaseEnvelope? Release, UpdaterPolicy Policy, int RetryAfterSeconds,
    SignedUpdaterControl? RecoveryAuthorization = null, SignedUpdaterControl? TrustTransition = null, int RetryGeneration = 0);
public sealed record UpdaterLeaseRequest(Guid ReleaseId, Guid AttemptId, string UpdaterVersion);
public sealed record UpdaterLeaseResponse(bool Granted, string Reason, Guid? LeaseId,
    DateTimeOffset? ExpiresAtUtc, int RetryAfterSeconds);
public sealed record UpdaterInstallRequest(Guid AttemptId, Guid LeaseId);
public sealed record UpdaterEvent(Guid EventId, Guid AttemptId, Guid ReleaseId, long Sequence,
    string Phase, string Code, string UpdaterVersion, DateTimeOffset OccurredAtUtc,
    IReadOnlyList<UpdaterComponentVersion> Components, long DownloadedBytes = 0,
    long TotalBytes = 0, bool AgentChanged = false, IReadOnlyList<string>? ChangedComponents = null);
public sealed record UpdaterConfirmation(bool Confirmed, string Reason);

// Recovery and trust transitions need an offline signature by an already trusted key.
public sealed record SignedUpdaterControl(string PayloadBase64, string SignatureBase64);
public sealed record UpdaterControlDocument(int SchemaVersion, Guid AuthorizationId,
    string Purpose, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc,
    Guid? ReleaseId, IReadOnlyDictionary<string, string>? PackageHashes,
    string? NextPublicKeyPem, string? PreviousKeySha256, long Generation);
