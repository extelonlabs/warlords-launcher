[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '../../artifacts/launcher'),
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Warlords.Launcher.csproj'
$arguments = @('build', $project, '-c', 'Release', '-o', $Output, '--nologo', '--verbosity', 'minimal', '-m:1')
if ($NoRestore) { $arguments += '--no-restore' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Warlords launcher build failed.' }
$executable = Join-Path $Output 'Warlords.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build did not produce Warlords.exe.' }
# Self-update swaps exactly one file, so the launcher must run with nothing beside it.
$extra = @(Get-ChildItem -LiteralPath $Output -Recurse -File | Where-Object FullName -ne (Get-Item -LiteralPath $executable).FullName)
if ($extra.Count -ne 0) { throw "Warlords.exe must be alone in the output. Also found: $($extra.Name -join ', ')" }
Get-Item -LiteralPath $executable | Select-Object FullName,Length
Get-FileHash -LiteralPath $executable -Algorithm SHA256
