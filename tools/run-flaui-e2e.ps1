# Publish the x64 EXE and run FlaUI scan→decide→preview→restore→verify→purge
# against a browsed TEMP fixture (never C:\Windows.old).
#
# Requires an elevated interactive desktop. A Medium IL testhost cannot attach
# to the requireAdministrator GUI (UIPI). The test itself creates
# %TEMP%\wor-flaui-e2e-*\OldInstall and forces PreferManualDelete.
#
# Usage (elevated PowerShell from repo root):
#   powershell -File tools/run-flaui-e2e.ps1

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run in an elevated Administrator session so FlaUI can attach."
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$log = Join-Path $env:TEMP ("WinOldRecovery-flaui-e2e-" + $stamp + ".log")
$publishDir = Join-Path $env:TEMP "wor-publish-flaui"
$csproj = Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj"
$testproj = Join-Path $root "tests\WinOldRecovery.App.Tests\WinOldRecovery.App.Tests.csproj"

Start-Transcript -Path $log | Out-Null
try {
    Write-Host ("Log={0}" -f $log)
    & $dotnet publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $exe = Join-Path $publishDir "WinOldRecovery.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "Published EXE was not found at $exe" }

    $env:RUN_FLAUI = "1"
    $env:WINOLD_RECOVERY_EXE = $exe
    & $dotnet test $testproj -c Release --filter FlaUiSmokeTests --nologo
    if ($LASTEXITCODE -ne 0) { throw "FlaUI e2e failed." }
    Write-Host "Passed=true"
}
finally {
    Stop-Transcript | Out-Null
    Write-Host ("Transcript={0}" -f $log)
}
