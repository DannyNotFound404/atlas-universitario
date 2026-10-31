using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miguel.Contracts;

namespace Atlas.Updater;

internal sealed class UpdateEngine(
    UpdaterPaths paths,
    UpdateLog log)
{
    private const string AgentServiceName = "MIGUEL Agent";
    private const string BackupServiceName = "ATLAS Backup Service";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        log.Write("Iniciando comprobación de actualización firmada.");
        AgentIdentity identity = await ReadIdentityAsync(cancellationToken);
        using SignedReleaseClient client = new(
            identity,
            paths.ReleasePublicKeyFile,
            log);
        VerifiedRelease? release =
            await client.GetReleaseAsync(cancellationToken);
        if (release is null)
        {
            ClearPendingUpdate();
            return 0;
        }

        UpdateState? state = await ReadStateAsync(cancellationToken);
        await RemoveRetiredBackupServiceAsync(cancellationToken);

        Dictionary<string, AtlasReleasePackage> updates =
            SelectUpdates(release.Manifest);
        if (updates.Count == 0)
        {
            await WriteStateAsync(
                CreateState(release.Manifest),
                cancellationToken);
            ClearPendingUpdate();
            log.Write(
                state?.AppliedReleaseId == release.Manifest.ReleaseId
                    ? $"Release {release.Manifest.ReleaseVersion} verificado; " +
                      "los componentes locales coinciden."
                    : $"Release {release.Manifest.ReleaseVersion} no requiere " +
                      "cambios locales.");
            return 0;
        }

        if (IsBackupRunning())
        {
            await WritePendingUpdateAsync(
                release.Manifest,
                "backupnas-running",
                cancellationToken);
            log.Write(
                "Actualización aplazada: BackupNAS está ejecutando una copia.");
            return 10;
        }

        EnsureSufficientDiskSpace(updates.Values);

        string releaseStaging = Path.Combine(
            paths.StagingDirectory,
            release.Manifest.ReleaseId.ToString("D"));
        RecreateDirectory(releaseStaging);
        Dictionary<string, DownloadedPackage> downloads =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (AtlasReleasePackage package in updates.Values)
            {
                log.Write(
                    $"Descargando {package.Component} {package.Version}.");
                downloads[package.Component] =
                    await client.DownloadAsync(
                        release.Manifest.ReleaseId,
                        package,
                        releaseStaging,
                        cancellationToken);
            }

            if (IsBackupRunning())
            {
                await WritePendingUpdateAsync(
                    release.Manifest,
                    "backupnas-started",
                    cancellationToken);
                log.Write(
                    "Actualización aplazada: BackupNAS comenzó una copia.");
                return 10;
            }

            ApplyContext context = new(releaseStaging);
            try
            {
                if (downloads.TryGetValue(
                        AtlasReleaseComponents.Agent,
                        out DownloadedPackage? agent))
                {
                    await ApplyAgentAsync(
                        agent,
                        context,
                        cancellationToken);
                }
                if (downloads.TryGetValue(
                        AtlasReleaseComponents.BackupNas,
                        out DownloadedPackage? backup))
                {
                    ApplyBackup(backup, context);
                }
                if (downloads.TryGetValue(
                        AtlasReleaseComponents.BackupService,
                        out DownloadedPackage? backupService))
                {
                    await ApplyBackupServiceAsync(
                        backupService,
                        context,
                        cancellationToken);
                }
                await VerifyAgentHealthAsync(cancellationToken);

                UpdateState newState = CreateState(release.Manifest);
                if (downloads.TryGetValue(
                        AtlasReleaseComponents.Updater,
                        out DownloadedPackage? updater))
                {
                    await SelfUpdateCompleter.WritePendingStateAsync(
                        newState,
                        paths,
                        cancellationToken);
                    StartSelfUpdate(updater.Path, release.Manifest.ReleaseId);
                    ClearPendingUpdate();
                    log.Write(
                        $"Release {release.Manifest.ReleaseVersion} aplicado; " +
                        "el reemplazo del actualizador continuará al salir.");
                    return 0;
                }

                await WriteStateAsync(newState, cancellationToken);
                ClearPendingUpdate();
                log.Write(
                    $"Release {release.Manifest.ReleaseVersion} aplicado correctamente.");
                return 0;
            }
            catch
            {
                await RollbackAsync(context, cancellationToken);
                throw;
            }
        }
        finally
        {
            // Si el helper de auto-reemplazo está usando el ejecutable nuevo,
            // Windows conservará el archivo; la limpieza se reintentará luego.
            TryDeleteDirectory(releaseStaging);
        }
    }

    private void EnsureSufficientDiskSpace(
        IEnumerable<AtlasReleasePackage> packages)
    {
        long downloadBytes = packages.Sum(package => package.SizeBytes);
        long rollbackBytes = GetDirectorySize(paths.AgentDirectory);
        if (File.Exists(paths.BackupExecutable))
        {
            rollbackBytes += new FileInfo(paths.BackupExecutable).Length;
        }

        long requiredBytes = Math.Max(
            1024L * 1024L * 1024L,
            checked((downloadBytes * 2) + rollbackBytes +
                    (512L * 1024L * 1024L)));
        string root = Path.GetPathRoot(
            Path.GetFullPath(paths.StagingDirectory))
            ?? throw new IOException(
                "No se pudo determinar la unidad de staging de ATLAS.");
        DriveInfo drive = new(root);
        if (!drive.IsReady)
        {
            throw new IOException(
                $"La unidad {root} no estÃ¡ disponible para actualizar ATLAS.");
        }
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            long requiredMiB = (long)Math.Ceiling(
                requiredBytes / (1024d * 1024d));
            long availableMiB = drive.AvailableFreeSpace / (1024L * 1024L);
            throw new IOException(
                $"Espacio insuficiente para actualizar ATLAS: se requieren " +
                $"{requiredMiB} MiB y hay {availableMiB} MiB disponibles.");
        }

        long availableMiBForLog =
            drive.AvailableFreeSpace / (1024L * 1024L);
        log.Write(
            $"Preflight de espacio correcto: {availableMiBForLog} MiB disponibles.");
    }

    private static long GetDirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"No se pudo calcular el espacio de reversiÃ³n para " +
                $"'{directory}'.",
                exception);
        }
    }

    private Dictionary<string, AtlasReleasePackage> SelectUpdates(
        AtlasReleaseManifest manifest)
    {
        bool backupNasEnabled = ComponentPolicy.IsBackupNasEnabled(
            paths.ComponentPolicyFile);
        if (!backupNasEnabled && File.Exists(paths.BackupExecutable))
        {
            backupNasEnabled = ComponentPolicy.RepairFromInstalledBackupNas(
                paths.ComponentPolicyFile,
                paths.BackupExecutable);
            if (backupNasEnabled)
            {
                log.Write(
                    "Política local de BackupNAS reparada a partir de la " +
                    "instalación protegida existente.");
            }
        }
        Dictionary<string, AtlasReleasePackage> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (AtlasReleasePackage package in manifest.Packages)
        {
            if (string.Equals(
                    package.Component,
                    AtlasReleaseComponents.BackupService,
                    StringComparison.OrdinalIgnoreCase))
            {
                log.Write(
                    "backup-service omitido: el componente VSS fue retirado " +
                    "de la línea estable de ATLAS.");
                continue;
            }
            bool isBackupComponent = string.Equals(
                    package.Component,
                    AtlasReleaseComponents.BackupNas,
                    StringComparison.OrdinalIgnoreCase);
            if (isBackupComponent && !backupNasEnabled)
            {
                log.Write(
                    $"{package.Component} omitido por la política local protegida.");
                continue;
            }

            string? installed = package.Component.ToLowerInvariant() switch
            {
                AtlasReleaseComponents.Agent =>
                    VersionTagComparer.ReadProductVersion(
                        paths.AgentExecutable),
                AtlasReleaseComponents.BackupNas =>
                    VersionTagComparer.ReadProductVersion(
                        paths.BackupExecutable),
                AtlasReleaseComponents.BackupService =>
                    VersionTagComparer.ReadProductVersion(
                        paths.BackupServiceExecutable),
                AtlasReleaseComponents.Updater =>
                    VersionTagComparer.ReadProductVersion(
                        paths.UpdaterExecutable),
                AtlasReleaseComponents.Setup => package.Version,
                _ => throw new InvalidDataException(
                    $"El release contiene el componente desconocido " +
                    $"'{package.Component}'.")
            };
            if (VersionTagComparer.IsTargetNewer(
                    package.Version,
                    installed))
            {
                result.Add(package.Component, package);
            }
            else
            {
                log.Write(
                    $"{package.Component} {installed ?? "sin versión"} " +
                    $"no requiere {package.Version}.");
            }
        }
        return result;
    }

    private async Task RemoveRetiredBackupServiceAsync(
        CancellationToken cancellationToken)
    {
        string sc = GetScPath();
        bool serviceExists = await ServiceExistsAsync(
            sc,
            BackupServiceName,
            cancellationToken);
        bool directoryExists = Directory.Exists(paths.BackupServiceDirectory);
        if (!serviceExists && !directoryExists)
        {
            return;
        }

        try
        {
            if (serviceExists)
            {
                _ = await ProcessRunner.RunAsync(
                    sc,
                    ["stop", BackupServiceName],
                    TimeSpan.FromSeconds(45),
                    log,
                    cancellationToken);
                int deleteResult = await ProcessRunner.RunAsync(
                    sc,
                    ["delete", BackupServiceName],
                    TimeSpan.FromSeconds(45),
                    log,
                    cancellationToken);
                if (deleteResult != 0 &&
                    await ServiceExistsAsync(
                        sc,
                        BackupServiceName,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"sc.exe delete devolvió {deleteResult}.");
                }
            }

            TryDeleteDirectory(paths.BackupServiceDirectory);
            if (Directory.Exists(paths.BackupServiceDirectory))
            {
                throw new IOException(
                    "Windows mantiene archivos del servicio VSS en uso.");
            }
            log.Write(
                "ATLAS Backup Service retirado: VSS ya no forma parte de " +
                "la línea estable.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            log.Write(
                "ADVERTENCIA: no se pudo completar el retiro del antiguo " +
                $"servicio VSS: {exception.Message}");
        }
    }

    private async Task ApplyAgentAsync(
        DownloadedPackage package,
        ApplyContext context,
        CancellationToken cancellationToken)
    {
        context.AgentRollbackDirectory = Path.Combine(
            context.Root,
            "rollback-agent");
        if (Directory.Exists(paths.AgentDirectory))
        {
            CopyDirectory(
                paths.AgentDirectory,
                context.AgentRollbackDirectory);
        }

        string packageRoot = Path.Combine(context.Root, "agent-package");
        ExtractPackage(package.Path, packageRoot);
        string installer = Path.Combine(
            packageRoot,
            "Install-MiguelAgent.ps1");
        string source = Path.Combine(packageRoot, "agent");
        if (!File.Exists(installer) ||
            !File.Exists(Path.Combine(source, "Miguel.Agent.exe")))
        {
            throw new InvalidDataException(
                "El paquete del agente no contiene el instalador esperado.");
        }

        context.AgentChanged = true;
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        int exitCode = await ProcessRunner.RunAsync(
            powershell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                installer,
                "-SourceDirectory",
                source
            ],
            TimeSpan.FromMinutes(8),
            log,
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"La instalación del agente devolvió {exitCode}.");
        }
    }

    private void ApplyBackup(
        DownloadedPackage package,
        ApplyContext context)
    {
        if (IsBackupRunning())
        {
            throw new InvalidOperationException(
                "BackupNAS inició una copia antes de actualizarse.");
        }
        Directory.CreateDirectory(paths.BackupDirectory);
        context.BackupRollbackFile = Path.Combine(
            context.Root,
            "rollback-BackupNAS.exe");
        string next = paths.BackupExecutable + ".next";
        File.Copy(package.Path, next, overwrite: true);
        if (File.Exists(paths.BackupExecutable))
        {
            File.Replace(
                next,
                paths.BackupExecutable,
                context.BackupRollbackFile,
                ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(next, paths.BackupExecutable);
        }
        context.BackupChanged = true;
        log.Write($"BackupNAS actualizado a {package.Package.Version}.");
    }

    private async Task ApplyBackupServiceAsync(
        DownloadedPackage package,
        ApplyContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                Path.GetExtension(package.Path),
                ".zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "backup-service debe distribuirse como un archivo ZIP firmado.");
        }

        string sc = GetScPath();
        context.BackupServicePreviouslyInstalled =
            await ServiceExistsAsync(sc, BackupServiceName, cancellationToken);
        context.BackupServicePreviouslyRunning =
            context.BackupServicePreviouslyInstalled &&
            await IsServiceRunningAsync(
                sc,
                BackupServiceName,
                cancellationToken);
        context.BackupServiceDirectoryExisted =
            Directory.Exists(paths.BackupServiceDirectory);
        context.BackupServiceRollbackDirectory = Path.Combine(
            context.Root,
            "rollback-backup-service");
        if (context.BackupServiceDirectoryExisted)
        {
            CopyDirectory(
                paths.BackupServiceDirectory,
                context.BackupServiceRollbackDirectory);
        }

        string packageRoot = Path.Combine(
            context.Root,
            "backup-service-package");
        ExtractPackage(package.Path, packageRoot);
        string[] executables = Directory.GetFiles(
            packageRoot,
            "ATLAS.Backup.Service.exe",
            SearchOption.AllDirectories);
        if (executables.Length != 1)
        {
            throw new InvalidDataException(
                "El paquete backup-service debe contener exactamente un " +
                "ATLAS.Backup.Service.exe.");
        }
        string sourceDirectory = Path.GetDirectoryName(executables[0])!;

        context.BackupServiceChanged = true;
        if (context.BackupServicePreviouslyInstalled)
        {
            _ = await ProcessRunner.RunAsync(
                sc,
                ["stop", BackupServiceName],
                TimeSpan.FromSeconds(45),
                log,
                cancellationToken);
        }

        int mirrorResult = await MirrorDirectoryAsync(
            sourceDirectory,
            paths.BackupServiceDirectory,
            cancellationToken);
        if (mirrorResult > 7 || !File.Exists(paths.BackupServiceExecutable))
        {
            throw new InvalidOperationException(
                $"No fue posible instalar los binarios de backup-service " +
                $"(Robocopy {mirrorResult}).");
        }

        if (!context.BackupServicePreviouslyInstalled)
        {
            await RunScRequiredAsync(
                sc,
                [
                    "create",
                    BackupServiceName,
                    "binPath=",
                    paths.BackupServiceExecutable,
                    "start=",
                    "auto",
                    "DisplayName=",
                    "ATLAS Backup Service"
                ],
                "crear el servicio de respaldo",
                cancellationToken);
        }
        await RunScRequiredAsync(
            sc,
            [
                "config",
                BackupServiceName,
                "binPath=",
                paths.BackupServiceExecutable,
                "start=",
                "auto"
            ],
            "configurar el servicio de respaldo",
            cancellationToken);
        await RunScRequiredAsync(
            sc,
            [
                "failure",
                BackupServiceName,
                "reset=",
                "86400",
                "actions=",
                "restart/60000/restart/60000/restart/300000"
            ],
            "configurar la recuperación del servicio de respaldo",
            cancellationToken);
        await RunScRequiredAsync(
            sc,
            ["start", BackupServiceName],
            "iniciar el servicio de respaldo",
            cancellationToken);
        await VerifyServiceHealthAsync(
            sc,
            BackupServiceName,
            cancellationToken);

        string? installedVersion = VersionTagComparer.ReadProductVersion(
            paths.BackupServiceExecutable);
        if (VersionTagComparer.IsTargetNewer(
                package.Package.Version,
                installedVersion))
        {
            throw new InvalidOperationException(
                $"backup-service no alcanzÃ³ la versiÃ³n firmada " +
                $"{package.Package.Version}; reporta " +
                $"{installedVersion ?? "sin versiÃ³n"}.");
        }
        log.Write(
            $"backup-service actualizado a {package.Package.Version}.");
    }

    private async Task VerifyAgentHealthAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.AgentExecutable))
        {
            throw new InvalidOperationException(
                "El ejecutable del agente no existe después de actualizar.");
        }
        string sc = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "sc.exe");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsServiceRunningAsync(
                    sc,
                    AgentServiceName,
                    cancellationToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                if (await IsServiceRunningAsync(
                        sc,
                        AgentServiceName,
                        cancellationToken))
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new InvalidOperationException(
            "El servicio ATLAS Agent no volvió a un estado saludable.");
    }

    private static async Task<bool> IsServiceRunningAsync(
        string sc,
        string serviceName,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = sc,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add(serviceName);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return false;
        }
        string output = await process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 &&
               output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ServiceExistsAsync(
        string sc,
        string serviceName,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = sc,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add(serviceName);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return false;
        }
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }

    private async Task VerifyServiceHealthAsync(
        string sc,
        string serviceName,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsServiceRunningAsync(
                    sc,
                    serviceName,
                    cancellationToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                if (await IsServiceRunningAsync(
                        sc,
                        serviceName,
                        cancellationToken))
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new InvalidOperationException(
            $"El servicio '{serviceName}' no quedÃ³ saludable.");
    }

    private async Task RunScRequiredAsync(
        string sc,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        int exitCode = await ProcessRunner.RunAsync(
            sc,
            arguments,
            TimeSpan.FromSeconds(45),
            log,
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"No fue posible {operation} (sc.exe {exitCode}).");
        }
    }

    private async Task<int> MirrorDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        string robocopy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "robocopy.exe");
        return await ProcessRunner.RunAsync(
            robocopy,
            [
                source,
                destination,
                "/MIR",
                "/XJ",
                "/R:2",
                "/W:1",
                "/NFL",
                "/NDL",
                "/NJH",
                "/NJS",
                "/NP"
            ],
            TimeSpan.FromMinutes(3),
            log,
            cancellationToken);
    }

    private static string GetScPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "sc.exe");

    private async Task RollbackAsync(
        ApplyContext context,
        CancellationToken cancellationToken)
    {
        log.Write("Aplicación fallida; iniciando reversión.");
        if (context.BackupServiceChanged)
        {
            try
            {
                string sc = GetScPath();
                if (await ServiceExistsAsync(
                        sc,
                        BackupServiceName,
                        cancellationToken))
                {
                    _ = await ProcessRunner.RunAsync(
                        sc,
                        ["stop", BackupServiceName],
                        TimeSpan.FromSeconds(45),
                        log,
                        cancellationToken);
                }

                if (context.BackupServiceDirectoryExisted &&
                    context.BackupServiceRollbackDirectory is not null &&
                    Directory.Exists(context.BackupServiceRollbackDirectory))
                {
                    int result = await MirrorDirectoryAsync(
                        context.BackupServiceRollbackDirectory,
                        paths.BackupServiceDirectory,
                        cancellationToken);
                    if (result > 7)
                    {
                        log.Write(
                            $"ERROR: no se pudo revertir backup-service " +
                            $"(Robocopy {result}).");
                    }
                }
                else
                {
                    TryDeleteDirectory(paths.BackupServiceDirectory);
                }

                if (context.BackupServicePreviouslyInstalled)
                {
                    _ = await ProcessRunner.RunAsync(
                        sc,
                        [
                            "config",
                            BackupServiceName,
                            "binPath=",
                            paths.BackupServiceExecutable,
                            "start=",
                            "auto"
                        ],
                        TimeSpan.FromSeconds(45),
                        log,
                        cancellationToken);
                    if (context.BackupServicePreviouslyRunning)
                    {
                        _ = await ProcessRunner.RunAsync(
                            sc,
                            ["start", BackupServiceName],
                            TimeSpan.FromSeconds(45),
                            log,
                            cancellationToken);
                    }
                }
                else if (await ServiceExistsAsync(
                             sc,
                             BackupServiceName,
                             cancellationToken))
                {
                    _ = await ProcessRunner.RunAsync(
                        sc,
                        ["delete", BackupServiceName],
                        TimeSpan.FromSeconds(45),
                        log,
                        cancellationToken);
                }
            }
            catch (Exception exception)
            {
                log.Write(
                    $"ERROR: la reversiÃ³n de backup-service fallÃ³: " +
                    exception.Message);
            }
        }
        if (context.BackupChanged &&
            context.BackupRollbackFile is not null &&
            File.Exists(context.BackupRollbackFile))
        {
            string failed = paths.BackupExecutable + ".failed";
            File.Replace(
                context.BackupRollbackFile,
                paths.BackupExecutable,
                failed,
                ignoreMetadataErrors: true);
        }
        if (context.AgentChanged &&
            context.AgentRollbackDirectory is not null &&
            Directory.Exists(context.AgentRollbackDirectory))
        {
            string sc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "sc.exe");
            _ = await ProcessRunner.RunAsync(
                sc,
                ["stop", AgentServiceName],
                TimeSpan.FromSeconds(30),
                log,
                cancellationToken);
            string robocopy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "robocopy.exe");
            int result = await ProcessRunner.RunAsync(
                robocopy,
                [
                    context.AgentRollbackDirectory,
                    paths.AgentDirectory,
                    "/MIR",
                    "/XJ",
                    "/R:2",
                    "/W:1",
                    "/NFL",
                    "/NDL",
                    "/NJH",
                    "/NJS",
                    "/NP"
                ],
                TimeSpan.FromMinutes(3),
                log,
                cancellationToken);
            if (result > 7)
            {
                log.Write(
                    $"ERROR: Robocopy no pudo revertir el agente ({result}).");
            }
            _ = await ProcessRunner.RunAsync(
                sc,
                ["start", AgentServiceName],
                TimeSpan.FromSeconds(30),
                log,
                cancellationToken);
        }
        log.Write("Reversión terminada.");
    }

    private void StartSelfUpdate(
        string newUpdater,
        Guid releaseId)
    {
        Directory.CreateDirectory(paths.UpdaterDirectory);
        ProcessStartInfo startInfo = new()
        {
            FileName = newUpdater,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--complete-self-update");
        startInfo.ArgumentList.Add(paths.UpdaterExecutable);
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(releaseId.ToString("D"));
        _ = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "No se pudo iniciar el reemplazo del actualizador.");
    }

    private async Task<AgentIdentity> ReadIdentityAsync(
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(paths.IdentityFile);
        AgentIdentity? identity =
            await JsonSerializer.DeserializeAsync<AgentIdentity>(
                stream,
                JsonOptions,
                cancellationToken);
        if (identity is null ||
            identity.SchemaVersion != ApiContract.SchemaVersion ||
            identity.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.DeviceKey) ||
            identity.ServerUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException(
                "La identidad local de ATLAS no es válida.");
        }
        return identity;
    }

    private async Task<UpdateState?> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.StateFile))
        {
            return null;
        }
        await using FileStream stream = File.OpenRead(paths.StateFile);
        return await JsonSerializer.DeserializeAsync<UpdateState>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private async Task WriteStateAsync(
        UpdateState state,
        CancellationToken cancellationToken)
    {
        string temporary = paths.StateFile + ".next";
        await using (FileStream stream = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                JsonOptions,
                cancellationToken);
        }
        File.Move(temporary, paths.StateFile, overwrite: true);
    }

    private async Task WritePendingUpdateAsync(
        AtlasReleaseManifest manifest,
        string reason,
        CancellationToken cancellationToken)
    {
        PendingUpdateStatus status = new(
            ApiContract.SchemaVersion,
            manifest.ReleaseId,
            manifest.ReleaseVersion,
            DateTimeOffset.UtcNow,
            reason);
        string temporary = paths.PendingUpdateFile + ".next";
        await using (FileStream stream = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                status,
                JsonOptions,
                cancellationToken);
        }
        File.Move(temporary, paths.PendingUpdateFile, overwrite: true);
    }

    private void ClearPendingUpdate()
    {
        try
        {
            File.Delete(paths.PendingUpdateFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static UpdateState CreateState(
        AtlasReleaseManifest manifest) =>
        new(
            ApiContract.SchemaVersion,
            manifest.ReleaseId,
            manifest.ReleaseVersion,
            DateTimeOffset.UtcNow);

    private static bool IsBackupRunning()
    {
        Process[] processes = Process.GetProcessesByName("BackupNAS");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void ExtractPackage(
        string archivePath,
        string destination)
    {
        Directory.CreateDirectory(destination);
        string boundary =
            Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        long total = 0;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }
            total = checked(total + entry.Length);
            if (total > 500_000_000)
            {
                throw new InvalidDataException(
                    "El agente descomprimido supera el límite permitido.");
            }
            string target = Path.GetFullPath(
                Path.Combine(
                    destination,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(
                    boundary,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "El paquete del agente intentó salir de su carpeta.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RecreateDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ApplyContext(string root)
    {
        public string Root { get; } = root;
        public bool AgentChanged { get; set; }
        public string? AgentRollbackDirectory { get; set; }
        public bool BackupChanged { get; set; }
        public string? BackupRollbackFile { get; set; }
        public bool BackupServiceChanged { get; set; }
        public bool BackupServicePreviouslyInstalled { get; set; }
        public bool BackupServicePreviouslyRunning { get; set; }
        public bool BackupServiceDirectoryExisted { get; set; }
        public string? BackupServiceRollbackDirectory { get; set; }
    }
}
