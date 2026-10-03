import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import {
  buildFoundationProbeImage,
  buildImageReceipt,
  verifyPublishedFoundationProbeImage,
  verifyPublishedImageReceipt,
} from '../build-foundation-probe-image.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const sourceHash = createHash('sha256')
  .update('infra/bicep/main.bicep').update('\0').update('content\n').update('\0').digest('hex');
const configDigest = `sha256:${'c'.repeat(64)}`;
const imageReference = `agentweaver-foundation-probe:source-${sourceSha}`;

function inspected(overrides = {}) {
  return {
    Id: configDigest,
    RepoDigests: [],
    Config: {
      User: '10001:10001',
      Labels: {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': sourceTree,
        'io.agentweaver.infrastructure-source-hash': sourceHash,
      },
    },
    ...overrides,
  };
}

test('build receipt binds a clean Git source to Docker-inspected local image metadata', () => {
  const calls = [];
  const writes = [];
  const run = (command, args) => {
    calls.push([command, args]);
    if (command === 'git' && args[0] === 'status') return '';
    if (command === 'git' && args[0] === 'rev-parse' && args[1] === 'HEAD') return `${sourceSha}\n`;
    if (command === 'git' && args[0] === 'rev-parse') return `${sourceTree}\n`;
    if (command === 'git' && args[0] === 'ls-files' && args[1] === '--others') return '';
    if (command === 'git' && args[0] === 'ls-files') return 'infra/bicep/main.bicep\n';
    if (command === 'git' && args[0] === 'show') return 'content\n';
    if (command === 'docker' && args[0] === 'image') return JSON.stringify([inspected()]);
    return '';
  };
  const result = buildFoundationProbeImage({
    repoRoot: 'repo',
    run,
    readFile: () => Buffer.from('content\n'),
    writeReceipt: (...args) => writes.push(args),
  });

  assert.equal(result.receipt.kind, 'foundation-probe-image');
  assert.equal(result.receipt.schemaVersion, 1);
  assert.equal(result.receipt.sourceSha, sourceSha);
  assert.equal(result.receipt.sourceTree, sourceTree);
  assert.equal(result.receipt.sourceHash, sourceHash);
  assert.equal(result.receipt.image.localReference, imageReference);
  assert.equal(result.receipt.image.localConfigDigest, configDigest);
  assert.equal(result.receipt.image.configUser, '10001:10001');
  assert.deepEqual(result.receipt.image.repositoryDigests, []);
  assert.equal('manifestDigest' in result.receipt.image, false);
  assert.equal(calls.some(([command, args]) => command === 'docker' && args[0] === 'push'), false);
  assert.equal(writes.length, 1);
  assert.equal(writes[0][0], result.receiptPath);
});

test('dirty source is rejected before Docker can build an image', () => {
  const calls = [];
  assert.throws(() => buildFoundationProbeImage({
    run(command, args) {
      calls.push([command, args]);
      return command === 'git' && args[0] === 'status' ? ' M source.cs\n' : '';
    },
    writeReceipt() {},
  }), /clean Git working tree/);
  assert.equal(calls.some(([command]) => command === 'docker'), false);
});

test('caller-provided source values cannot replace Git-derived source', () => {
  const run = (command, args) => {
    if (command === 'git' && args[0] === 'status') return '';
    if (command === 'git' && args[0] === 'rev-parse' && args[1] === 'HEAD') return `${sourceSha}\n`;
    if (command === 'git' && args[0] === 'rev-parse') return `${sourceTree}\n`;
    if (command === 'git' && args[0] === 'ls-files' && args[1] === '--others') return '';
    if (command === 'git' && args[0] === 'ls-files') return 'infra/bicep/main.bicep\n';
    if (command === 'git' && args[0] === 'show') return 'content\n';
    if (command === 'docker' && args[0] === 'image') return JSON.stringify([inspected()]);
    return '';
  };
  const result = buildFoundationProbeImage({
    sourceSha: 'd'.repeat(40),
    sourceTree: 'e'.repeat(40),
    run,
    readFile: () => Buffer.from('content\n'),
    writeReceipt() {},
  });
  assert.equal(result.receipt.sourceSha, sourceSha);
  assert.equal(result.receipt.sourceTree, sourceTree);
  assert.equal(result.receipt.sourceHash, sourceHash);
});

