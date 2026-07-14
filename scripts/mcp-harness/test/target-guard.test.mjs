import test from 'node:test';
import assert from 'node:assert/strict';
import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';

test('target guard allows local and staging targets', () => {
  assert.equal(assertTargetAllowed('http://localhost:5000/mcp'), undefined);
  assert.equal(assertTargetAllowed('https://mcp.staging.example.test/mcp'), undefined);
  assert.equal(assertTargetAllowed('https://mcp.staging/mcp'), undefined);
});

test('target guard requires two distinct production confirmations', () => {
  assert.throws(() => assertTargetAllowed('https://prod.example.test/mcp'), /--allow-prod/);
  assert.throws(() => assertTargetAllowed('https://prod.example.test/mcp', { allowProd: true }), /understand/);
  assert.doesNotThrow(() => assertTargetAllowed('https://prod.example.test/mcp', { allowProd: true, confirmProduction: true }));
});
