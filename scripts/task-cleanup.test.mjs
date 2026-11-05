import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const scriptSource = path.resolve('scripts/cleanup-task.ps1');

function run(command, args, cwd, env) {
  const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', input: '', timeout: 20000 });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${command}: ${result.stderr || result.stdout}`);
  return result.stdout.trim();
}

function fixture(t) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'zgstokenbar-cleanup-test-'));
  const taskId = `cleanup-test-${randomUUID().replaceAll('-', '')}`;
  const output = path.join(os.tmpdir(), `zgstokenbar-${taskId}-${randomUUID().replaceAll('-', '')}`);
  const repo = path.join(base, 'repo');
  fs.mkdirSync(path.join(repo, 'scripts'), { recursive: true });
  fs.mkdirSync(path.join(output, 'nested'), { recursive: true });
  fs.writeFileSync(path.join(output, 'nested', 'artifact.txt'), 'task-owned data');
  fs.writeFileSync(path.join(output, 'literal [1].txt'), 'literal filename');
  const script = path.join(repo, 'scripts', 'cleanup-task.ps1');
  fs.copyFileSync(scriptSource, script);
  const env = { ...process.env, GIT_CONFIG_GLOBAL: path.join(base, 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1', GIT_TERMINAL_PROMPT: '0' };
  const git = (...args) => run('git', args, repo, env);
  git('init', '-b', 'main');
  git('config', 'user.name', 'Cleanup fixture');
  git('config', 'user.email', 'cleanup@example.test');
  git('add', 'scripts/cleanup-task.ps1');
  git('commit', '-m', 'fixture');
  const commit = git('rev-parse', 'HEAD');
  git('update-ref', 'refs/remotes/origin/main', commit);
  const branch = 'codex/cleanup-fixture';
  git('branch', branch);
  const manifest = path.join(repo, 'release', 'task-cleanup', taskId, 'manifest.json');
  const scope = { schemaVersion: 1, taskId, temporaryRoots: [output], localBranches: [{ name: branch, commit }] };
  const save = () => {
    fs.mkdirSync(path.dirname(manifest), { recursive: true });
    fs.writeFileSync(manifest, JSON.stringify(scope));
  };
  save();
  const invoke = (...args) => {
    const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', script, '-Manifest', manifest, ...args],
      { cwd: repo, env, encoding: 'utf8', input: '', timeout: 20000 });
    assert.ifError(result.error);
    assert.ok(result.stdout.trim().startsWith('{'), result.stderr || result.stdout);
    return { ...result, receipt: JSON.parse(result.stdout) };
  };
  t.after(() => {
    // These are exclusively synthetic test fixtures, never a user cleanup manifest.
    fs.rmSync(output, { recursive: true, force: true });
    fs.rmSync(base, { recursive: true, force: true });
  });
  return { base, repo, output, script, manifest, scope, save, invoke, git, branch, commit, env };
}

function rejected(result, f) {
  assert.notEqual(result.status, 0);
  assert.equal(result.receipt.ok, false);
  assert.ok(result.receipt.errors.length > 0);
  assert.ok(fs.existsSync(path.join(f.output, 'nested', 'artifact.txt')));
  assert.equal(f.git('rev-parse', `refs/heads/${f.branch}`), f.scope.localBranches[0].commit);
}

test('cleanup previews exact scope, applies with closed stdin, and is idempotent', t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  assert.equal(preview.receipt.mode, 'preview');
  assert.match(preview.receipt.planHash, /^[0-9a-f]{64}$/);
  assert.equal(preview.receipt.temporaryRoots[0].files, 2);
  assert.equal(preview.receipt.temporaryRoots[0].bytes, Buffer.byteLength('task-owned data') + Buffer.byteLength('literal filename'));
  assert.ok(fs.existsSync(f.output));
  assert.equal(f.git('rev-parse', `refs/heads/${f.branch}`), f.commit);
  const applied = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.equal(applied.status, 0, JSON.stringify(applied.receipt));
  assert.equal(applied.receipt.ok, true);
  assert.equal(applied.receipt.temporaryRoots[0].status, 'removed');
  assert.equal(applied.receipt.localBranches[0].status, 'removed');
  assert.equal(fs.existsSync(f.output), false);
  assert.equal(f.git('branch', '--list', f.branch), '');
  const repeated = f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash);
  assert.equal(repeated.status, 0, JSON.stringify(repeated.receipt));
  assert.equal(repeated.receipt.temporaryRoots[0].status, 'absent');
  assert.equal(repeated.receipt.localBranches[0].status, 'absent');
  assert.equal(JSON.parse(fs.readFileSync(path.join(path.dirname(f.manifest), 'result.json'))).ok, true);
});

