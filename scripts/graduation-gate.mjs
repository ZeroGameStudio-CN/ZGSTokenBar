import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import process from 'node:process';

const testFiles = readdirSync('scripts')
  .filter(file => file.endsWith('.test.mjs'))
  .sort()
  .map(file => `scripts/${file}`);

const artifactsPath = mkdtempSync(join(tmpdir(), 'zgstokenbar-verify-'));
const appOutput = join(artifactsPath, 'app-publish');
const cliOutput = join(artifactsPath, 'cli-publish');
const acceptanceOutput = join(artifactsPath, 'plugin-acceptance');
const retiredCommandsOutput = join(artifactsPath, 'retired-commands');
const captureOutput = join(artifactsPath, 'mini-captures');
const pluginPackage = join(artifactsPath, 'ZGSTokenBar.Plugin.AiGatewayObserver.zgsplugin');
const portableOutput = join(artifactsPath, 'portable-release');
const pluginShaPlaceholder = '__ZGSTOKENBAR_PLUGIN_SHA256__';
let artifactsRemoved = false;

function removeArtifacts() {
  if (artifactsRemoved) return;
  artifactsRemoved = true;
  try {
    rmSync(artifactsPath, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  } catch (error) {
    console.warn(`Could not remove temporary build artifacts: ${error.message}`);
  }
}

const commands = [
  ['node', ['scripts/graduation-static-checks.mjs']],
  ['node', ['scripts/generate-builtin-plugin-registry.mjs', '--check']],
  ['node', ['--test', ...testFiles]],
  ['pwsh', [
    '-NoProfile',
    '-File', 'scripts/build-ai-gateway-observer-plugin.ps1',
    '-OutputPath', pluginPackage,
  ]],
  ['pwsh', [
    '-NoProfile',
    '-File', 'scripts/build-native-portable.ps1',
    '-BundledPluginPackagePath', pluginPackage,
    '-ReleaseDirectory', portableOutput,
  ]],
  ['dotnet', [
    'run',
    '--project', 'tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj',
    '-c', 'Release',
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
  ]],
  ['dotnet', [
    'run',
    '--project', 'tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj',
    '-c', 'Release',
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
    '--', '--native-window-lifecycle',
  ]],
  ['dotnet', [
    'run',
    '--project', 'tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj',
    '-c', 'Release',
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
    '--', '--ai-gateway-plugin-package', pluginPackage, pluginShaPlaceholder,
  ]],
  ['dotnet', [
    'publish',
    'src/ZGSTokenBar.App/ZGSTokenBar.App.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', appOutput,
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
    `-p:BundledPluginPackage=${pluginPackage}`,
  ]],
  ['dotnet', [
    'publish',
    'tools/ZGSTokenBar.Cli/ZGSTokenBar.Cli.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', cliOutput,
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
  ]],
  [join(cliOutput, 'ZGSTokenBar.Cli.exe'), [
    '--json', 'acceptance', 'run', '--isolated', '--artifacts', acceptanceOutput,
  ]],
  ['node', [
    'scripts/retired-commands-acceptance.mjs', join(cliOutput, 'ZGSTokenBar.Cli.exe'), retiredCommandsOutput,
  ]],
  ['dotnet', [
    'run',
    '--project', 'tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj',
    '-c', 'Release',
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
    '--', '--taskbar-mini-captures', captureOutput,
  ]],
  ['git', ['diff', '--check']],
  ['git', ['diff', '--cached', '--check']],
];

let failureCode = 0;
try {
  for (const [command, args] of commands) {
    const resolvedArgs = args.map((argument) => argument === pluginShaPlaceholder
      ? createHash('sha256').update(readFileSync(pluginPackage)).digest('hex')
      : argument);
    console.log(`\n> ${command} ${resolvedArgs.join(' ')}`);
    const result = spawnSync(command, resolvedArgs, { stdio: 'inherit' });
    if (result.error) {
      console.error(result.error.message);
      failureCode = 1;
      break;
    }
    if (result.status !== 0) {
      failureCode = result.status ?? 1;
      break;
    }
  }
  if (failureCode === 0) {
    const appFiles = readdirSync(appOutput).sort();
    const cliFiles = readdirSync(cliOutput).sort();
    if (appFiles.length !== 1 || appFiles[0] !== 'ZGSTokenBar.exe') {
      console.error(`App publish is not single-file: ${appFiles.join(', ')}`);
      failureCode = 1;
    } else if (cliFiles.length !== 1 || cliFiles[0] !== 'ZGSTokenBar.Cli.exe') {
      console.error(`CLI publish is not one NativeAOT executable: ${cliFiles.join(', ')}`);
      failureCode = 1;
    }
  }
} finally {
  removeArtifacts();
}

if (failureCode !== 0) process.exit(failureCode);
console.log('\nZGSTokenBar graduation gate passed');
