import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const cli = fs.readdirSync('tools/ZGSTokenBar.Cli')
  .filter(file => file.endsWith('.cs'))
  .sort()
  .map(file => fs.readFileSync('tools/ZGSTokenBar.Cli/' + file, 'utf8'))
  .join('\n');
const cliProject = fs.readFileSync('tools/ZGSTokenBar.Cli/ZGSTokenBar.Cli.csproj', 'utf8');
const app = fs.readFileSync('src/ZGSTokenBar.App/Program.cs', 'utf8');
const context = fs.readFileSync('src/ZGSTokenBar.App/QuotaApplicationContext.cs', 'utf8');
const host = fs.readFileSync('src/ZGSTokenBar.Host/ZgsTokenBarHost.cs', 'utf8');
const packaging = fs.readFileSync('scripts/build-native-portable.ps1', 'utf8');
const graduationGate = fs.readFileSync('scripts/graduation-gate.mjs', 'utf8');
const packageConfig = JSON.parse(fs.readFileSync('package.json', 'utf8'));

test('shipped CLI exposes canonical API commands and compatibility aliases', () => {
  assert.match(cli, /command is "settings"/);
  assert.match(cli, /command is "status"/);
  assert.match(cli, /command is "version"/);
  assert.match(cli, /CandidateApplicationPath\(\)/);
  assert.match(cli, /ZGSTokenBar\.dll/);
  assert.match(cli, /BuildIdForArtifact\(artifact\)/);
  assert.match(cli, /SHA256\.HashData\(stream\)/);
  assert.match(cli, /ToLowerInvariant\(\)/);
  assert.match(cli, /Process\.GetProcessById/);
  assert.match(cli, /"buildId"/);
  assert.match(cli, /command is "sub2api"/);
  assert.match(cli, /sub2api provision\|configure\|status\|disconnect/);
  assert.match(cli, /command is "economy"/);
  assert.match(cli, /economy status\|install\|set off\|ask\|on/);
  assert.match(cli, /CodexEconomyRouter\.ResolveProfile/);
  assert.match(cli, /--codex-home/);
  assert.doesNotMatch(cli, /AppSettings.*Economy|Economy.*AppSettings/);
  assert.match(cli, /RandomNumberGenerator\.GetBytes\(32\)/);
  assert.match(cli, /SHA256\.HashData/);
  assert.match(cli, /Local\\ZGSTokenBar\.App\.Activate/);
  assert.match(cli, /"plugin" => await PluginCommandAsync/);
  assert.match(cli, /"mini" => await MiniCommandAsync/);
  assert.match(cli, /"ui\.mini\.setArea"/);
  assert.match(cli, /mini width <area-id> <logical-px>/);
  assert.match(cli, /mini move <area-id> \[before-area-id\]/);
  assert.match(cli, /"ui\.mini\.moveArea"/);
  assert.match(cli, /\("expectedUiRevision", current\.UiRevision\)/);
  assert.match(host, /"ui\.mini\.moveArea" => await MiniAreaMoveAsync/);
  assert.match(cli, /"watch" => await WatchCommandAsync/);
  assert.match(cli, /acceptance run --isolated/);
  assert.match(cli, /DeprecatedAlias/);
  assert.doesNotMatch(cli, /settings\.json|quota-cache|credentials|auth\.json/);
  assert.doesNotMatch(cli, /SUB2API_ADMIN_PASSWORD|\/api\/v1\/auth\/login|access_token/);
  assert.match(app, /--settings/);
  assert.match(context, /openSettingsOnStart/);
});