test('cleanup refuses changed plans and missing plan hashes before any deletion', t => {
  const f = fixture(t);
  const preview = f.invoke();
  rejected(f.invoke('-Apply'), f);
  fs.writeFileSync(path.join(f.output, 'new-file.txt'), 'created after preview');
  rejected(f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash), f);
  assert.ok(fs.existsSync(path.join(f.output, 'new-file.txt')));
});

test('cleanup refuses workspace paths, other task names and unregistered manifest fields', t => {
  const f = fixture(t);
  f.scope.temporaryRoots = [f.repo];
  f.save();
  rejected(f.invoke(), f);
  f.scope.temporaryRoots = [f.output];
  f.scope.taskId = 'someone-else';
  f.save();
  rejected(f.invoke(), f);
  f.scope.taskId = path.basename(path.dirname(f.manifest));
  f.scope.allowOutsideTemp = true;
  f.save();
  rejected(f.invoke(), f);
});

test('cleanup refuses a nested junction without following or deleting its target', t => {
  const f = fixture(t);
  const protectedDirectory = path.join(f.base, 'protected');
  fs.mkdirSync(protectedDirectory);
  fs.writeFileSync(path.join(protectedDirectory, 'keep.txt'), 'must survive');
  fs.symlinkSync(protectedDirectory, path.join(f.output, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  rejected(f.invoke(), f);
  assert.equal(fs.readFileSync(path.join(protectedDirectory, 'keep.txt'), 'utf8'), 'must survive');
});

test('cleanup refuses a redirected output root and protects branches used by another worktree', t => {
  const f = fixture(t);
  const moved = path.join(f.base, 'moved-output');
  fs.renameSync(f.output, moved);
  fs.symlinkSync(moved, f.output, process.platform === 'win32' ? 'junction' : 'dir');
  rejected(f.invoke(), f);
  fs.unlinkSync(f.output);
  fs.renameSync(moved, f.output);
  f.git('worktree', 'add', path.join(f.base, 'other-worktree'), f.branch);
  rejected(f.invoke(), f);
});

test('cleanup binds preview to the entry script and file contents', t => {
  const f = fixture(t);
  const preview = f.invoke();
  fs.appendFileSync(f.script, '\n# changed after preview\n');
  rejected(f.invoke('-Apply', '-ExpectedPlanHash', preview.receipt.planHash), f);
  const next = f.invoke();
  fs.writeFileSync(path.join(f.output, 'nested', 'artifact.txt'), 'edited task data');
  rejected(f.invoke('-Apply', '-ExpectedPlanHash', next.receipt.planHash), f);
});

test('cleanup refuses checked-out, unmerged and changed branch tips', t => {
  const f = fixture(t);
  f.git('switch', f.branch);
  rejected(f.invoke(), f);
  fs.writeFileSync(path.join(f.repo, 'unmerged.txt'), 'unmerged');
  f.git('add', 'unmerged.txt');
  f.git('commit', '-m', 'unmerged task work');
  f.git('switch', 'main');
  const changed = f.invoke();
  assert.notEqual(changed.status, 0);
  assert.ok(fs.existsSync(path.join(f.output, 'nested', 'artifact.txt')));
  f.scope.localBranches[0].commit = f.git('rev-parse', `refs/heads/${f.branch}`);
  f.save();
  rejected(f.invoke(), f);
});

test('cleanup requires the task commit in both local and remote main', t => {
  const f = fixture(t);
  f.git('switch', f.branch);
  fs.writeFileSync(path.join(f.repo, 'task.txt'), 'task');
  f.git('add', 'task.txt');
  f.git('commit', '-m', 'task');
  f.scope.localBranches[0].commit = f.git('rev-parse', 'HEAD');
  f.git('switch', 'main');
  f.git('merge', '--ff-only', f.branch);
  f.save();
  rejected(f.invoke(), f);
});

test('cleanup detects an in-use file and exits instead of waiting for input', t => {
  const f = fixture(t);
  const preview = f.invoke();
  assert.equal(preview.status, 0, JSON.stringify(preview.receipt));
  const wrapper = path.join(f.base, 'hold-file.ps1');
  fs.writeFileSync(wrapper, `#Requires -Version 7.0
param([string] $File, [string] $Script, [string] $Manifest, [string] $Hash)
$held = [IO.File]::Open($File, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { & pwsh -NoProfile -NonInteractive -File $Script -Manifest $Manifest -Apply -ExpectedPlanHash $Hash; $code = $LASTEXITCODE }
finally { $held.Dispose() }
exit $code
`);
  const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', wrapper,
    '-File', path.join(f.output, 'nested', 'artifact.txt'), '-Script', f.script, '-Manifest', f.manifest,
    '-Hash', preview.receipt.planHash], { cwd: f.repo, env: f.env, encoding: 'utf8', input: '', timeout: 20000 });
  assert.ifError(result.error);
  rejected({ ...result, receipt: JSON.parse(result.stdout) }, f);
});
