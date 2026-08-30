param(
  [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\ProcessPlugins\ZGSTokenBar.Plugin.AiGatewayObserver\ZGSTokenBar.Plugin.AiGatewayObserver.csproj'
$pluginSource = Split-Path -Parent $project
$iconSource = Join-Path $root 'src\ZGSTokenBar.App\Assets\deepseek-whale-icon.png'
$version = '1.2.3'
$release = Join-Path $root 'release'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  $OutputPath = Join-Path $release "ZGSTokenBar.Plugin.AiGatewayObserver-v$version.zgsplugin"
}
$package = [System.IO.Path]::GetFullPath($OutputPath)
if (-not [string]::Equals([System.IO.Path]::GetExtension($package), '.zgsplugin', [System.StringComparison]::OrdinalIgnoreCase)) {
  throw 'Plugin package output must use the .zgsplugin extension.'
}
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "zgstokenbar-ai-gateway-plugin-$([Guid]::NewGuid().ToString('N'))"))
$publish = Join-Path $temporaryRoot 'publish'
$staging = Join-Path $temporaryRoot 'package'
$artifacts = Join-Path $temporaryRoot 'artifacts'

function Get-Sha256([string] $Path) {
  $stream = [System.IO.File]::OpenRead($Path)
  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
  } finally {
    $sha256.Dispose()
    $stream.Dispose()
  }
}

function New-FileDeclaration([string] $Root, [string] $RelativePath) {
  $path = Join-Path $Root $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
  $item = Get-Item -LiteralPath $path
  return [ordered]@{
    path = $RelativePath
    bytes = $item.Length
    sha256 = Get-Sha256 $path
  }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $package) -Force | Out-Null
New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging 'locales') -Force | Out-Null
try {
  dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --artifacts-path $artifacts `
    -o $publish
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  $publishedFiles = @(Get-ChildItem -LiteralPath $publish -File)
  if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'ZGSTokenBar.Plugin.AiGatewayObserver.exe') {
    throw "Expected one native plugin executable, found: $($publishedFiles.Name -join ', ')"
  }
  Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination (Join-Path $staging $publishedFiles[0].Name)
  Copy-Item -LiteralPath $iconSource -Destination (Join-Path $staging 'icon.png')
  Copy-Item -LiteralPath (Join-Path $pluginSource 'locales\en.json') -Destination (Join-Path $staging 'locales\en.json')
  Copy-Item -LiteralPath (Join-Path $pluginSource 'locales\zh-CN.json') -Destination (Join-Path $staging 'locales\zh-CN.json')

  $declaredPaths = @(
    'ZGSTokenBar.Plugin.AiGatewayObserver.exe',
    'icon.png',
    'locales/en.json',
    'locales/zh-CN.json'
  )
  $manifest = [ordered]@{
    schemaVersion = 1
    id = 'zgstokenbar.provider.ai-gateway'
    version = $version
    hostApiMajor = 1
    hostApiMinMinor = 0
    runtime = 'process'
    required = $false
    commandNamespace = 'ai-gateway'
    capabilities = @('balance', 'local-credentials')
    defaultEnabled = $false
    order = 400
    requires = @()
    displayName = 'DeepSeek Harness'
    entrypoint = 'ZGSTokenBar.Plugin.AiGatewayObserver.exe'
    files = @($declaredPaths | ForEach-Object { New-FileDeclaration $staging $_ })
    icon = 'icon.png'
    locales = @('locales/en.json', 'locales/zh-CN.json')
    credentialSlots = @()
    handshakeTimeoutSeconds = 5
    callTimeoutSeconds = 12
    disposeTimeoutSeconds = 2
  }
  $manifestPath = Join-Path $staging 'plugin-manifest.v1.json'
  [System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

  if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($relative in @('plugin-manifest.v1.json') + $declaredPaths) {
      $source = Join-Path $staging $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
      [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $archive,
        $source,
        $relative,
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
  } finally {
    $archive.Dispose()
  }

  $packageItem = Get-Item -LiteralPath $package
  [pscustomobject]@{
    Package = $packageItem.FullName
    Bytes = $packageItem.Length
    SHA256 = Get-Sha256 $packageItem.FullName
  } | Format-List
} finally {
  $temporaryPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
  if ($temporaryRoot.StartsWith($temporaryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $temporaryRoot)) {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
  }
}
