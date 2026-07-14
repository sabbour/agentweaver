import test from 'node:test';
import assert from 'node:assert/strict';
import { assertTargetAllowed } from '../target-guard.mjs';

test('target guard accepts localhost and staging targets', () => {
  assert.doesNotThrow(() => assertTargetAllowed('http://127.0.0.1:5000'));
  assert.doesNotThrow(() => assertTargetAllowed('https://agentweaver.westus.staging.aksapp.io'));
});

test('target guard rejects production regardless of insecure TLS', () => {
  assert.throws(() => assertTargetAllowed('https://agentweaver.example.com', { insecure: true }), /refusing non-staging/i);
});

test('target guard requires both production confirmations', () => {
  assert.throws(() => assertTargetAllowed('https://agentweaver.example.com', { allowProd: true }), /refusing non-staging/i);
  assert.doesNotThrow(() => assertTargetAllowed('https://agentweaver.example.com', {
    allowProd: true, confirmProduction: true,
  }));
});