test('receipt rejects a local image whose embedded labels mismatch source', () => {
  assert.throws(() => buildImageReceipt({
    sourceSha,
    sourceTree,
    sourceHash,
    imageReference,
    inspect: inspected({ Config: { Labels: {
      'org.opencontainers.image.revision': sourceSha,
      'io.agentweaver.source-tree': 'f'.repeat(40),
      'io.agentweaver.infrastructure-source-hash': sourceHash,
    } } }),
  }), /labels do not match/);
});

test('receipt rejects images without an explicit numeric non-root UID and GID', () => {
  assert.throws(() => buildImageReceipt({
    sourceSha,
    sourceTree,
    sourceHash,
    imageReference,
    inspect: inspected({ Config: {
      User: 'foundationprobe',
      Labels: {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': sourceTree,
        'io.agentweaver.infrastructure-source-hash': sourceHash,
      },
    } }),
  }), /numeric non-root UID and GID/);
});

test('receipt rejects missing or malformed Docker digests instead of trusting caller claims', () => {
  for (const RepoDigests of [undefined, ['registry.invalid/probe:tag'], ['registry.invalid/probe@sha256:bad']]) {
    assert.throws(() => buildImageReceipt({
      sourceSha,
      sourceTree,
      sourceHash,
      imageReference,
      inspect: inspected({ RepoDigests }),
    }), /repository manifest digests/);
  }
});

test('published digest is accepted only from Docker inspection of the same local image config', () => {
  const localReceipt = buildImageReceipt({
    sourceSha, sourceTree, sourceHash, imageReference, inspect: inspected(),
  });
  const registryReference = 'registry.example/agentweaver/foundation-probe:v1';
  const digest = `registry.example/agentweaver/foundation-probe@sha256:${'d'.repeat(64)}`;
  const verified = verifyPublishedImageReceipt(localReceipt, registryReference, inspected({
    RepoDigests: [digest],
  }));
  assert.equal(verified.image.localConfigDigest, configDigest);
  assert.equal(verified.image.publishedReference, registryReference);
  assert.deepEqual(verified.image.repositoryDigests, [digest]);

  assert.throws(() => verifyPublishedImageReceipt(localReceipt, registryReference, inspected({
    Id: `sha256:${'e'.repeat(64)}`,
    RepoDigests: [digest],
  })), /does not match/);
  assert.throws(() => verifyPublishedImageReceipt(localReceipt,
    'registry.example/agentweaver/foundation-probe@sha256:' + 'd'.repeat(64), inspected({
      RepoDigests: [digest],
    })), /without a caller-supplied digest/);
  assert.throws(() => verifyPublishedImageReceipt(localReceipt, registryReference, inspected({
    RepoDigests: [],
  })), /registry manifest digest/);
});

test('published image verifier pulls and inspects an image without accepting digest input or pushing', () => {
  const localReceipt = buildImageReceipt({
    sourceSha, sourceTree, sourceHash, imageReference, inspect: inspected(),
  });
  const registryReference = 'registry.example/agentweaver/foundation-probe:v1';
  const digest = `registry.example/agentweaver/foundation-probe@sha256:${'d'.repeat(64)}`;
  const calls = [];
  const writes = [];
  const result = verifyPublishedFoundationProbeImage(registryReference, {
    repoRoot: 'repo',
    readFile: () => localReceipt,
    run(command, args) {
      calls.push([command, args]);
      if (command === 'docker' && args[0] === 'image') {
        return JSON.stringify([inspected({ RepoDigests: [digest] })]);
      }
      return '';
    },
    writeReceipt: (...args) => writes.push(args),
  });
  assert.deepEqual(calls.map(([, args]) => args[0]), ['pull', 'image']);
  assert.equal(calls.some(([command, args]) => command === 'docker' && args[0] === 'push'), false);
  assert.deepEqual(result.receipt.image.repositoryDigests, [digest]);
  assert.equal(writes.length, 1);
});
