import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { applyTargetValues, targetValuePairs } from '../target-values.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');

test('the default target keeps the tracked manifests and unknown targets fail', () => {
  assert.deepEqual(targetValuePairs(root), []);
  assert.equal(applyTargetValues('agentweaver-pg.postgres.database.azure.com', []),
    'agentweaver-pg.postgres.database.azure.com');
  assert.throws(() => targetValuePairs(root, 'unknown'), /not a registered install target/);
});

test('awwus2np01 values replace every default-target value and keep the fixed P1 role names', () => {
  const files = execFileSync('git', ['ls-files', 'deploy/k8s'], { cwd: root, encoding: 'utf8' })
    .split('\n').filter(file => file && !file.startsWith('deploy/k8s/targets/'));
  const text = files.map(file => readFileSync(path.join(root, file), 'utf8')).join('\n');
  const pairs = targetValuePairs(root, 'awwus2np01');
  for (const [from] of pairs) assert.ok(text.includes(from), `${from} no longer appears in deploy/k8s`);

  const result = applyTargetValues(text, pairs);
  for (const [from] of pairs) assert.ok(!result.includes(from), `${from} remains`);
  for (const value of ['eastus2euap', 'agentweaver-kv-v1', 'agentweaverblob', 'agentweaver-pg.',
    'agentweaver-id-identity-broker']) {
    assert.ok(!result.includes(value), `${value} remains`);
  }
  for (const role of ['agentweaver-id-projects-config', 'agentweaver-id-p1-postgres-migration']) {
    assert.ok(text.includes(role) && result.includes(role), `${role} must stay`);
  }
});
