using System.Text.Json;
using Miguel.Contracts;
using Npgsql;

namespace Miguel.Server.Admin;

public sealed record AdminAgentDeliveryHealthView(bool Reported, AgentDeliveryHealth? Health,
    DateTimeOffset? ReceivedAtUtc);

public static class AdminAgentDeliveryHealthEndpoints
{
    public static bool IsValid(AgentDeliveryHealth? health) => health is null ||
        health.QuarantinedEvents is >= 0 and <= 1_000_000 &&
        (health.LastRejectionCode is null || health.LastRejectionCode.Length is > 0 and <= 64 &&
            health.LastRejectionCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

    public static void MapAgentDeliveryHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/devices/{id:guid}/delivery-health", async
            (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.CreateCommand("SELECT agent_delivery_health::text,agent_delivery_health_received_at_utc FROM devices WHERE id=$1");
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var health = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<AgentDeliveryHealth>(reader.GetString(0), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Results.Ok(new AdminAgentDeliveryHealthView(health is not null, health,
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)));
        }).RequireAuthorization(AdminEndpointExtensions.OperationsReadPolicy).RequireRateLimiting("admin");
    }
}
