# Kill the published EXE mid-copy and resume (PENDING_MANUAL / I15).
# If this window is not elevated, the EXE is started with RunAs (UAC).
# Medium IL cannot Stop-Process an elevated EXE; the script then uses
# UAC taskkill /PID (never /IM, so a live 1M --scan is not killed).
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
    Write-Host "Writing 2,000 x 512 KiB copy files..."
    $payload = New-Object byte[] (512 * 1024)
    1..20 | ForEach-Object {
        $dir = Join-Path $source ("d" + $_.ToString("D2"))
        New-Item -ItemType Directory -Path $dir | Out-Null
        1..100 | ForEach-Object {
            [System.IO.File]::WriteAllBytes((Join-Path $dir ("f" + $_.ToString("D3") + ".bin")), $payload)
        }
    }
    $sourceFiles = @(Get-ChildItem $source -Recurse -File).Count

    Write-Host "Publishing x64 single-file EXE..."
    $app = Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj"
    & $dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    $exe = Join-Path $publish "WinOldRecovery.exe"

    Write-Host "Starting published --restore (UAC if needed)..."
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $start = @{ FilePath = $exe; PassThru = $true; ArgumentList = @("--restore", $source, $dest, "--report", $report1) }
    if (-not $elevated) { $start.Verb = "RunAs" }
    $p = Start-Process @start
    if (-not $p) { throw "Could not start published EXE." }

    $deadline = (Get-Date).AddSeconds(60)
    $copied = 0
    do {
        Start-Sleep -Milliseconds 250
        $copied = @(Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue).Count
        $alive = [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue)
    } while ($alive -and $copied -eq 0 -and (Get-Date) -lt $deadline)

    if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
        throw "Copy process $($p.Id) exited before kill (copied $copied / $sourceFiles). Payload finished too fast."
    }
    if ($copied -le 0) {
        throw "Copy had not written destination files after 60s."
    }
    if ($copied -ge $sourceFiles) {
        throw "Copy finished before kill ($copied files). Increase payload."
    }

    Write-Host "Killing pid $($p.Id) after $copied / $sourceFiles files..."
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
        Write-Host "Stop-Process blocked (elevated). Requesting UAC taskkill /PID..."
        $killer = Start-Process -FilePath "$env:SystemRoot\System32\taskkill.exe" -Verb RunAs -PassThru -ArgumentList @("/F", "/PID", "$($p.Id)")
        if (-not $killer) { throw "UAC declined for taskkill." }
        $killer.WaitForExit()
        if ($killer.ExitCode -ne 0) { throw "taskkill exited $($killer.ExitCode)." }
    }
    if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
        throw "Could not kill pid $($p.Id)."
    }
    Write-Host "Killed pid $($p.Id) with dest still incomplete."

    Start-Sleep -Seconds 1
    Write-Host "Resuming published --restore..."
    $resumeStart = @{ FilePath = $exe; PassThru = $true; ArgumentList = @("--restore", $source, $dest, "--report", $report2) }
    if (-not $elevated) { $resumeStart.Verb = "RunAs" }
    $resume = Start-Process @resumeStart
    if (-not $resume) { throw "Could not start resume EXE." }
    $resume.WaitForExit()
    $resumeExit = $resume.ExitCode

    Write-Host "Resume exit $resumeExit"
    if (Test-Path $report2) { Get-Content -Raw $report2 }
    $partials = @(Get-ChildItem $dest -Recurse -Filter "*.winold-partial" -ErrorAction SilentlyContinue)
    if ($partials.Count -ne 0) { throw "Leftover partials: $($partials.Count)" }
    $destFiles = @(Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue).Count
    if ($destFiles -ne $sourceFiles) {
        throw "Destination file count $destFiles does not match source $sourceFiles (Keep-Both duplicates?)."
    }
    if (-not (Test-Path $report2) -or ((Get-Content -Raw $report2) -notmatch "Passed: true")) {
        throw "Resume report did not pass. See $report2 / $log"
    }
    Write-Host "Published kill-copy resume passed. Transcript: $log"
}
finally {
    Stop-Transcript | Out-Null
}
