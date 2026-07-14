import test from 'node:test';
import assert from 'node:assert/strict';
import { checkCapabilities } from '../lib/capabilities-contract.mjs';

const contract = {
  contractVersion: '1.0.0',
  capabilities: [{
    capability: 'submit-run', tools: ['run_submit'],
    in: { requires: { project_id: 'string', task: 'string' } },
    out: { requires: { run_id: 'string', status: 'string' } },
  }, { capability: 'one-call', tools: ['run_task'], optional: true }],
};
const tool = {
  name: 'run_submit',
  inputSchema: { type: 'object', required: ['project_id', 'task'], properties: { project_id: { type: 'string' }, task: { type: 'string' }, extra: { type: 'boolean' } } },
  outputSchema: { type: 'object', required: ['run_id', 'status'], properties: { run_id: { type: 'string' }, status: { type: 'string' }, extra: { type: 'string' } } },
};

test('capability contract accepts additive live schema changes', () => {
  const report = checkCapabilities([tool], contract);
  assert.equal(report.ok, true);
  assert.equal(report.results.find((item) => item.capability === 'one-call').status, 'SKIP');
});

test('capability contract rejects removal and type changes', () => {
  const missing = checkCapabilities([], contract);
  assert.equal(missing.ok, false);
  assert.match(missing.results[0].message, /required tool missing/);
  const changed = structuredClone(tool);
  changed.inputSchema.properties.project_id.type = 'number';
  const report = checkCapabilities([changed], contract);
  assert.equal(report.ok, false);
  assert.match(report.results[0].message, /type changed/);
});
