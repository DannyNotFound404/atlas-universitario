using System.Text.Json;
using Miguel.Contracts;

namespace Miguel.Agent.Outbox;

public sealed class BackupReportInbox(
    AgentPaths paths,
    OutboxStore outbox,
    ILogger<BackupReportInbox> logger)
{
    private const long MaximumReportBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private string ActivityFile => Path.Combine(paths.BackupOutboxDirectory, "activity.json");

    public bool IsBackupRunning(DateTimeOffset now) => ReadActivity().Any(r =>
        r.Status == BackupRunStatus.Running && BackupTelemetry.Result(r, now) == nameof(BackupRunStatus.Running));

    private List<BackupRunReport> ReadActivity()
    {
        try { return JsonSerializer.Deserialize<List<BackupRunReport>>(File.ReadAllText(ActivityFile), JsonOptions) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private async Task RecordActivityAsync(BackupRunReport report, CancellationToken token)
    {
        var reports = ReadActivity();
        var previous = reports.FirstOrDefault(r => r.EventId == report.EventId);
        if (previous is not null && (previous.Status != BackupRunStatus.Running ||
            previous.Evidence?.Sequence >= report.Evidence?.Sequence)) return;
        reports.RemoveAll(r => r.EventId == report.EventId || r.FinishedAtUtc < DateTimeOffset.UtcNow.AddDays(-1));
        reports.Add(report);
        await File.WriteAllTextAsync(ActivityFile + ".tmp", JsonSerializer.Serialize(reports.OrderByDescending(r => r.FinishedAtUtc).Take(200), JsonOptions), token);
        File.Move(ActivityFile + ".tmp", ActivityFile, true);
    }

    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        foreach (string file in Directory
                     .EnumerateFiles(paths.BackupInboxDirectory, "*.json")
                     // Recover the newest signal/terminal report first after an
                     // offline period, rather than drain hours of old heartbeats.
                     .OrderByDescending(path => path, StringComparer.Ordinal)
                     .Take(100))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileInfo information = new(file);
                if (information.Length is <= 0 or > MaximumReportBytes)
                {
                    if (information.Length == 0 && information.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-2)) continue;
                    Reject(file, "tamaño inválido");
                    continue;
                }

                BackupRunReport? report;
                await using (FileStream stream = new(
                                 file,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read))
                {
                    report = await JsonSerializer
                        .DeserializeAsync<BackupRunReport>(
                            stream,
                            JsonOptions,
                            cancellationToken);
                }

                if (!IsValid(report))
                {
                    Reject(file, "contenido inválido");
                    continue;
                }

                var previous = ReadActivity().FirstOrDefault(r => r.EventId == report!.EventId);
                if (previous is not null && (previous.Status != BackupRunStatus.Running ||
                    (previous.Evidence is not null && report!.Evidence is not null &&
                     previous.Evidence.Sequence >= report.Evidence.Sequence)))
                {
                    // The previous activity was written only after durable
                    // enqueue. Discarding an older signal cannot lose the latest
                    // result or reopen a completed/cancelled run.
                    File.Delete(file);
                    continue;
                }

                await outbox.EnqueueAsync(
                    OutboxEventTypes.BackupRun,
                    report!,
                    cancellationToken);
                await RecordActivityAsync(report!, cancellationToken);
                File.Delete(file);
                logger.LogInformation(
                    "Resultado de BackupNAS importado: {EventId}.",
                    report!.EventId);
            }
            catch (JsonException)
            {
                // Older clients wrote directly into .json with FileShare.Read.
                // Let those writers finish before preserving a malformed report.
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-2))
                    Reject(file, "JSON inválido");
            }
            catch (IOException ex)
            {
                logger.LogDebug(
                    ex,
                    "El reporte {Report} todavía está siendo escrito.",
                    Path.GetFileName(file));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(
                    ex,
                    "No se pudo leer el reporte {Report}.",
                    Path.GetFileName(file));
            }
        }
    }

    private static bool IsValid(BackupRunReport? report) => BackupTelemetry.IsValid(report, DateTimeOffset.UtcNow);

    private void Reject(string file, string reason)
    {
        logger.LogWarning(
            "Reporte local de BackupNAS rechazado ({Reason}): {Report}.",
            reason,
            Path.GetFileName(file));
        try
        {
            string rejected = Path.Combine(paths.BackupOutboxDirectory, "Rejected");
            Directory.CreateDirectory(rejected);
            File.Move(file, Path.Combine(rejected, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(file)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
