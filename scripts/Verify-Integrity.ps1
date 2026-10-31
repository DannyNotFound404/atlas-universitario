#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$manifest = Join-Path $repo 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'No existe el manifiesto de la entrega.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$count = 0
foreach ($line in [IO.File]::ReadAllLines($manifest)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([a-f0-9]{64}) \*(.+)$') { throw 'Línea no válida en SHA256SUMS.txt.' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if (-not $seen.Add($relative)) { throw "Entrada repetida: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $repo $relative))
    if (-not $path.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Ruta fuera de la entrega.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Falta el archivo: $relative" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) { throw "Archivo modificado: $relative. Conserve el original y revise el cambio." }
    $count++
}
if ($count -eq 0) { throw 'El manifiesto está vacío.' }
Write-Output "ATLAS_SOURCE_INTEGRITY_OK files=$count"
