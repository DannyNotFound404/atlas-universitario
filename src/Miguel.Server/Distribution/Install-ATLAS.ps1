[CmdletBinding()]
param(
    [string]$ConfigurationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding =
    [Text.UTF8Encoding]::new($false)
$icaclsExe = Join-Path $env:SystemRoot "System32\icacls.exe"

trap {
    # Setup.exe reconoce este prefijo y muestra la causa real al usuario en
    # lugar de limitarse a informar un codigo de salida generico.
    [Console]::Error.WriteLine("ATLAS_ERROR:$($_.Exception.Message)")
    exit 1
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Abra PowerShell como administrador y ejecute nuevamente."
    }
}

function Protect-AdminSystemDirectory {
    param([Parameter(Mandatory)][string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null

    # La concesion inicial permite reparar ACL incompletas de una ejecucion
    # interrumpida. Despues se elimina la herencia de ProgramData.
    & $icaclsExe `
        $Path `
        /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" `
        "*S-1-5-32-544:(OI)(CI)(F)" `
        /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo recuperar el control de $Path"
    }
    & $icaclsExe $Path /inheritance:r /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo eliminar la herencia de permisos en $Path"
    }
    & $icaclsExe `
        $Path `
        /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" `
        "*S-1-5-32-544:(OI)(CI)(F)" `
        /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron aplicar permisos seguros a $Path"
    }

    & $icaclsExe (Join-Path $Path "*") /reset /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron propagar permisos seguros en $Path"
    }
}

function Protect-UpdaterStatusDirectory {
    param(
        [Parameter(Mandatory)][string]$UpdaterDirectory,
        [Parameter(Mandatory)][string]$StatusDirectory
    )

    New-Item -ItemType Directory -Path $StatusDirectory -Force | Out-Null

    # El usuario interactivo necesita atravesar la raiz, pero no obtiene
    # permisos heredables sobre Logs, Staging, state.json ni la identidad.
    & $icaclsExe `
        $UpdaterDirectory `
        /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" `
        "*S-1-5-32-544:(OI)(CI)(F)" `
        "*S-1-5-11:(RX)" `
        /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo habilitar el acceso controlado al estado de ATLAS."
    }

    & $icaclsExe $StatusDirectory /inheritance:r /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo proteger la carpeta de estado de ATLAS."
    }
    & $icaclsExe `
        $StatusDirectory `
        /grant:r `
        "*S-1-5-18:(OI)(CI)(F)" `
        "*S-1-5-32-544:(OI)(CI)(F)" `
        "*S-1-5-11:(OI)(CI)(RX)" `
        /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo publicar el estado de actualizacion de ATLAS."
    }
    & $icaclsExe (Join-Path $StatusDirectory "*") /reset /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron propagar los permisos del estado de ATLAS."
    }
}

function New-DeviceSecret {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return [Convert]::ToBase64String($bytes).
        TrimEnd('=').
        Replace('+', '-').
        Replace('/', '_')
}

function Get-AtlasHttpStatusCode {
    param([Parameter(Mandatory)][Management.Automation.ErrorRecord]$Record)

    $response = $Record.Exception.Response
    if ($null -eq $response) {
        return $null
    }

    try {
        return [int]$response.StatusCode
    }
    catch {
        return $null
    }
}

function Assert-AtlasFreeSpace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$RequiredBytes,
        [Parameter(Mandatory)][string]$Operation
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "No se pudo determinar la unidad para $Operation."
    }

    $drive = [IO.DriveInfo]::new($root)
    if (-not $drive.IsReady) {
        throw "La unidad $root no esta disponible para $Operation."
    }
    if ($drive.AvailableFreeSpace -lt $RequiredBytes) {
        $requiredMiB = [Math]::Ceiling($RequiredBytes / 1MB)
        $availableMiB = [Math]::Floor($drive.AvailableFreeSpace / 1MB)
        throw (
            "Espacio insuficiente para $Operation. Se requieren al menos " +
            "$requiredMiB MiB y solo hay $availableMiB MiB disponibles en " +
            "$root. No se modifico la instalacion.")
    }
}

