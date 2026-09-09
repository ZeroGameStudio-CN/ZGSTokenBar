import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const [cliPath, fixtureRoot] = process.argv.slice(2);
if (!cliPath || !fixtureRoot) throw new Error('Expected published CLI path and fixture root.');
fs.mkdirSync(fixtureRoot, { recursive: true });
const profile = path.join(fixtureRoot, 'codex-profile');
fs.mkdirSync(profile);
const configPath = path.join(profile, 'config.toml');
const config = 'model = "synthetic-root"\n';
fs.writeFileSync(configPath, config);

function invoke(args, status) {
  const result = spawnSync(cliPath, ['--json', ...args], { encoding: 'utf8', windowsHide: true });
  if (result.error) throw result.error;
  assert.equal(result.status, status, result.stdout + result.stderr);
  assert.equal(result.stderr, '');
  return JSON.parse(result.stdout);
}

const help = invoke(['help'], 0);
assert.doesNotMatch(JSON.stringify(help.result), /economy|sol-luna-delegation/);
for (const args of [
  ['economy', 'status'],
  ['economy', 'install'],
  ['economy', 'set', 'on'],
]) {
  const payload = invoke([...args, '--codex-home', profile], 2);
  assert.equal(payload.error.code, 'unknown_command');
  assert.equal(fs.readFileSync(configPath, 'utf8'), config);
  assert.deepEqual(fs.readdirSync(profile), ['config.toml']);
}
const absentProfile = path.join(fixtureRoot, 'must-not-be-created');
invoke(['economy', 'status', '--codex-home', absentProfile], 2);
assert.equal(fs.existsSync(absentProfile), false);
console.log('PASS removed CLI routes are unknown and have no profile side effects');
