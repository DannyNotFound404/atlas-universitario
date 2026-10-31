# Compilar y ejecutar ATLAS Universitario

Esta entrega contiene fuentes de ATLAS Server `0.9.50-u.2` y sus componentes. Los scripts preparan una evaluación local independiente. No actualizan la VM universitaria existente, no conectan con producción y no instalan aplicaciones en equipos de usuarios.

## 1. Requisitos

- PowerShell 7.4 o posterior, en Windows o Linux. Windows PowerShell 5.1 no sirve para estos scripts.
- SDK .NET `10.0.302` o un parche posterior de la misma banda admitido por `global.json`.
- PostgreSQL local con `psql` disponible y un administrador que pueda crear una base nueva. El servidor y el rol de evaluación deben estar en el mismo equipo.
- Conexión a Internet durante la primera restauración de paquetes desde NuGet. Una vez restauradas las dependencias y compilado el servidor, el catálogo de vulnerabilidades queda desactivado en este lanzador local.
- Para compilar los componentes: Windows x64. Backup y la biblioteca Desktop apuntan a .NET 8 Windows; el SDK 10 restaura sus paquetes de referencia. Las salidas Windows se publican con runtime incluido. Si se ejecutan proyectos .NET 8 sin `publish --self-contained`, también hace falta Windows Desktop Runtime 8.