function Test-ExistingAtlasIdentity {
    param(
        [Parameter(Mandatory)][object]$Identity,
        [Parameter(Mandatory)][Uri]$ExpectedServerUrl
    )

    [Uri]$existingServerUrl = $null
    if (-not [Uri]::TryCreate(
            [string]$Identity.serverUrl,
            [UriKind]::Absolute,
            [ref]$existingServerUrl) -or
        [string]::IsNullOrWhiteSpace([string]$Identity.deviceId) -or
        [string]::IsNullOrWhiteSpace([string]$Identity.deviceKey)) {
        return "Orphaned"
    }

    if ($existingServerUrl.AbsoluteUri -ne $ExpectedServerUrl.AbsoluteUri) {
        throw (
            "Se detecto una identidad ATLAS perteneciente a otro servidor " +
            "($($existingServerUrl.AbsoluteUri)). No se modifico nada.")
    }

    $headers = @{
        "X-Miguel-Device-Id" = [string]$Identity.deviceId
        "Authorization" = "Miguel $([string]$Identity.deviceKey)"
    }
    try {
        Invoke-WebRequest `
            -Method Get `
            -Uri ([Uri]::new(
                $ExpectedServerUrl,
                "/api/v1/agent/releases")) `
            -Headers $headers `
            -UseBasicParsing `
            -TimeoutSec 20 | Out-Null
        return "Active"
    }
    catch {
        $statusCode = Get-AtlasHttpStatusCode -Record $_
        if ($statusCode -in @(401, 403)) {
            return "Revoked"
        }
        if ($null -ne $statusCode) {
            throw (
                "El servidor ATLAS respondio HTTP $statusCode al validar la " +
                "instalacion anterior. Por seguridad no se modifico nada.")
        }
        throw (
            "No fue posible validar la instalacion anterior con el servidor " +
            "ATLAS. Revise la red y vuelva a intentarlo; no se modifico nada.")
    }
}

function Test-AtlasComponentsPresent {
    param([Parameter(Mandatory)][string]$IdentityPath)

    if (Test-Path -LiteralPath $IdentityPath -PathType Leaf) {
        return $true
    }
    if ($null -ne (Get-Service -Name "MIGUEL Agent" -ErrorAction SilentlyContinue) -or
        $null -ne (Get-Service -Name "ATLAS Backup Service" -ErrorAction SilentlyContinue)) {
        return $true
    }
    foreach ($taskName in @("ATLAS BackupNAS", "ATLAS Updater")) {
        if ($null -ne (Get-ScheduledTask `
                -TaskName $taskName `
                -ErrorAction SilentlyContinue)) {
            return $true
        }
    }

    foreach ($path in @(
            (Join-Path $env:ProgramFiles "MIGUEL\Agent"),
            (Join-Path $env:ProgramFiles "ATLAS\BackupNAS"),
            (Join-Path $env:ProgramFiles "ATLAS\BackupService"),
            (Join-Path $env:ProgramFiles "ATLAS\Updater"),
            (Join-Path $env:ProgramData "MIGUEL"),
            (Join-Path $env:ProgramData "ATLAS\BackupInbox"),
            (Join-Path $env:ProgramData "ATLAS\Updater"))) {
        if (Test-Path -LiteralPath $path) {
            return $true
        }
    }
    return $false
}

function Backup-AndRemovePreviousAtlas {
    param(
        [object]$Identity,
        [Parameter(Mandatory)][string]$Reason
    )

    $archiveRoot = Join-Path $env:ProgramData "ATLAS-Archive"
    $archiveName = "{0}-{1}-preinstall" -f `
        $env:COMPUTERNAME,
        [DateTimeOffset]::Now.ToString("yyyyMMdd-HHmmss")
    $archiveDirectory = Join-Path $archiveRoot $archiveName
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null

    $backupConfiguration = Join-Path `
        $env:ProgramFiles `
        "ATLAS\BackupNAS\config.txt"
    $preservedConfiguration = $null
    if (Test-Path -LiteralPath $backupConfiguration -PathType Leaf) {
        $preservedConfiguration = Join-Path `
            $archiveDirectory `
            "BackupNAS-config.txt"
        Copy-Item `
            -LiteralPath $backupConfiguration `
            -Destination $preservedConfiguration `
            -Force
    }

    if ($null -ne $Identity) {
        [ordered]@{
            serverUrl = [string]$Identity.serverUrl
            deviceId = [string]$Identity.deviceId
            agencyCode = [string]$Identity.agencyCode
            enrolledAtUtc = [string]$Identity.enrolledAtUtc
        } |
            ConvertTo-Json |
            Set-Content `
                -LiteralPath (Join-Path $archiveDirectory "identity-redacted.json") `
                -Encoding UTF8
    }

    foreach ($taskName in @("ATLAS BackupNAS", "ATLAS Updater")) {
        $task = Get-ScheduledTask `
            -TaskName $taskName `
            -ErrorAction SilentlyContinue
        if ($null -eq $task) {
            continue
        }
        try {
            Export-ScheduledTask -TaskName $taskName |
                Set-Content `
                    -LiteralPath (Join-Path `
                        $archiveDirectory `
                        ($taskName.Replace(' ', '-') + ".xml")) `
                    -Encoding Unicode
        }
        catch {
            Write-Warning "No se pudo archivar la tarea $taskName."
        }
        Stop-ScheduledTask `
            -TaskName $taskName `
            -ErrorAction SilentlyContinue
    }

    $service = Get-Service -Name "MIGUEL Agent" -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        [ordered]@{
            name = $service.Name
            displayName = $service.DisplayName
            status = $service.Status.ToString()
            startType = $service.StartType.ToString()
        } |
            ConvertTo-Json |
            Set-Content `
                -LiteralPath (Join-Path $archiveDirectory "service.json") `
                -Encoding UTF8
        if ($service.Status -ne "Stopped") {
            Stop-Service -Name "MIGUEL Agent" -Force
            $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }
    }

    $backupService = Get-Service `
        -Name "ATLAS Backup Service" `
        -ErrorAction SilentlyContinue
    if ($null -ne $backupService -and $backupService.Status -ne "Stopped") {
        Stop-Service -Name "ATLAS Backup Service" -Force
        $backupService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    $managedRoots = @(
        (Join-Path $env:ProgramFiles "MIGUEL\Agent"),
        (Join-Path $env:ProgramFiles "ATLAS\BackupNAS"),
        (Join-Path $env:ProgramFiles "ATLAS\BackupService"),
        (Join-Path $env:ProgramFiles "ATLAS\Updater"))
    foreach ($processName in @(
            "Miguel.Agent",
            "BackupNAS",
            "ATLAS.Backup.Service",
            "ATLAS.Updater")) {
        foreach ($process in @(Get-Process `
                -Name $processName `
                -ErrorAction SilentlyContinue)) {
            try {
                $processPath = $process.Path
                if ($managedRoots | Where-Object {
                        $processPath.StartsWith(
                            $_ + "\",
                            [StringComparison]::OrdinalIgnoreCase)
                    }) {
                    if ($processName -eq "BackupNAS") {
                        if (-not $process.CloseMainWindow()) {
                            throw (
                                "Cierre BackupNAS antes de migrar ATLAS; " +
                                "no se interrumpira una copia en curso.")
                        }
                        if (-not $process.WaitForExit(15000)) {
                            throw (
                                "BackupNAS continua trabajando. Espere a que " +
                                "termine la copia, cierre la ventana y vuelva " +
                                "a ejecutar el instalador.")
                        }
                    }
                    else {
                        Stop-Process -Id $process.Id -Force
                    }
                }
            }
            catch {
                if ($processName -eq "BackupNAS") {
                    throw
                }
                Write-Warning "No se pudo detener $processName ($($process.Id))."
            }
        }
    }

    foreach ($taskName in @("ATLAS BackupNAS", "ATLAS Updater")) {
        if ($null -ne (Get-ScheduledTask `
                -TaskName $taskName `
                -ErrorAction SilentlyContinue)) {
            Unregister-ScheduledTask `
                -TaskName $taskName `
                -Confirm:$false
        }
    }
    if ($null -ne $service) {
        & (Join-Path $env:SystemRoot "System32\sc.exe") `
            delete `
            "MIGUEL Agent" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "No fue posible retirar el servicio ATLAS anterior."
        }
        $service.Dispose()
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 250
            $remainingService = Get-Service `
                -Name "MIGUEL Agent" `
                -ErrorAction SilentlyContinue
        } while ($null -ne $remainingService -and
            [DateTimeOffset]::UtcNow -lt $deadline)
        if ($null -ne $remainingService) {
            throw (
                "Windows no termino de retirar el servicio ATLAS anterior. " +
                "Reinicie el equipo y ejecute nuevamente este instalador.")
        }
    }
    if ($null -ne $backupService) {
        & (Join-Path $env:SystemRoot "System32\sc.exe") `
            delete `
            "ATLAS Backup Service" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "No fue posible retirar ATLAS Backup Service anterior."
        }
        $backupService.Dispose()
    }

    foreach ($path in @(
            (Join-Path $env:ProgramFiles "MIGUEL\Agent"),
            (Join-Path $env:ProgramFiles "ATLAS\BackupNAS"),
            (Join-Path $env:ProgramFiles "ATLAS\BackupService"),
            (Join-Path $env:ProgramFiles "ATLAS\Updater"),
            (Join-Path $env:ProgramData "MIGUEL"),
            (Join-Path $env:ProgramData "ATLAS\BackupInbox"),
            (Join-Path $env:ProgramData "ATLAS\Setup"),
            (Join-Path $env:ProgramData "ATLAS\Updater"),
            (Join-Path $env:ProgramData "ATLAS\Trust"))) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    [ordered]@{
        schemaVersion = 1
        computerName = $env:COMPUTERNAME
        migratedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = $Reason
        preservedBackupConfiguration = $null -ne $preservedConfiguration
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $archiveDirectory "migration.json") `
            -Encoding UTF8
    Protect-AdminSystemDirectory -Path $archiveDirectory

    Write-Host "La instalacion anterior fue archivada y retirada de forma segura."
    Write-Host "Evidencia: $archiveDirectory"
    return $preservedConfiguration
}

function Get-ComponentExtension {
    param([Parameter(Mandatory)][string]$Component)

    switch ($Component.ToLowerInvariant()) {
        "agent" { return ".zip" }
        "backupnas" { return ".exe" }
        "backup-service" { return ".zip" }
        "updater" { return ".exe" }
        default { throw "Componente ATLAS no reconocido: $Component" }
    }
}

function Write-BackupConfiguration {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [object]$SetupConfiguration,
        [string]$PreservedConfigurationPath
    )

    $configurationPath = Join-Path $InstallDirectory "config.txt"
    if (Test-Path -LiteralPath $configurationPath -PathType Leaf) {
        Write-Host "Se conserva la configuración existente de BackupNAS."
        return
    }

    if ($null -eq $SetupConfiguration -and
        -not [string]::IsNullOrWhiteSpace($PreservedConfigurationPath) -and
        (Test-Path `
            -LiteralPath $PreservedConfigurationPath `
            -PathType Leaf)) {
        Copy-Item `
            -LiteralPath $PreservedConfigurationPath `
            -Destination $configurationPath `
            -Force
        Write-Host "Se restauro la configuracion anterior de BackupNAS."
        return
    }

    if ($null -ne $SetupConfiguration) {
        $nasAddress = ([string]$SetupConfiguration.nasAddress).Trim()
        $nasShare = ([string]$SetupConfiguration.nasShare).Trim()
        $folders = @(
            $SetupConfiguration.folders |
                ForEach-Object { ([string]$_).Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )
    }
    else {
        do {
            $nasAddress = (Read-Host "IPv4 o nombre del NAS").Trim()
        } while ([string]::IsNullOrWhiteSpace($nasAddress))

        do {
            $nasShare = (
                Read-Host "Carpeta compartida asignada al usuario").Trim()
        } while (
            [string]::IsNullOrWhiteSpace($nasShare) -or
            $nasShare.Contains("\") -or
            $nasShare.Contains("/"))

        $folderInput = Read-Host `
            "Carpetas separadas por coma [Documents,Desktop]"
        if ([string]::IsNullOrWhiteSpace($folderInput)) {
            $folderInput = "Documents,Desktop"
        }
        $folders = @(
            $folderInput.Split(',') |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )
    }

    if ([string]::IsNullOrWhiteSpace($nasAddress)) {
        throw "La dirección del NAS no puede estar vacía."
    }
    if ([string]::IsNullOrWhiteSpace($nasShare) -or
        $nasShare.Contains("\") -or
        $nasShare.Contains("/")) {
        throw "El recurso compartido del NAS no es válido."
    }
    if ($folders.Count -eq 0) {
        throw "Debe configurar al menos una carpeta para BackupNAS."
    }

    $lines = @(
        "# Configuración administrada por ATLAS"
        "# Las credenciales SMB nunca se guardan en este archivo."
        "NAS_IP=$nasAddress"
        "NAS_SHARE=$nasShare"
        "EXCLUIR_OCULTOS=true"
        "EXCLUIR_TEMPORALES=true"
        ""
    )
    for ($index = 0; $index -lt $folders.Count; $index++) {
        $lines += "CARPETA$($index + 1)=$($folders[$index])"
    }
    $lines | Set-Content -LiteralPath $configurationPath -Encoding UTF8
}

function Register-BackupTask {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$TargetUser
    )

    $action = New-ScheduledTaskAction -Execute $Executable
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $TargetUser
    # BackupNAS tiene prioridad al iniciar la sesión. El actualizador espera
    # dos horas y coordina cualquier cierre futuro mediante su marcador.
    $trigger.Delay = "PT5S"
    $principal = New-ScheduledTaskPrincipal `
        -UserId $TargetUser `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Hours 8) `
        -MultipleInstances IgnoreNew
    $task = New-ScheduledTask `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings
    Register-ScheduledTask `
        -TaskName "ATLAS BackupNAS" `
        -InputObject $task `
        -Force | Out-Null
}

function Write-ComponentPolicy {
    param([Parameter(Mandatory)][bool]$BackupNasEnabled)

    $configurationDirectory = Join-Path $env:ProgramData "MIGUEL\Config"
    New-Item `
        -ItemType Directory `
        -Path $configurationDirectory `
        -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        backupNasEnabled = $BackupNasEnabled
        backupProvider = if ($BackupNasEnabled) { "ATLAS" } else { "Acronis" }
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $configurationDirectory "components.json") `
            -Encoding UTF8
}

function Remove-RetiredAtlasBackupService {
    $serviceName = "ATLAS Backup Service"
    $serviceDirectory = Join-Path $env:ProgramFiles "ATLAS\BackupService"
    $scExe = Join-Path $env:SystemRoot "System32\sc.exe"

    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        if ($existing.Status -ne "Stopped") {
            Stop-Service -Name $serviceName -Force
            $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(45))
        }
        & $scExe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "No fue posible retirar el antiguo servicio VSS de ATLAS."
        }
        $existing.Dispose()
    }
    if (Test-Path -LiteralPath $serviceDirectory) {
        Remove-Item -LiteralPath $serviceDirectory -Recurse -Force
    }
}

function Register-UpdaterTask {
    param([Parameter(Mandatory)][string]$Executable)

    $action = New-ScheduledTaskAction -Execute $Executable
    $startup = New-ScheduledTaskTrigger -AtStartup
    $startup.Delay = "PT2H"
    $logon = New-ScheduledTaskTrigger -AtLogOn
    $logon.Delay = "PT2H"
    $daily = New-ScheduledTaskTrigger -Daily -At "12:00"
    $principal = New-ScheduledTaskPrincipal `
        -UserId "SYSTEM" `
        -LogonType ServiceAccount `
        -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -RunOnlyIfNetworkAvailable `
        -RestartCount 6 `
        -RestartInterval (New-TimeSpan -Minutes 5) `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
        -MultipleInstances IgnoreNew
    $task = New-ScheduledTask `
        -Action $action `
        -Trigger @($startup, $logon, $daily) `
        -Principal $principal `
        -Settings $settings
    Register-ScheduledTask `
        -TaskName "ATLAS Updater" `
        -InputObject $task `
        -Force | Out-Null
}

