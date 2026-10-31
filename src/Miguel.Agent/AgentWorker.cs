using System.Diagnostics;
using Microsoft.Extensions.Options;
using Miguel.Agent.Identity;
using Miguel.Agent.Inventory;
using Miguel.Agent.Outbox;
using Miguel.Agent.Security;
using Miguel.Agent.Transport;
using Miguel.Contracts;

namespace Miguel.Agent;

public sealed class AgentWorker(
    AgentIdentityStore identityStore,
    SystemInventoryCollector inventoryCollector,
    InteractiveUserCollector interactiveUserCollector,
    SecurityPostureCollector securityPostureCollector,
    WindowsSecurityEventCollector securityEventCollector,
    SecurityEventCursorStore securityCursorStore,
    OutboxStore outbox,
    BackupReportInbox backupReportInbox,
    BackupClientVersionProvider backupClientVersionProvider,
    AgentApiClient apiClient,
    RemotePolicyClient remotePolicy,
    IOptions<AgentOptions> optionsAccessor,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly AgentOptions options = optionsAccessor.Value;
    private readonly DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
    private DateTimeOffset nextInventoryAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextSecurityPostureAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextSecurityEventsAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextHeartbeatAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextPolicyAtUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "El ciclo del agente falló; se reintentará.");
                if (options.RunOnce)
                {
                    Environment.ExitCode = 1;
                }
            }

            if (options.RunOnce)
            {
                applicationLifetime.StopApplication();
                return;
            }

            double heartbeatSeconds = remotePolicy.Current?.HeartbeatSeconds ?? options.HeartbeatInterval.TotalSeconds;
            double eventSeconds = remotePolicy.Current?.EventSeconds ?? options.SecurityEventInterval.TotalSeconds;
            await Task.Delay(AddJitter(TimeSpan.FromSeconds(Math.Min(60, Math.Min(heartbeatSeconds,eventSeconds)))), timeProvider, stoppingToken);
        }
    }

    private async Task ExecuteCycleAsync(CancellationToken cancellationToken)
    {
        AgentIdentity identity = await identityStore.LoadAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        // A bad inventory or a transport queue error must not silence liveness.
        await AgentCycle.RunAsync(() => CollectAndFlushAsync(identity, now, cancellationToken),
            () => SendHeartbeatAsync(identity, now, cancellationToken), cancellationToken);
    }

    private async Task CollectAndFlushAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now >= nextPolicyAtUtc)
        {
            nextPolicyAtUtc = now.AddMinutes(5);
            if (await remotePolicy.RefreshAsync(identity,cancellationToken))
            {
                nextInventoryAtUtc = now;
                nextSecurityPostureAtUtc = now;
                nextSecurityEventsAtUtc = now;
                nextHeartbeatAtUtc = now;
            }
        }
        var policy = remotePolicy.Current;

        if (now >= nextInventoryAtUtc)
        {
            DeviceInventory inventory = inventoryCollector.Capture();
            await outbox.EnqueueAsync(
                OutboxEventTypes.Inventory,
                inventory,
                cancellationToken);
            nextInventoryAtUtc = now.Add(policy is null ? options.InventoryInterval : TimeSpan.FromHours(policy.InventoryHours));
            logger.LogInformation("Inventario local capturado: {SnapshotId}.", inventory.SnapshotId);
        }

        if (now >= nextSecurityPostureAtUtc)
        {
            SecurityPostureSnapshot posture = securityPostureCollector.Capture();
            await outbox.EnqueueAsync(
                OutboxEventTypes.SecurityPosture,
                posture,
                cancellationToken);
            nextSecurityPostureAtUtc = now.Add(policy is null ? options.SecurityPostureInterval : TimeSpan.FromHours(policy.PostureHours));
            logger.LogInformation(
                "Postura de seguridad capturada: {SnapshotId}.",
                posture.SnapshotId);
        }

        if (now >= nextSecurityEventsAtUtc)
        {
            IReadOnlyDictionary<string, long> cursors =
                await securityCursorStore.LoadAsync(cancellationToken);
            SecurityEventCapture capture = securityEventCollector.Capture(
                cursors,
                options.SecurityEventInitialLookback,
                policy?.EventBatchSize ?? options.SecurityEventBatchSize);
            if (capture.Batch is not null)
            {
                await outbox.EnqueueAsync(
                    OutboxEventTypes.SecurityEvents,
                    capture.Batch,
                    cancellationToken);
                logger.LogInformation(
                    "Se capturaron {EventCount} eventos de seguridad.",
                    capture.Batch.Events.Count);
            }
            await securityCursorStore.SaveAsync(capture.Cursors, cancellationToken);
            nextSecurityEventsAtUtc = now.Add(policy is null ? options.SecurityEventInterval : TimeSpan.FromSeconds(policy.EventSeconds));
        }

        await backupReportInbox.ImportAsync(cancellationToken);

        await outbox.FlushAsync(
            (envelope, token) => apiClient.SendOutboxEnvelopeAsync(identity, envelope, token),
            cancellationToken);
    }

    private async Task SendHeartbeatAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now < nextHeartbeatAtUtc) return;
        InteractiveUserObservation? interactiveUser = null;
        try { interactiveUser = interactiveUserCollector.Capture(); }
        catch (Exception error) { logger.LogWarning(error, "No se pudo observar el usuario; el heartbeat continúa."); }
        int quarantined = outbox.QuarantinedCount();
        if (quarantined > 0)
            logger.LogWarning("Hay {QuarantinedEvents} eventos conservados para revisión técnica en la cuarentena local.", quarantined);
        AgentHeartbeat heartbeat = new(
            ApiContract.SchemaVersion,
            now,
            startedAtUtc,
            AgentVersion.Current,
            backupClientVersionProvider.GetCurrentVersion(),
            backupReportInbox.IsBackupRunning(now),
            outbox.Count(),
            interactiveUser,
            now,
            outbox.DeliveryHealth());
        await apiClient.EnviarHeartbeatAsync(identity, heartbeat, cancellationToken);
        nextHeartbeatAtUtc = now.Add(remotePolicy.Current is null ? options.HeartbeatInterval : TimeSpan.FromSeconds(remotePolicy.Current.HeartbeatSeconds));
        logger.LogInformation("Heartbeat enviado para {DeviceId}.", identity.DeviceId);
    }

    private static TimeSpan AddJitter(TimeSpan interval)
    {
        double factor = 0.9 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromMilliseconds(Math.Max(1000, interval.TotalMilliseconds * factor));
    }
}
