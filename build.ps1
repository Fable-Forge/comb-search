# Comb build entry. Keep this file ASCII only:
# Windows PowerShell 5.1 reads BOM-less scripts as the ANSI code page (GBK on Chinese Windows).
# Usage (from the repository root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 [-Csc <path to csc.exe>] [-RunTests]
# Without -Csc, the Roslyn csc.exe of the newest Visual Studio / Build Tools install is located with vswhere.
param([string]$Csc = '', [switch]$RunTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$programFilesX86 = ${env:ProgramFiles(x86)}
$refDir = Join-Path $programFilesX86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$bin = Join-Path $root 'bin'

if (-not $Csc) {
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw ('vswhere.exe not found: ' + $vswhere + '. Install Visual Studio 2022 Build Tools, or pass -Csc <path to csc.exe>.') }
    $Csc = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe') | Select-Object -First 1
    if (-not $Csc) { throw 'Roslyn csc.exe not found by vswhere. Install the MSBuild component of Visual Studio Build Tools, or pass -Csc <path to csc.exe>.' }
}
if (-not (Test-Path -LiteralPath $Csc)) { throw ('csc.exe not found: ' + $Csc) }
if (-not (Test-Path -LiteralPath $refDir)) { throw ('.NET Framework 4.8 reference assemblies not found: ' + $refDir + '. Install the .NET Framework 4.8 targeting pack.') }
Write-Output ('Using ' + $Csc)

# A running Comb.exe locks the output file.
$running = Get-Process -Name 'Comb' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'bin\Comb.exe is running. Quit it from the tray menu (or stop the process) first.' }

if (-not (Test-Path -LiteralPath $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

function Get-Refs([string[]]$names) {
    $list = @()
    foreach ($n in $names) {
        $p = Join-Path $refDir ($n + '.dll')
        if (-not (Test-Path -LiteralPath $p)) { throw ('reference not found: ' + $p) }
        $list += ('/reference:' + $p)
    }
    return $list
}

$common = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:latest', '/codepage:65001', '/optimize+', '/utf8output')
$appRefs = Get-Refs @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms',
                      'WindowsBase', 'PresentationCore', 'PresentationFramework', 'System.Xaml')
$probeRefs = Get-Refs @('mscorlib', 'System', 'System.Core')
$iconRefs = Get-Refs @('mscorlib', 'System', 'System.Drawing')

# App icon: tools\IconGen.cs draws it and writes bin\Comb.ico, embedded below with /win32icon
$iconGen = Join-Path $bin 'IconGen.exe'
$icon = Join-Path $bin 'Comb.ico'
Write-Output 'Compiling IconGen.exe'
& $Csc @common @iconRefs '/target:exe' ('/out:' + $iconGen) (Join-Path $root 'tools\IconGen.cs')
if ($LASTEXITCODE -ne 0) { throw ('IconGen.exe build failed, csc exit ' + $LASTEXITCODE) }
& $iconGen $icon
if ($LASTEXITCODE -ne 0) { throw ('IconGen.exe failed, exit ' + $LASTEXITCODE) }

# Main app: everything under src\
$appSrc = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
# Probe: tools\Probe.cs + the engine module only (the engine must not depend on WPF/WinForms)
$engineSrc = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Engine') -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$probeSrc = @((Join-Path $root 'tools\Probe.cs')) + $engineSrc

Write-Output ('Compiling Comb.exe (' + $appSrc.Count + ' files)')
& $Csc @common @appRefs '/target:winexe' ('/win32icon:' + $icon) ('/out:' + (Join-Path $bin 'Comb.exe')) @appSrc
if ($LASTEXITCODE -ne 0) { throw ('Comb.exe build failed, csc exit ' + $LASTEXITCODE) }

Write-Output ('Compiling Probe.exe (' + $probeSrc.Count + ' files)')
& $Csc @common @probeRefs '/target:exe' ('/out:' + (Join-Path $bin 'Probe.exe')) @probeSrc
if ($LASTEXITCODE -ne 0) { throw ('Probe.exe build failed, csc exit ' + $LASTEXITCODE) }

if ($RunTests) {
    $testRefs = Get-Refs @('mscorlib', 'System', 'System.Core')
    $testSuites = @(
        @{ Name = 'HotkeyRegressionTests'; Platform = 'anycpu'; Sources = @('src\App\DoubleCtrlHook.cs', 'tools\HotkeyRegressionTests.cs') },
        @{ Name = 'HotkeyRegressionTests-x86'; Platform = 'x86'; Sources = @('src\App\DoubleCtrlHook.cs', 'tools\HotkeyRegressionTests.cs') },
        @{ Name = 'WindowPlacementRegressionTests'; Platform = 'anycpu'; Sources = @('src\App\WindowPlacement.cs', 'tools\WindowPlacementRegressionTests.cs') }
    )
    foreach ($suite in $testSuites) {
        $testExe = Join-Path $bin ($suite.Name + '.exe')
        $testSources = @($suite.Sources | ForEach-Object { Join-Path $root $_ })
        Write-Output ('Compiling ' + $suite.Name)
        & $Csc @common @testRefs '/target:exe' ('/platform:' + $suite.Platform) ('/out:' + $testExe) @testSources
        if ($LASTEXITCODE -ne 0) { throw ($suite.Name + ' build failed, csc exit ' + $LASTEXITCODE) }
        & $testExe
        if ($LASTEXITCODE -ne 0) { throw ($suite.Name + ' failed, exit ' + $LASTEXITCODE) }
    }
}

Get-Item -LiteralPath (Join-Path $bin 'Comb.exe'), (Join-Path $bin 'Probe.exe') |
    ForEach-Object { Write-Output ('  ' + $_.Name + '  ' + $_.Length + ' bytes') }
$appVersion = (Get-Item -LiteralPath (Join-Path $bin 'Comb.exe')).VersionInfo
Write-Output ('Comb version: ' + $appVersion.ProductVersion + ' (file ' + $appVersion.FileVersion + ')')
Write-Output 'BUILD OK'
