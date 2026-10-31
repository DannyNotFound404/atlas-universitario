namespace Miguel.Contracts;

public sealed record AgentHeartbeat(
    int SchemaVersion,
    DateTimeOffset SentAtUtc,
    DateTimeOffset AgentStartedAtUtc,
    string AgentVersion,
    string? BackupClientVersion,
    bool BackupRunning,
    int PendingEvents,
    InteractiveUserObservation? InteractiveUser = null,
    DateTimeOffset? InteractiveUserCheckedAtUtc = null,
    AgentDeliveryHealth? DeliveryHealth = null);

public sealed record AgentDeliveryHealth(
    int QuarantinedEvents,
    DateTimeOffset? LastQuarantineAtUtc = null,
    string? LastRejectionCode = null);

public sealed record InteractiveUserObservation(
    string AccountName,
    string? DisplayName,
    string? SecurityIdentifier,
    string SessionType,
    string ResolutionSource);

