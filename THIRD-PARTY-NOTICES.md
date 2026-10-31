# Componentes de terceros

Las condiciones restrictivas de `LICENSE.md` se aplican únicamente a las aportaciones propias cuyos derechos correspondan al titular declarado. No reemplazan ni limitan las licencias de terceros.

## Código y recursos incluidos

**Tabler Icons 3.46.0**, Copyright (c) 2020–2026 Paweł Kuna, licencia MIT. Se conserva íntegramente su aviso en `src/Miguel.Server/wwwroot/assets/vendor/tabler/LICENSE.txt`, junto al subconjunto SVG utilizado. Fuente: https://github.com/tabler/tabler-icons.

## Dependencias declaradas que se restauran por NuGet

| Paquete | Versión fijada | Licencia declarada por el paquete |
|---|---|---|
| Npgsql | 10.0.3 | PostgreSQL |
| Microsoft.Extensions.Hosting | 10.0.10 | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.10 | MIT |
| System.Management | 10.0.10 | MIT |
| System.Diagnostics.EventLog | 10.0.10 | MIT |

La identificación anterior se verificó con los metadatos `.nuspec` de esas versiones. Los paquetes, sus dependencias transitivas, los SDK y los runtimes no se incluyen como binarios en este repositorio; se obtienen de sus distribuidores y se rigen por sus licencias. La lista de dependencias resueltas queda en los archivos generados `obj/project.assets.json` después de restaurar. Deben revisarse sus avisos antes de redistribuir ejecutables.

Fuentes de los distribuidores: https://www.nuget.org/packages/Npgsql/10.0.3 y https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.10. Los servicios externos mencionados en la aplicación, como Acronis, NVD, CISA KEV y FIRST EPSS, no se redistribuyen ni se presentan como desarrollos propios. Esta entrega no contiene instaladores de Acronis ni sus credenciales.
