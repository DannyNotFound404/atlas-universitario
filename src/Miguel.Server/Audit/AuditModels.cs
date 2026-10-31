using System.Text.Json;

namespace Miguel.Server.Audit;

public sealed record AuditOverview(
    DateTimeOffset GeneratedAtUtc,
    long TotalEvents,
    long EventsLast24Hours,
    long FailedAccessLast24Hours,
    long DistinctActors30Days,
    long EnrollmentEvents,
    long ConfirmedEnrollments,
    long SystemEnrollments,
    long UnavailableEnrollments,
    long Reenrollments,
    long ActiveEnrollmentTokens,
    IReadOnlyList<AuditFilterOption> Actors);

public sealed record AuditFilterOption(string Value, string Label);

public sealed record AuditEventPage(
    int Page,
    int PageSize,
    long Total,
    IReadOnlyList<AuditEventView> Items);

public sealed record AuditEventView(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string ActorType,
    string ActorId,
    string ActorDisplayName,
    string Action,
    string ActionLabel,
    string Module,
    string ModuleLabel,
    string Outcome,
    string? TargetType,
    string? TargetId,
    JsonElement? Detail,
    string? TargetDisplayName = null,
    bool ContextCaptured = false);

public sealed record AuditEnrollmentPage(
    int Page,
    int PageSize,
    long Total,
    IReadOnlyList<AuditEnrollmentView> Items);

public sealed record AuditEnrollmentView(
    long Id,
    Guid? IssuanceId,
    Guid DeviceId,
    string MachineName,
    string AgencyCode,
    string AgencyName,
    string? IssuedBy,
    string IssuedByDisplayName,
    string? IssuedByRole,
    string IssuanceSource,
    string? IssuedFromIp,
    string? EnrolledFromIp,
    string? InstallerVersion,
    DateTimeOffset? IssuedAtUtc,
    DateTimeOffset EnrolledAtUtc,
    string AgentVersion,
    string? BackupClientVersion,
    string AttributionStatus,
    bool IsReenrollment,
    bool ExecutionIdentityVerified,
    string? InteractiveUserAccount,
    string? InteractiveUserDisplayName,
    DateTimeOffset? InteractiveUserCheckedAtUtc,
    bool IssuerNameCaptured = false);

public sealed record AuditEventQuery(
    string Search,
    string Actor,
    string Module,
    string Outcome,
    string Period,
    int Page,
    int PageSize,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string Target = "");

public sealed record AuditEnrollmentQuery(
    string Search,
    string Agency,
    string Attribution,
    string Period,
    int Page,
    int PageSize,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record EnrollmentIssuanceContext(
    string ActorId,
    string ActorRole,
    string RemoteAddress,
    string Source,
    string? InstallerVersion);

public sealed record EnrollmentTokenIssue(
    string Token,
    Guid IssuanceId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string AgencyCode,
    string? InstallerVersion);
