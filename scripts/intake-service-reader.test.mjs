import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('intake collector reads bounded metadata without writes, payload leakage or replay duplication', () => {
  const fixture = fileURLToPath(new URL('../tests/intake_service_reader_test.py', import.meta.url));
  const result = spawnSync('python', ['-I', '-B', fixture], { encoding: 'utf8', timeout: 30000, maxBuffer: 1024 * 1024 });
  assert.equal(result.status, 0, result.error?.message ?? result.stderr ?? result.stdout);
});
