#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$PsqlPath = 'psql',
    [ValidateRange(1024,65535)][int]$DatabasePort = 5432,
    [Security.SecureString]$DatabasePassword,
    [Security.SecureString]$TlsPassword,
    [switch]$CreateAdministrator
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repo '.local'
$server = Join-Path $repo 'artifacts/server'
$assembly = Join-Path $server 'Miguel.Server.dll'
$environmentFile = Join-Path $local 'setup-environment.json'
foreach ($path in @($assembly,$environmentFile,(Join-Path $local 'pki/server-tls.pfx'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Faltan archivos locales. Ejecute Initialize-Local.ps1 y Build.ps1 -Target Server.' }
}
$setupProfile = Get-Content -LiteralPath $environmentFile -Raw | ConvertFrom-Json
$url = [uri]$setupProfile.serverUrl
if ($setupProfile.environmentName -cne 'UNIVERSITY_GIT' -or $url.Scheme -cne 'https' -or
    $url.Host -notin @('localhost','127.0.0.1') -or $url.AbsolutePath -ne '/') {
    throw 'Run-Server sólo admite el perfil local HTTPS localhost/127.0.0.1. No administra una VM ni un entorno remoto.'
}
if (@(Get-ChildItem Env: | Where-Object Name -match '^(ASPNETCORE_)?Kestrel__Endpoints__').Count) {
    throw 'Hay endpoints Kestrel heredados en el entorno. Abra una terminal limpia antes de usar el lanzador local.'
}
$dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$psql = (Get-Command $PsqlPath -CommandType Application -ErrorAction Stop).Source
if (-not $DatabasePassword) { $DatabasePassword = Read-Host 'Contraseña del rol local atlas_universitario_git' -AsSecureString }
if (-not $TlsPassword) { $TlsPassword = Read-Host 'Frase usada en Initialize-Local para el certificado TLS' -AsSecureString }
function Reveal([Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
$databaseSecret = Reveal $DatabasePassword
$tlsSecret = Reveal $TlsPassword
$connection = [Data.Common.DbConnectionStringBuilder]::new()
$connection['Host']='127.0.0.1'; $connection['Port']=$DatabasePort
$connection['Database']='atlas_universitario_git'; $connection['Username']='atlas_universitario_git'
$connection['Password']=$databaseSecret; $connection['Timeout']=10; $connection['Command Timeout']=30
$data = Join-Path $local 'data'
$paths = @('data-protection','releases','training-evidence','governance-documents','backup-evidence','nas-mounts')
foreach ($name in $paths) { [void](New-Item -ItemType Directory -Path (Join-Path $data $name) -Force) }
[void](New-Item -ItemType Directory -Path (Join-Path $repo 'artifacts/distribution') -Force)
$variables = @{
    'ConnectionStrings__Miguel'=$connection.ConnectionString
    'ASPNETCORE_ENVIRONMENT'='Development'; 'DOTNET_ENVIRONMENT'='Development'
    'ASPNETCORE_URLS'="https://127.0.0.1:$($url.Port)"; 'DOTNET_URLS'="https://127.0.0.1:$($url.Port)"
    'ASPNETCORE_HTTP_PORTS'=''; 'ASPNETCORE_HTTPS_PORTS'=''
    'Kestrel__Certificates__Default__Path'=(Join-Path $local 'pki/server-tls.pfx')
    'Kestrel__Certificates__Default__Password'=$tlsSecret
    'Miguel__AllowInsecureHttp'='false'; 'Miguel__PublicBaseUrl'=$url.AbsoluteUri
    'Miguel__PublicCertificatePath'=(Join-Path $local 'pki/root-ca.crt')
    'Miguel__ReleasePublicKeyPath'=(Join-Path $local 'pki/release-signing.public.pem')
    'Miguel__DistributionPath'=(Join-Path $repo 'artifacts/distribution')
    'Miguel__DataProtectionPath'=(Join-Path $data 'data-protection')
    'Miguel__ReleaseStoragePath'=(Join-Path $data 'releases')
    'Miguel__TrainingEvidencePath'=(Join-Path $data 'training-evidence')
    'Miguel__GovernanceDocumentPath'=(Join-Path $data 'governance-documents')
    'Miguel__BackupEvidencePath'=(Join-Path $data 'backup-evidence')
    'Miguel__NasMountRoot'=(Join-Path $data 'nas-mounts')
    'Miguel__NasProvisionerSocketPath'=(Join-Path $local 'no-provisioner.sock')
    'Miguel__NetworkDiscoveryEnabled'='false'; 'Miguel__NetworkDiscoveryAutomaticEnabled'='false'
    'VulnerabilityIntelligence__Enabled'='false'
    'PGPASSWORD'=$databaseSecret; 'PGCONNECT_TIMEOUT'='10'; 'PGOPTIONS'='-c default_transaction_read_only=on'
}
$previous = @{}
try {
    foreach ($name in $variables.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$variables[$name],'Process')
    }
    # La base de evaluación se identifica por nombre y rol; no se aceptan cadenas arbitrarias.
    $identity = & $psql -X -A -t --no-password -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $DatabasePort -U atlas_universitario_git -d atlas_universitario_git -c "SELECT current_database() || '|' || current_user;"
    if ($LASTEXITCODE -ne 0 -or ($identity -join '').Trim() -cne 'atlas_universitario_git|atlas_universitario_git') { throw 'No se pudo verificar la base local de evaluación.' }
    $hasNas = & $psql -X -A -t --no-password -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $DatabasePort -U atlas_universitario_git -d atlas_universitario_git -c "SELECT to_regclass('public.nas_appliances') IS NOT NULL;"
    if ($LASTEXITCODE -ne 0) { throw 'Falló la comprobación local de NAS.' }
    if (($hasNas -join '').Trim() -ceq 't') {
        $enabled = & $psql -X -A -t --no-password -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $DatabasePort -U atlas_universitario_git -d atlas_universitario_git -c 'SELECT count(*) FROM nas_appliances WHERE enabled;'
        if ($LASTEXITCODE -ne 0 -or ($enabled -join '').Trim() -cne '0') { throw 'Hay NAS habilitados en esta base. El lanzador local se detuvo antes de iniciar sondeos. Use una base de evaluación vacía o revise su configuración.' }
    }
    # PGOPTIONS sólo protege las consultas psql anteriores; el servidor aplica las migraciones locales.
    [Environment]::SetEnvironmentVariable('PGOPTIONS',$previous['PGOPTIONS'],'Process')
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$previous['PGPASSWORD'],'Process')
    Push-Location $server
    try {
        if ($CreateAdministrator) {
            & $dotnet $assembly admins create evaluador 'Evaluador local' Administrator
        } else {
            Write-Output "ATLAS local: $($url.AbsoluteUri)login"
            Write-Output 'Sólo escucha en 127.0.0.1. Ctrl+C detiene el servidor. No habilite NAS reales ni importe datos institucionales.'
            & $dotnet $assembly
        }
        if ($LASTEXITCODE -ne 0) { throw 'El proceso del servidor terminó con error. Revise la salida; no repita migraciones manualmente.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
    $databaseSecret=$null; $tlsSecret=$null; $connection.Clear(); $variables.Clear()
}
