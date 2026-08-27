import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const workflow = fs.readFileSync('.github/workflows/ci.yml', 'utf8');

test('public CI runs the complete CLI graduation gate on hosted Windows', () => {
  assert.match(workflow, /runs-on:\s+windows-latest/);
  assert.match(workflow, /actions\/checkout@[0-9a-f]{40} # v7\.0\.1/);
  assert.match(workflow, /actions\/setup-dotnet@[0-9a-f]{40} # v6\.0\.0/);
  assert.match(workflow, /global-json-file:\s+global\.json/);
  assert.match(workflow, /actions\/setup-node@[0-9a-f]{40} # v7\.0\.0/);
  assert.match(workflow, /node-version:\s+24/);
  assert.match(workflow, /npm ci/);
  assert.match(workflow, /npm run verify/);
  assert.doesNotMatch(workflow, /self-hosted|zgs-build-executor|tail\.zerogamestudio/i);
});