test('build identity fixtures hash payloads, not semantic versions or candidate paths', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'zgs-build-identity-'));
  try {
    const candidateDirectory = path.join(root, 'candidate');
    const runningDirectory = path.join(root, 'running');
    const copiedDirectory = path.join(root, 'copied');
    const fallbackDirectory = path.join(root, 'fallback');
    for (const directory of [candidateDirectory, runningDirectory, copiedDirectory, fallbackDirectory]) {
      fs.mkdirSync(directory);
    }

    const semanticVersion = '3.0.0';
    const candidatePayload = Buffer.from(`version=${semanticVersion}\nimplementation=candidate\n`);
    const runningPayload = Buffer.from(`version=${semanticVersion}\nimplementation=running\n`);
    fs.writeFileSync(path.join(candidateDirectory, 'ZGSTokenBar.dll'), candidatePayload);
    fs.writeFileSync(path.join(candidateDirectory, 'ZGSTokenBar.exe'), Buffer.from('fallback decoy'));
    fs.writeFileSync(path.join(runningDirectory, 'ZGSTokenBar.dll'), runningPayload);
    fs.writeFileSync(path.join(copiedDirectory, 'ZGSTokenBar.dll'), candidatePayload);
    fs.writeFileSync(path.join(fallbackDirectory, 'ZGSTokenBar.exe'), runningPayload);

    const artifactPath = directory => {
      const libraryPath = path.join(directory, 'ZGSTokenBar.dll');
      return fs.existsSync(libraryPath) ? libraryPath : path.join(directory, 'ZGSTokenBar.exe');
    };
    const buildId = filePath => {
      try {
        return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
      } catch (error) {
        if (error?.code === 'ENOENT' || error?.code === 'EACCES' || error?.code === 'EPERM') return null;
        throw error;
      }
    };

    const candidatePath = artifactPath(candidateDirectory);
    const runningPath = artifactPath(runningDirectory);
    const copiedPath = artifactPath(copiedDirectory);
    assert.equal(path.basename(candidatePath), 'ZGSTokenBar.dll');
    assert.equal(path.basename(artifactPath(fallbackDirectory)), 'ZGSTokenBar.exe');
    assert.notEqual(buildId(candidatePath), buildId(runningPath));
    assert.equal(buildId(candidatePath), buildId(copiedPath));
    assert.match(buildId(candidatePath), /^[0-9a-f]{64}$/);
    assert.equal(buildId(path.join(root, 'missing', 'ZGSTokenBar.exe')), null);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('CLI is a small native executable and part of the portable contract', () => {
  assert.match(cliProject, /<PublishAot>true<\/PublishAot>/);
  assert.match(cliProject, /<AssemblyName>ZGSTokenBar\.Cli<\/AssemblyName>/);
  assert.match(packaging, /'ZGSTokenBar\.Cli\.exe'/);
  assert.match(packaging, /Sign-And-Verify \$cliExecutable/);
  assert.equal(packageConfig.scripts.cli, 'dotnet run --project tools/ZGSTokenBar.Cli/ZGSTokenBar.Cli.csproj -c Release --');
});

test('headless commands stay in-process and never fall through to the desktop pipe', () => {
  assert.match(cli, /if \(options\.Profile == "headless"\)[\s\S]{0,160}InvokeHeadlessAsync/);
  assert.match(cli, /Event watch requires the desktop profile/);
  assert.match(cli, /persistProfileState: false/);
});

test('credential-changing CLI commands await running-app settings reload', () => {
  assert.match(cli, /ReloadRunningAppSettingsAsync/);
  assert.match(cli, /\("reloadSettings", true\)/);
  assert.match(cli, /"runtime_sync_failed"/);
  assert.match(cli, /or JsonException/);
  assert.match(cli, /return 3;/);
  assert.doesNotMatch(cli, /_ = ReconcileRunningAppCredentialsAsync/);
  assert.doesNotMatch(cli, /_ = RequestRunningAppRefreshAsync/);
  assert.match(host, /EnsureFields\(parameters, \["reloadSettings"\]\)/);
  assert.match(context, /Task\.Run\(_store\.Load, cancellationToken\)/);
  assert.match(context, /ApplySettingsSnapshot\([\s\S]*?UpdateProviderActivity\(requestRefresh: false\)/);
  assert.doesNotMatch(
    context,
    /ApplySettingsSnapshot\([\s\S]*?_activeProviders = ActiveProviders\(_settings\)[\s\S]*?UpdateProviderActivity/,
  );
});
