# Open a >260-character fixture the same way the app does, then confirm Explorer
# selected the nearest ancestor under 259 characters (PENDING_MANUAL).
#
# Usage (from repo root, interactive desktop):
#   powershell -File tools/run-explorer-long-path.ps1

$ErrorActionPreference = "Stop"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stamp = [guid]::NewGuid().ToString("N")
$log = Join-Path $env:TEMP ("WinOldRecovery-explorer-longpath-" + $stamp + ".log")
$work = Join-Path $env:TEMP ("WinOldRecovery-ExplorerLong-" + $stamp)
$csproj = Join-Path $env:TEMP ("wor-explorer-select-" + $stamp)
$selectFile = Join-Path $env:TEMP ("wor-explorer-select-" + $stamp + ".txt")

Start-Transcript -Path $log | Out-Null
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $current = $work
    while (([IO.Path]::Combine($current, "deep-file.txt")).Length -lt 310) {
        $current = [IO.Path]::Combine(
            $current,
            ("segment-{0:D4}-abcdefghijklmnopqrstuvwxyz" -f $current.Length))
        [IO.Directory]::CreateDirectory("\\?\$current") | Out-Null
    }

    $leaf = [IO.Path]::Combine($current, "deep-file.txt")
    [IO.File]::WriteAllText("\\?\$leaf", "long path fixture")
    Write-Host ("LeafLength={0}" -f $leaf.Length)
    Write-Host ("Leaf={0}" -f $leaf)
    if ($leaf.Length -le 260) {
        throw "The fixture path was not longer than 260 characters."
    }

    New-Item -ItemType Directory -Path $csproj | Out-Null
    Set-Content -Path (Join-Path $csproj "wor.csproj") -Encoding UTF8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$root\src\WinOldRecovery.Core\WinOldRecovery.Core.csproj" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $csproj "Program.cs") -Encoding UTF8 -Value @"
using WinOldRecovery.Core.IO;
string leaf = args[0];
string argument = ExplorerSelect.BuildSelectArgument(leaf);
File.WriteAllText(args[1], argument);
Console.WriteLine(argument);
"@

    $proj = Join-Path $csproj "wor.csproj"
    & $dotnet build $proj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build ExplorerSelect helper failed." }
    $helper = Join-Path $csproj "bin\Release\net10.0-windows\wor.exe"
    & $helper $leaf $selectFile
    if ($LASTEXITCODE -ne 0) { throw "ExplorerSelect helper failed." }

    $argument = (Get-Content -Raw $selectFile).Trim()
    if (-not $argument.StartsWith("/select,")) {
        throw "ExplorerSelect did not return a /select, argument."
    }

    $selected = $argument.Substring("/select,".Length)
    Write-Host ("SelectArgument={0}" -f $argument)
    Write-Host ("SelectedLength={0}" -f $selected.Length)
    if ($selected.Length -gt 259) {
        throw "ExplorerSelect returned a path longer than 259 characters."
    }
    if (-not $leaf.StartsWith($selected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Selected ancestor is not a prefix of the leaf."
    }

    $beforeHwnds = @{}
    $shell = New-Object -ComObject Shell.Application
    foreach ($window in @($shell.Windows())) {
        try { $beforeHwnds[[int]$window.HWND] = $true } catch { }
    }

    $explorer = Start-Process -FilePath "explorer.exe" -ArgumentList @($argument) -PassThru
    Write-Host ("ExplorerPid={0}" -f $explorer.Id)

    $match = $null
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 400
        $shell = New-Object -ComObject Shell.Application
        foreach ($window in @($shell.Windows())) {
            try {
                $hwnd = [int]$window.HWND
                if ($beforeHwnds.ContainsKey($hwnd)) { continue }
                $items = @($window.Document.SelectedItems())
                foreach ($item in $items) {
                    if ([string]::Equals($item.Path, $selected, [StringComparison]::OrdinalIgnoreCase)) {
                        $match = [PSCustomObject]@{
                            Hwnd = $hwnd
                            Location = $window.LocationURL
                            Selected = $item.Path
                            Name = $item.Name
                        }
                        break
                    }
                }
                if ($match) { break }
            }
            catch { }
        }
    } while ((-not $match) -and (Get-Date) -lt $deadline)

    $passed = $null -ne $match
    Write-Host ("Passed={0}" -f $passed.ToString().ToLowerInvariant())
    if ($match) {
        Write-Host ("ExplorerHwnd={0}" -f $match.Hwnd)
        Write-Host ("ExplorerLocation={0}" -f $match.Location)
        Write-Host ("SelectedPath={0}" -f $match.Selected)
        Write-Host ("SelectedName={0}" -f $match.Name)
        try {
            $shell = New-Object -ComObject Shell.Application
            foreach ($window in @($shell.Windows())) {
                try {
                    if ([int]$window.HWND -eq [int]$match.Hwnd) {
                        $window.Quit()
                        break
                    }
                }
                catch { }
            }
        }
        catch { }
    }
    else {
        Write-Host "Explorer did not report the selected ancestor within 20 seconds."
        throw "Explorer long-path selection was not observed."
    }
}
finally {
    Stop-Transcript | Out-Null
    if (Test-Path $csproj) { Remove-Item $csproj -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $selectFile) { Remove-Item $selectFile -Force -ErrorAction SilentlyContinue }
    if (Test-Path $work) {
        cmd /c ("rmdir /s /q \\?\{0}" -f $work) | Out-Null
    }
}

Write-Host ("Transcript={0}" -f $log)
