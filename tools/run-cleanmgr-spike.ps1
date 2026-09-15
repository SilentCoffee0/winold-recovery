# Arm Previous Installations StateFlags0777, run cleanmgr /sagerun:777, poll
# Windows.old disappearance, then remove the flag (PENDING_MANUAL / M0 spike e).
#
# This is the same sage id PurgeExecutor uses. It is a real Disk Cleanup run.
# Refuse unless WOR_CLEANMGR_CONFIRM=SETUP_CREATED_WINDOWS_OLD so a leftover
# C:\Windows.old on a development machine cannot be deleted by accident.
#
# Usage (elevated PowerShell on a disposable Windows 11 VM that already has a
# setup-created Windows.old):
#   $env:WOR_CLEANMGR_CONFIRM='SETUP_CREATED_WINDOWS_OLD'
#   powershell -File tools/run-cleanmgr-spike.ps1
#   powershell -File tools/run-cleanmgr-spike.ps1 -WindowsOldPath 'D:\Windows.old'

param(
    [string] $WindowsOldPath = "C:\Windows.old"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run in an elevated Administrator session."
}

if ($env:WOR_CLEANMGR_CONFIRM -ne "SETUP_CREATED_WINDOWS_OLD") {
    Write-Error @"
Refusing to run cleanmgr. Set WOR_CLEANMGR_CONFIRM=SETUP_CREATED_WINDOWS_OLD only
on a disposable VM whose Windows.old was created by Windows Setup. A fake folder
and this development machine's leftover C:\Windows.old are not valid targets.
"@
}

$windowsOld = [IO.Path]::GetFullPath($WindowsOldPath)
if (-not (Test-Path -LiteralPath $windowsOld)) {
    throw "Windows.old was not found at $windowsOld"
}

$volumeCaches = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches"
$previousKey = Join-Path $volumeCaches "Previous Installations"
if (-not (Test-Path -LiteralPath $previousKey)) {
    throw "Previous Installations volume cache key is missing. This is not a valid cleanup-handler host."
}

$stamp = [guid]::NewGuid().ToString("N")
$log = Join-Path $env:TEMP ("WinOldRecovery-cleanmgr-" + $stamp + ".log")
$marker = Join-Path $env:TEMP ("WinOldRecovery-cleanmgr-keep-me-" + $stamp + ".txt")
$sageName = "StateFlags0777"
$beforeFlags = @{}

function Get-VolumeCacheFlags {
    $map = @{}
    Get-ChildItem -LiteralPath $volumeCaches | ForEach-Object {
        $name = $_.PSChildName
        $value = (Get-ItemProperty -LiteralPath $_.PSPath -Name $sageName -ErrorAction SilentlyContinue).$sageName
        if ($null -ne $value) {
            $map[$name] = [int]$value
        }
    }
    return $map
}

Start-Transcript -Path $log | Out-Null
try {
    Set-Content -Path $marker -Value "do not delete" -Encoding UTF8
    $beforeFlags = Get-VolumeCacheFlags
    Write-Host ("WindowsOld={0}" -f $windowsOld)
    Write-Host ("Created={0}" -f (Get-Item -LiteralPath $windowsOld).CreationTime)
    Write-Host ("Marker={0}" -f $marker)
    Write-Host ("ExistingStateFlags0777Count={0}" -f $beforeFlags.Count)

    Set-ItemProperty -LiteralPath $previousKey -Name $sageName -Value 2 -Type DWord
    Write-Host "Armed Previous Installations StateFlags0777=2"

    $started = Get-Date
    $proc = Start-Process -FilePath "$env:SystemRoot\System32\cleanmgr.exe" -ArgumentList @("/sagerun:777") -PassThru
    Write-Host ("CleanmgrPid={0}" -f $proc.Id)
    $goneAt = $null
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        if (-not (Test-Path -LiteralPath $windowsOld) -and $null -eq $goneAt) {
            $goneAt = Get-Date
            Write-Host ("WindowsOldGoneAfterSeconds={0:N1}" -f (($goneAt - $started).TotalSeconds))
        }
    }

    $exitSeconds = ((Get-Date) - $started).TotalSeconds
    Write-Host ("CleanmgrExit={0}" -f $proc.ExitCode)
    Write-Host ("CleanmgrExitSeconds={0:N1}" -f $exitSeconds)
    if (Test-Path -LiteralPath $windowsOld) {
        Write-Host "WindowsOldStillPresent=true"
    }
    else {
        if ($null -eq $goneAt) {
            Write-Host ("WindowsOldGoneAfterSeconds={0:N1}" -f $exitSeconds)
        }
        Write-Host "WindowsOldStillPresent=false"
    }

    Remove-ItemProperty -LiteralPath $previousKey -Name $sageName -ErrorAction SilentlyContinue
    Write-Host "Disarmed Previous Installations StateFlags0777"

    $afterFlags = Get-VolumeCacheFlags
    $leaked = @($afterFlags.Keys | Where-Object { $_ -ne "Previous Installations" -and -not $beforeFlags.ContainsKey($_) })
    $changed = @()
    foreach ($key in $beforeFlags.Keys) {
        if ($key -eq "Previous Installations") { continue }
        if (-not $afterFlags.ContainsKey($key)) { continue }
        if ([int]$afterFlags[$key] -ne [int]$beforeFlags[$key]) {
            $changed += $key
        }
    }

    $markerIntact = Test-Path -LiteralPath $marker
    Write-Host ("MarkerIntact={0}" -f $markerIntact)
    Write-Host ("LeakedStateFlags0777={0}" -f ($leaked -join ","))
    Write-Host ("ChangedUnrelatedFlags={0}" -f ($changed -join ","))

    $passed = $markerIntact -and $leaked.Count -eq 0 -and $changed.Count -eq 0 -and -not (Test-Path -LiteralPath $windowsOld)
    Write-Host ("Passed={0}" -f $passed.ToString().ToLowerInvariant())
    if (-not $passed) {
        throw "cleanmgr spike did not pass. See $log"
    }
}
finally {
    try {
        Remove-ItemProperty -LiteralPath $previousKey -Name $sageName -ErrorAction SilentlyContinue
    }
    catch { }
    Stop-Transcript | Out-Null
}

Write-Host ("Transcript={0}" -f $log)
