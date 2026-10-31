namespace Miguel.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan InventoryInterval { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan SecurityPostureInterval { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan SecurityEventInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan SecurityEventInitialLookback { get; init; } = TimeSpan.FromMinutes(15);
    public int SecurityEventBatchSize { get; init; } = 250;
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string BackupClientVersion { get; init; } = "5.5.2-atlas.1";
    public string? BackupClientExecutablePath { get; init; }
    public bool RunOnce { get; init; }
}
