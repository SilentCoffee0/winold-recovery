# One-million-node scan memory ceiling (M7 / PENDING_MANUAL)
#
# Builds a 1000x1000 empty-file tree, publishes the x64 EXE, and scans it with
# WinOldRecovery.exe --scan (not testhost). Does not run cleanmgr.
#
# Usage (from repo root):
#   powershell -File tools/run-1m-scan.ps1
#
# If this window is not elevated, the published EXE is started with RunAs (UAC).

$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$target = Join-Path $env:TEMP ("WinOldRecovery-1m-fixture-" + $stamp)
$report = Join-Path $env:TEMP ("WinOldRecovery-1m-scan-" + $stamp + ".txt")
$log = Join-Path $env:TEMP ("WinOldRecovery-1m-scan-" + $stamp + ".log")
Start-Transcript -Path $log | Out-Null
try {
    Write-Host "Building FixtureGen (Release)..."
    & $dotnet build (Join-Path $root "tools\FixtureGen\FixtureGen.csproj") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "FixtureGen build failed." }

    $dll = Join-Path $root "tools\FixtureGen\bin\Release\net10.0-windows\FixtureGen.dll"
    Write-Host "Writing 1,000,000 empty files (1000 folders x 1000 files) at $target ..."
    & $dotnet $dll --scale-tree $target
    if ($LASTEXITCODE -ne 0) { throw "Scale-tree generation failed." }

    Write-Host "Publishing x64 single-file EXE..."
    $app = Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj"
    & $dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    $exe = Join-Path $root "src\WinOldRecovery.App\bin\Release\net10.0-windows\win-x64\publish\WinOldRecovery.exe"
    if (-not (Test-Path $exe)) {
        $exe = Join-Path $root "src\WinOldRecovery.App\bin\publish\win-x64\WinOldRecovery.exe"
    }
    if (-not (Test-Path $exe)) { throw "Published EXE was not found." }

    Write-Host "Scanning with published EXE --scan ..."
    Write-Host "Report will be $report"
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($elevated) {
        & $exe --scan $target --report $report
        if ($LASTEXITCODE -ne 0) { throw "Published scan exited $LASTEXITCODE" }
    }
    else {
        $p = Start-Process -FilePath $exe -Verb RunAs -PassThru -ArgumentList @("--scan", $target, "--report", $report)
        if (-not $p) { throw "UAC elevation was declined." }
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) { throw "Published scan exited $($p.ExitCode)" }
    }

    if (-not (Test-Path $report)) { throw "Scan report was not written: $report" }
    Write-Host (Get-Content -Raw $report)
    if ((Get-Content -Raw $report) -notmatch "Passed: true") {
        throw "Published 1M scan did not pass. See $report"
    }

    Write-Host "Published 1M scan passed. Record $report in docs/spikes/PENDING_MANUAL.md"
    Write-Host "Transcript: $log"
}
finally {
    Stop-Transcript | Out-Null
}
