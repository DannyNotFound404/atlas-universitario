using Miguel.Server.Admin;

namespace Miguel.Server.Software;

public sealed class SoftwareNotificationService(SoftwareRepository software, AdminNotificationRepository notifications,
    TimeProvider clock, ILogger<SoftwareNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), clock, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var summary = await software.Summary(stoppingToken);
                DateTimeOffset now = clock.GetUtcNow(); string day = now.ToString("yyyy-MM-dd");
                var candidates = new List<AdminNotificationCandidate>();
                void Add(string key, string title, string message) => candidates.Add(new("software:" + key + ":" + day, "SOFTWARE", "WARNING", title, message, "software", null, now));
                int licenses = summary.GetProperty("expiringLicenses").GetInt32();
                if (licenses > 0) Add("licenses", "Revisar vigencia de licencias", $"{licenses} registros de licencia están vencidos o vencen en los próximos 30 días. Consulte Licencias en Software.");
                int pending = summary.GetProperty("affectedDevices").GetInt32();
                if (pending > 0) Add("findings", "Software requiere revisión", $"Se observaron pendientes de catálogo, licenciamiento, soporte o actualidad en {pending} equipos. Consulte Pendientes en Software.");
                if (summary.GetProperty("reviewDue").GetBoolean()) Add("review", "Revisión semestral de software pendiente", "Registre la revisión del semestre, sus responsables y la evidencia. Referencia: Política V4 §6 Software, pág. 16.");
                await notifications.UpsertAsync(candidates, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudieron actualizar los avisos de software."); }
            await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
        }
    }
}
