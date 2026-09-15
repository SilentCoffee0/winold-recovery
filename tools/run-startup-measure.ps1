# Publish ReadyToRun and non-ReadyToRun x64 EXEs, then measure cold and warm
# time-to-window (PENDING_MANUAL elevated WPF launch). Does not run cleanmgr.
#
# On a clean VM, delete extraction caches before the cold run. Numbers from a
# development machine with a warm Defender cache do not satisfy the spike.
#
# Usage (interactive desktop; UAC will prompt if this window is not elevated):
#   powershell -File tools/run-startup-measure.ps1

$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$log = Join-Path $env:TEMP ("WinOldRecovery-startup-" + $stamp + ".log")
$r2rOut = Join-Path $env:TEMP ("wor-publish-startup-r2r-" + $stamp)
$jitOut = Join-Path $env:TEMP ("wor-publish-startup-jit-" + $stamp)

function Clear-ExtractionCache {
    $roots = @(
        (Join-Path $env:TEMP ".net"),
        (Join-Path $env:LOCALAPPDATA "Temp\.net")
    )
    foreach ($cacheRoot in $roots) {
        if (-not (Test-Path -LiteralPath $cacheRoot)) { continue }
        Get-ChildItem -LiteralPath $cacheRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "WinOldRecovery*" } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Measure-Launch {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $started = Get-Date
    $p = Start-Process -FilePath $Exe -PassThru
    $windowAt = $null
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 50
        try { $p.Refresh() } catch { break }
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowAt = Get-Date
            break
        }
    }

    $elapsed = if ($null -ne $windowAt) { ($windowAt - $started).TotalSeconds } else { $null }
    $sessions = Join-Path $env:LOCALAPPDATA "WinOldRecovery\sessions"
    $fresh = @(Get-ChildItem $sessions -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTime -gt $started.AddSeconds(-2) } |
        Sort-Object CreationTime -Descending |
        Select-Object -First 1)

    Write-Host ("[{0}] pid={1} exited={2} handle=0x{3:X} title='{4}' windowSeconds={5}" -f `
        $Label, $p.Id, $p.HasExited, $p.MainWindowHandle.ToInt64(), $p.MainWindowTitle, $elapsed)
    if ($fresh.Count -gt 0) {
        $db = Join-Path $fresh[0].FullName "session.db"
        $sessionLog = Join-Path $fresh[0].FullName "log.txt"
        Write-Host ("[{0}] session={1} db={2} log={3}" -f `
            $Label, $fresh[0].Name, (Test-Path $db), (Test-Path $sessionLog))
    }
    else {
        Write-Host ("[{0}] session=none" -f $Label)
    }

    if (-not $p.HasExited) {
        Stop-Process -Id $p.Id -Force
        Start-Sleep -Seconds 1
    }

    if ($null -eq $elapsed) {
        throw "$Label never showed a main window."
    }
}

Start-Transcript -Path $log | Out-Null
try {
    Write-Host "Publishing ReadyToRun (win-x64 profile)..."
    New-Item -ItemType Directory -Path $r2rOut -Force | Out-Null
    & $dotnet publish (Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj") `
        -c Release -r win-x64 --self-contained -p:PublishProfile=win-x64 -o $r2rOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "ReadyToRun publish failed." }

    Write-Host "Publishing non-ReadyToRun..."
    New-Item -ItemType Directory -Path $jitOut -Force | Out-Null
    & $dotnet publish (Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj") `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -o $jitOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "non-ReadyToRun publish failed." }

    $r2rExe = Join-Path $r2rOut "WinOldRecovery.exe"
    $jitExe = Join-Path $jitOut "WinOldRecovery.exe"
    Write-Host ("R2R={0} bytes={1}" -f $r2rExe, (Get-Item $r2rExe).Length)
    Write-Host ("JIT={0} bytes={1}" -f $jitExe, (Get-Item $jitExe).Length)

    Clear-ExtractionCache
    Measure-Launch -Exe $r2rExe -Label "r2r-cold"
    Measure-Launch -Exe $r2rExe -Label "r2r-warm"
    Clear-ExtractionCache
    Measure-Launch -Exe $jitExe -Label "jit-cold"
    Measure-Launch -Exe $jitExe -Label "jit-warm"
    Write-Host "Passed=true"
}
finally {
    Stop-Transcript | Out-Null
}

Write-Host ("Transcript={0}" -f $log)
