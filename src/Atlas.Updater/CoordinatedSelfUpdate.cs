using System.Diagnostics;
using Miguel.Contracts;

namespace Atlas.Updater;

internal static class CoordinatedSelfUpdate
{
    public static async Task<int> CompleteAsync(int parentId, Guid release, UpdaterPaths paths, UpdateLog log, CancellationToken ct)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 10; }
        FileStream processLock;
        try { processLock = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 12; }
        using var heldLock = processLock;
        UpdateJournal? journal;
        try { journal = DurableFile.Read<UpdateJournal>(paths.JournalFile); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { log.Write("El ayudante no pudo verificar su diario; no se modificaron componentes."); return 21; }
        if (!OwnsJournal(journal, release)) { log.Write("El ayudante ya no corresponde a la operación pendiente."); return 10; }
        var platform = new WindowsUpdatePlatform(paths, log);
        try
        {
            if (journal!.Phase == "ROLLING_BACK") return await new CoordinatedUpdateEngine(paths, log, platform).RecoverAsync(ct);
            AtlasReleasePackage package = journal.Packages.Single(p => p.Component == "updater");
            string source = Environment.ProcessPath ?? throw new IOException("self_source_missing");
            if (!await ResumableDownload.ValidAsync(source, package, ct)) throw new IOException("self_hash_mismatch");
            string next = paths.UpdaterExecutable + ".next";
            File.Copy(source, next, true);
            File.Replace(next, paths.UpdaterExecutable, paths.UpdaterExecutable + ".previous", true);
            if (VersionTagComparer.ReadProductVersion(paths.UpdaterExecutable) != package.Version) throw new IOException("self_version_mismatch");
            int health = await ProcessRunner.RunAsync(paths.UpdaterExecutable, ["--health-check"], TimeSpan.FromSeconds(30), log, ct);
            if (health != 0) throw new IOException("self_health_failed");
            DurableFile.Write(paths.JournalFile, journal with { Phase = "AWAITING_CONFIRMATION" });
            File.Delete(paths.PendingUpdateFile);
            log.Write("Actualizador reemplazado y comprobado; pendiente del reporte de confirmación.");
            return 10;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write($"Reemplazo propio interrumpido ({ex.GetType().Name}); iniciando recuperación durable.");
            return await new CoordinatedUpdateEngine(paths, log, platform).RecoverAsync(ct);
        }
    }
    internal static bool OwnsJournal(UpdateJournal? journal, Guid release) => journal is not null &&
        journal.ReleaseId == release && journal.UpdaterChanged && journal.Phase is "SELF_PENDING" or "ROLLING_BACK";
}
