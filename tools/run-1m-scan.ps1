# One-million-node scan memory ceiling (M7 / PENDING_MANUAL)
#
# Does not replace elevated 100k FixtureGen (tools/run-elevated-m0.ps1).
# Portable generation skips deny-ACL / orphan-SID / EFS hazards.
#
# Usage (from repo root):
#   powershell -File tools/run-1m-scan.ps1
#   powershell -File tools/run-1m-scan.ps1 -Files 1000000

param(
    [int] $Files = 1000000
)

$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:TEMP ("WinOldRecovery-1m-fixture-" + [guid]::NewGuid().ToString("N"))

Write-Host "Building FixtureGen (Release)..."
& $dotnet build (Join-Path $root "tools\FixtureGen\FixtureGen.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "FixtureGen build failed." }

$dll = Join-Path $root "tools\FixtureGen\bin\Release\net10.0-windows\FixtureGen.dll"
Write-Host "Generating portable $Files-file fixture at $target ..."
& $dotnet $dll $target --files $Files --portable
if ($LASTEXITCODE -ne 0) { throw "FixtureGen failed." }

Write-Host "Publishing x64 single-file EXE..."
& $dotnet publish (Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "Fixture is ready at $target"
Write-Host "Scan it with the published EXE (UAC), then record peak working set (< 1.5 GB) and duration in docs/spikes/PENDING_MANUAL.md."
Write-Host "This script does not launch the requireAdministrator EXE (Medium IL cannot approve UAC)."