function Get-AtlasProductVersion {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
}

function Assert-AtlasInstallation {
    param(
        [Parameter(Mandatory)][object]$Identity,
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][bool]$InstallBackupNas,
        [Parameter(Mandatory)][string]$UpdaterExecutable,
        [string]$BackupExecutable,
        [string]$BackupTargetUser
    )

    $agentExecutable = Join-Path `
        $env:ProgramFiles `
        "MIGUEL\Agent\Miguel.Agent.exe"
    $agentService = Get-Service `
        -Name "MIGUEL Agent" `
        -ErrorAction SilentlyContinue
    if ($null -eq $agentService -or $agentService.Status -ne "Running") {
        throw "La comprobacion final no encontro ATLAS Agent en ejecucion."
    }

    $expectedVersions = @{}
    foreach ($package in $Release.packages) {
        $expectedVersions[[string]$package.component] = [string]$package.version
    }
    $installedVersions = [ordered]@{
        agent = Get-AtlasProductVersion -Path $agentExecutable
        updater = Get-AtlasProductVersion -Path $UpdaterExecutable
    }
    if ($InstallBackupNas) {
        $installedVersions.backupnas = Get-AtlasProductVersion `
            -Path $BackupExecutable
    }
    foreach ($component in @($installedVersions.Keys)) {
        if ([string]::IsNullOrWhiteSpace([string]$installedVersions[$component])) {
            throw "La comprobacion final no encontro $component instalado."
        }
        if ($expectedVersions.ContainsKey($component) -and
            [string]$installedVersions[$component] -ne
                [string]$expectedVersions[$component]) {
            throw (
                "$component reporta $($installedVersions[$component]), " +
                "pero el release exige $($expectedVersions[$component]).")
        }
    }

    $updaterTask = Get-ScheduledTask `
        -TaskName "ATLAS Updater" `
        -ErrorAction SilentlyContinue
    if ($null -eq $updaterTask -or
        [string]$updaterTask.Principal.UserId -ne "SYSTEM" -or
        -not (@($updaterTask.Actions) | Where-Object {
            [string]$_.Execute -eq $UpdaterExecutable
        })) {
        throw "La tarea ATLAS Updater no quedo registrada para SYSTEM."
    }
    $updaterTriggers = @($updaterTask.Triggers)
    $updaterBoot = @($updaterTriggers | Where-Object {
        $_.CimClass.CimClassName -eq "MSFT_TaskBootTrigger" -and
        [string]$_.Delay -eq "PT2H"
    })
    $updaterLogon = @($updaterTriggers | Where-Object {
        $_.CimClass.CimClassName -eq "MSFT_TaskLogonTrigger" -and
        [string]$_.Delay -eq "PT2H"
    })
    $updaterDaily = @($updaterTriggers | Where-Object {
        $_.CimClass.CimClassName -eq "MSFT_TaskDailyTrigger"
    })
    if ($updaterBoot.Count -ne 1 -or
        $updaterLogon.Count -ne 1 -or
        $updaterDaily.Count -ne 1) {
        throw "Los desencadenadores de ATLAS Updater no coinciden con el estandar."
    }

    $backupTask = Get-ScheduledTask `
        -TaskName "ATLAS BackupNAS" `
        -ErrorAction SilentlyContinue
    if ($InstallBackupNas) {
        if ($null -eq $backupTask -or
            -not (@($backupTask.Actions) | Where-Object {
                [string]$_.Execute -eq $BackupExecutable
            }) -or
            -not (@($backupTask.Triggers) | Where-Object {
                $_.CimClass.CimClassName -eq "MSFT_TaskLogonTrigger" -and
                [string]$_.Delay -eq "PT5S"
            })) {
            throw "La tarea ATLAS BackupNAS no cumple el inicio a los 5 segundos."
        }
    }
    elseif ($null -ne $backupTask) {
        throw "Se encontro una tarea BackupNAS en un endpoint configurado para Acronis."
    }

    if ($null -ne (Get-Service `
            -Name "ATLAS Backup Service" `
            -ErrorAction SilentlyContinue)) {
        throw "El antiguo servicio VSS continua registrado."
    }

    $policyPath = Join-Path `
        $env:ProgramData `
        "MIGUEL\Config\components.json"
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    if ([bool]$policy.backupNasEnabled -ne $InstallBackupNas) {
        throw "La politica protegida de componentes no coincide con la seleccion."
    }

    $releaseVersionProperty = $Release.PSObject.Properties["releaseVersion"]
    $releaseVersion = if ($null -ne $releaseVersionProperty -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$releaseVersionProperty.Value)) {
        [string]$releaseVersionProperty.Value
    }
    else {
        $publishedAtProperty = $Release.PSObject.Properties["publishedAtUtc"]
        if ($null -ne $publishedAtProperty -and
            -not [string]::IsNullOrWhiteSpace(
                [string]$publishedAtProperty.Value)) {
            "catalog-$([string]$publishedAtProperty.Value)"
        }
        else {
            "catalogo-sin-version"
        }
    }

    $receiptDirectory = Join-Path $env:ProgramData "ATLAS\Setup"
    New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        result = "success"
        installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        serverUrl = [string]$Identity.serverUrl
        deviceId = [string]$Identity.deviceId
        agencyCode = [string]$Identity.agencyCode
        machineName = $env:COMPUTERNAME
        backupNasEnabled = $InstallBackupNas
        backupTargetUser = $BackupTargetUser
        versions = $installedVersions
        releaseVersion = $releaseVersion
    } |
        ConvertTo-Json -Depth 5 |
        Set-Content `
            -LiteralPath (Join-Path $receiptDirectory "installation-receipt.json") `
            -Encoding UTF8
    Protect-AdminSystemDirectory -Path $receiptDirectory
}

Assert-Administrator

$sourceDirectory = $PSScriptRoot
$bootstrapPath = Join-Path $sourceDirectory "atlas-bootstrap.json"
$certificatePath = Join-Path $sourceDirectory "atlas-root-ca.crt"
$releasePublicKeyPath = Join-Path `
    $sourceDirectory `
    "atlas-release-signing.public.pem"
if (-not (Test-Path -LiteralPath $bootstrapPath -PathType Leaf)) {
    throw "El instalador no contiene atlas-bootstrap.json."
}
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "El instalador no contiene el certificado público de ATLAS."
}
if (-not (Test-Path -LiteralPath $releasePublicKeyPath -PathType Leaf)) {
    throw "El instalador no contiene la autoridad pública de releases ATLAS."
}

$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw |
    ConvertFrom-Json
if ($bootstrap.product -ne "ATLAS" -or $bootstrap.schemaVersion -ne 1) {
    throw "El vínculo de instalación no pertenece a ATLAS."
}
$installerId = [Guid]::Empty
if (-not [Guid]::TryParse(
        [string]$bootstrap.installerId,
        [ref]$installerId) -or
    $installerId -eq [Guid]::Empty) {
    throw "El identificador del instalador ATLAS no es válido."
}
if ([DateTimeOffset]::Parse($bootstrap.expiresAtUtc) -le
    [DateTimeOffset]::UtcNow) {
    throw "Este instalador de un solo uso ya expiró."
}

$setupConfiguration = $null
if (-not [string]::IsNullOrWhiteSpace($ConfigurationPath)) {
    $resolvedConfigurationPath = [IO.Path]::GetFullPath(
        $ConfigurationPath)
    if (-not (Test-Path `
            -LiteralPath $resolvedConfigurationPath `
            -PathType Leaf)) {
        throw "No se encontró la configuración inicial de ATLAS."
    }
    $setupConfiguration = Get-Content `
        -LiteralPath $resolvedConfigurationPath `
        -Raw |
        ConvertFrom-Json
}
$installBackupNas = $true
if ($null -ne $setupConfiguration -and
    $null -ne $setupConfiguration.PSObject.Properties["installBackupNas"]) {
    $installBackupNas = [bool]$setupConfiguration.installBackupNas
}

# Esta comprobacion ocurre antes de importar confianza o consumir el token.
# La comprobacion exacta se repite cuando el servidor entrega el catalogo.
Assert-AtlasFreeSpace `
    -Path $env:TEMP `
    -RequiredBytes 1GB `
    -Operation "la instalacion Golden de ATLAS"

Write-Host "Instalando la confianza del servidor ATLAS..."
Import-Certificate `
    -FilePath $certificatePath `
    -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

Write-Host "Instalando la autoridad de releases ATLAS..."
$trustDirectory = Join-Path $env:ProgramData "ATLAS\Trust"
$installedReleaseKey = Join-Path `
    $trustDirectory `
    "release-signing.public.pem"
New-Item -ItemType Directory -Path $trustDirectory -Force | Out-Null
Copy-Item `
    -LiteralPath $releasePublicKeyPath `
    -Destination $installedReleaseKey `
    -Force
Protect-AdminSystemDirectory -Path $trustDirectory

$serverUrl = [Uri]$bootstrap.serverUrl
$programData = Join-Path $env:ProgramData "MIGUEL"
$identityPath = Join-Path $programData "Config\agent-identity.json"
$setupDirectory = Join-Path $env:ProgramData "ATLAS\Setup"
$resumePath = Join-Path $setupDirectory "installer-resume.json"
$preservedBackupConfiguration = $null
$resumeMatches = $false
$existingIdentity = $null

if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
    try {
        $existingIdentity = Get-Content -LiteralPath $identityPath -Raw |
            ConvertFrom-Json
    }
    catch {
        Write-Warning (
            "La identidad local anterior esta incompleta y se tratara como " +
            "una instalacion huerfana.")
    }

    if ($null -ne $existingIdentity -and
        (Test-Path -LiteralPath $resumePath -PathType Leaf)) {
        try {
            $existingResume = Get-Content -LiteralPath $resumePath -Raw |
                ConvertFrom-Json
            $resumeMatches =
                [string]$existingResume.installerId -eq `
                    $installerId.ToString() -and
                [string]$existingResume.serverUrl -eq $serverUrl.AbsoluteUri
        }
        catch {
            $resumeMatches = $false
        }
    }
}

