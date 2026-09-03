import test from 'node:test';
import assert from 'node:assert/strict';
import { isLoopbackTarget, networkTargetEvidence, validateNetworkTarget } from '../target-guard.mjs';

test('transport validation accepts arbitrary HTTPS and loopback HTTP targets', () => {
  assert.equal(validateNetworkTarget('https://agentweaver.example.com').origin, 'https://agentweaver.example.com');
  assert.equal(validateNetworkTarget('https://service.corp.internal:8443/api').pathname, '/api');
  assert.doesNotThrow(() => validateNetworkTarget('http://127.42.0.1:5000'));
  assert.doesNotThrow(() => validateNetworkTarget('http://dev.localhost:5000'));
  assert.equal(isLoopbackTarget('::1'), true);
});

test('transport validation rejects malformed or unsafe targets', () => {
  assert.throws(() => validateNetworkTarget('/relative'), /absolute http/);
  assert.throws(() => validateNetworkTarget('ftp://example.com'), /unsupported/);
  assert.throws(() => validateNetworkTarget('https://user:pass@example.com'), /userinfo/);
  assert.throws(() => validateNetworkTarget('https://example.com/#fragment'), /fragment/);
  assert.throws(() => validateNetworkTarget('http://example.com'), /HTTPS is required/);
});

test('sanitized preflight evidence records source and transport but never a credential value', () => {
  assert.deepEqual(networkTargetEvidence('https://arbitrary.example/mcp', {
    surface: 'mcp', authSource: 'environment', exactPath: '/mcp',
  }), {
    surface: 'mcp',
    transport: 'http',
    targetOrigin: 'https://arbitrary.example',
    targetPath: '/mcp',
    authSource: 'environment',
    projectId: null,
    runId: null,
    cleanupIntent: 'none',
    cleanupResult: 'not-started',
    tlsMode: 'system-default',
  });
});

test('persisted API target evidence strips query names, values, fragments, and userinfo', () => {
    const evidence = networkTargetEvidence('https://example.test/api/path?credential=canary', {
      surface: 'api',
      authSource: 'environment',
    });
    assert.equal(evidence.targetOrigin, 'https://example.test');
    assert.equal(evidence.targetPath, '/api/path');
    assert.doesNotMatch(JSON.stringify(evidence), /credential|canary/);
    assert.throws(
      () => networkTargetEvidence('https://user:secret@example.test/api', { surface: 'api' }),
      /userinfo/,
    );
    assert.throws(
      () => networkTargetEvidence('https://example.test/api#secret', { surface: 'api' }),
      /fragment/,
    );
});

test('exact path validation rejects path prefixes, suffixes, and trailing slashes', () => {
  assert.equal(validateNetworkTarget('https://example.com/mcp', { exactPath: '/mcp' }).pathname, '/mcp');
  for (const target of [
    'https://example.com/',
    'https://example.com/mcp/',
    'https://example.com/prefix/mcp',
    'https://example.com/mcp/extra',
    'https://example.com/mcp?transport=stream',
  ]) {
    assert.throws(() => validateNetworkTarget(target, { exactPath: '/mcp' }), /exactly "\/mcp"/);
  }
});

test('retired environment and TLS bypass flags are absent from active harness entry points', async () => {
  const { readFile } = await import('node:fs/promises');
  const { dirname, join } = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
  const files = [
    'api-harness/run-persona.mjs',
    'api-harness/lib/client.mjs',
    'ui-harness/agent-driver-ui/tools.mjs',
    'ui-harness/agent-driver-ui/session-worker.mjs',
    'mcp-harness/run-persona.mjs',
    'mcp-harness/smoke/mcp-cli-smoke.mjs',
  ];
  const retired = [
    ['allow', 'prod'].join('-'),
    ['i', 'understand', 'prod'].join('-'),
    ['allow', 'insecure', 'prod'].join('-'),
    ['confirm', 'production'].join('-'),
    ['NODE', 'TLS', 'REJECT', 'UNAUTHORIZED'].join('_'),
  ];
  for (const file of files) {
    const source = await readFile(join(root, file), 'utf8');
    for (const value of retired) assert.equal(source.includes(value), false, `${file} retains ${value}`);
  }
});
