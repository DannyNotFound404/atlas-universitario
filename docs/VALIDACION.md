# Validación de la entrega de fuentes

Fecha de preparación: **29 de octubre de 2026**. Base: servidor **0.9.50-u.2**, Grafito Acero y 44 migraciones. No es una certificación de seguridad ni un despliegue en una instalación existente.

## Comprobaciones realizadas

- Compilación y publicación local del servidor y de todos los componentes mediante `scripts/Build.ps1 -Target All`, con SDK .NET 10.0.302 y las dependencias fijadas en `Directory.Packages.props`.
- **63 comprobaciones de componentes:** carga de la configuración embebida de Setup, coincidencia de certificados y clave pública con sus hashes, versiones de ocho ensamblados, atribución de Backup, cuatro entradas del catálogo y estructura completa del ZIP del agente.
- **76 comprobaciones de `.gitignore` con Git real** en una carpeta de prueba separada: exclusión de estados locales, secretos y salidas; conservación de fuentes y ejemplos. Además, se comprobó la inclusión de los archivos de la carpeta en el índice y la conservación de sus bytes con `core.autocrlf=true`, incluidos los archivos del namespace `Outbox` del agente. El manifiesto se agrega después de esta comprobación.
- Revisión de las fuentes y documentación compartibles para retirar identificadores, destinos, cuentas, rutas y confianza de las instalaciones existentes. Se conserva el aviso MIT de Tabler Icons.
- Arranque real de `Run-Server.ps1` en una base PostgreSQL de evaluación separada, con servidor HTTPS enlazado únicamente a loopback. Se observaron 44 migraciones y catálogo universitario 0.9.50-u.2, y una respuesta `ready` usando la CA local.

La consulta HTTPS en Windows usa el ajuste `--ssl-revoke-best-effort` documentado en la guía: la CA local no publica una lista de revocación. No se utilizó `-k`. El primer recorrido automatizado con Schannel sin ese ajuste no completó su consulta; se conservó como fallo y la respuesta correcta se verificó con el comando corregido. Esto no se presenta como una prueba automatizada integral de todas las funciones.

La compilación y las pruebas generaron confianza y salidas temporales nuevas. Esos archivos se conservaron fuera de la carpeta entregable y no se incluyen en Git ni en el ZIP de fuentes.

## Alcance y límites

No se instalaron componentes Windows, no se firmó ni activó una actualización, no se vinculó un agente y no se ejecutó una copia o restauración en un NAS. No se probaron desde este entregable una instalación Linux completa, la VM existente, Acronis o los servicios externos de vulnerabilidades. El módulo de redes de la base universitaria se conserva desactivado por defecto.

No se importaron datos reales. Las claves y certificados de la VM universitaria y de la instalación de trabajo no forman parte de la entrega. Las configuraciones del clon deben generarse y custodiarse localmente.

La carpeta se entrega **sin `.git`, sin commits ni remoto configurado**. Su publicación es un paso posterior del propietario, siguiendo el README. Las referencias de autoría y fecha son declaraciones de la entrega; los hashes permiten comprobar integridad, no acreditan por sí solos titularidad ni antigüedad.
