namespace Miguel.Contracts;

public static class ApiContract
{
    public const int SchemaVersion = 1;
    public const string EnrollmentRoute = "/api/v1/enrollment";
    public const string HeartbeatRoute = "/api/v1/agent/heartbeat";
    public const string InventoryRoute = "/api/v1/agent/inventory";
    public const string BackupRunsRoute = "/api/v1/agent/backup-runs";
    public const string SecurityPostureRoute = "/api/v1/agent/security/posture";
    public const string SecurityEventsRoute = "/api/v1/agent/security/events";
    public const string DeviceIdHeader = "X-Miguel-Device-Id";
    public const string AuthorizationScheme = "Miguel";
}
