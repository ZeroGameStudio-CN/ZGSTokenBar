$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = [System.IO.Path]::GetFullPath((Join-Path $root 'release'))
$projectPath = Join-Path $root 'src\ZGSTokenBar.App\ZGSTokenBar.App.csproj'
$cliProjectPath = Join-Path $root 'tools\ZGSTokenBar.Cli\ZGSTokenBar.Cli.csproj'
$buildPropsPath = Join-Path $root 'Directory.Build.props'
$licensePath = Join-Path $root 'LICENSE'
$thirdPartyNoticesPath = Join-Path $root 'src\ZGSTokenBar.App\Assets\THIRD_PARTY_NOTICES.md'
[xml] $buildProps = Get-Content -LiteralPath $buildPropsPath
$version = @($buildProps.Project.PropertyGroup | ForEach-Object { $_.ZGSTokenBarVersion } | Where-Object { $_ })[0].Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props does not define ZGSTokenBarVersion.' }
$output = [System.IO.Path]::GetFullPath((Join-Path $release "ZGSTokenBar-v$version"))
$zip = [System.IO.Path]::GetFullPath((Join-Path $release "ZGSTokenBar-Portable-v$version.zip"))
$checksums = [System.IO.Path]::GetFullPath((Join-Path $release "ZGSTokenBar-v$version-SHA256.txt"))
$cliOutput = [System.IO.Path]::GetFullPath((Join-Path $release ".zgstokenbar-cli-v$version"))
$artifactsPath = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "zgstokenbar-dist-$([Guid]::NewGuid().ToString('N'))"))
$releasePrefix = $release.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$requireSignature = [string]::Equals($env:ZTB_REQUIRE_SIGNATURE, '1', [System.StringComparison]::Ordinal)

function Get-Sha256([string] $Path) {
  $stream = [System.IO.File]::OpenRead($Path)
  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
  } finally {
    $sha256.Dispose()
    $stream.Dispose()
  }
}

function Find-SignTool {
  $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($fromPath) { return $fromPath.Source }

  $kits = [Environment]::GetFolderPath('ProgramFilesX86')
  if (-not [string]::IsNullOrWhiteSpace($kits)) {
    $bin = Join-Path $kits 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $bin) {
      $candidate = Get-ChildItem -LiteralPath $bin -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
      if ($candidate) { return $candidate.FullName }
    }
  }

  return $null
}

function Resolve-CertificatePath {
  $link = @($env:WIN_CSC_LINK, $env:CSC_LINK) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
  if ([string]::IsNullOrWhiteSpace($link)) { return $null }
  $link = $link.Trim()
  if ($link -match '^file://') { $link = ([Uri]$link).LocalPath }
  if ($link -match '^https?://') {
    throw 'Signing requires a local certificate file; resolve remote CSC_LINK before packaging.'
  }
  $resolved = [System.IO.Path]::GetFullPath($link)
  if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "Native signing certificate file was not found: $resolved"
  }
  return $resolved
}

function Sign-And-Verify([string] $Path) {
  $certificate = Resolve-CertificatePath
  $password = @($env:WIN_CSC_KEY_PASSWORD, $env:CSC_KEY_PASSWORD) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
  if (-not $certificate -or [string]::IsNullOrWhiteSpace($password)) {
    if ($requireSignature) {
      throw 'Public packaging requires CSC_LINK/WIN_CSC_LINK and CSC_KEY_PASSWORD/WIN_CSC_KEY_PASSWORD.'
    }
    return 'NotSigned (self-use build)'
  }

  $signTool = Find-SignTool
  if (-not $signTool) { throw 'signtool.exe is required for native code signing.' }
  & $signTool sign /fd SHA256 /td SHA256 /tr 'http://timestamp.digicert.com' /f $certificate /p $password $Path
  if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE." }
  & $signTool verify /pa /all /tw $Path
  if ($LASTEXITCODE -ne 0) { throw "signtool timestamp verification failed with exit code $LASTEXITCODE." }
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Authenticode verification failed: $($signature.Status)"
  }
  return "Valid · $($signature.SignerCertificate.Subject)"
}