if (-not $resumeMatches -and
    (Test-AtlasComponentsPresent -IdentityPath $identityPath)) {
    if ($null -eq $existingIdentity) {
        Write-Host (
            "Se detecto una instalacion ATLAS huerfana. El instalador " +
            "firmado archivara su evidencia y reparara el endpoint.")
        $preservedBackupConfiguration = Backup-AndRemovePreviousAtlas `
            -Identity $null `
            -Reason "Instalacion huerfana o incompleta reparada"
    }
    else {
        Write-Host "Validando la vinculacion ATLAS anterior..."
        $identityState = Test-ExistingAtlasIdentity `
            -Identity $existingIdentity `
            -ExpectedServerUrl $serverUrl
        if ($identityState -eq "Active") {
            throw (
                "Este equipo conserva una vinculacion ATLAS activa " +
                "(DeviceId: $([string]$existingIdentity.deviceId)). " +
                "Retire primero el activo desde el portal ATLAS y vuelva " +
                "a ejecutar este instalador. No se reemplazaron la identidad " +
                "ni los componentes existentes.")
        }

        Write-Host "Preparando la migracion controlada de ATLAS..."
        $preservedBackupConfiguration = Backup-AndRemovePreviousAtlas `
            -Identity $existingIdentity `
            -Reason $(if ($identityState -eq "Orphaned") {
                "Identidad local incompleta reparada"
            }
            else {
                "Identidad retirada o revocada por el servidor"
            })
    }

    # La migracion retira las autoridades locales anteriores. Se restauran
    # exclusivamente desde este instalador verificado antes de inscribir.
    New-Item -ItemType Directory -Path $trustDirectory -Force | Out-Null
    Copy-Item `
        -LiteralPath $releasePublicKeyPath `
        -Destination $installedReleaseKey `
        -Force
    Protect-AdminSystemDirectory -Path $trustDirectory
}
Protect-AdminSystemDirectory -Path $setupDirectory
if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
    if (-not (Test-Path -LiteralPath $resumePath -PathType Leaf)) {
        throw (
            "Este equipo ya está vinculado a ATLAS. " +
            "Use el actualizador; no ejecute otro instalador de inscripción.")
    }
    $resume = Get-Content -LiteralPath $resumePath -Raw |
        ConvertFrom-Json
    if ([string]$resume.installerId -ne $installerId.ToString()) {
        throw (
            "La instalación pendiente pertenece a otro instalador ATLAS. " +
            "No se reemplazó la identidad existente.")
    }
    if ([string]$resume.serverUrl -ne $serverUrl.AbsoluteUri) {
        throw "El servidor de la instalación pendiente no coincide."
    }
    Write-Host "Reanudando la instalación ATLAS en este equipo."
    $identity = Get-Content -LiteralPath $identityPath -Raw |
        ConvertFrom-Json
}
else {
    Write-Host "Vinculando este equipo con ATLAS..."
    $installationId = [Guid]::NewGuid()
    $deviceKey = New-DeviceSecret
    $enrollmentBody = @{
        schemaVersion = 1
        installationId = $installationId
        enrollmentToken = [string]$bootstrap.enrollmentToken
        deviceKey = $deviceKey
        machineName = $env:COMPUTERNAME
        agentVersion = "bootstrap"
        backupClientVersion = $null
    } | ConvertTo-Json
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri ([Uri]::new($serverUrl, "/api/v1/enrollment")) `
        -ContentType "application/json" `
        -Body $enrollmentBody

    $identity = [ordered]@{
        schemaVersion = 1
        serverUrl = $serverUrl.AbsoluteUri
        deviceId = [string]$response.deviceId
        deviceKey = $deviceKey
        agencyCode = [string]$response.agencyCode
        enrolledAtUtc = [string]$response.enrolledAtUtc
    }
    $identityDirectory = Split-Path -Parent $identityPath
    New-Item -ItemType Directory -Path $identityDirectory -Force |
        Out-Null
    $identity |
        ConvertTo-Json |
        Set-Content -LiteralPath $identityPath -Encoding UTF8

    New-Item -ItemType Directory -Path $setupDirectory -Force |
        Out-Null
    [ordered]@{
        installerId = $installerId.ToString()
        serverUrl = $serverUrl.AbsoluteUri
        agencyCode = [string]$response.agencyCode
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $resumePath -Encoding UTF8
}

# La clave del endpoint nunca queda expuesta a usuarios estandar durante las
# descargas. Install-MiguelAgent agregara despues solamente el SID del servicio.
Protect-AdminSystemDirectory -Path $programData

$headers = @{
    "X-Miguel-Device-Id" = [string]$identity.deviceId
    "Authorization" = "Miguel $([string]$identity.deviceKey)"
}
$release = Invoke-RestMethod `
    -Method Get `
    -Uri ([Uri]::new($serverUrl, "/api/v1/agent/releases")) `
    -Headers $headers

$selectedPackageBytes = [long]0
foreach ($package in $release.packages) {
    $component = [string]$package.component
    if ($component -eq "backup-service") {
        continue
    }
    if (-not $installBackupNas -and $component -eq "backupnas") {
        continue
    }
    $selectedPackageBytes += [long]$package.sizeBytes
}
$requiredInstallBytes = [Math]::Max(
    1GB,
    ($selectedPackageBytes * 3) + 512MB)
Assert-AtlasFreeSpace `
    -Path $env:TEMP `
    -RequiredBytes $requiredInstallBytes `
    -Operation "descargar, verificar e instalar los componentes ATLAS"

$staging = Join-Path $env:TEMP ("ATLAS-Setup-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    $downloads = @{}
    foreach ($package in $release.packages) {
        if ([string]$package.component -eq "backup-service") {
            Write-Host (
                "backup-service omitido: VSS fue retirado de la linea " +
                "estable de ATLAS.")
            continue
        }
        $isBackupComponent = [string]$package.component -in @(
            "backupnas")
        if (-not $installBackupNas -and $isBackupComponent) {
            Write-Host "$($package.component) omitido por seleccion del administrador."
            continue
        }
        $extension = Get-ComponentExtension $package.component
        $destination = Join-Path $staging (
            $package.component + "-" + $package.version + $extension)
        Write-Host "Descargando $($package.component) $($package.version)..."
        Invoke-WebRequest `
            -Uri ([Uri]::new($serverUrl, $package.downloadUrl)) `
            -Headers $headers `
            -OutFile $destination `
            -UseBasicParsing
        $actualHash = (Get-FileHash `
            -LiteralPath $destination `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne ([string]$package.sha256).ToLowerInvariant()) {
            throw "Falló la integridad del componente $($package.component)."
        }
        $downloads[[string]$package.component] = $destination
    }

    $requiredComponents = @("agent", "updater")
    if ($installBackupNas) {
        $requiredComponents += @("backupnas")
    }
    foreach ($required in $requiredComponents) {
        if (-not $downloads.ContainsKey($required)) {
            throw "El servidor no publicó el componente obligatorio: $required."
        }
    }

    Write-Host "Instalando el agente ATLAS..."
    $agentPackage = Join-Path $staging "AgentPackage"
    Expand-Archive `
        -LiteralPath $downloads["agent"] `
        -DestinationPath $agentPackage `
        -Force
    & (Join-Path $agentPackage "Install-MiguelAgent.ps1") `
        -SourceDirectory (Join-Path $agentPackage "agent")

    Write-ComponentPolicy -BackupNasEnabled $installBackupNas
    Remove-RetiredAtlasBackupService
    $targetUser = $null
    if ($installBackupNas) {
        Write-Host "Instalando BackupNAS..."
        $backupDirectory = Join-Path $env:ProgramFiles "ATLAS\BackupNAS"
        New-Item -ItemType Directory -Path $backupDirectory -Force |
            Out-Null
        $backupExecutable = Join-Path $backupDirectory "BackupNAS.exe"
        Copy-Item `
            -LiteralPath $downloads["backupnas"] `
            -Destination $backupExecutable `
            -Force
        Write-BackupConfiguration `
            -InstallDirectory $backupDirectory `
            -SetupConfiguration $setupConfiguration `
            -PreservedConfigurationPath $preservedBackupConfiguration

        if ($null -ne $setupConfiguration) {
            $targetUser = ([string]$setupConfiguration.targetUser).Trim()
            if ([string]::IsNullOrWhiteSpace($targetUser)) {
                throw "La cuenta Windows para BackupNAS no puede estar vacía."
            }
            $targetUserId = $targetUser
            $targetUserSidProperty = $setupConfiguration.PSObject.Properties[
                "targetUserSid"]
            if ($null -ne $targetUserSidProperty) {
                $candidateSid = ([string]$targetUserSidProperty.Value).Trim()
                if ($candidateSid -notmatch '^S-1-(?:\d+-){1,14}\d+$') {
                    throw "El SID del usuario interactivo no es válido."
                }
                $targetUserId = $candidateSid
            }
        }
        else {
            $defaultUser = "$env:USERDOMAIN\$env:USERNAME"
            $targetUser = Read-Host (
                "Cuenta Windows que ejecutará BackupNAS [$defaultUser]")
            if ([string]::IsNullOrWhiteSpace($targetUser)) {
                $targetUser = $defaultUser
            }
            $targetUserId = $targetUser
        }
        Write-Host "Programando BackupNAS para $targetUser."
        Register-BackupTask `
            -Executable $backupExecutable `
            -TargetUser $targetUserId
    }
    else {
        Write-Host "BackupNAS no se instalara; proveedor registrado: Acronis."
        if ($null -ne (Get-ScheduledTask `
                -TaskName "ATLAS BackupNAS" `
                -ErrorAction SilentlyContinue)) {
            Unregister-ScheduledTask `
                -TaskName "ATLAS BackupNAS" `
                -Confirm:$false
        }
        $backupDirectory = Join-Path $env:ProgramFiles "ATLAS\BackupNAS"
        if (Test-Path -LiteralPath $backupDirectory) {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        }
        $backupService = Get-Service `
            -Name "ATLAS Backup Service" `
            -ErrorAction SilentlyContinue
        if ($null -ne $backupService) {
            if ($backupService.Status -ne "Stopped") {
                Stop-Service -Name "ATLAS Backup Service" -Force
                $backupService.WaitForStatus(
                    "Stopped",
                    [TimeSpan]::FromSeconds(45))
            }
            & (Join-Path $env:SystemRoot "System32\sc.exe") `
                delete `
                "ATLAS Backup Service" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "No se pudo retirar ATLAS Backup Service."
            }
            $backupService.Dispose()
        }
        $backupServiceDirectory = Join-Path `
            $env:ProgramFiles `
            "ATLAS\BackupService"
        if (Test-Path -LiteralPath $backupServiceDirectory) {
            Remove-Item `
                -LiteralPath $backupServiceDirectory `
                -Recurse `
                -Force
        }
    }

    Write-Host "Instalando ATLAS Updater..."
    $updaterDirectory = Join-Path $env:ProgramFiles "ATLAS\Updater"
    New-Item -ItemType Directory -Path $updaterDirectory -Force |
        Out-Null
    $updaterExecutable = Join-Path `
        $updaterDirectory `
        "ATLAS.Updater.exe"
    Copy-Item `
        -LiteralPath $downloads["updater"] `
        -Destination $updaterExecutable `
        -Force
    Register-UpdaterTask -Executable $updaterExecutable
    $updaterDataDirectory = Join-Path $env:ProgramData "ATLAS\Updater"
    New-Item `
        -ItemType Directory `
        -Path (Join-Path $updaterDataDirectory "Staging") `
        -Force | Out-Null
    New-Item `
        -ItemType Directory `
        -Path (Join-Path $updaterDataDirectory "Logs") `
        -Force | Out-Null
    $updaterStatusDirectory = Join-Path $updaterDataDirectory "Status"
    New-Item `
        -ItemType Directory `
        -Path $updaterStatusDirectory `
        -Force | Out-Null
    Protect-AdminSystemDirectory -Path $updaterDataDirectory
    Protect-UpdaterStatusDirectory `
        -UpdaterDirectory $updaterDataDirectory `
        -StatusDirectory $updaterStatusDirectory

    Write-Host "Comprobando la instalacion completa de ATLAS..."
    Assert-AtlasInstallation `
        -Identity $identity `
        -Release $release `
        -InstallBackupNas $installBackupNas `
        -UpdaterExecutable $updaterExecutable `
        -BackupExecutable $(if ($installBackupNas) {
            $backupExecutable
        }
        else {
            $null
        }) `
        -BackupTargetUser $targetUser

    Remove-Item -LiteralPath $bootstrapPath -Force
    if (Test-Path -LiteralPath $resumePath -PathType Leaf) {
        Remove-Item -LiteralPath $resumePath -Force
    }
    Write-Host ""
    Write-Host "ATLAS quedó instalado correctamente." -ForegroundColor Green
    Write-Host "Agencia: $([string]$identity.agencyCode)"
    Write-Host "Equipo: $env:COMPUTERNAME"
    Write-Host "La credencial de un solo uso fue retirada."
    try {
        Start-ScheduledTask -TaskName "ATLAS Updater"
        Write-Host "La comprobacion inicial de actualizaciones fue iniciada."
    }
    catch {
        Write-Warning (
            "ATLAS quedo instalado, pero la comprobacion inicial se " +
            "ejecutara al proximo inicio del equipo.")
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
