# ATLAS Universitario

Plataforma de inventario de equipos, seguimiento de respaldos, evidencias, auditoría y administración de componentes Windows.

**Autor declarado:** Danny Josué Orellana Osorio. **Uso permitido:** consulta y evaluación, incluida compilación y ejecución para ese fin. Los demás usos requieren autorización escrita; consulte [LICENSE.md](LICENSE.md).

## Qué contiene esta entrega

Código fuente del servidor y de sus diez proyectos, las 44 migraciones de base de datos, el portal Grafito Acero claro/oscuro y scripts portables para compilar. No contiene ejecutables, bases de datos, registros, copias de seguridad, cuentas de una organización ni las claves o certificados de una instalación existente.

Es la fuente universitaria **0.9.50-u.2**, preparada como entrega **2026.10.29-source.1**. Conserva el comportamiento de esa versión: Atlas Backup 5.13 y el módulo de redes de la edición universitaria, desactivado por defecto. No incluye la retirada del escáner ni las modificaciones posteriores de la edición de trabajo. No es un paquete para actualizar esa instalación.

| Proyecto | Función | Versión |
|---|---|---|
| `Miguel.Server` | API, portal y control central | 0.9.50-u.2 |
| `Miguel.Agent` | Inventario y telemetría del equipo Windows | 0.4.4-u.1 |
| `BackupNAS` | Atlas Backup: copia del usuario a su destino configurado | 5.13.0-u.2 |
| `Atlas.Setup` | Instalador de componentes | 0.6.1-u.2 |
| `Atlas.Publisher` | Preparación y firma de versiones | 0.3.1-u.2 |
| `Atlas.Updater` | Actualización verificada de componentes | 0.4.0-rc.1 |
| `Atlas.Release.Tool` | Utilidades de generación de releases | 0.1.0-u.1 |
| `Atlas.Nas.Provisioner` | Aprovisionamiento NAS en Linux | 1.0.0-rc.1 |
| `Atlas.Desktop` | Controles visuales compartidos | 1.1.0-rc.1 |
| `Miguel.Contracts` | Contratos compartidos | 0.1.0-pilot.2 |

El servidor centraliza seguimiento y evidencia; el contenido de las copias permanece en el destino configurado. La lógica de seguimiento conserva una retención de respaldo de 180 días. No se distribuye documentación normativa de otra organización ni se declara que esta entrega tenga una política aprobada propia.

## Organización

```text
src/                    Servidor, aplicaciones y bibliotecas compartidas.
database/               Migraciones originales de la edición universitaria.
design/                 Paleta Grafito Acero de portal y escritorio.
config/                 Ejemplo de configuración; no contiene confianza activa.
scripts/                Inicialización local, compilación y ejecución.
docs/                   Guía completa y documentación de autoría.
ATLAS.Universitario.slnx Solución de los diez proyectos.
Directory.Packages.props Versiones de dependencias NuGet.
global.json             Selección del SDK .NET.
LICENSE.md              Condiciones para el código propio.
THIRD-PARTY-NOTICES.md   Componentes externos y sus licencias.
SHA256SUMS.txt           Integridad de los archivos originales entregados.
```

## Compilar y ejecutar

Empiece por [docs/COMPILAR-Y-EJECUTAR.md](docs/COMPILAR-Y-EJECUTAR.md). Incluye requisitos y todos los comandos, separados para PowerShell y Linux. Los componentes de escritorio se compilan en Windows. El servidor utiliza .NET 10 y PostgreSQL; Atlas Backup y los controles visuales también necesitan .NET 8 Windows Desktop.

La confianza para un instalador de evaluación se genera de nuevo dentro de `.local/`. No reutilice certificados o claves de otra instalación. Esa carpeta y las salidas `artifacts/` están excluidas de Git y de la entrega fuente. Compilar no instala agentes, no activa una publicación y no cambia la VM actual.

## Subir el código a Git

Para recibir el ZIP, coloque a su lado el archivo `.zip.sha256.txt` proporcionado y ejecute este bloque en **PowerShell 7**. Se solicita la ruta absoluta al ZIP; no incluye rutas de la estación del autor.

