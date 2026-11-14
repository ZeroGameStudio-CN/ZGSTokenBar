#Requires -Version 7.0

# Supported platform: Windows 10/11 with PowerShell 7 (ZGSTokenBar).
# This entry only removes explicitly listed, ignored .NET build files in a
# linked worktree. It never removes a worktree, branch, source file, or data root.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $Manifest,
  [switch] $Apply,
  [string] $ExpectedPlanHash
)

$ErrorActionPreference = 'Stop'
$env:GIT_NO_LAZY_FETCH = '1'
$env:GIT_TERMINAL_PROMPT = '0'
$env:GIT_OPTIONAL_LOCKS = '0'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$receiptDirectory = $null
$exitCode = 0
$report = [ordered]@{
  schemaVersion = 1
  mode = if ($Apply) { 'apply' } else { 'preview' }
  ok = $false
  removed = @()
  errors = @()
}

function Full-Path([string] $Path) {
  if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'An absolute path is required.' }
  return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Assert-NoLinks([string] $Path) {
  $cursor = [IO.Path]::GetFullPath($Path)
  while ($cursor) {
    $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
    if ($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Offline)) {
      throw "A linked, reparse-point or offline path is not eligible: $cursor"
    }
    $parent = [IO.Directory]::GetParent($cursor)
    $cursor = if ($parent) { $parent.FullName } else { $null }
  }
}

function Assert-Within([string] $Root, [string] $Path) {
  $prefix = (Full-Path $Root) + [IO.Path]::DirectorySeparatorChar
  $full = Full-Path $Path
  if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Target escaped the linked worktree: $full"
  }
  return $full
}

