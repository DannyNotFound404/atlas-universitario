[CmdletBinding()]
param(
    [string]$SourceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$serviceName = "MIGUEL Agent"
$displayName = "ATLAS Agent"
$serviceAccount = "NT SERVICE\$serviceName"
$installDirectory = Join-Path $env:ProgramFiles "MIGUEL\Agent"
$dataDirectory = Join-Path $env:ProgramData "MIGUEL"
$backupInboxDirectory = Join-Path $env:ProgramData "ATLAS\BackupInbox"
$identityFile = Join-Path $dataDirectory "Config\agent-identity.json"
$scExe = Join-Path $env:SystemRoot "System32\sc.exe"
$icaclsExe = Join-Path $env:SystemRoot "System32\icacls.exe"
$robocopyExe = Join-Path $env:SystemRoot "System32\robocopy.exe"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Ejecute este instalador desde PowerShell como administrador."
    }
}

function Invoke-ServiceController {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $scExe @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "sc.exe falló ($exitCode): $($output -join [Environment]::NewLine)"
    }
}

function Invoke-Icacls {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $icaclsExe @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "icacls.exe falló ($exitCode): $($output -join [Environment]::NewLine)"
    }
}

function Protect-Directory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet("RX", "M")]
        [string]$ServicePermission,

        [Parameter(Mandatory)]
        [string]$ServiceSid
    )

    # La primera concesión recupera instalaciones incompletas antes de
    # intentar recorrer sus archivos.
    Invoke-Icacls @(
        $Path,
        "/grant:r",
        "*S-1-5-18:(OI)(CI)(F)",
        "*S-1-5-32-544:(OI)(CI)(F)",
        "*${ServiceSid}:(OI)(CI)($ServicePermission)",
        "/C")

    # Solo la raíz deja de heredar permisos de ProgramData o Program Files.
    # Sus descendientes vuelven a heredar las reglas protegidas de esta raíz;
    # esto permite crear y renombrar atómicamente archivos de la cola.
    Invoke-Icacls @($Path, "/inheritance:r", "/C")
    Invoke-Icacls @(
        $Path,
        "/grant:r",
        "*S-1-5-18:(OI)(CI)(F)",
        "*S-1-5-32-544:(OI)(CI)(F)",
        "*${ServiceSid}:(OI)(CI)($ServicePermission)",
        "/C")

    $descendants = Join-Path $Path "*"
    Invoke-Icacls @($descendants, "/reset", "/T", "/C")
    Invoke-Icacls @($Path, "/remove:g", "*S-1-5-19", "/T", "/C")
}

function Protect-BackupInbox {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ServiceSid
    )

    Invoke-Icacls @($Path, "/inheritance:r", "/C")
    Invoke-Icacls @(
        $Path,
        "/grant:r",
        "*S-1-5-18:(OI)(CI)(F)",
        "*S-1-5-32-544:(OI)(CI)(F)",
        "*${ServiceSid}:(OI)(CI)(M)",
        "*S-1-5-11:(OI)(CI)(W)",
        "/C")
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory)]
        [object]$Service,

        [Parameter(Mandatory)]
        [ValidateSet("Running", "Stopped")]
        [string]$ExpectedStatus,

        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $Service.Refresh()
        if ($Service.Status.ToString() -eq $ExpectedStatus) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "El servicio no alcanzó el estado '$ExpectedStatus' en $TimeoutSeconds segundos."
}

Assert-Administrator

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $PSScriptRoot "..\..\agent"
}

$source = [IO.Path]::GetFullPath($SourceDirectory)
$sourceExecutable = Join-Path $source "Miguel.Agent.exe"
$sourceSettings = Join-Path $source "agentsettings.json"

if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "No se encontró el agente publicado: $sourceExecutable"
}

if (-not (Test-Path -LiteralPath $sourceSettings -PathType Leaf)) {
    throw "No se encontró la configuración publicada: $sourceSettings"
}

if (-not (Test-Path -LiteralPath $identityFile -PathType Leaf)) {
    throw @"
Este equipo todavía no tiene identidad en:
$identityFile

Ejecute primero 'Miguel.Agent.exe enroll URL_DEL_SERVIDOR' y vuelva a intentarlo.
"@
}

