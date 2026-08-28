import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const workflow = fs.readFileSync('.github/workflows/ci.yml', 'utf8');
const releaseWorkflow = fs.readFileSync('.github/workflows/release.yml', 'utf8');

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

test('tag releases fail closed unless signed portable assets pass the full gate', () => {
  assert.match(releaseWorkflow, /tags:\s*\['v\*'\]/);
  assert.match(releaseWorkflow, /contents:\s*write/);
  assert.match(releaseWorkflow, /npm run verify/);
  assert.match(releaseWorkflow, /WINDOWS_SIGNING_CERTIFICATE_BASE64/);
  assert.match(releaseWorkflow, /WINDOWS_SIGNING_CERTIFICATE_PASSWORD/);
  assert.match(releaseWorkflow, /ZTB_REQUIRE_SIGNATURE=1/);
  assert.match(releaseWorkflow, /gh release create/);
  assert.match(releaseWorkflow, /ZGSTokenBar-Portable-v\$version\.zip/);
  assert.match(releaseWorkflow, /ZGSTokenBar-v\$version-SHA256\.txt/);
});
