# 200 MB VHDX destination disk-full check (PENDING_MANUAL / I16).
# Requires Administrator. Does not run cleanmgr.
#
# Usage (from repo root, Administrator):
#   powershell -File tools/run-disk-full-vhdx.ps1

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run in an elevated Administrator session."
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$work = Join-Path $env:TEMP ("WinOldRecovery-diskfull-" + $stamp)
$vhdx = Join-Path $work "dest.vhdx"
$mount = Join-Path $work "mount"
$source = Join-Path $work "Windows.old"
$log = Join-Path $env:TEMP ("WinOldRecovery-diskfull-" + $stamp + ".log")
$attached = $false
New-Item -ItemType Directory -Path $work, $mount, $source | Out-Null
Start-Transcript -Path $log | Out-Null
try {
    Write-Host "Building RestoreHarness (Release)..."
    & $dotnet build (Join-Path $root "tools\RestoreHarness\RestoreHarness.csproj") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "RestoreHarness build failed." }

    Write-Host "Writing source payload and creating 200 MB VHDX..."
    $canary = Join-Path $source "canary.txt"
    Set-Content -Path $canary -Value ("canary-" + $stamp) -NoNewline
    $payload = Join-Path $source "payload.bin"
    $buffer = New-Object byte[] (1024 * 1024)
    $stream = [System.IO.File]::Open($payload, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        for ($i = 0; $i -lt 40; $i++) { $stream.Write($buffer, 0, $buffer.Length) }
    }
    finally { $stream.Dispose() }

    $dp = Join-Path $work "diskpart.txt"
    @"
create vdisk file="$vhdx" maximum=200 type=expandable
select vdisk file="$vhdx"
attach vdisk
create partition primary
format fs=ntfs quick label=WORDF
assign mount="$mount"
"@ | Set-Content -Path $dp -Encoding ascii
    $dpOut = & diskpart /s $dp
    $dpOut | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "diskpart create/attach failed." }
    $attached = $true

    $marker = Join-Path $mount "keep-me.txt"
    Set-Content -Path $marker -Value "do-not-delete" -NoNewline

    $dll = Join-Path $root "tools\RestoreHarness\bin\Release\net10.0-windows\RestoreHarness.dll"
    Write-Host "Running RestoreHarness --probe-disk-full..."
    & $dotnet $dll --probe-disk-full $source $mount
    if ($LASTEXITCODE -ne 0) { throw "Disk-full probe failed." }

    Write-Host "200 MB VHDX disk-full probe passed. Transcript: $log"
    Write-Host "Work directory: $work"
}
finally {
    if ($attached) {
        $detach = Join-Path $work "diskpart-detach.txt"
        @"
select vdisk file="$vhdx"
detach vdisk
"@ | Set-Content -Path $detach -Encoding ascii
        & diskpart /s $detach | Out-Null
    }
    Stop-Transcript | Out-Null
}
