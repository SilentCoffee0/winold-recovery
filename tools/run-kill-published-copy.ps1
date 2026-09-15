# Kill the published EXE mid-copy and resume (PENDING_MANUAL / I15).
# If this window is not elevated, the EXE is started with RunAs (UAC).
# Medium IL cannot Stop-Process an elevated EXE; the script records that and
# still tries a second --restore so resume can be checked if the first copy died.
#
# Usage (from repo root):
#   powershell -File tools/run-kill-published-copy.ps1

$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$work = Join-Path $env:TEMP ("WinOldRecovery-KillPub-" + $stamp)
$source = Join-Path $work "Windows.old\Desktop"
$dest = Join-Path $work "Recovered\Desktop"
$report1 = Join-Path $env:TEMP ("WinOldRecovery-kill-copy-1-" + $stamp + ".txt")
$report2 = Join-Path $env:TEMP ("WinOldRecovery-kill-copy-2-" + $stamp + ".txt")
$log = Join-Path $env:TEMP ("WinOldRecovery-kill-copy-" + $stamp + ".log")
$publish = Join-Path $env:TEMP ("wor-publish-killcopy-" + $stamp)
New-Item -ItemType Directory -Path $source, $dest | Out-Null
Start-Transcript -Path $log | Out-Null
try {
    Write-Host "Writing 2,000 copy files..."
    1..20 | ForEach-Object {
        $dir = Join-Path $source ("d" + $_.ToString("D2"))
        New-Item -ItemType Directory -Path $dir | Out-Null
        1..100 | ForEach-Object {
            $bytes = New-Object byte[] 8192
            [System.IO.File]::WriteAllBytes((Join-Path $dir ("f" + $_.ToString("D3") + ".bin")), $bytes)
        }
    }

    Write-Host "Publishing x64 single-file EXE..."
    $app = Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj"
    & $dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    $exe = Join-Path $publish "WinOldRecovery.exe"

    Write-Host "Starting published --restore (UAC if needed)..."
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($elevated) {
        $p = Start-Process -FilePath $exe -PassThru -ArgumentList @("--restore", $source, $dest, "--report", $report1)
    }
    else {
        $p = Start-Process -FilePath $exe -Verb RunAs -PassThru -ArgumentList @("--restore", $source, $dest, "--report", $report1)
    }
    if (-not $p) { throw "Could not start published EXE." }
    Start-Sleep -Seconds 2
    try {
        Stop-Process -Id $p.Id -Force -ErrorAction Stop
        Write-Host "Killed pid $($p.Id)"
    }
    catch {
        Write-Host "Could not kill pid $($p.Id): $($_.Exception.Message)"
        Write-Host "End WinOldRecovery.exe in Task Manager, then rerun this script's resume half, or wait for the copy to finish."
    }

    Start-Sleep -Seconds 1
    Write-Host "Resuming published --restore..."
    if ($elevated) {
        & $exe --restore $source $dest --report $report2
        $resumeExit = $LASTEXITCODE
    }
    else {
        $resume = Start-Process -FilePath $exe -Verb RunAs -PassThru -ArgumentList @("--restore", $source, $dest, "--report", $report2)
        $resume.WaitForExit()
        $resumeExit = $resume.ExitCode
    }

    Write-Host "Resume exit $resumeExit"
    if (Test-Path $report2) { Get-Content -Raw $report2 }
    $partials = @(Get-ChildItem $dest -Recurse -Filter "*.winold-partial" -ErrorAction SilentlyContinue)
    if ($partials.Count -ne 0) { throw "Leftover partials: $($partials.Count)" }
    if (-not (Test-Path $report2) -or ((Get-Content -Raw $report2) -notmatch "Passed: true")) {
        throw "Resume report did not pass. See $report2 / $log"
    }
    Write-Host "Published kill-copy resume passed. Transcript: $log"
}
finally {
    Stop-Transcript | Out-Null
}
