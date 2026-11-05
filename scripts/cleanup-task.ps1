#Requires -Version 7.0

[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $Manifest,
  [switch] $Apply,
  [string] $ExpectedPlanHash
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$receiptDirectory = $null
$exitCode = 0
$report = [ordered]@{
  schemaVersion = 1
  mode = if ($Apply) { 'apply' } else { 'preview' }
  ok = $false
  temporaryRoots = @()
  localBranches = @()
  errors = @()
}

function Full-Path([string] $Path) {
  if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "An absolute path is required: $Path" }
  return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-NoLinks([string] $Path) {
  $cursor = [IO.Path]::GetFullPath($Path)
  while ($cursor) {
    $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
      throw "Links and reparse points are not cleanup targets: $cursor"
    }
    $parent = [IO.Directory]::GetParent($cursor)
    $cursor = if ($parent) { $parent.FullName } else { $null }
  }
}

function Hash-Bytes([byte[]] $Bytes) {
  $hasher = [Security.Cryptography.SHA256]::Create()
  try { return [BitConverter]::ToString($hasher.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
  finally { $hasher.Dispose() }
}

function Read-FileState([string] $Path, [string] $RelativePath) {
  # An exclusive read detects an open producer/consumer instead of forcing its files away.
  $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
  $hasher = [Security.Cryptography.SHA256]::Create()
  try {
    return [ordered]@{
      path = $RelativePath
      bytes = $stream.Length
      writtenTicks = [IO.File]::GetLastWriteTimeUtc($Path).Ticks.ToString()
      sha256 = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
  } finally { $hasher.Dispose(); $stream.Dispose() }
}

function Snapshot-Root([string] $Path, [string] $TaskId) {
  $full = Full-Path $Path
  $temporary = Full-Path ([IO.Path]::GetTempPath())
  if (-not [string]::Equals([IO.Path]::GetDirectoryName($full), $temporary, [StringComparison]::OrdinalIgnoreCase) -or
      [IO.Path]::GetFileName($full) -cnotmatch ('^zgstokenbar-' + [regex]::Escape($TaskId) + '-[a-f0-9]{32}$')) {
    throw "Target must be this task's direct child of the system temporary directory: $full"
  }
  Assert-NoLinks $full
  if ((Full-Path (Get-Location).ProviderPath).StartsWith($full + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
      [string]::Equals((Full-Path (Get-Location).ProviderPath), $full, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The current working directory is inside the cleanup target: $full"
  }
  if (-not (Test-Path -LiteralPath $full)) { return [ordered]@{ path = $full; exists = $false; files = @(); directories = @() } }
  $rootItem = Get-Item -LiteralPath $full -Force
  if (-not $rootItem.PSIsContainer) { throw "Temporary target is not a directory: $full" }
  $files = [Collections.Generic.List[object]]::new()
  $directories = [Collections.Generic.List[string]]::new()
  $pending = [Collections.Generic.Stack[string]]::new()
  $pending.Push($full)
  while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    Assert-NoLinks $directory
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
      if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked entry in task output: $($item.FullName)" }
      $relative = [IO.Path]::GetRelativePath($full, $item.FullName)
      if ($item.PSIsContainer) {
        $directories.Add($relative)
        $pending.Push($item.FullName)
      } else {
        if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) { throw "Read-only entry requires separate disposition: $($item.FullName)" }
        $files.Add((Read-FileState $item.FullName $relative))
      }
    }
  }
  return [ordered]@{
    path = $full
    exists = $true
    createdTicks = $rootItem.CreationTimeUtc.Ticks.ToString()
    files = @($files.ToArray() | Sort-Object { $_.path })
    directories = @($directories.ToArray() | Sort-Object)
  }
}

function Git-Output([string[]] $Arguments) {
  $output = @(& git -C $repoRoot @Arguments 2>&1)
  if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
  return ($output -join "`n").Trim()
}

function Snapshot-Branch($Branch) {
  if ($Branch -isnot [Collections.IDictionary] -or @($Branch.Keys | Where-Object { $_ -notin @('name', 'commit') }).Count -or
      $Branch.name -isnot [string] -or $Branch.name -cnotmatch '^codex/[a-zA-Z0-9][a-zA-Z0-9._/-]*$' -or
      $Branch.commit -isnot [string] -or $Branch.commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'A local codex/ branch and its exact 40-character commit are required.'
  }
  $name = $Branch.name
  [void](Git-Output @('check-ref-format', '--branch', $name))
  $reference = "refs/heads/$name"
  $tip = @(& git -C $repoRoot rev-parse --verify --quiet "$reference^{commit}" 2>&1)
  if ($LASTEXITCODE -eq 1) { return [ordered]@{ name = $name; commit = $Branch.commit; exists = $false } }
  if ($LASTEXITCODE -ne 0) { throw "Cannot inspect branch: $name" }
  if (($tip -join '').Trim() -ne $Branch.commit) { throw "Branch tip changed: $name" }
  if ((Git-Output @('worktree', 'list', '--porcelain')).Split("`n").Trim() -contains "branch $reference") {
    throw "Branch is checked out in a worktree: $name"
  }
  foreach ($base in @('refs/heads/main', 'refs/remotes/origin/main')) {
    [void](Git-Output @('merge-base', '--is-ancestor', $Branch.commit, $base))
  }
  return [ordered]@{ name = $name; commit = $Branch.commit; exists = $true }
}

function Write-Receipt {
  $json = $report | ConvertTo-Json -Depth 12
  if ($receiptDirectory) {
    $name = if ($Apply) { 'result.json' } else { 'preview.json' }
    $destination = Join-Path $receiptDirectory $name
    Assert-NoLinks $destination
    $staging = Join-Path $receiptDirectory ('.receipt-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($staging, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($staging, $destination, $true)
  }
  Write-Output $json
}

try {
  $manifestPath = Full-Path $Manifest
  Assert-NoLinks $manifestPath
  if ((Get-Item -LiteralPath $manifestPath).Length -gt 1048576) { throw 'Cleanup manifest is too large.' }
  $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
  $scope = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable
  if ($scope -isnot [Collections.IDictionary] -or $scope.schemaVersion -ne 1 -or
      $scope.taskId -isnot [string] -or $scope.taskId -cnotmatch '^[a-z0-9][a-z0-9-]{0,79}$' -or
      @($scope.Keys | Where-Object { $_ -notin @('schemaVersion', 'taskId', 'temporaryRoots', 'localBranches') }).Count) {
    throw 'Invalid cleanup manifest schema.'
  }
  $expectedManifest = Full-Path (Join-Path $repoRoot "release/task-cleanup/$($scope.taskId)/manifest.json")
  if (-not [string]::Equals($manifestPath, $expectedManifest, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Manifest must be recorded at $expectedManifest"
  }
  $receiptDirectory = [IO.Path]::GetDirectoryName($manifestPath)
  $report.taskId = $scope.taskId
  $report.manifest = $manifestPath
  $report.manifestSha256 = Hash-Bytes $manifestBytes
  $report.entrySha256 = Hash-Bytes ([IO.File]::ReadAllBytes($PSCommandPath))
  if ($Apply -and $ExpectedPlanHash -notmatch '^[0-9a-fA-F]{64}$') { throw '-Apply requires the SHA-256 plan hash returned by preview.' }
  if ($scope.temporaryRoots -isnot [array] -or $scope.localBranches -isnot [array] -or
      $scope.temporaryRoots.Count -gt 16 -or $scope.localBranches.Count -gt 16 -or
      $scope.temporaryRoots.Count + $scope.localBranches.Count -eq 0) {
    throw 'Specify 1-16 temporary roots and/or 1-16 local branches as arrays.'
  }
  if (-not [string]::Equals((Full-Path (Git-Output @('rev-parse', '--show-toplevel'))),
      (Full-Path $repoRoot), [StringComparison]::OrdinalIgnoreCase)) { throw 'Script repository identity does not match Git.' }

  $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $roots = @($scope.temporaryRoots | ForEach-Object {
    if ($_ -isnot [string] -or -not $seen.Add((Full-Path $_))) { throw 'Duplicate or invalid temporary root.' }
    Snapshot-Root $_ $scope.taskId
  })
  $seen.Clear()
  $branches = @($scope.localBranches | ForEach-Object {
    $branch = Snapshot-Branch $_
    if (-not $seen.Add($branch.name)) { throw 'Duplicate branch in cleanup scope.' }
    $branch
  })
  $plan = [ordered]@{ entrySha256 = $report.entrySha256; manifestSha256 = $report.manifestSha256; roots = $roots; branches = $branches }
  $report.planHash = Hash-Bytes ([Text.Encoding]::UTF8.GetBytes(($plan | ConvertTo-Json -Depth 16 -Compress)))
  $report.temporaryRoots = @($roots | ForEach-Object {
    $bytes = 0L
    foreach ($file in $_.files) { $bytes += [long]$file.bytes }
    [ordered]@{ path = $_.path; files = $_.files.Count; bytes = $bytes;
      status = if ($_.exists) { 'would-remove' } else { 'absent' }; filesRemoved = 0 }
  })
  $report.localBranches = @($branches | ForEach-Object {
    [ordered]@{ name = $_.name; commit = $_.commit; status = if ($_.exists) { 'would-remove' } else { 'absent' } }
  })
  $anythingExists = @($roots | Where-Object { $_.exists }).Count + @($branches | Where-Object { $_.exists }).Count -gt 0
  if ($Apply -and $anythingExists -and $ExpectedPlanHash -ne $report.planHash) { throw 'Cleanup plan changed; inspect a fresh preview before applying.' }

  if ($Apply) {
    for ($index = 0; $index -lt $roots.Count; $index++) {
      $root = $roots[$index]
      if (-not $root.exists) { continue }
      $result = $report.temporaryRoots[$index]
      try {
        Assert-NoLinks $root.path
        if ((Get-Item -LiteralPath $root.path).CreationTimeUtc.Ticks.ToString() -ne $root.createdTicks) { throw 'Temporary root was replaced.' }
        foreach ($file in $root.files) {
          $path = Join-Path $root.path $file.path
          Assert-NoLinks $path
          $current = Read-FileState $path $file.path
          if ($current.sha256 -ne $file.sha256 -or $current.writtenTicks -ne $file.writtenTicks) { throw "File changed during cleanup: $path" }
          Remove-Item -LiteralPath $path -Confirm:$false
          $result.filesRemoved++
        }
        # Delete only empty directories: a newly created, unregistered file is never swept up.
        foreach ($relative in @($root.directories | Sort-Object Length -Descending)) {
          $path = Join-Path $root.path $relative
          Assert-NoLinks $path
          [IO.Directory]::Delete($path, $false)
        }
        Assert-NoLinks $root.path
        [IO.Directory]::Delete($root.path, $false)
        $result.status = 'removed'
      } catch { $result.status = 'failed'; $report.errors += $_.Exception.Message }
    }
    for ($index = 0; $index -lt $branches.Count; $index++) {
      $branch = $branches[$index]
      if (-not $branch.exists) { continue }
      $result = $report.localBranches[$index]
      try {
        $current = Snapshot-Branch @{ name = $branch.name; commit = $branch.commit }
        if ($current.exists) { [void](Git-Output @('branch', '-d', '--', $branch.name)) }
        $result.status = if ($current.exists) { 'removed' } else { 'absent' }
      } catch { $result.status = 'failed'; $report.errors += $_.Exception.Message }
    }
  }
  $report.ok = $report.errors.Count -eq 0
  if (-not $report.ok) { $exitCode = 3 }
} catch {
  $report.errors += $_.Exception.Message
  $exitCode = 2
}
Write-Receipt
exit $exitCode