```powershell
$atlasZip = Read-Host "Ruta absoluta a ATLAS-Universitario-Git-2026.10.29.zip"
if (-not [IO.Path]::IsPathRooted($atlasZip)) { throw 'Se requiere una ruta absoluta.' }
$atlasZip = (Resolve-Path -LiteralPath $atlasZip).Path
$atlasExpected = ((Get-Content -LiteralPath ($atlasZip + '.sha256.txt') -Raw).Trim() -split '\s+')[0]
if ($atlasExpected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $atlasZip -Algorithm SHA256).Hash -ine $atlasExpected) { throw 'SHA-256 distinto: no extraer.' }
$atlasParent = 'C:\Proyectos'
$atlasFolder = Join-Path $atlasParent 'ATLAS-Universitario-Git-2026.10.29'
if (Test-Path -LiteralPath $atlasFolder) { throw 'La carpeta ya existe: consérvela y revise antes de continuar.' }
New-Item -ItemType Directory -Path $atlasParent -Force
Expand-Archive -LiteralPath $atlasZip -DestinationPath $atlasParent
& (Join-Path $atlasFolder 'scripts/Verify-Integrity.ps1')
```

Espere `ATLAS_SOURCE_INTEGRITY_OK`. Si falla un hash, deténgase. El manifiesto describe los archivos originales: una modificación posterior legítima también cambia su hash.

Después, cree un repositorio vacío en su proveedor; para controlar quién evalúa el código, puede elegirlo privado. No añada README o licencia desde el proveedor porque ya están incluidos aquí. Si extrajo en otra carpeta, cambie la primera línea del bloque siguiente.

**PowerShell en Windows; un comando por línea:**

```powershell
Set-Location "C:\Proyectos\ATLAS-Universitario-Git-2026.10.29"
git --version
git init -b main
git config --local user.name "Danny Josué Orellana Osorio"
$atlasEmail = Read-Host "Correo que quieres asociar al commit; puedes usar el noreply de tu proveedor"
git config --local user.email "$atlasEmail"
git add .
git status --short
git diff --cached --stat
git diff --cached --name-only
```

Revise la lista antes de continuar. Debe incluir fuentes, documentación y ejemplos. Si aparecen `.local`, `bin`, `obj`, `artifacts`, claves, certificados, configuraciones personales, registros o bases de datos, deténgase y quite esos archivos del índice. No use `git add -f` para incluirlos.

Después de revisar, continúe en PowerShell:

```powershell
git commit -m "Entrega inicial ATLAS Universitario 2026.10.29-source.1"
git tag -a v2026.10.29-source.1 -m "Entrega de fuentes para consulta y evaluacion"
$atlasRemote = Read-Host "URL del repositorio vacio que creaste"
git remote add origin "$atlasRemote"
git remote -v
git push -u origin main
git push origin v2026.10.29-source.1
```

Si ya clonó una copia sin modificar, puede comprobarla desde su raíz con `pwsh -NoProfile -File ./scripts/Verify-Integrity.ps1`. En Linux también puede usar `sha256sum --check SHA256SUMS.txt`.

El correo y la URL se introducen por el propietario; no se inventan ni se incluyen claves de acceso. Compruebe en el proveedor que el commit y la etiqueta están visibles con la privacidad elegida. Estos comandos no se han ejecutado como parte de la preparación del ZIP.

Para retirar del índice un archivo añadido por error, conservándolo en el disco:

```powershell
$atlasPath = Read-Host "Ruta relativa exacta del archivo que no debes publicar"
git rm --cached -- "$atlasPath"
git status --short
```

Si un secreto ya se publicó, quitarlo de un commit nuevo no lo elimina del historial: hay que revocarlo y revisar el historial antes de seguir compartiendo.

## Autoría e integridad

Conserve la entrega original y su comprobante SHA-256. `.gitattributes` conserva los bytes originales, incluidos los finales de línea, para que Git no altere esos hashes al importar o clonar. Un archivo editado después tendrá otro hash, como corresponde. La licencia, los avisos, los commits y una etiqueta conservada permiten documentar la procedencia; no garantizan impedir copias ni sustituyen una acreditación externa de fecha o titularidad. Consulte [docs/AUTORIA.md](docs/AUTORIA.md), [AUTHORS.md](AUTHORS.md) y [CITATION.cff](CITATION.cff) para la referencia del proyecto.

Los componentes de terceros mantienen sus propias condiciones, especialmente Tabler Icons bajo MIT. Esta entrega es código disponible para evaluación; no se presenta como software de libre reutilización.
