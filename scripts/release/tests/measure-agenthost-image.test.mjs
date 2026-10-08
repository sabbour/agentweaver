import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';
import test from 'node:test';
import { measureAgentHostImage } from '../measure-agenthost-image.mjs';

function fixture(change) {
  const source = { sha: 'a'.repeat(40), tree: 'b'.repeat(40) };
  const entries = new Map();
  function blob(bytes, mediaType) {
    const digest = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
    entries.set(`blobs/sha256/${digest.slice(7)}`, bytes);
    return { mediaType, digest, size: bytes.length };
  }
  const config = {
    os: 'linux', architecture: change === 'platform' ? 'arm64' : 'amd64',
    config: {
      User: change === 'root' ? '0:0' : '1654:1654',
      Entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
      Labels: { 'org.opencontainers.image.revision': source.sha, 'io.agentweaver.source-tree': source.tree },
    },
  };
  if (change === 'source') config.config.Labels['org.opencontainers.image.revision'] = 'c'.repeat(40);
  const layer = blob(gzipSync(Buffer.from('controlled image content')), 'application/vnd.oci.image.layer.v1.tar+gzip');
  if (change === 'uncompressed') layer.mediaType = 'application/vnd.oci.image.layer.v1.tar';
  const manifest = {
    schemaVersion: 2, mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: blob(Buffer.from(JSON.stringify(config)), 'application/vnd.oci.image.config.v1+json'),
    layers: [layer, { ...layer }],
  };
  if (change === 'size') manifest.layers[0].size++;
  if (change === 'conflict') manifest.layers[1].size++;
  const descriptor = blob(Buffer.from(JSON.stringify(manifest)), manifest.mediaType);
  if (change === 'manifest') entries.set(`blobs/sha256/${descriptor.digest.slice(7)}`, Buffer.from('{}'));
  const index = Buffer.from(JSON.stringify({ schemaVersion: 2, manifests: [descriptor] }));
  return {
    source, index, manifest, descriptor, entries,
    read: async entry => {
      assert.ok(entries.has(entry), `Unexpected OCI entry: ${entry}`);
      return entries.get(entry);
    },
  };
}

test('pull-byte measurement uses exact compressed blobs, deduplicates layers, and pins the platform manifest', async () => {
  const f = fixture();
  const receipt = await measureAgentHostImage(f.index, f.read, f.source);
  assert.equal(receipt.image.digest, f.descriptor.digest);
  assert.equal(receipt.image.platform, 'linux/amd64');
  assert.equal(receipt.image.compressedPullBytes,
    f.descriptor.size + f.manifest.config.size + f.manifest.layers[0].size);
  assert.notEqual(receipt.image.digest, f.manifest.config.digest);
});

for (const change of ['platform', 'root', 'source', 'uncompressed', 'size', 'conflict', 'manifest']) {
  test(`rejects invalid OCI ${change} evidence instead of substituting inspect.Size`, async () => {
    const f = fixture(change);
    await assert.rejects(() => measureAgentHostImage(f.index, f.read, f.source));
  });
}

test('rejects a tag index with multiple platforms', async () => {
  const f = fixture();
  await assert.rejects(() => measureAgentHostImage(
    Buffer.from(JSON.stringify({ schemaVersion: 2, manifests: [f.descriptor, f.descriptor] })), f.read, f.source));
});
