#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string] $WindowsSdkVersion = '10.0.26100.0',
    [switch] $Preview
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'SlotFixture.exe'
$obj = Join-Path $output 'slot-fixture.obj'
if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $obj)) {
    throw 'Use a new fixture output directory.'
}

$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vs) { throw 'Visual Studio C++ x64 build tools are required.' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$kit = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10'
$includes = @('um', 'shared', 'ucrt', 'cppwinrt', 'winrt' | ForEach-Object { Join-Path $kit "Include/$WindowsSdkVersion/$_" })
$libraries = @('um', 'ucrt' | ForEach-Object { Join-Path $kit "Lib/$WindowsSdkVersion/$_/x64" })
$sdkTools = Join-Path $kit "bin/$WindowsSdkVersion/x64"
foreach ($path in ($includes + $libraries + $sdkTools)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Missing Windows SDK directory: $path" }
}
$oldPath = $env:PATH
$oldInclude = $env:INCLUDE
$oldLib = $env:LIB
try {
    $env:INCLUDE = (@($oldInclude) + $includes) -join ';'
    $env:LIB = (@($oldLib) + $libraries) -join ';'
    $env:PATH = "$sdkTools;$oldPath"
    $source = Join-Path $PSScriptRoot 'slot-fixture.cpp'
    $manifest = Join-Path $PSScriptRoot 'fixture.manifest'
    $arguments = @('/nologo', '/std:c++20', '/EHsc', '/O1', '/MT', '/W4', '/DUNICODE', '/D_UNICODE',
        "/Fo$obj", "/Fe$exe", $source, '/link', 'WindowsApp.lib', 'user32.lib', 'ole32.lib',
        'oleaut32.lib', '/MANIFEST:EMBED', "/MANIFESTINPUT:$manifest")
    & cl.exe @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($Preview) { & $exe --preview } else { & $exe }
    exit $LASTEXITCODE
} finally {
    $env:PATH = $oldPath
    $env:INCLUDE = $oldInclude
    $env:LIB = $oldLib
}
