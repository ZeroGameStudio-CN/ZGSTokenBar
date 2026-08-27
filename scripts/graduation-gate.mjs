import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
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
const captureOutput = join(artifactsPath, 'mini-captures');
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
    'publish',
    'src/ZGSTokenBar.App/ZGSTokenBar.App.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', appOutput,
    '--artifacts-path', artifactsPath,
    '--disable-build-servers',
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
    console.log(`\n> ${command} ${args.join(' ')}`);
    const result = spawnSync(command, args, { stdio: 'inherit' });
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
