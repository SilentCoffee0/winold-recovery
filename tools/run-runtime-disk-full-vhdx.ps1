# Runtime ERROR_DISK_FULL during CopyTree, then resume (PENDING_MANUAL / I16 step 4).
# Destination is large enough to pass preflight (plan + 5% + 1 GB), then a filler
# file consumes the margin so copy pauses. The filler is deleted (not user data)
# and restore resumes. Does not run cleanmgr. Does not touch C:\Windows.old.
#
# Usage (from repo root):
#   powershell -File tools/run-runtime-disk-full-vhdx.ps1

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
    Write-Host "Requesting Administrator for diskpart..."
    $hostExe = Join-Path $PSHOME "powershell.exe"
    $p = Start-Process -FilePath $hostExe -Verb RunAs -Wait -PassThru -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath)
    if (-not $p) { throw "UAC elevation was declined." }
    exit $p.ExitCode
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$work = Join-Path $env:TEMP ("WinOldRecovery-runtime-diskfull-" + $stamp)
$vhdx = Join-Path $work "dest.vhdx"
$mount = Join-Path $work "mount"
$source = Join-Path $work "Windows.old"
$log = Join-Path $env:TEMP ("WinOldRecovery-runtime-diskfull-" + $stamp + ".log")
$attached = $false
New-Item -ItemType Directory -Path $work, $mount, $source | Out-Null
Start-Transcript -Path $log | Out-Null
try {
    Write-Host "Building RestoreHarness (Release)..."
    & $dotnet build (Join-Path $root "tools\RestoreHarness\RestoreHarness.csproj") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "RestoreHarness build failed." }

    Write-Host "Writing 80 MB source payload..."
    Set-Content -Path (Join-Path $source "canary.txt") -Value ("canary-" + $stamp) -NoNewline
    $payload = Join-Path $source "payload.bin"
    $buffer = New-Object byte[] (1024 * 1024)
    $stream = [System.IO.File]::Open($payload, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        for ($i = 0; $i -lt 80; $i++) { $stream.Write($buffer, 0, $buffer.Length) }
    }
    finally { $stream.Dispose() }

    Write-Host "Creating 1400 MB VHDX (must pass 1 GB I16 preflight)..."
    $dp = Join-Path $work "diskpart.txt"
    @"
create vdisk file="$vhdx" maximum=1400 type=expandable
select vdisk file="$vhdx"
attach vdisk
create partition primary
format fs=ntfs quick label=WORRT
assign mount="$mount"
"@ | Set-Content -Path $dp -Encoding ascii
    $dpOut = & diskpart /s $dp
    $dpOut | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "diskpart create/attach failed." }
    $attached = $true

    Set-Content -Path (Join-Path $mount "keep-me.txt") -Value "do-not-delete" -NoNewline

    $dll = Join-Path $root "tools\RestoreHarness\bin\Release\net10.0-windows\RestoreHarness.dll"
    Write-Host "Running RestoreHarness --probe-runtime-disk-full..."
    & $dotnet $dll --probe-runtime-disk-full $source $mount
    if ($LASTEXITCODE -ne 0) { throw "Runtime disk-full probe failed." }

    Write-Host "Runtime disk-full pause+resume passed. Transcript: $log"
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
