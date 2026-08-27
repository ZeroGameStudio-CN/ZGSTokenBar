import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const [cliPath, fixtureRoot] = process.argv.slice(2);
if (!cliPath || !fixtureRoot) {
  throw new Error('Usage: node scripts/economy-cli-acceptance.mjs <cli-path> <fixture-root>');
}

fs.mkdirSync(fixtureRoot, { recursive: true });

function run(args) {
  const result = spawnSync(cliPath, args, {
    encoding: 'utf8',
    windowsHide: true,
  });
  if (result.error) throw result.error;
  return result;
}

function json(args, expectedStatus) {
  const result = run(args.includes('--json') ? args : ['--json', ...args]);
  assert.equal(result.status, expectedStatus, `${args.join(' ')} exit code\n${result.stderr}`);
  assert.equal(result.stderr, '', `${args.join(' ')} keeps JSON errors on stdout`);
  return JSON.parse(result.stdout);
}

const profile = path.join(fixtureRoot, 'profile');
fs.mkdirSync(profile, { recursive: true });

let payload = json(['economy', 'status', '--codex-home', profile], 0);
assert.equal(payload.ok, true);
assert.equal(payload.result.mode, 'unconfigured');
assert.equal(payload.result.skillInstalled, false);

payload = json(['economy', 'install', '--codex-home', profile], 0);
assert.equal(payload.result.mode, 'unconfigured');
assert.equal(payload.result.skillInstalled, true);

payload = json(['economy', 'set', 'ask', '--codex-home', profile], 0);
assert.equal(payload.result.mode, 'ask');
assert.equal(payload.result.skillInstalled, true);

payload = json(['economy', 'status', '--codex-home', profile, '--json'], 0);
assert.equal(payload.result.mode, 'ask');

const onResult = run(['economy', 'set', 'on', '--codex-home', profile]);
assert.equal(onResult.status, 0, onResult.stderr);
assert.equal(onResult.stdout.split(/\r?\n/, 1)[0], 'on');

payload = json(['economy', 'set', '--codex-home', profile], 2);
assert.equal(payload.ok, false);
assert.equal(payload.error.code, 'invalid_arguments');
payload = json(['economy', 'set', 'turbo', '--codex-home', profile], 2);
assert.equal(payload.ok, false);
assert.equal(payload.error.code, 'invalid_arguments');

const conflictProfile = path.join(fixtureRoot, 'conflict');
fs.mkdirSync(conflictProfile, { recursive: true });
const conflictPath = path.join(conflictProfile, 'config.toml');
const conflict = '[agents]\ndefault_subagent_model = "other"\n';
fs.writeFileSync(conflictPath, conflict);
payload = json(['economy', 'set', 'on', '--codex-home', conflictProfile], 4);
assert.equal(payload.ok, false);
assert.equal(payload.error.code, 'codex_economy_conflict');
assert.equal(fs.readFileSync(conflictPath, 'utf8'), conflict);
assert.equal(fs.existsSync(path.join(conflictProfile, 'skills', 'sol-luna-delegation')), false);

console.log('PASS economy CLI acceptance');