function Hash-Bytes([byte[]] $Bytes) {
  $hasher = [Security.Cryptography.SHA256]::Create()
  try { return [BitConverter]::ToString($hasher.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
  finally { $hasher.Dispose() }
}

function Git-Output([string] $Root, [string[]] $Arguments) {
  $output = @(& git --no-optional-locks -C $Root @Arguments 2>&1)
  if ($LASTEXITCODE -ne 0) { throw "Git could not verify the worktree: $($Arguments[0])" }
  return ($output -join [Environment]::NewLine).Trim()
}

function Read-FileState([string] $Path, [string] $RelativePath) {
  Assert-NoLinks $Path
  $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
  if ($item.PSIsContainer -or ($item.Attributes -band ([IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Offline))) {
    throw "An output is not a writable local file: $RelativePath"
  }
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

function Assert-BuildName([string] $Name) {
  if ($Name -imatch '\.(dll|exe|pdb|cache|up2date)$' -or
      $Name -imatch '(\.deps\.json|\.runtimeconfig\.json|\.nuget\.dgspec\.json|^project\.assets\.json)$' -or
      $Name -imatch '\.csproj\.FileListAbsolute\.txt$' -or
      $Name -imatch '(\.AssemblyInfo\.cs|\.GlobalUsings\.g\.cs|\.AssemblyAttributes\.cs)$' -or
      $Name -imatch '\.nuget\.g\.(props|targets)$' -or
      $Name -imatch '\.GeneratedMSBuildEditorConfig\.editorconfig$') { return }
  throw "The file name is not a recognized build output: $Name"
}

function Get-IgnoredPaths([string] $Root, [string[]] $Paths) {
  $info = [Diagnostics.ProcessStartInfo]::new()
  $info.FileName = 'git'
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardInput = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
  foreach ($arg in @('--no-optional-locks', '-C', $Root, 'check-ignore', '--stdin', '-z')) {
    $info.ArgumentList.Add($arg)
  }
  $process = [Diagnostics.Process]::new()
  $process.StartInfo = $info
  try {
    if (-not $process.Start()) { throw 'Git ignore verification could not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    foreach ($path in $Paths) {
      $process.StandardInput.Write($path)
      $process.StandardInput.Write([char]0)
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(30000)) {
      $process.Kill()
      throw 'Git ignore verification timed out.'
    }
    $output = $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw 'Git could not verify the ignored outputs.' }
    return @($output.Split([char[]]@([char]0), [StringSplitOptions]::RemoveEmptyEntries))
  } finally { $process.Dispose() }
}


function Write-Receipt([switch] $Quiet) {
  $json = $report | ConvertTo-Json -Depth 12
  if ($receiptDirectory) {
    $name = if ($Apply) { 'worktree-build-result.json' } else { 'worktree-build-preview.json' }
    $destination = Join-Path $receiptDirectory $name
    $staging = Join-Path $receiptDirectory ('.receipt-' + [guid]::NewGuid().ToString('N') + '.tmp')
    Assert-NoLinks $receiptDirectory
    if (Test-Path -LiteralPath $destination) { Assert-NoLinks $destination }
    [IO.File]::WriteAllText($staging, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($staging, $destination, $true)
  }
  if (-not $Quiet) { Write-Output $json }
}

try {
  $manifestPath = Full-Path $Manifest
  Assert-NoLinks $manifestPath
  if ((Get-Item -LiteralPath $manifestPath).Length -gt 1048576) { throw 'Cleanup manifest is too large.' }
  $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
  $scope = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable
  $allowedFields = @('schemaVersion', 'taskId', 'worktree', 'expectedHead', 'expectedBranch', 'closureEvidence', 'authorizationRef', 'files')
  if ($scope -isnot [Collections.IDictionary] -or $scope.schemaVersion -ne 1 -or
      $scope.taskId -isnot [string] -or $scope.taskId -cnotmatch '^[a-z0-9][a-z0-9-]{0,79}$' -or
      @($scope.Keys | Where-Object { $_ -notin $allowedFields }).Count -ne 0 -or
      $scope.expectedHead -cnotmatch '^[a-f0-9]{40}$' -or
      $scope.expectedBranch -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$' -or
      [string]::IsNullOrWhiteSpace($scope.closureEvidence) -or
      [string]::IsNullOrWhiteSpace($scope.authorizationRef) -or
      $scope.files -isnot [array] -or $scope.files.Count -lt 1 -or $scope.files.Count -gt 2000) {
    throw 'Invalid worktree build cleanup manifest.'
  }
  $expectedManifest = Full-Path (Join-Path $repoRoot "release/task-cleanup/$($scope.taskId)/worktree-builds.json")
  if (-not [string]::Equals($manifestPath, $expectedManifest, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The manifest is outside the project task cleanup directory.'
  }
  $receiptDirectory = [IO.Path]::GetDirectoryName($manifestPath)
  $resultPath = Join-Path $receiptDirectory 'worktree-build-result.json'
  if ($Apply -and (Test-Path -LiteralPath $resultPath)) {
    $receiptDirectory = $null
    throw 'A result already exists; inspect it before any retry.'
  }
  if ($Apply -and $ExpectedPlanHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Apply requires the preview plan hash.' }

  $worktree = Full-Path $scope.worktree
  Assert-NoLinks $worktree
  if ([string]::Equals($worktree, (Full-Path $repoRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The entry cannot clean its own checkout.'
  }
  $gitEntry = Get-Item -LiteralPath (Join-Path $worktree '.git') -Force -ErrorAction Stop
  if ($gitEntry.PSIsContainer -or ($gitEntry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The target must be a linked worktree, not the primary checkout.'
  }
  $actualRoot = Full-Path (Git-Output $worktree @('rev-parse', '--path-format=absolute', '--show-toplevel'))
  $targetCommon = Full-Path (Git-Output $worktree @('rev-parse', '--path-format=absolute', '--git-common-dir'))
  $ownerCommon = Full-Path (Git-Output $repoRoot @('rev-parse', '--path-format=absolute', '--git-common-dir'))
  if (-not [string]::Equals($worktree, $actualRoot, [StringComparison]::OrdinalIgnoreCase) -or
      -not [string]::Equals($targetCommon, $ownerCommon, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The worktree does not belong to this project.'
  }
  $records = (Git-Output $repoRoot @('worktree', 'list', '--porcelain')) -split '\r?\n\r?\n'
  $matches = @($records | Where-Object {
    $lines = $_ -split '\r?\n'
    $lines.Count -gt 0 -and $lines[0].StartsWith('worktree ') -and
      [string]::Equals((Full-Path $lines[0].Substring(9)), $worktree, [StringComparison]::OrdinalIgnoreCase)
  })
  if ($matches.Count -ne 1 -or $matches[0] -match '(?m)^(locked|prunable)( |$)') {
    throw 'The worktree is not registered and unlocked.'
  }
  if ((Git-Output $worktree @('rev-parse', 'HEAD')) -cne $scope.expectedHead -or
      (Git-Output $worktree @('branch', '--show-current')) -cne $scope.expectedBranch -or
      (Git-Output $worktree @('status', '--porcelain=v1', '--untracked-files=normal'))) {
    throw 'The worktree head, branch or clean status changed.'
  }

  $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $paths = [Collections.Generic.List[string]]::new()
  $states = [Collections.Generic.List[object]]::new()
  $directoryRoots = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
  $verifiedProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $totalBytes = 0L
  foreach ($file in $scope.files) {
    if ($file -isnot [Collections.IDictionary] -or
        @($file.Keys | Where-Object { $_ -notin @('path', 'sha256', 'bytes') }).Count -ne 0 -or
        $file.path -isnot [string] -or $file.path -cnotmatch '^(src|tests)/(?:[A-Za-z0-9_.-]+/){1,4}(bin|obj)/[A-Za-z0-9_.@+ ,=()/-]+$' -or
        $file.path -match '(^|/)\.\.?(/|$)|//' -or
        $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $file.bytes -isnot [ValueType] -or [long]$file.bytes -lt 0 -or
        -not $seen.Add($file.path)) {
      throw 'Invalid, duplicate or unsafe build output path.'
    }
    $parts = $file.path -split '/'
    $anchor = -1
    for ($i = 1; $i -lt $parts.Count - 1; $i++) {
      if ($parts[$i] -in @('bin', 'obj')) { $anchor = $i; break }
    }
    if ($anchor -lt 2) { throw 'Build output must be inside a project bin or obj directory.' }
    $projectRel = ($parts[0..($anchor - 1)] -join '/')
    $projectDir = Assert-Within $worktree (Join-Path $worktree $projectRel)
    if ($verifiedProjects.Add($projectRel)) {
      Assert-NoLinks $projectDir
      if (@(Get-ChildItem -LiteralPath $projectDir -Filter '*.csproj' -File -Force).Count -ne 1) {
        throw "Build output does not belong to one project: $projectRel"
      }
    }
    Assert-BuildName $parts[-1]
    $full = Assert-Within $worktree (Join-Path $worktree $file.path)
    $state = Read-FileState $full $file.path
    if ($state.sha256 -cne $file.sha256 -or $state.bytes -ne [long]$file.bytes) {
      throw "Build output differs from the manifest: $($file.path)"
    }
    $totalBytes += [long]$state.bytes
    if ($totalBytes -gt 512MB) { throw 'The selected output exceeds the bounded cleanup size.' }
    $paths.Add($file.path)
    $states.Add($state)
    $directoryRoots[$file.path] = Assert-Within $worktree (Join-Path $worktree ($parts[0..$anchor] -join '/'))
  }
  $ignored = @(Get-IgnoredPaths $worktree $paths.ToArray())
  if ($ignored.Count -ne $paths.Count) { throw 'At least one target is not Git-ignored.' }
  $ignoredSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($entry in $ignored) { [void]$ignoredSet.Add($entry) }
  foreach ($entry in $paths) {
    if (-not $ignoredSet.Contains($entry)) { throw 'An ignored target could not be verified.' }
  }

  $plan = [ordered]@{
    entrySha256 = Hash-Bytes ([IO.File]::ReadAllBytes($PSCommandPath))
    manifestSha256 = Hash-Bytes $manifestBytes
    worktree = $worktree
    expectedHead = $scope.expectedHead
    expectedBranch = $scope.expectedBranch
    files = @($states.ToArray() | Sort-Object { $_.path })
  }
  $report.taskId = $scope.taskId
  $report.worktree = $worktree
  $report.targetCount = $states.Count
  $report.targetBytes = $totalBytes
  $report.planHash = Hash-Bytes ([Text.Encoding]::UTF8.GetBytes(($plan | ConvertTo-Json -Depth 12 -Compress)))
  if ($Apply -and $ExpectedPlanHash -cne $report.planHash) { throw 'The plan changed after preview.' }
  if ($Apply) {
    $report.executionState = 'applying'
    Write-Receipt -Quiet
    foreach ($state in $plan.files) {
      $path = Assert-Within $worktree (Join-Path $worktree $state.path)
      $current = Read-FileState $path $state.path
      if ($current.sha256 -cne $state.sha256 -or $current.bytes -ne $state.bytes -or
          $current.writtenTicks -cne $state.writtenTicks) { throw "A target changed during cleanup: $($state.path)" }
      Remove-Item -LiteralPath $path -Confirm:$false
      $report.removed += @($state.path)
      if ($report.removed.Count % 25 -eq 0) { Write-Receipt -Quiet }
    }
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $report.removed) {
      $boundary = $directoryRoots[$relative]
      $parent = [IO.Path]::GetDirectoryName((Assert-Within $worktree (Join-Path $worktree $relative)))
      while ($parent -and ($parent.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($parent, $boundary, [StringComparison]::OrdinalIgnoreCase))) {
        [void]$directories.Add($parent)
        if ([string]::Equals($parent, $boundary, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [IO.Path]::GetDirectoryName($parent)
      }
    }
    foreach ($directory in @($directories | Sort-Object Length -Descending)) {
      if (-not (Test-Path -LiteralPath $directory)) { continue }
      $null = Assert-Within $worktree $directory
      Assert-NoLinks $directory
      if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory, $false) }
    }
  }
  $report.ok = $true
  $report.executionState = if ($Apply) { 'completed' } else { 'preview' }
} catch {
  $report.errors += $_.Exception.Message
  $report.executionState = if ($Apply -and $report.removed.Count -gt 0) { 'partial' } else { 'refused' }
  $exitCode = if ($Apply -and $report.removed.Count -gt 0) { 3 } else { 2 }
}
Write-Receipt
exit $exitCode
