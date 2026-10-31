namespace Atlas.Updater;

internal static class TaskCoordinator
{
    internal const string CoordinatedBackupMinimumVersion =
        "5.7.0-atlas.1";

    private const string Script = """
        param(
            [Parameter(Mandatory)][string]$UpdaterExecutable,
            [Parameter(Mandatory)]
            [ValidateSet("Legacy", "Coordinated")]
            [string]$StartupMode
        )
        Set-StrictMode -Version Latest
        $ErrorActionPreference = "Stop"

        $updaterData = Join-Path $env:ProgramData "ATLAS\Updater"
        $statusDirectory = Join-Path $updaterData "Status"
        New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null
        $icacls = Join-Path $env:SystemRoot "System32\icacls.exe"
        & $icacls $updaterData /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-11:(RX)" /C | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo habilitar el acceso de recorrido al estado ATLAS."
        }
        & $icacls $statusDirectory /inheritance:r /C | Out-Null
        & $icacls `
            $statusDirectory `
            /grant:r `
            "*S-1-5-18:(OI)(CI)(F)" `
            "*S-1-5-32-544:(OI)(CI)(F)" `
            "*S-1-5-11:(OI)(CI)(RX)" `
            /C | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo proteger el estado publico de ATLAS."
        }

        $action = New-ScheduledTaskAction -Execute $UpdaterExecutable
        $startup = New-ScheduledTaskTrigger -AtStartup
        $logon = New-ScheduledTaskTrigger -AtLogOn
        if ($StartupMode -eq "Coordinated") {
            $startup.Delay = "PT2M"
            $logon.Delay = "PT5M"
        }
        else {
            # Compatibilidad: el updater debe instalar primero el BackupNAS
            # que conoce el protocolo de cierre coordinado.
            $startup.Delay = "PT15S"
        }
        # Stable per-device offset plus a bounded random delay avoids a fleet-wide noon spike.
        $machineBytes = [Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)
        $machineHash = [Security.Cryptography.SHA256]::Create().ComputeHash($machineBytes)
        $offsetSeconds = [BitConverter]::ToUInt16($machineHash, 0) % 900
        $periodic = New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddSeconds($offsetSeconds)) `
            -RepetitionInterval (New-TimeSpan -Minutes 15)
        $periodic.RandomDelay = "PT2M"
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -StartWhenAvailable `
            -MultipleInstances IgnoreNew `
            -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
            -RestartCount 2 `
            -RestartInterval (New-TimeSpan -Minutes 15)
        $principal = New-ScheduledTaskPrincipal `
            -UserId "SYSTEM" `
            -LogonType ServiceAccount `
            -RunLevel Highest
        Register-ScheduledTask `
            -TaskName "ATLAS Updater" `
            -Action $action `
            -Trigger @($startup, $logon, $periodic) `
            -Settings $settings `
            -Principal $principal `
            -Description "Actualiza componentes ATLAS desde releases firmados." `
            -Force | Out-Null

        $backupTask = Get-ScheduledTask `
            -TaskName "ATLAS BackupNAS" `
            -ErrorAction SilentlyContinue
        if ($null -ne $backupTask) {
            $backupDelay = if ($StartupMode -eq "Coordinated") {
                "PT5S"
            }
            else {
                "PT1M30S"
            }
            [xml]$xml = Export-ScheduledTask -TaskName "ATLAS BackupNAS"
            $namespace = $xml.DocumentElement.NamespaceURI
            $manager = [Xml.XmlNamespaceManager]::new($xml.NameTable)
            $manager.AddNamespace("t", $namespace)
            $changed = $false
            foreach ($trigger in $xml.SelectNodes(
                    "//t:Triggers/t:LogonTrigger",
                    $manager)) {
                $delay = $trigger.SelectSingleNode("t:Delay", $manager)
                if ($null -eq $delay) {
                    $delay = $xml.CreateElement("Delay", $namespace)
                    $userId = $trigger.SelectSingleNode("t:UserId", $manager)
                    if ($null -ne $userId) {
                        $trigger.InsertBefore($delay, $userId) | Out-Null
                    }
                    else {
                        $trigger.AppendChild($delay) | Out-Null
                    }
                }
                if ($delay.InnerText -ne $backupDelay) {
                    $delay.InnerText = $backupDelay
                    $changed = $true
                }
            }
            if ($changed) {
                Register-ScheduledTask `
                    -TaskName "ATLAS BackupNAS" `
                    -Xml $xml.OuterXml `
                    -Force | Out-Null
            }
        }
        Write-Output (
            "Tareas ATLAS normalizadas en modo {0}." -f $StartupMode)
        """;

    internal static string ResolveStartupMode(
        string? installedBackupVersion) =>
        VersionTagComparer.IsAtLeast(
            installedBackupVersion,
            CoordinatedBackupMinimumVersion)
                ? "Coordinated"
                : "Legacy";

    public static async Task EnsureAsync(
        UpdaterPaths paths,
        UpdateLog log,
        CancellationToken cancellationToken)
    {
        string revisionFile = Path.Combine(paths.RootDirectory, "task-schedule-v2.json");
        var revision = DurableFile.Read<DateTimeOffset?>(revisionFile);
        if (revision is not null && revision > DateTimeOffset.UtcNow.AddDays(-1)) return;
        string startupMode = ResolveStartupMode(
            VersionTagComparer.ReadProductVersion(paths.BackupExecutable));
        string scriptPath = Path.Combine(
            paths.StagingDirectory,
            "repair-atlas-tasks.ps1");
        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                Script,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            int result = await ProcessRunner.RunAsync(
                powershell,
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                    "-UpdaterExecutable",
                    paths.UpdaterExecutable,
                    "-StartupMode",
                    startupMode
                ],
                TimeSpan.FromMinutes(2),
                log,
                cancellationToken);
            if (result != 0)
            {
                log.Write(
                    $"ADVERTENCIA: no se pudieron normalizar las tareas ATLAS ({result}).");
            }
            else
            {
                DurableFile.Write(revisionFile, DateTimeOffset.UtcNow);
                log.Write(
                    startupMode == "Coordinated"
                        ? "Comprobación coordinada cada 15 minutos, con desfase por equipo."
                        : "Modo de transicion: updater prioritario; BackupNAS conserva +90 s.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Write(
                $"ADVERTENCIA: no se pudieron normalizar las tareas ATLAS: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
