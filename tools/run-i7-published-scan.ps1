# Publish the x64 EXE and watch netstat -ano while it runs --scan on a TEMP
# OldInstall fixture (never C:\Windows.old). Fails if that PID opens a
# non-loopback TCP/UDP remote (SAFETY_MODEL I7).
#
# Requires an elevated session so the requireAdministrator EXE starts without
# a UAC prompt. Medium IL skips the matching integration test instead.
#
# Usage (elevated PowerShell from repo root):
#   powershell -File tools/run-i7-published-scan.ps1

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run in an elevated Administrator session so WinOldRecovery.exe can start."
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$log = Join-Path $env:TEMP ("WinOldRecovery-i7-scan-" + $stamp + ".log")
$publishDir = Join-Path $env:TEMP "wor-publish-i7"
$work = Join-Path $env:TEMP ("WinOldRecovery-I7-scan-" + $stamp)
$source = Join-Path $work "OldInstall"
$report = Join-Path $work "report.txt"
$csproj = Join-Path $root "src\WinOldRecovery.App\WinOldRecovery.App.csproj"

function Test-RemoteEndpoint([string] $NetstatLine, [int] $ProcessId) {
    $parts = $NetstatLine.Trim() -split "\s+"
    if ($parts.Length -lt 4) { return $false }
    $proto = $parts[0]
    if ($proto -ne "TCP" -and $proto -ne "UDP") { return $false }
    if ($parts[-1] -ne [string]$ProcessId) { return $false }
    $foreign = $parts[2]
    if ($foreign -eq "*:*") { return $false }
    if ($foreign.StartsWith("0.0.0.0:") -or $foreign.StartsWith("[::]:") -or $foreign.StartsWith("[::0]:")) { return $false }
    if ($foreign.StartsWith("127.0.0.1:") -or $foreign.StartsWith("[::1]:")) { return $false }
    if ($proto -eq "TCP") {
        if ($parts.Length -lt 5) { return $false }
        $state = $parts[3]
        if ($state -ne "ESTABLISHED" -and $state -ne "SYN_SENT" -and $state -ne "SYN_RECEIVED") { return $false }
    }
    return $true
}

Start-Transcript -Path $log | Out-Null
$code = 1
$proc = $null
try {
    Write-Host ("Log={0}" -f $log)
    New-Item -ItemType Directory -Path (Join-Path $source "Users\Alice\Desktop") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $source "Users\Alice\Desktop\note.txt") -Value "i7" -Encoding utf8

    & $dotnet publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $exe = Join-Path $publishDir "WinOldRecovery.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "Published EXE was not found at $exe" }

    $proc = Start-Process -FilePath $exe -ArgumentList @("--scan", $source, "--report", $report) -PassThru -WindowStyle Hidden
    $remote = New-Object System.Collections.Generic.List[string]
    $deadline = [datetime]::UtcNow.AddMinutes(2)
    while (-not $proc.HasExited) {
        if ([datetime]::UtcNow -gt $deadline) {
            throw "Published --scan exceeded two minutes."
        }
        $netstat = & netstat.exe -ano
        foreach ($line in $netstat) {
            if (Test-RemoteEndpoint $line $proc.Id) {
                $remote.Add($line)
            }
        }
        Start-Sleep -Milliseconds 50
    }

    $netstat = & netstat.exe -ano
    foreach ($line in $netstat) {
        if (Test-RemoteEndpoint $line $proc.Id) {
            $remote.Add($line)
        }
    }

    Write-Host ("ExitCode={0}" -f $proc.ExitCode)
    if ($proc.ExitCode -ne 0) { throw "Published --scan exited $($proc.ExitCode)." }
    if (-not (Test-Path -LiteralPath $report)) { throw "Scan report was not written." }
    if ($remote.Count -gt 0) {
        throw ("Published --scan opened remote sockets: " + ($remote -join "; "))
    }

    Write-Host "Passed=true"
    $code = 0
}
catch {
    Write-Host $_
    $code = 1
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    Stop-Transcript | Out-Null
    Write-Host ("Transcript={0}" -f $log)
}
exit $code
