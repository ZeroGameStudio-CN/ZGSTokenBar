import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const [cliPath, fixtureRoot] = process.argv.slice(2);
if (!cliPath || !fixtureRoot) {
  throw new Error('Usage: node scripts/economy-cli-acceptance.mjs <cli-path> <fixture-root>');
}
fs.mkdirSync(fixtureRoot, { recursive: true });

function json(args, expectedStatus = 0) {
  const result = spawnSync(cliPath, ['--json', ...args], { encoding: 'utf8', windowsHide: true });
  if (result.error) throw result.error;
  assert.equal(result.status, expectedStatus, result.stdout + result.stderr);
  assert.equal(result.stderr, '');
  return JSON.parse(result.stdout);
}

const originalConfig = 'model = "root-fixture"\nmodel_reasoning_effort = "high"\n';
function fixture(name, config = originalConfig) {
  const home = path.join(fixtureRoot, name);
  fs.mkdirSync(home, { recursive: true });
  fs.writeFileSync(path.join(home, 'config.toml'), config);
  return home;
}
const readConfig = home => fs.readFileSync(path.join(home, 'config.toml'), 'utf8');
const profile = fixture('profile');
let payload = json(['economy', 'status', '--codex-home', profile]);
assert.equal(payload.result.mode, 'unconfigured');
assert.equal(payload.result.ready, false);

// The skill is installed externally; the published CLI can only observe it.
const skillPath = path.join(profile, 'skills', 'sol-luna-delegation', 'SKILL.md');
fs.mkdirSync(path.dirname(skillPath), { recursive: true });
fs.writeFileSync(skillPath, 'external skill content\n');
payload = json(['economy', 'status', '--codex-home', profile]);
assert.equal(payload.result.mode, 'task');
assert.equal(payload.result.management, 'external');
assert.equal(payload.result.readOnly, true);
assert.equal(payload.result.ready, true);
assert.equal(readConfig(profile), originalConfig);
assert.equal(fs.existsSync(path.join(path.dirname(skillPath), '.zgstokenbar-skill.json')), false);

for (const command of [['install'], ['set', 'off'], ['set', 'ask'], ['set', 'on']]) {
  payload = json(['economy', ...command, '--codex-home', profile], 2);
  assert.equal(payload.error.code, 'invalid_arguments');
  assert.equal(readConfig(profile), originalConfig);
  assert.equal(fs.readFileSync(skillPath, 'utf8'), 'external skill content\n');
}

const disabledConfig = originalConfig + '[[skills.config]]\npath = ' + JSON.stringify(skillPath) + '\nenabled = false\n';
fs.writeFileSync(path.join(profile, 'config.toml'), disabledConfig);
payload = json(['economy', 'status', '--codex-home', profile]);
assert.equal(payload.result.mode, 'off');
assert.equal(payload.result.ready, false);
assert.equal(readConfig(profile), disabledConfig);
const missingHome = path.join(fixtureRoot, 'must-not-be-created');
json(['economy', 'install', '--codex-home', missingHome], 2);
json(['economy', 'status', '--codex-home', missingHome]);
assert.equal(fs.existsSync(missingHome), false);
console.log('PASS external skill read-only CLI acceptance');
