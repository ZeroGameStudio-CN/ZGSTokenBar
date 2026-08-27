import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

function fail(message) {
  console.error(`brand graduation check failed: ${message}`);
  process.exitCode = 1;
}

function collectTextFiles(root) {
  if (!fs.existsSync(root)) return [];
  return fs.readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const target = path.join(root, entry.name);
    if (entry.isDirectory()) {
      if (['bin', 'obj', 'assets'].includes(entry.name)) return [];
      return collectTextFiles(target);
    }
    return /\.(cs|csproj|json|manifest|md|mjs|ps1|ya?ml)$/.test(entry.name) ? [target] : [];
  });
}

const buildPropsSource = fs.readFileSync('Directory.Build.props', 'utf8');
const productVersion = buildPropsSource.match(/<ZGSTokenBarVersion>([^<]+)<\/ZGSTokenBarVersion>/)?.[1]?.trim();
if (!productVersion) fail('Directory.Build.props must define ZGSTokenBarVersion');
const globalConfig = JSON.parse(fs.readFileSync('global.json', 'utf8'));
if (globalConfig.sdk?.version !== '10.0.111'
    || globalConfig.sdk?.rollForward !== 'disable'
    || globalConfig.sdk?.allowPrerelease !== false) {
  fail('global.json must pin stable .NET SDK 10.0.111 with rollForward disabled');
}

const packageConfig = JSON.parse(fs.readFileSync('package.json', 'utf8'));
const packageLock = JSON.parse(fs.readFileSync('package-lock.json', 'utf8'));
if (packageConfig.name !== 'zgstokenbar' || packageConfig.version !== productVersion) {
  fail(`package identity must be zgstokenbar v${productVersion}`);
}
if (packageLock.version !== productVersion || packageLock.packages?.['']?.version !== productVersion) {
  fail('package-lock version must match Directory.Build.props');
}
if (packageConfig.dependencies || packageConfig.devDependencies || packageConfig.build) {
  fail('Electron dependencies and packager configuration must stay removed');
}
for (const script of ['build', 'test', 'dist', 'verify']) {
  if (!packageConfig.scripts?.[script]) fail(`missing ${script} script`);
}

const requiredPaths = [
  'Directory.Build.props',
  'global.json',
  'src/ZGSTokenBar.App/ZGSTokenBar.App.csproj',
  'src/ZGSTokenBar.Core/ZGSTokenBar.Core.csproj',
  'src/ZGSTokenBar.PluginSdk/ZGSTokenBar.PluginSdk.csproj',
  'src/ZGSTokenBar.Host/ZGSTokenBar.Host.csproj',
  'src/ZGSTokenBar.Transport.NamedPipe/ZGSTokenBar.Transport.NamedPipe.csproj',
  'tests/ZGSTokenBar.Tests/ZGSTokenBar.Tests.csproj',
  'tools/ZGSTokenBar.Cli/ZGSTokenBar.Cli.csproj',
  'schemas/plugin-manifest.v1.json',
  'schemas/local-api.v1.json',
  'assets/zgs-tokenbar-icon-master.png',
];
for (const requiredPath of requiredPaths) {
  if (!fs.existsSync(requiredPath)) fail(`missing ${requiredPath}`);
}

for (const retiredPath of [
  'native',
  'src/main',
  'src/renderer',
  'src/bridge',
  'tsconfig.json',
  'tsconfig.renderer.json',
  'EULA.txt',
  'EULA.ko.txt',
  'tools/ZGSTokenBar.WindowProbe/ZGSTokenBar.WindowProbe.csproj',
]) {
  if (fs.existsSync(retiredPath)) fail(`retired fork path still exists: ${retiredPath}`);
}

const appProject = fs.readFileSync('src/ZGSTokenBar.App/ZGSTokenBar.App.csproj', 'utf8');
for (const expected of [
  '<AssemblyName>ZGSTokenBar</AssemblyName>',
  '<RootNamespace>ZGSTokenBar.App</RootNamespace>',
  '<Product>ZGSTokenBar</Product>',
  '<Authors>ZeroGameStudio</Authors>',
]) {
  if (!appProject.includes(expected)) fail(`app project is missing ${expected}`);
}

const projectFiles = [
  ...collectTextFiles('src'),
  ...collectTextFiles('tests'),
  ...collectTextFiles('tools'),
].filter(file => file.endsWith('.csproj'));
for (const projectFile of projectFiles) {
  const projectSource = fs.readFileSync(projectFile, 'utf8');
  if (/<Version>/.test(projectSource)) {
    fail(`project version must come from Directory.Build.props: ${projectFile}`);
  }
}

const activeFiles = [
  ...collectTextFiles('src'),
  ...collectTextFiles('tests'),
  ...collectTextFiles('tools'),
  ...collectTextFiles('scripts'),
  ...collectTextFiles('.github'),
  'package.json',
  'README.md',
  'README.zh-CN.md',
  'SECURITY.md',
  'CONTRIBUTING.md',
  'CODE_OF_CONDUCT.md',
].filter(fs.existsSync);

const forbiddenText = [
  ['Where', 'MyTokens'].join(''),
  ['tail', '.', 'zerogamestudio', '.', 'com'].join(''),
  ['zgs', '-', 'worker'].join(''),
  ['personal', '-', 'codex', '-', 'skills'].join(''),
];
for (const file of activeFiles) {
  const source = fs.readFileSync(file, 'utf8');
  for (const forbidden of forbiddenText) {
    if (source.toLowerCase().includes(forbidden.toLowerCase())) {
      fail(`public source contains private or legacy text in ${file}`);
    }
  }
}

if (process.exitCode) process.exit(process.exitCode);
console.log('brand graduation static checks passed');
