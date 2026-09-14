# Requires an elevated PowerShell window. Does not run cleanmgr.
# Usage (from repo root, Administrator):
#   powershell -File tools/run-elevated-m0.ps1
#   powershell -File tools/run-elevated-m0.ps1 -Files 100000

param(
    [int] $Files = 100000
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run in an elevated Administrator session."
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:TEMP ("WinOldRecovery-elevated-fixture-" + [guid]::NewGuid().ToString("N"))

Write-Host "Building FixtureGen (Release)..."
& $dotnet build (Join-Path $root "tools\FixtureGen\FixtureGen.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "FixtureGen build failed." }

$dll = Join-Path $root "tools\FixtureGen\bin\Release\net10.0-windows\FixtureGen.dll"
Write-Host "Generating elevated fixture at $target with $Files node_modules files..."
& $dotnet $dll $target --files $Files
if ($LASTEXITCODE -ne 0) { throw "FixtureGen failed." }

Write-Host "Self-check..."
& $dotnet $dll --self-check-only $target
if ($LASTEXITCODE -ne 0) { throw "Self-check failed." }

Write-Host "Elevated FixtureGen and self-check passed. Record the path in docs/spikes/PENDING_MANUAL.md:"
Write-Host $target
