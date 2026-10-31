using System.Text.Json;

namespace Miguel.Contracts;

public sealed record OutboxEnvelope(
    int SchemaVersion,
    Guid EventId,
    string EventType,
    DateTimeOffset CreatedAtUtc,
    JsonElement Payload);

public static class OutboxEventTypes
{
    public const string Inventory = "device.inventory.v1";
    public const string BackupRun = "backup.run.v1";
    public const string SecurityPosture = "security.posture.v1";
    public const string SecurityEvents = "security.events.v1";
}
