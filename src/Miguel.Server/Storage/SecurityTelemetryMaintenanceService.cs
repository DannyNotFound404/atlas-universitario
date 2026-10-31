using Microsoft.Extensions.Options;

namespace Miguel.Server.Storage;

public sealed class SecurityTelemetryMaintenanceService(
    SecurityRepository repository,
    IOptions<ServerOptions> optionsAccessor,
    ILogger<SecurityTelemetryMaintenanceService> logger,
    Miguel.Server.Configuration.OperationalSettingsService? settings = null) : BackgroundService
{
    private readonly ServerOptions options = optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PruneAsync(stoppingToken);
        using PeriodicTimer timer = new(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PruneAsync(stoppingToken);
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retention = settings is null ? null : (await settings.RetentionAsync(cancellationToken)).Value;
            await repository.PruneAsync(
                retention?.EventsDays ?? options.SecurityEventRetentionDays,
                retention?.PostureDays ?? options.SecurityPostureRetentionDays,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // El servidor se está deteniendo.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo aplicar la retención de telemetría de seguridad.");
        }
    }
}
