import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const source = path.resolve('scripts/cleanup-worktree-builds.ps1');
const windowsOnly = { skip: process.platform !== 'win32' };

function run(command, args, cwd, env) {
  const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', input: '', timeout: 30000 });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return result.stdout.trim();
}

function fixture(t) {
  const temp = path.resolve(os.tmpdir());
  const base = fs.mkdtempSync(path.join(temp, 'zgstokenbar-worktree-build-test-'));
  assert.equal(path.dirname(path.resolve(base)), temp);
  const repo = path.join(base, 'repo');
  const worktree = path.join(base, 'linked');
  fs.mkdirSync(path.join(repo, 'scripts'), { recursive: true });
  fs.mkdirSync(path.join(repo, 'src', 'Demo'), { recursive: true });
  fs.copyFileSync(source, path.join(repo, 'scripts', 'cleanup-worktree-builds.ps1'));
  fs.writeFileSync(path.join(repo, '.gitignore'), 'release/\n**/bin/\n**/obj/\n');
  fs.writeFileSync(path.join(repo, 'src', 'Demo', 'Demo.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />\n');
  const env = {
    ...process.env,
    GIT_CONFIG_GLOBAL: path.join(base, 'gitconfig'),
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_TERMINAL_PROMPT: '0',
  };
  const git = (...args) => run('git', args, repo, env);
  git('init', '-b', 'main');
  git('config', 'user.name', 'Cleanup fixture');
  git('config', 'user.email', 'cleanup@example.test');
  git('add', '.');
  git('commit', '-m', 'fixture');
  const head = git('rev-parse', 'HEAD');
  const branch = 'fixture/completed';
  git('worktree', 'add', '-b', branch, worktree, 'main');
  const output = path.join(worktree, 'src', 'Demo', 'bin', 'Release', 'net10.0');
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(path.join(output, 'Demo.dll'), 'reproducible build');
  fs.writeFileSync(path.join(output, 'Demo.deps.json'), '{"deps":{}}');
  fs.writeFileSync(path.join(output, 'settings.json'), '{"private":true}');
  fs.writeFileSync(path.join(output, 'AiGatewayObserver.dll'), 'protected');
  const evidence = (relative) => {
    const bytes = fs.readFileSync(path.join(worktree, ...relative.split('/')));
    return { path: relative, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
  };
  const taskId = 'fixture-' + randomUUID().replaceAll('-', '');
  const manifest = path.join(repo, 'release', 'task-cleanup', taskId, 'worktree-builds.json');
  const scope = {
    schemaVersion: 1,
    taskId,
    worktree,
    expectedHead: head,
    expectedBranch: branch,
    closureEvidence: 'Synthetic completed task.',
    authorizationRef: 'Synthetic fixture only.',
    files: [
      evidence('src/Demo/bin/Release/net10.0/Demo.dll'),
      evidence('src/Demo/bin/Release/net10.0/Demo.deps.json'),
    ],
  };
  const save = () => {
    fs.mkdirSync(path.dirname(manifest), { recursive: true });
    fs.writeFileSync(manifest, JSON.stringify(scope));
  };
  save();
  const script = path.join(repo, 'scripts', 'cleanup-worktree-builds.ps1');
  const invoke = (...args) => {
    const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', script, '-Manifest', manifest, ...args],
      { cwd: repo, env, encoding: 'utf8', input: '', timeout: 30000 });
    assert.ifError(result.error);
    assert.ok(result.stdout.trim().startsWith('{'), result.stderr || result.stdout);
    return { ...result, receipt: JSON.parse(result.stdout) };
  };
  t.after(() => {
    // Only this test's own synthetic root is removed.
    const resolved = fs.realpathSync(base);
    assert.equal(path.dirname(resolved).toLowerCase(), fs.realpathSync(temp).toLowerCase());
    assert.match(path.basename(resolved), /^zgstokenbar-worktree-build-test-/);
    fs.rmSync(resolved, { recursive: true, force: true });
  });
  return { base, repo, worktree, output, branch, head, manifest, scope, save, invoke, git, env, evidence, script };
}

test('removes only listed build files and preserves the linked worktree, branch and other files', windowsOnly, t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  assert.equal(preview.receipt.targetCount, 2);
  assert.match(preview.receipt.planHash, /^[a-f0-9]{64}$/);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.equal(applied.status, 0, JSON.stringify(applied.receipt));
  assert.equal(applied.receipt.removed.length, 2);
  assert.equal(fs.existsSync(path.join(f.output, 'Demo.dll')), false);
  assert.equal(fs.existsSync(path.join(f.output, 'Demo.deps.json')), false);
  assert.ok(fs.existsSync(path.join(f.output, 'settings.json')));
  assert.ok(fs.existsSync(path.join(f.output, 'AiGatewayObserver.dll')));
  assert.ok(fs.existsSync(path.join(f.worktree, '.git')));
  assert.equal(f.git('rev-parse', 'refs/heads/' + f.branch), f.head);
  assert.equal(f.git('status', '--porcelain'), '');
  const receiptPath = path.join(path.dirname(f.manifest), 'worktree-build-result.json');
  const originalReceipt = fs.readFileSync(receiptPath, 'utf8');
  assert.notEqual(f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash).status, 0);
  assert.equal(fs.readFileSync(receiptPath, 'utf8'), originalReceipt);
});

test('refuses a changed build file after preview without deleting another target', windowsOnly, t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  fs.writeFileSync(path.join(f.output, 'Demo.dll'), 'changed after preview');
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.notEqual(applied.status, 0);
  assert.equal(applied.receipt.removed.length, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.deps.json')));
  assert.equal(f.git('rev-parse', 'refs/heads/' + f.branch), f.head);
});

