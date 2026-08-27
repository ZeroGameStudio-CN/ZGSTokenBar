import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const workflow = fs.readFileSync('.github/workflows/ci.yml', 'utf8');

test('public CI runs the complete CLI graduation gate on hosted Windows', () => {
  assert.match(workflow, /runs-on:\s+windows-latest/);
  assert.match(workflow, /actions\/checkout@v4/);
  assert.match(workflow, /actions\/setup-dotnet@v4/);
  assert.match(workflow, /global-json-file:\s+global\.json/);
  assert.match(workflow, /actions\/setup-node@v4/);
  assert.match(workflow, /node-version:\s+24/);
  assert.match(workflow, /npm ci/);
  assert.match(workflow, /npm run verify/);
  assert.doesNotMatch(workflow, /self-hosted|zgs-build-executor|tail\.zerogamestudio/i);
});
