import assert from 'node:assert/strict';
import test from 'node:test';

import { redact } from '../redaction.mjs';

test('redact strips URL userinfo, query names and values, and fragments recursively', () => {
  const canary = 'credential-canary';
  const result = redact({
    command: ['node', `https://user:${canary}@example.test/path?${canary}=${canary}#${canary}`],
    error: `request failed: https://example.test/path?${canary}=${canary}#${canary}`,
    nested: { token: canary },
    context: { execution_key: canary },
    provider: { provider_key: canary },
    detail: `response context: {"execution_key":"${canary}"}`,
  });
  const persisted = JSON.stringify(result);
  assert.equal(persisted.includes(canary), false);
  assert.match(result.command[1], /^https:\/\/example\.test\/path$/);
  assert.equal(result.nested.token, '[REDACTED]');
  assert.equal(result.context.execution_key, '[REDACTED]');
  assert.equal(result.provider.provider_key, '[REDACTED]');
  assert.equal(result.detail.includes(canary), false);
});

test('redact preserves descriptor structure while replacing sensitive values', () => {
  const first = 'credential-canary-descriptor-first';
  const second = 'credential-canary-descriptor-second';
  const fixture = (secret) => ({
    headers: [
      { name: 'Authorization', value: `Bearer ${secret}`, status: 'active' },
      { key: 'Cookie', values: [secret, secret] },
      { type: 'provider', value: secret },
      ['provider-key', secret],
      { name: 'X-Correlation-Id', value: 'corr-42' },
      { type: 'execution', value: 'completed' },
      { name: 'rapid-mode', value: 'enabled' },
      { label: 'api-version', value: '2026-09-23' },
      ['execution', 'completed'],
    ],
    nestedJson: JSON.stringify({
      executionContext: { descriptor: 'execution key', val: secret },
      timestamp: '2026-09-23T19:00:00.000Z',
    }),
  });

  const redactedFirst = redact(fixture(first));
  const redactedSecond = redact(fixture(second));
  assert.deepEqual(redactedFirst, redactedSecond);
  assert.deepEqual(redactedFirst.headers, [
    { name: 'Authorization', value: '[REDACTED]', status: 'active' },
    { key: 'Cookie', values: ['[REDACTED]', '[REDACTED]'] },
    { type: 'provider', value: '[REDACTED]' },
    ['provider-key', '[REDACTED]'],
    { name: 'X-Correlation-Id', value: 'corr-42' },
    { type: 'execution', value: 'completed' },
    { name: 'rapid-mode', value: 'enabled' },
    { label: 'api-version', value: '2026-09-23' },
    ['execution', 'completed'],
  ]);
  assert.deepEqual(JSON.parse(redactedFirst.nestedJson), {
    executionContext: { descriptor: 'execution key', val: '[REDACTED]' },
    timestamp: '2026-09-23T19:00:00.000Z',
  });
  assert.equal(JSON.stringify(redactedFirst).includes(first), false);
  assert.equal(JSON.stringify(redactedSecond).includes(second), false);
});