$resolvedInstallDirectory = [IO.Path]::GetFullPath($installDirectory)
if ($source.TrimEnd("\") -eq $resolvedInstallDirectory.TrimEnd("\")) {
    throw "La carpeta de origen no puede ser la misma carpeta protegida de instalación."
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService -and $existingService.Status -ne "Stopped") {
    Write-Host "Deteniendo la instalación anterior de $displayName..."
    Stop-Service -Name $serviceName -Force
    Wait-ServiceState -Service $existingService -ExpectedStatus "Stopped"
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $backupInboxDirectory -Force | Out-Null

Write-Host "Copiando archivos verificados del agente..."
& $robocopyExe $source $installDirectory /MIR /XJ /R:2 /W:1 /COPY:DAT /DCOPY:DAT /NFL /NDL /NJH /NJS /NP
$robocopyExitCode = $LASTEXITCODE
if ($robocopyExitCode -gt 7) {
    throw "Robocopy no pudo instalar el agente. Código: $robocopyExitCode"
}

$installedExecutable = Join-Path $installDirectory "Miguel.Agent.exe"
$quotedBinaryPath = "`"$installedExecutable`""

if ($null -eq $existingService) {
    Invoke-ServiceController @(
        "create", $serviceName,
        "binPath=", $quotedBinaryPath,
        "start=", "demand",
        "obj=", $serviceAccount,
        "depend=", "Tcpip",
        "DisplayName=", $displayName)
}
else {
    Invoke-ServiceController @(
        "config", $serviceName,
        "binPath=", $quotedBinaryPath,
        "start=", "demand",
        "obj=", $serviceAccount,
        "depend=", "Tcpip",
        "DisplayName=", $displayName)
}

Invoke-ServiceController @(
    "description", $serviceName,
    "Inventario, estado y telemetría de respaldo para la plataforma ATLAS.")
Invoke-ServiceController @(
    "failure", $serviceName,
    "reset=", "86400",
    "actions=", "restart/60000/restart/300000/restart/900000")
Invoke-ServiceController @("failureflag", $serviceName, "1")
Invoke-ServiceController @("sidtype", $serviceName, "unrestricted")

$serviceSid = [Security.Principal.NTAccount]::new($serviceAccount).Translate(
    [Security.Principal.SecurityIdentifier]).Value

Write-Host "Protegiendo binarios e identidad del equipo..."
Protect-Directory `
    -Path $installDirectory `
    -ServicePermission "RX" `
    -ServiceSid $serviceSid
Protect-Directory `
    -Path $dataDirectory `
    -ServicePermission "M" `
    -ServiceSid $serviceSid
Protect-BackupInbox `
    -Path $backupInboxDirectory `
    -ServiceSid $serviceSid

# Con el servicio detenido es seguro retirar únicamente escrituras temporales
# incompletas. Los eventos .json pendientes nunca se eliminan aquí.
Get-ChildItem -LiteralPath (Join-Path $dataDirectory "Outbox") `
    -Filter "*.tmp" `
    -File `
    -ErrorAction SilentlyContinue |
    Remove-Item -Force

if (-not [Diagnostics.EventLog]::SourceExists($serviceName)) {
    [Diagnostics.EventLog]::CreateEventSource($serviceName, "Application")
}

Invoke-ServiceController @("config", $serviceName, "start=", "delayed-auto")
Start-Service -Name $serviceName

$installedService = Get-Service -Name $serviceName
Wait-ServiceState -Service $installedService -ExpectedStatus "Running"

Start-Sleep -Seconds 2
$installedService.Refresh()
if ($installedService.Status -ne "Running") {
    throw "El servicio se inició, pero no permaneció activo. Revise el registro Application."
}

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable).ProductVersion

Write-Host ""
Write-Host "ATLAS Agent quedó instalado correctamente." -ForegroundColor Green
Write-Host "Servicio: $serviceName"
Write-Host "Estado: $($installedService.Status)"
Write-Host "Inicio: automático retardado"
Write-Host "Cuenta: $serviceAccount"
Write-Host "Versión: $version"
Write-Host "Identidad conservada: $identityFile"
