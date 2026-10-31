using System.Diagnostics;
using System.Text.Json;

namespace Atlas.Updater;

internal static class SelfUpdateCompleter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(
        string targetExecutable,
        int parentProcessId,
        Guid releaseId,
        UpdaterPaths paths,
        UpdateLog log,
        CancellationToken cancellationToken)
    {
        bool replaced = false;
        FileStream? updateLock = null;
        try
        {
            string expectedTarget = Path.GetFullPath(paths.UpdaterExecutable);
            string requestedTarget = Path.GetFullPath(targetExecutable);
            if (!string.Equals(
                    expectedTarget,
                    requestedTarget,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "La ruta de actualización propia no es válida.");
            }

            await WaitForParentAsync(
                parentProcessId,
                cancellationToken);
            updateLock = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string pendingStatePath = Path.Combine(paths.RootDirectory, $"state-{releaseId:D}.pending.json");
            UpdateState pending = DurableFile.Read<UpdateState>(pendingStatePath) ?? throw new IOException("pending_state_missing");
            if (pending.AppliedReleaseId != releaseId) throw new IOException("pending_release_mismatch");
            Directory.CreateDirectory(paths.UpdaterDirectory);
            string source = Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "No se pudo determinar el ejecutable del actualizador.");
            string next = requestedTarget + ".next";
            string previous = requestedTarget + ".previous";
            File.Copy(source, next, overwrite: true);
            if (File.Exists(requestedTarget))
            {
                File.Replace(
                    next,
                    requestedTarget,
                    previous,
                    ignoreMetadataErrors: true);
                replaced = true;
            }
            else
            {
                File.Move(next, requestedTarget);
            }

            if (VersionTagComparer.ReadProductVersion(requestedTarget) != VersionTagComparer.ReadProductVersion(source))
                throw new IOException("bridge_version_mismatch");
            int health = await ProcessRunner.RunAsync(requestedTarget, ["--health-check"], TimeSpan.FromSeconds(30), log, cancellationToken);
            if (health != 0) throw new IOException("bridge_health_failed");
            await TaskCoordinator.EnsureAsync(paths, log, cancellationToken);
            File.Move(
                pendingStatePath,
                paths.StateFile,
                overwrite: true);
            log.Write(
                $"Actualizador reemplazado y release {releaseId:D} confirmado.");
            return 0;
        }
        catch (Exception exception)
        {
            string previous = paths.UpdaterExecutable + ".previous";
            if (replaced && File.Exists(previous))
            {
                try { File.Copy(previous, paths.UpdaterExecutable + ".next", true); File.Move(paths.UpdaterExecutable + ".next", paths.UpdaterExecutable, true); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                { log.Write("La versión anterior del actualizador se conserva para recuperación manual."); }
            }
            log.Write(
                $"ERROR al completar actualización propia: {exception.Message}");
            return 1;
        }
        finally { updateLock?.Dispose(); }
    }

    public static async Task WritePendingStateAsync(
        UpdateState state,
        UpdaterPaths paths,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        string path = Path.Combine(
            paths.RootDirectory,
            $"state-{state.AppliedReleaseId:D}.pending.json");
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await JsonSerializer.SerializeAsync(
            stream,
            state,
            JsonOptions,
            cancellationToken);
    }

    private static async Task WaitForParentAsync(
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // El proceso anterior ya terminó.
        }
    }
}