Descargas y documentación oficiales: [PowerShell](https://learn.microsoft.com/powershell/scripting/install/installing-powershell), [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0), [PostgreSQL](https://www.postgresql.org/download/). Estos instaladores son prerrequisitos del equipo; no están dentro del repositorio.

Abra una terminal **dentro de la raíz del clon**, donde está `ATLAS.Universitario.slnx`. Los comandos siguientes obtienen desde allí la ruta absoluta, por lo que no dependen de la carpeta del autor.

## 2. Preparar en Windows

Ejecute en **PowerShell 7**. Cada línea es un comando separado. Deténgase si falta una herramienta o falla un comando.

```powershell
$Repo = (Resolve-Path -LiteralPath '.').Path
Get-Item -LiteralPath (Join-Path $Repo 'ATLAS.Universitario.slnx')
$PSVersionTable.PSVersion
dotnet --version
dotnet --list-sdks
Get-Command psql -CommandType Application
```

Si `dotnet --version` no selecciona el SDK de `global.json`, instale la versión indicada antes de continuar. Si `psql` no está en `PATH`, añada la carpeta `bin` de su instalación local de PostgreSQL a esta terminal, o use su ruta completa con `-PsqlPath` al ejecutar el servidor.

Genere confianza nueva para este clon. Se solicita una frase de al menos 14 caracteres sin mostrarla. Guárdela: se utiliza para la clave de firma y el certificado TLS locales. No use una contraseña de producción.

```powershell
& (Join-Path $Repo 'scripts/Initialize-Local.ps1')
Get-Content -LiteralPath (Join-Path $Repo '.local/setup-environment.json')
Get-FileHash -LiteralPath (Join-Path $Repo '.local/pki/root-ca.crt') -Algorithm SHA256
Get-FileHash -LiteralPath (Join-Path $Repo '.local/pki/release-signing.public.pem') -Algorithm SHA256
```

Resultado esperado: `ATLAS_GIT_LOCAL_INITIALIZED`. Las dos huellas deben coincidir con `rootCertificateSha256` y `releasePublicKeySha256` del perfil. Son hashes de los bytes PEM, como exige Setup. No son la huella DER del certificado.

`Initialize-Local.ps1` sólo escribe dentro de `.local`, que está excluida de Git. Genera una CA RSA, un certificado TLS con contraseña y una clave **ECDSA P-256** cifrada compatible con Publisher. No instala certificados en Windows ni importa confianza de la VM existente. Si `.local` ya existe, se detiene para conservarla.

## 3. Crear una base local vacía en Windows

Abra `psql` con un administrador de **su PostgreSQL local**. `-W` solicita su contraseña sin colocarla en el comando. El puerto mostrado es `5432`; si usa otro, utilice el mismo al lanzar el servidor.

```powershell
$Psql = (Get-Command psql -CommandType Application).Source
& $Psql -X -h 127.0.0.1 -p 5432 -U postgres -d postgres -W
```

En la consola **psql**, compruebe primero si el nombre ya existe:

```sql
SELECT datname FROM pg_database WHERE datname = 'atlas_universitario_git';
SELECT rolname FROM pg_roles WHERE rolname = 'atlas_universitario_git';
```

Si cualquiera devuelve una fila, deténgase y revise esa base/rol antes de continuar. No borre ni reutilice una base desconocida. Si ambos están ausentes, ejecute cada línea:

```sql
CREATE ROLE atlas_universitario_git LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password atlas_universitario_git
CREATE DATABASE atlas_universitario_git OWNER atlas_universitario_git;
\connect atlas_universitario_git
GRANT USAGE, CREATE ON SCHEMA public TO atlas_universitario_git;
\q
```

`\password` solicita y confirma una contraseña nueva para este rol local. No la escriba en archivos del repositorio. El rol necesita crear estructuras dentro de su propia base porque la aplicación aplica las 44 migraciones al iniciar. No necesita superusuario ni acceso a otras bases.

## 4. Compilar servidor y componentes en Windows

Para compilar únicamente el servidor:

```powershell
& (Join-Path $Repo 'scripts/Build.ps1') -Target Server
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/server/Miguel.Server.dll')
```

Para compilar servidor y todos los componentes:

```powershell
& (Join-Path $Repo 'scripts/Build.ps1') -Target All
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/agent/Miguel.Agent.exe')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/backup/BackupNAS.exe')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/setup/ATLAS.Setup.exe')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/updater/ATLAS.Updater.exe')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/publisher/ATLAS.Publisher.exe')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/release-tool/ATLAS.Release.Tool.dll')
Get-Item -LiteralPath (Join-Path $Repo 'artifacts/components/nas-provisioner/Atlas.Nas.Provisioner.dll')
Get-Content -LiteralPath (Join-Path $Repo 'artifacts/distribution/atlas-package-catalog.json')
```

Resultado esperado: `ATLAS_GIT_BUILD_OK target=Server` o `target=All`. `-Target Components` compila sólo los componentes y compone la distribución. Puede pasar `-DotnetPath 'C:/ruta/al/dotnet.exe'` para seleccionar un ejecutable ya instalado.

El script no ejecuta Setup, Agent, Updater, Backup ni NAS Provisioner. No crea una publicación firmada ni activa anillos. La carpeta `artifacts/distribution` reúne los cuatro paquetes que espera Publisher y su catálogo con hashes; Publisher y las herramientas quedan en `artifacts/components`. El ZIP del agente incluye `agent/` y `Install-MiguelAgent.ps1` en su raíz, como requiere Setup. Conservar ese instalador en las fuentes no lo ejecuta.

Setup se compila con estas tres propiedades, resueltas automáticamente desde `.local`:

```text
AtlasEnvironmentFile=.local/setup-environment.json
AtlasRootCertificateFile=.local/pki/root-ca.crt
AtlasReleasePublicKeyFile=.local/pki/release-signing.public.pem
```

No cambie sólo el JSON: los archivos públicos y sus hashes deben coincidir. Para evaluar un servidor HTTPS diferente puede crear el clon inicialmente con `Initialize-Local.ps1 -BaseUrl 'https://nombre-de-su-servidor/'`; ello configura el destino de Setup, pero no instala ese servidor. El lanzador de esta guía sólo acepta `localhost` o `127.0.0.1`.

## 5. Crear el primer administrador y ejecutar en Windows

El primer comando aplica las migraciones a la base de evaluación y crea el usuario `evaluador`. Solicita las contraseñas de PostgreSQL y TLS, y después pide dos veces la contraseña administrativa en la consola del servidor. Debe ejecutarse en una terminal interactiva, no con entrada redirigida.

```powershell
& (Join-Path $Repo 'scripts/Run-Server.ps1') -CreateAdministrator
& (Join-Path $Repo 'scripts/Run-Server.ps1')
```

El segundo comando deja el servidor en primer plano. Resultado esperado: escucha en `https://127.0.0.1:5443`; abra `https://localhost:5443/login`. `Ctrl+C` lo detiene. Las contraseñas pasan al proceso mediante su entorno y se restauran al finalizar; no se guardan en `.env` ni en el historial de comandos.

En **otra terminal PowerShell 7**, dentro del clon, valide TLS y disponibilidad sin desactivar la comprobación del certificado:

```powershell
$Repo = (Resolve-Path -LiteralPath '.').Path
curl.exe --version
curl.exe --fail --show-error --ssl-revoke-best-effort --cacert (Join-Path $Repo '.local/pki/root-ca.crt') 'https://localhost:5443/health/ready'
```

Resultado esperado: `{"status":"ready"}`. El curl de Windows con Schannel debe admitir `--ssl-revoke-best-effort` (7.70.0 o posterior). La CA local de evaluación no publica listas de revocación: esta opción acepta que no estén disponibles; conserva la comprobación de la cadena, el nombre y la vigencia del certificado. No equivale a `-k`. [Referencia oficial de curl](https://curl.se/docs/manpage.html#--ssl-revoke-best-effort).

El navegador no confía automáticamente en la CA recién generada. Si desea confiar en ella para esta evaluación, compruebe antes su hash y, **opcionalmente**, importe sólo su CA en el almacén de su usuario:

```powershell
Get-FileHash -LiteralPath (Join-Path $Repo '.local/pki/root-ca.crt') -Algorithm SHA256
Import-Certificate -FilePath (Join-Path $Repo '.local/pki/root-ca.crt') -CertStoreLocation 'Cert:\CurrentUser\Root'
```

Los scripts no ejecutan esta importación. No importe certificados recibidos de una fuente desconocida. No hace falta importar la clave privada.

## 6. Servidor en Linux

Linux admite compilar y ejecutar el servidor; los componentes Windows se compilan con el bloque anterior en Windows. Este recorrido requiere que `pwsh`, el SDK de `global.json`, `psql`, PostgreSQL local y `curl` ya estén instalados. No sustituye la receta de despliegue de una VM ni instala servicios de sistema.

Ejecute en la **terminal Linux**, estando dentro del clon:

```bash
ATLAS_REPO="$(pwd -P)"
test -f "${ATLAS_REPO}/ATLAS.Universitario.slnx"
pwsh --version
dotnet --version
dotnet --list-sdks
psql --version
pwsh -NoProfile -File "${ATLAS_REPO}/scripts/Initialize-Local.ps1"
pwsh -NoProfile -File "${ATLAS_REPO}/scripts/Build.ps1" -Target Server
```

Para crear la base nueva, abra psql local como administrador:

```bash
sudo -u postgres psql -X -d postgres
```

Compruebe la ausencia del rol/base y después ejecute exactamente las sentencias SQL del apartado 3, incluida `\password`. No ejecute los `CREATE` si los nombres ya existen.

De nuevo en la **terminal Linux**:

```bash
pwsh -NoProfile -File "${ATLAS_REPO}/scripts/Run-Server.ps1" -CreateAdministrator
pwsh -NoProfile -File "${ATLAS_REPO}/scripts/Run-Server.ps1"
```

En otra terminal dentro del clon:

```bash
ATLAS_REPO="$(pwd -P)"
curl --fail --show-error --cacert "${ATLAS_REPO}/.local/pki/root-ca.crt" 'https://localhost:5443/health/ready'
psql -X -h 127.0.0.1 -p 5432 -U atlas_universitario_git -d atlas_universitario_git -W -c 'SELECT count(*) FROM schema_migrations;'
```

Espere `ready` y `44` migraciones. No ejecute los SQL de migración por separado: la aplicación los aplica y registra.

## 7. Límites de esta ejecución y diagnóstico

- La base está vacía de equipos, personas y copias reales. Las migraciones incluyen estructuras y catálogos; el repositorio no contiene un dump institucional ni carga automáticamente los datos de la VM universitaria.
- Se desactivan `Miguel__NetworkDiscoveryEnabled`, `Miguel__NetworkDiscoveryAutomaticEnabled` y `VulnerabilityIntelligence__Enabled` para este proceso. No se instala Nmap.
- El motor NAS de esta versión no tiene un interruptor global. Antes de iniciar, el lanzador verifica que no exista ningún NAS habilitado. Una base nueva no tiene NAS configurados. Si hay uno habilitado, se detiene: no cambia silenciosamente esa configuración. No habilite NAS reales desde el portal local ni importe una base externa mientras esté ejecutándose.
- El almacén de evidencia y las claves Data Protection se crean en `.local/data`. Ninguna de estas salidas debe añadirse a Git. Los scripts no implantan una política de retención institucional ni sustituyen pruebas de copia/restauración reales.
- Un build correcto demuestra compilación y generación de paquetes. No demuestra instalación, copia en NAS, restauración, vínculo de agente o despliegue en una VM.

Si falla NuGet, revise conexión, SDK y `NuGet.Config`; no elimine la validación de firmas ni cambie versiones al azar. Si falla PostgreSQL, compruebe el servicio local y las credenciales sin pegarlas en la conversación:

```powershell
& $Psql -X -h 127.0.0.1 -p 5432 -U atlas_universitario_git -d atlas_universitario_git -W -c 'SELECT current_database(), current_user;'
Get-NetTCPConnection -LocalPort 5443 -ErrorAction SilentlyContinue
```

Equivalentes en Linux:

```bash
systemctl is-active postgresql
psql -X -h 127.0.0.1 -p 5432 -U atlas_universitario_git -d atlas_universitario_git -W -c 'SELECT current_database(), current_user;'
ss -ltn 'sport = :5443'
```

Si falla TLS, confirme que utiliza la frase elegida en `Initialize-Local` y la CA de este mismo clon. Si falla una migración, conserve el mensaje y deténgase: no borre la base para ocultar el fallo. Para repetir el trabajo en otro equipo, haga otro clon y genere materiales locales nuevos.
