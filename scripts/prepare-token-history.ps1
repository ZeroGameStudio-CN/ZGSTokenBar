param(
  [Parameter(Mandatory)][string] $PackageRoot,
  [Parameter(Mandatory)][string] $IndexPath,
  [Parameter(Mandatory)][string] $StagingRoot,
  [string] $DependencyIdsPath,
  [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe'
)
$ErrorActionPreference = 'Stop'
$packageRootPath = [IO.Path]::GetFullPath($PackageRoot)
$stagingPath = [IO.Path]::GetFullPath($StagingRoot)
if ($stagingPath.StartsWith($packageRootPath, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Staging must be separate from the original archive.'
}
if (Test-Path -LiteralPath $stagingPath) {
  if (-not (Test-Path -LiteralPath (Join-Path $stagingPath '.token-history-staging'))) {
    throw 'Existing staging directory is not owned by this importer.'
  }
} else {
  New-Item -ItemType Directory -Path $stagingPath | Out-Null
  New-Item -ItemType File -Path (Join-Path $stagingPath '.token-history-staging') | Out-Null
}
$known = @{}
$index = Get-Content -Raw -LiteralPath $IndexPath | ConvertFrom-Json
foreach ($file in $index.files) { $known[$file.key] = $file }
$dependencies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($DependencyIdsPath) {
  foreach ($id in [IO.File]::ReadAllLines($DependencyIdsPath)) {
    if ($id -notmatch '^[a-fA-F0-9-]{36}$') { throw 'Invalid dependency session ID.' }
    [void]$dependencies.Add($id)
  }
}
$manifestFiles = @(Get-ChildItem -LiteralPath $packageRootPath -Filter '*.manifest.json' -File -Recurse)
$prepared = 0
$skipped = 0
foreach ($manifestFile in $manifestFiles) {
  $manifest = Get-Content -Raw -LiteralPath $manifestFile.FullName | ConvertFrom-Json
  $selected = @($manifest.files | Where-Object {
    $name = [IO.Path]::GetFileName($_.relative_path) -replace '\.jsonl\.bak$', '.jsonl'
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
      [Text.Encoding]::UTF8.GetBytes($name))).ToLowerInvariant()
    $old = $known[$key]
    $dependency = $name -match '([a-fA-F0-9-]{36})\.jsonl$' -and $dependencies.Contains($Matches[1])
    $dependency -or -not $old -or $old.accountingVersion -ne 2 -or $old.length -lt $_.bytes
  })
  if ($selected.Count -eq 0) { $skipped++; continue }
  $package = $manifestFile.FullName.Substring(0, $manifestFile.FullName.Length - '.manifest.json'.Length)
  $digest = $manifest.package_sha256
  if ($digest -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid package digest.' }
  $destination = Join-Path $stagingPath "archived_sessions/$digest"
  $receipt = Join-Path $stagingPath "$digest.complete.json"
  foreach ($file in $selected) {
    $relative = [string]$file.relative_path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)|[\r\n:*?]' -or
        $relative -notmatch '\.jsonl(\.bak)?$') { throw 'Unsafe archive entry.' }
  }
  $verified = Test-Path -LiteralPath $receipt
  if ($verified) {
    foreach ($file in $selected) {
      $path = Join-Path $destination $file.relative_path
      if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) {
        $verified = $false
        break
      }
    }
  }
  if (-not $verified) {
    Write-Output "Reading $([IO.Path]::GetFileName($package)): $($selected.Count) sessions"
    if ((Get-FileHash -LiteralPath $package).Hash -ne $digest) { throw 'Package checksum mismatch.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $list = Join-Path $stagingPath "$digest.entries.txt"
    [IO.File]::WriteAllLines($list, [string[]]($selected | ForEach-Object { $_.relative_path }), [Text.UTF8Encoding]::new($false))
    & $SevenZip x -y -bd -bso0 -bsp0 -spd -scsUTF-8 "-o$destination" $package "@$list"
    if ($LASTEXITCODE -ne 0) { throw "Archive extraction failed: $LASTEXITCODE" }
    foreach ($file in $selected) {
      $path = Join-Path $destination $file.relative_path
      if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or
          (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw 'Extracted session verification failed.' }
      if ($path.EndsWith('.jsonl.bak', [StringComparison]::OrdinalIgnoreCase)) {
        $session = $path.Substring(0, $path.Length - 4)
        if (-not (Test-Path -LiteralPath $session)) { Copy-Item -LiteralPath $path -Destination $session }
      }
    }
    [IO.File]::WriteAllText($receipt, ($selected | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
  }
  $prepared += $selected.Count
}
[pscustomobject]@{ Manifests = $manifestFiles.Count; SkippedPackages = $skipped; PreparedSessions = $prepared; StagingRoot = $stagingPath } | ConvertTo-Json
