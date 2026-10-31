[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '../../artifacts/launcher'),
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Warlords.Launcher.csproj'
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Output, '--nologo', '--verbosity', 'minimal', '-m:1')
if ($NoRestore) { $arguments += '--no-restore' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Warlords launcher publish failed.' }
$executable = Join-Path $Output 'Warlords.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Publish did not produce Warlords.exe.' }
Get-Item -LiteralPath $executable | Select-Object FullName,Length
Get-FileHash -LiteralPath $executable -Algorithm SHA256