test('rejects user settings, path traversal and a primary checkout', windowsOnly, t => {
  const f = fixture(t);
  const original = structuredClone(f.scope);
  f.scope.files = [f.evidence('src/Demo/bin/Release/net10.0/settings.json')];
  f.save();
  assert.notEqual(f.invoke().status, 0);
  f.scope.files = [{ path: '../outside.dll', bytes: 1, sha256: '0'.repeat(64) }];
  f.save();
  assert.notEqual(f.invoke().status, 0);
  Object.assign(f.scope, original);
  f.scope.worktree = f.repo;
  f.save();
  assert.notEqual(f.invoke().status, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'settings.json')));
});

test('rejects a dirty worktree and an incorrect expected commit', windowsOnly, t => {
  const f = fixture(t);
  fs.appendFileSync(path.join(f.worktree, 'src', 'Demo', 'Demo.csproj'), '<!-- modified -->');
  assert.notEqual(f.invoke().status, 0);
  f.git('-C', f.worktree, 'restore', '--', 'src/Demo/Demo.csproj');
  f.scope.expectedHead = '0'.repeat(40);
  f.save();
  assert.notEqual(f.invoke().status, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
});

test('binds the preview to the cleanup entry and refuses a stale script', windowsOnly, t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  fs.appendFileSync(f.script, '\n# changed after preview\n');
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.notEqual(applied.status, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
});

test('refuses an exclusively open output without deleting any files', windowsOnly, t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  const wrapper = path.join(f.base, 'hold.ps1');
  fs.writeFileSync(wrapper, '#Requires -Version 7.0\n' +
    'param([string] $Target, [string] $Entry, [string] $Manifest, [string] $Plan)\n' +
    '$held = [IO.File]::Open($Target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)\n' +
    'try { & pwsh -NoProfile -NonInteractive -File $Entry -Manifest $Manifest -Apply -ExpectedPlanHash $Plan; $code = $LASTEXITCODE }\n' +
    'finally { $held.Dispose() }\nexit $code\n');
  const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', wrapper,
    '-Target', path.join(f.output, 'Demo.dll'), '-Entry', f.script, '-Manifest', f.manifest, '-Plan', preview.receipt.planHash],
  { cwd: f.repo, env: f.env, encoding: 'utf8', input: '', timeout: 30000 });
  assert.ifError(result.error);
  assert.notEqual(result.status, 0);
  const receipt = JSON.parse(result.stdout);
  assert.equal(receipt.removed.length, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.deps.json')));
});



test('refuses a locked linked worktree', windowsOnly, t => {
  const f = fixture(t);
  f.git('worktree', 'lock', f.worktree);
  try {
    assert.notEqual(f.invoke().status, 0);
    assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  } finally {
    f.git('worktree', 'unlock', f.worktree);
  }
});

test('refuses a tracked file even when its path resembles build output', windowsOnly, t => {
  const f = fixture(t);
  f.git('-C', f.worktree, 'add', '-f', 'src/Demo/bin/Release/net10.0/Demo.dll');
  f.git('-C', f.worktree, 'commit', '-m', 'tracked build fixture');
  f.scope.expectedHead = f.git('rev-parse', 'refs/heads/' + f.branch);
  f.save();
  assert.notEqual(f.invoke().status, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.deps.json')));
});


test('handles a bounded batch with spaces without widening its file scope', windowsOnly, t => {
  const f = fixture(t);
  f.scope.files = [];
  for (let index = 0; index < 140; index++) {
    const name = 'Fixture ' + String(index).padStart(3, '0') + '.dll';
    fs.writeFileSync(path.join(f.output, name), 'generated');
    f.scope.files.push(f.evidence('src/Demo/bin/Release/net10.0/' + name));
  }
  f.save();
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.equal(applied.status, 0, JSON.stringify(applied.receipt));
  assert.equal(applied.receipt.removed.length, 140);
  assert.ok(fs.existsSync(path.join(f.output, 'settings.json')));
  assert.ok(fs.existsSync(path.join(f.output, 'AiGatewayObserver.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
});

test('refuses a linked ancestor in an isolated test checkout', windowsOnly, t => {
  const f = fixture(t);
  const external = path.join(f.base, 'isolated-external');
  const junction = path.join(f.worktree, 'src', 'Demo', 'bin', 'Release', 'escape');
  fs.mkdirSync(external);
  fs.writeFileSync(path.join(external, 'Escape.dll'), 'do not touch');
  fs.symlinkSync(external, junction, 'junction');
  f.scope.files = [f.evidence('src/Demo/bin/Release/escape/Escape.dll')];
  f.save();
  assert.notEqual(f.invoke().status, 0);
  assert.ok(fs.existsSync(path.join(external, 'Escape.dll')));
  assert.ok(fs.existsSync(path.join(f.output, 'Demo.dll')));
});


test('removes empty build directories without removing their project', windowsOnly, t => {
  const f = fixture(t);
  fs.unlinkSync(path.join(f.output, 'settings.json'));
  fs.unlinkSync(path.join(f.output, 'AiGatewayObserver.dll'));
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.equal(applied.status, 0, JSON.stringify(applied.receipt));
  assert.equal(fs.existsSync(path.join(f.worktree, 'src', 'Demo', 'bin')), false);
  assert.ok(fs.existsSync(path.join(f.worktree, 'src', 'Demo', 'Demo.csproj')));
  assert.equal(f.git('rev-parse', 'refs/heads/' + f.branch), f.head);
});
