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

function serviceFixture({ duplicateAmd64 = false, mutateConfig = () => {}, tamper, missingToken = false } = {}) {
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
      return { ok: true, status: 200, json: async () => missingToken ? {} : ({ token: 'registry-pull-token' }) };
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
  return { calls, fetchImpl, childDigest, configDigest, topDigest, configBytes, topReference };
}

test('service registry verification checks raw index, unique amd64 child, and source-bound config', async () => {
  const fixture = serviceFixture();
  const result = await verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, fixture.fetchImpl);

  assert.equal(result.image, `${fixture.topReference}${fixture.childDigest}`);
  assert.equal(result.digest, fixture.childDigest);
  assert.deepEqual([...result.acceptedDigests], [fixture.topDigest, fixture.childDigest]);
  assert.equal(fixture.calls[0][1].headers.authorization, undefined);
  assert.ok(fixture.calls.slice(1).every(([, options]) =>
    options.headers.authorization === 'Bearer registry-pull-token'));
  assert.deepEqual({
    configDigest: result.configDigest,
    sourceSha: result.sourceSha,
    sourceTree: result.sourceTree,
    version: result.version,
  }, { configDigest: fixture.configDigest, sourceSha, sourceTree, version });
});

test('service registry verification rejects duplicate amd64 children', async () => {
  const fixture = serviceFixture({ duplicateAmd64: true });
  await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, fixture.fetchImpl),
    /exactly one valid linux\/amd64 platform manifest/);
});

test('service registry verification rejects missing and malformed identity labels', async () => {
  const invalidLabels = [
    labels => { delete labels['org.opencontainers.image.revision']; },
    labels => { labels['org.opencontainers.image.revision'] = 'not-a-sha'; },
    labels => { delete labels['io.agentweaver.source-tree']; },
    labels => { labels['io.agentweaver.source-tree'] = 'not-a-tree'; },
    labels => { delete labels['org.opencontainers.image.version']; },
    labels => { labels['org.opencontainers.image.version'] = ''; },
  ];
  for (const mutateLabels of invalidLabels) {
    const fixture = serviceFixture({
      mutateConfig: config => mutateLabels(config.config.Labels),
    });
    await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, fixture.fetchImpl),
      /does not contain valid source, tree, version, and linux\/amd64 identity/);
  }
});

test('service registry verification rejects non-matching raw registry bytes', async () => {
  for (const tamper of ['top', 'child', 'config']) {
    const fixture = serviceFixture({ tamper });
    await assert.rejects(verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, fixture.fetchImpl),
      /bytes do not match the exact published digest|config bytes do not match their manifest descriptor/);
  }
});

test('service registry verification reports authorization and token failures', async () => {
  const unauthorized = async () => ({ ok: false, status: 401 });
  await assert.rejects(verifyServiceRegistryImage(`${topReference}${`sha256:${'c'.repeat(64)}`}`, unauthorized),
    /anonymous pull verification \(HTTP 401\)/);

  const missingToken = serviceFixture({ missingToken: true });
  await assert.rejects(verifyServiceRegistryImage(
    `${missingToken.topReference}${missingToken.topDigest}`, missingToken.fetchImpl),
  /GHCR returned no pull token/);
});

test('service registry verification returns the actual allowlisted config labels', async () => {
  const actual = {
    sourceSha: 'c'.repeat(40),
    sourceTree: 'd'.repeat(40),
    version: '1.2.3-preview.4',
  };
  const fixture = serviceFixture({
    mutateConfig(config) {
      config.config.Labels['org.opencontainers.image.revision'] = actual.sourceSha;
      config.config.Labels['io.agentweaver.source-tree'] = actual.sourceTree;
      config.config.Labels['org.opencontainers.image.version'] = actual.version;
    },
  });
  const result = await verifyServiceRegistryImage(`${fixture.topReference}${fixture.topDigest}`, fixture.fetchImpl);
  assert.deepEqual(
    { sourceSha: result.sourceSha, sourceTree: result.sourceTree, version: result.version },
    actual,
  );
});
