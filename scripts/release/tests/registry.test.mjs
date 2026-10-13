import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { verifyServiceRegistryImage } from '../registry.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const version = '1.0.0';
const topReference = 'ghcr.io/sabbour/agentweaver-events-and-sessions@';

function digest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

function serviceFixture({ duplicateAmd64 = false, mutateConfig = () => {}, tamper } = {}) {
  const config = {
    os: 'linux',
    architecture: 'amd64',
    config: {
      Labels: {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': sourceTree,
        'org.opencontainers.image.version': version,
      },
    },
  };
  mutateConfig(config);
  const configBytes = Buffer.from(JSON.stringify(config));
  const configDigest = digest(configBytes);
  const childBytes = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: {
      mediaType: 'application/vnd.oci.image.config.v1+json',
      digest: configDigest,
      size: configBytes.length,
    },
  }));
  const childDigest = digest(childBytes);
  const descriptor = {
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    digest: childDigest,
    size: childBytes.length,
    platform: { os: 'linux', architecture: 'amd64' },
  };
  const topBytes = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.index.v1+json',
    manifests: duplicateAmd64 ? [descriptor, { ...descriptor }] : [descriptor],
  }));
  const topDigest = digest(topBytes);
  const calls = [];
  const response = (bytes, manifestDigest) => ({
    ok: true,
    status: 200,
    headers: { get: name => name.toLowerCase() === 'docker-content-digest' ? manifestDigest : null },
    text: async () => bytes.toString('utf8'),
    arrayBuffer: async () => bytes,
  });
  const fetchImpl = async (url, options = {}) => {
    const value = String(url);
    calls.push([value, options]);
    if (value.startsWith('https://ghcr.io/token?')) {
      return { ok: true, status: 200, json: async () => ({ token: 'registry-pull-token' }) };
    }
    if (value.endsWith(`/manifests/${topDigest}`)) {
      return response(tamper === 'top' ? Buffer.from('{"tampered":true}') : topBytes, topDigest);
    }
    if (value.endsWith(`/manifests/${childDigest}`)) {
      return response(tamper === 'child' ? Buffer.from('{"tampered":true}') : childBytes, childDigest);
    }
    if (value.endsWith(`/blobs/${configDigest}`)) {
      return response(tamper === 'config' ? Buffer.from('{"tampered":true}') : configBytes, null);
    }
    assert.fail(`unexpected registry request: ${value}`);
  };
  return { calls, fetchImpl, childDigest, topDigest, configBytes, topReference };
}

test('private service registry verification checks raw index, unique amd64 child, and source-bound config', async () => {
  const fixture = serviceFixture();
  const result = await verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, {
    sourceSha,
    sourceTree,
    version,
    username: 'test-user',
    token: 'test-token',
    fetchImpl: fixture.fetchImpl,
  });

  assert.equal(result.image, `${fixture.topReference}${fixture.childDigest}`);
  assert.equal(result.digest, fixture.childDigest);
  assert.deepEqual([...result.acceptedDigests], [fixture.topDigest, fixture.childDigest]);
  assert.equal(fixture.calls[0][1].headers.authorization,
    `Basic ${Buffer.from('test-user:test-token').toString('base64')}`);
  assert.ok(fixture.calls.slice(1).every(([, options]) =>
    options.headers.authorization === 'Bearer registry-pull-token'));
});

test('private service registry verification rejects duplicate amd64 children', async () => {
  const fixture = serviceFixture({ duplicateAmd64: true });
  await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, {
    sourceSha,
    sourceTree,
    version,
    username: 'test-user',
    token: 'test-token',
    fetchImpl: fixture.fetchImpl,
  }), /exactly one valid linux\/amd64 platform manifest/);
});

test('private service registry verification rejects mismatched source labels', async () => {
  const fixture = serviceFixture({
    mutateConfig(config) {
      config.config.Labels['org.opencontainers.image.revision'] = 'b'.repeat(40);
    },
  });
  await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, {
    sourceSha,
    sourceTree,
    version,
    username: 'test-user',
    token: 'test-token',
    fetchImpl: fixture.fetchImpl,
  }), /does not match its source, tree, version, and linux\/amd64 provenance/);
});

test('private service registry verification rejects non-matching raw registry bytes', async () => {
  for (const tamper of ['top', 'child', 'config']) {
    const fixture = serviceFixture({ tamper });
    await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, {
      sourceSha,
      sourceTree,
      version,
      username: 'test-user',
      token: 'test-token',
      fetchImpl: fixture.fetchImpl,
    }), /bytes do not match the exact published digest|config bytes do not match their manifest descriptor/);
  }
});