function Assert-Sha256([string] $Path, [string] $Expected) {
  $actual = Get-Sha256 $Path
  if (-not [string]::Equals($actual, $Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "SHA-256 verification failed for $Path"
  }
}

if (-not $output.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Output path escaped the release directory: $output"
}
if (-not $zip.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "ZIP path escaped the release directory: $zip"
}
if (-not $checksums.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Checksum path escaped the release directory: $checksums"
}
if (-not $cliOutput.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "CLI output path escaped the release directory: $cliOutput"
}

New-Item -ItemType Directory -Path $release -Force | Out-Null
if (Test-Path -LiteralPath $output) {
  Remove-Item -LiteralPath $output -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
  Remove-Item -LiteralPath $zip -Force
}
if (Test-Path -LiteralPath $checksums) {
  Remove-Item -LiteralPath $checksums -Force
}
if (Test-Path -LiteralPath $cliOutput) {
  Remove-Item -LiteralPath $cliOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsPath | Out-Null
try {
dotnet publish $projectPath `
  -c Release `
  -r win-x64 `
  --artifacts-path $artifactsPath `
  -o $output
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

$publishedFiles = @(Get-ChildItem -LiteralPath $output -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'ZGSTokenBar.exe') {
  throw "Expected a single portable executable, found: $($publishedFiles.Name -join ', ')"
}
$executable = $publishedFiles[0]

try {
  dotnet publish $cliProjectPath `
    -c Release `
    -r win-x64 `
    --artifacts-path $artifactsPath `
    -o $cliOutput
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  $cliFiles = @(Get-ChildItem -LiteralPath $cliOutput -File)
  if ($cliFiles.Count -ne 1 -or $cliFiles[0].Name -ne 'ZGSTokenBar.Cli.exe') {
    throw "Expected one native CLI executable, found: $($cliFiles.Name -join ', ')"
  }
  $cliExecutable = Join-Path $output 'ZGSTokenBar.Cli.exe'
  Copy-Item -LiteralPath $cliFiles[0].FullName -Destination $cliExecutable
} finally {
  if (Test-Path -LiteralPath $cliOutput) {
    Remove-Item -LiteralPath $cliOutput -Recurse -Force
  }
}

foreach ($notice in @(
  @{ Source = $licensePath; Destination = (Join-Path $output 'LICENSE') },
  @{ Source = $thirdPartyNoticesPath; Destination = (Join-Path $output 'THIRD_PARTY_NOTICES.md') }
)) {
  if (-not (Test-Path -LiteralPath $notice.Source -PathType Leaf)) {
    throw "Required release notice is missing: $($notice.Source)"
  }
  Copy-Item -LiteralPath $notice.Source -Destination $notice.Destination
}

$signature = Sign-And-Verify $executable.FullName
$cliSignature = Sign-And-Verify $cliExecutable
$packageFiles = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$expectedPackageFiles = @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'ZGSTokenBar.Cli.exe', 'ZGSTokenBar.exe')
if (($packageFiles.Count -ne $expectedPackageFiles.Count) -or (Compare-Object $expectedPackageFiles $packageFiles.Name)) {
  throw "Unexpected native package contents: $($packageFiles.Name -join ', ')"
}

Compress-Archive -LiteralPath $packageFiles.FullName -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
  $entries = @($archive.Entries)
  if (($entries.Count -ne $expectedPackageFiles.Count) -or (Compare-Object $expectedPackageFiles ($entries.FullName | Sort-Object))) {
    throw "Unexpected ZIP entries: $($entries.FullName -join ', ')"
  }
  $executableEntry = @($entries | Where-Object FullName -eq 'ZGSTokenBar.exe')[0]
  if ($executableEntry.Length -ne $executable.Length) {
    throw "ZIP entry length does not match the published executable."
  }
  $cliEntry = @($entries | Where-Object FullName -eq 'ZGSTokenBar.Cli.exe')[0]
  if ($cliEntry.Length -ne (Get-Item -LiteralPath $cliExecutable).Length) {
    throw "ZIP entry length does not match the published CLI."
  }
} finally {
  $archive.Dispose()
}

$executableHash = Get-Sha256 $executable.FullName
$cliHash = Get-Sha256 $cliExecutable
$zipHash = Get-Sha256 $zip
[System.IO.File]::WriteAllLines(
  $checksums,
  @(
    "$executableHash *ZGSTokenBar.exe",
    "$cliHash *ZGSTokenBar.Cli.exe",
    "$zipHash *$([System.IO.Path]::GetFileName($zip))"
  ),
  [System.Text.UTF8Encoding]::new($false))
Assert-Sha256 $executable.FullName $executableHash
Assert-Sha256 $cliExecutable $cliHash
Assert-Sha256 $zip $zipHash
} finally {
  if (Test-Path -LiteralPath $artifactsPath) {
    Remove-Item -LiteralPath $artifactsPath -Recurse -Force
  }
}

[pscustomobject]@{
  Version = $version
  Executable = $executable.FullName
  ExecutableMB = [math]::Round($executable.Length / 1MB, 2)
  ExecutableSHA256 = $executableHash
  Signature = $signature
  CliSignature = $cliSignature
  Zip = $zip
  ZipMB = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
  ZipSHA256 = $zipHash
  Checksums = $checksums
} | Format-List
