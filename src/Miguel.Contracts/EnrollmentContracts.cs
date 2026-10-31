namespace Miguel.Contracts;

public sealed record EnrollmentRequest(
    int SchemaVersion,
    Guid InstallationId,
    string EnrollmentToken,
    string DeviceKey,
    string MachineName,
    string AgentVersion,
    string? BackupClientVersion);

public sealed record EnrollmentResponse(
    int SchemaVersion,
    Guid DeviceId,
    string AgencyCode,
    DateTimeOffset EnrolledAtUtc);

public sealed record AgentIdentity(
    int SchemaVersion,
    Uri ServerUrl,
    Guid DeviceId,
    string DeviceKey,
    string AgencyCode,
    DateTimeOffset EnrolledAtUtc);
