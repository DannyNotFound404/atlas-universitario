#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Server','Components','All')][string]$Target = 'Server',
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$output = Join-Path $repo 'artifacts'
$utf8 = [Text.UTF8Encoding]::new($false)
Push-Location $repo
try {
    $version = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.') { throw 'Se requiere el SDK .NET 10 seleccionado por global.json.' }
    if ($Target -in @('Components','All') -and -not $IsWindows) { throw 'La compilación de los componentes Windows de esta guía se ejecuta en Windows con PowerShell 7.' }
    $local = Join-Path $repo '.local'
    $environment = Join-Path $local 'setup-environment.json'
    $certificate = Join-Path $local 'pki/root-ca.crt'
    $publicKey = Join-Path $local 'pki/release-signing.public.pem'
    if ($Target -in @('Components','All')) {
        foreach ($path in @($environment,$certificate,$publicKey)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Ejecute explícitamente scripts/Initialize-Local.ps1 antes de compilar componentes. Build no genera claves.' }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $repo 'deploy/windows/Install-MiguelAgent.ps1') -PathType Leaf)) {
            throw 'Falta deploy/windows/Install-MiguelAgent.ps1; no puede componerse el paquete del agente.'
        }
        $setupProfile = Get-Content -LiteralPath $environment -Raw | ConvertFrom-Json
        if ($setupProfile.environmentName -cne 'UNIVERSITY_GIT' -or $setupProfile.schemaVersion -ne 1 -or
            $setupProfile.rootCertificateSha256 -ine (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash -or
            $setupProfile.releasePublicKeySha256 -ine (Get-FileHash -LiteralPath $publicKey -Algorithm SHA256).Hash) {
            throw 'El perfil local y los archivos públicos no coinciden. No se compiló Setup.'
        }
    }
    [void](New-Item -ItemType Directory -Path $output -Force)
    function Publish([string]$Name,[string]$Destination,[string[]]$Extra=@()) {
        $project = Join-Path $repo "src/$Name/$Name.csproj"
        & $dotnet publish $project --configuration Release --output $Destination '-p:DebugType=None' '-p:DebugSymbols=false' @Extra
        if ($LASTEXITCODE -ne 0) { throw "Falló publish de $Name. Detenga el proceso y revise la salida anterior." }
    }
    if ($Target -in @('Server','All')) {
        Publish 'Miguel.Server' (Join-Path $output 'server') @('--self-contained','false','-p:UseAppHost=false')
    }
    if ($Target -in @('Components','All')) {
        $components = Join-Path $output 'components'
        $windows = @('--runtime','win-x64','--self-contained','true','-p:PublishTrimmed=false')
        Publish 'Miguel.Agent' (Join-Path $components 'agent') $windows
        Publish 'BackupNAS' (Join-Path $components 'backup') ($windows + @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true'))
        Publish 'Atlas.Updater' (Join-Path $components 'updater') $windows
        Publish 'Atlas.Setup' (Join-Path $components 'setup') ($windows + @("-p:AtlasEnvironmentFile=$environment","-p:AtlasRootCertificateFile=$certificate","-p:AtlasReleasePublicKeyFile=$publicKey"))
        Publish 'Atlas.Publisher' (Join-Path $components 'publisher') $windows
        Publish 'Atlas.Release.Tool' (Join-Path $components 'release-tool') @('--self-contained','false','-p:UseAppHost=false')
        Publish 'Atlas.Nas.Provisioner' (Join-Path $components 'nas-provisioner') @('--self-contained','false','-p:UseAppHost=false')
        $distribution = Join-Path $output 'distribution'
        [void](New-Item -ItemType Directory -Path $distribution -Force)
        Compress-Archive -LiteralPath @((Join-Path $components 'agent'),(Join-Path $repo 'deploy/windows/Install-MiguelAgent.ps1')) -DestinationPath (Join-Path $distribution 'atlas-agent.zip') -Force
        Copy-Item -LiteralPath (Join-Path $components 'backup/BackupNAS.exe'),(Join-Path $components 'setup/ATLAS.Setup.exe'),(Join-Path $components 'updater/ATLAS.Updater.exe') -Destination $distribution -Force
        Copy-Item -LiteralPath $environment -Destination (Join-Path $distribution 'atlas-setup-environment.json') -Force
        $specs = @(
            @{component='agent';version='0.4.4-u.1';fileName='atlas-agent.zip';contentType='application/zip'},
            @{component='backupnas';version='5.13.0-u.2';fileName='BackupNAS.exe';contentType='application/octet-stream'},
            @{component='updater';version='0.4.0-rc.1';fileName='ATLAS.Updater.exe';contentType='application/vnd.microsoft.portable-executable'},
            @{component='setup';version='0.6.1-u.2';fileName='ATLAS.Setup.exe';contentType='application/vnd.microsoft.portable-executable'}
        )
        $packages = foreach ($spec in $specs) {
            $file = Get-Item -LiteralPath (Join-Path $distribution $spec.fileName)
            [ordered]@{ component=$spec.component; version=$spec.version; fileName=$spec.fileName; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes=$file.Length; contentType=$spec.contentType }
        }
        $catalog = [ordered]@{schemaVersion=1;publishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');packages=@($packages)}
        [IO.File]::WriteAllText((Join-Path $distribution 'atlas-package-catalog.json'), ($catalog | ConvertTo-Json -Depth 6) + "`n", $utf8)
    }
    Write-Output "ATLAS_GIT_BUILD_OK target=$Target"
    Write-Output "Salida: $output"
    Write-Output 'Los archivos se compilaron; no se instalaron, firmaron, activaron ni distribuyeron.'
} finally { Pop-Location }
