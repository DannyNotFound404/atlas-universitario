using System.Security.Principal;

namespace Atlas.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--health-check") return 0;
        if (!IsAdministrator()) return 5;
        UpdaterPaths paths = new();
        UpdateLog log = new(paths);
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        string operation = "update-check";
        Guid? releaseId = null;
        int result;
        try
        {
            if (args.Length == 3 && args[0] == "--complete-coordinated-update" &&
                int.TryParse(args[1], out int coordinatedParent) && Guid.TryParse(args[2], out Guid coordinatedRelease))
            {
                operation = "self-update"; releaseId = coordinatedRelease;
                result = await CoordinatedSelfUpdate.CompleteAsync(coordinatedParent, coordinatedRelease, paths, log, CancellationToken.None);
            }
            else if (args.Length == 4 &&
                string.Equals(
                    args[0],
                    "--complete-self-update",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[2], out int parentProcessId) &&
                Guid.TryParse(args[3], out Guid parsedReleaseId))
            {
                operation = "self-update";
                releaseId = parsedReleaseId;
                result = await SelfUpdateCompleter.RunAsync(
                    args[1],
                    parentProcessId,
                    parsedReleaseId,
                    paths,
                    log,
                    CancellationToken.None);
            }
            else
            {
                result = await RunUpdateCheckAsync(paths, log);
            }
        }
        catch (Exception exception)
        {
            log.Write($"ERROR no controlado: {exception.Message}");
            result = 1;
        }

        UpdaterStatusWriter.TryWrite(
            paths,
            startedAtUtc,
            operation,
            result,
            releaseId);
        return result;
    }

    private static async Task<int> RunUpdateCheckAsync(
        UpdaterPaths paths,
        UpdateLog log)
    {
        if (!IsAdministrator())
        {
            log.Write("ERROR: ATLAS Updater requiere privilegios de SYSTEM o administrador.");
            return 5;
        }

        paths.EnsureDirectories();
        FileStream processLock;
        try
        {
            processLock = new FileStream(
                paths.LockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            log.Write("Otra ejecución del actualizador sigue activa.");
            return 12;
        }

        using (processLock)
        {
            await TaskCoordinator.EnsureAsync(
                paths,
                log,
                CancellationToken.None);
            CoordinatedUpdateEngine engine = new(paths, log, new WindowsUpdatePlatform(paths, log));
            using var operationLimit = new CancellationTokenSource(TimeSpan.FromMinutes(100));
            return await engine.RunAsync(operationLimit.Token);
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) ||
               string.Equals(
                   identity.User?.Value,
                   "S-1-5-18",
                   StringComparison.Ordinal);
    }
}
