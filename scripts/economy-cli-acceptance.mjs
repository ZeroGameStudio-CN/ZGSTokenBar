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

payload = json(['economy', 'install', '--codex-home', profile]);
assert.equal(payload.result.mode, 'task');
assert.equal(payload.result.policy, 'task-scoped-confirmation');
assert.equal(payload.result.ready, true);
assert.equal(payload.result.skillInstalled, true);
assert.equal(readConfig(profile).startsWith(originalConfig), true);
assert.doesNotMatch(readConfig(profile), /default_subagent_model|default_subagent_reasoning_effort/);
assert.equal(fs.existsSync(path.join(profile, 'skills', 'sol-luna-delegation', 'scripts', 'set_economy_mode.py')), false);
const installedConfig = readConfig(profile);
json(['economy', 'install', '--codex-home', profile]);
assert.equal(readConfig(profile), installedConfig);

for (const mode of ['off', 'ask', 'on']) {
  const home = fixture('legacy-' + mode);
  const skillPath = path.join(home, 'skills', 'sol-luna-delegation', 'SKILL.md');
  const defaults = mode === 'on'
    ? '# BEGIN sol-luna-delegation economy agent defaults\ndefault_subagent_model = "gpt-5.6-luna"\ndefault_subagent_reasoning_effort = "max"\n# END sol-luna-delegation economy agent defaults\n'
    : '';
  const legacy = originalConfig + '[agents]\nmax_concurrent_threads_per_session = 3\n' + defaults
    + '\n# BEGIN sol-luna-delegation economy skill switch\n[[skills.config]]\n'
    + 'path = ' + JSON.stringify(skillPath) + '\nenabled = ' + (mode !== 'off')
    + '\n# END sol-luna-delegation economy skill switch\n';
  fs.writeFileSync(path.join(home, 'config.toml'), legacy);
  assert.equal(json(['economy', 'status', '--codex-home', home]).result.mode, mode);
  assert.equal(json(['economy', 'install', '--codex-home', home]).result.ready, true);
  assert.equal(readConfig(home).startsWith(originalConfig), true);
  assert.match(readConfig(home), /max_concurrent_threads_per_session = 3/);
  assert.doesNotMatch(readConfig(home), /default_subagent_model|default_subagent_reasoning_effort/);
}

const manual = originalConfig + '[agents]\ndefault_subagent_model = "manual-model"\ndefault_subagent_reasoning_effort = "low"\n';
const manualProfile = fixture('manual-defaults', manual);
assert.equal(json(['economy', 'install', '--codex-home', manualProfile]).result.ready, true);
assert.equal(readConfig(manualProfile).startsWith(manual), true);

for (const mode of ['off', 'ask', 'on', 'turbo']) {
  payload = json(['economy', 'set', mode, '--codex-home', profile], 2);
  assert.equal(payload.error.code, 'invalid_arguments');
  assert.equal(readConfig(profile), installedConfig);
}

const conflictProfile = path.join(fixtureRoot, 'unmanaged-skill');
const conflictSkillPath = path.join(conflictProfile, 'skills', 'sol-luna-delegation', 'SKILL.md');
const conflict = '[[skills.config]]\npath = ' + JSON.stringify(conflictSkillPath) + '\nenabled = false\n';
fixture('unmanaged-skill', conflict);
payload = json(['economy', 'install', '--codex-home', conflictProfile], 4);
assert.equal(payload.error.code, 'codex_economy_conflict');
assert.equal(readConfig(conflictProfile), conflict);
assert.equal(fs.existsSync(path.dirname(conflictSkillPath)), false);

console.log('PASS task-scoped assistant CLI acceptance');
