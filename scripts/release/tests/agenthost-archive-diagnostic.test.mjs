import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { allowedFields, archivePins as pins, describeInspect, runAgentHostArchiveDiagnostic } from '../agenthost-archive-diagnostic.mjs';

const helper = 'b'.repeat(40);
const bytes = Buffer.from('synthetic archive');
const sha = createHash('sha256').update(bytes).digest('hex');
const env = {
  GITHUB_REPOSITORY: 'sabbour/agentweaver', GITHUB_REF: 'refs/heads/v1', GITHUB_EVENT_NAME: 'workflow_dispatch',
  EXPECTED_SOURCE: helper, GH_TOKEN: 'secret', GITHUB_TOKEN: 'secret',
};
const image = (overrides = {}) => ({
  Id: pins.configDigest, Os: 'linux', Architecture: 'amd64', Env: ['SECRET=raw'],
  Config: { User: pins.user, Entrypoint: pins.entrypoint, Labels: {
    'org.opencontainers.image.revision': pins.imageSourceSha, 'io.agentweaver.source-tree': pins.sourceTree,
    'org.opencontainers.image.version': pins.version, 'arbitrary.label': 'raw-label',
  } }, ...overrides,
});

function run(inspect, { hash = sha } = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'ah-diag-'));
  writeFileSync(path.join(dir, pins.archive), bytes);
  const calls = [];
  const spawn = (bin, args, options) => {
    calls.push([bin, ...args]);
    assert.equal(options.env.GH_TOKEN, undefined);
    if (bin === 'git') return { status: 0, stdout: args[0] === 'status' ? '' : helper, stderr: '' };
    return { status: 0, stdout: args[0] === 'image' ? JSON.stringify(inspect) : 'raw load output', stderr: '' };
  };
  try {
    return { calls, result: runAgentHostArchiveDiagnostic(dir, { env, spawn, expectedArchiveSha256: hash }) };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('hashes before load, only loads and inspects, and reports allowlisted booleans', () => {
  const { calls, result } = run(image());
  const docker = calls.filter(call => call[0] === 'docker').map(call => call[1]);
  assert.deepEqual(docker, ['load', 'image']);
  assert.equal(result.archiveHashVerifiedBeforeLoad, true);
  assert.equal(result.allExpectedFieldsMatch, true);
  assert.equal(result.comparisons.idEqualsConfigDigest, true);
  assert.deepEqual(Object.keys(result.present), allowedFields);
  const text = JSON.stringify(result);
  for (const secret of ['SECRET=raw', 'raw-label', 'raw load output', 'dotnet']) assert.equal(text.includes(secret), false);
});

test('reports a manifest-digest Id as a mismatch without failing', () => {
  const { result } = run(image({ Id: pins.manifestDigest }));
  assert.equal(result.status, 'completed-read-only');
  assert.equal(result.comparisons.idEqualsConfigDigest, false);
  assert.equal(result.comparisons.idEqualsManifestDigest, true);
  assert.equal(result.allExpectedFieldsMatch, false);
});

test('refuses before any load when the archive hash differs', () => {
  const calls = [];
  const dir = mkdtempSync(path.join(tmpdir(), 'ah-diag-'));
  writeFileSync(path.join(dir, pins.archive), bytes);
  try {
    assert.throws(() => runAgentHostArchiveDiagnostic(dir, { env, spawn: (bin, args) => {
      calls.push(bin + ' ' + args[0]);
      return { status: 0, stdout: bin === 'git' ? (args[0] === 'status' ? '' : helper) : '', stderr: '' };
    } }), error => error.diagnostic.failureCode === 'ARCHIVE_HASH_MISMATCH');
    assert.equal(calls.some(call => call.startsWith('docker')), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('refuses the original image SHA as the workflow source', () => {
  assert.throws(() => runAgentHostArchiveDiagnostic('.', { env: { ...env, EXPECTED_SOURCE: pins.imageSourceSha },
    spawn: () => assert.fail('no command may run') }), error => error.diagnostic.failureCode === 'DIAGNOSTIC_SCOPE_INVALID');
});

test('workflow is separate, read-only, dispatch-only and has no build or publish step', () => {
  const workflow = readFileSync('.github/workflows/v1-agenthost-archive-diagnostic.yml', 'utf8').replaceAll('\r\n', '\n');
  assert.match(workflow, /on:\n  workflow_dispatch:\n    inputs:\n      expected_source:/);
  assert.match(workflow, /run-id: 38286624230\n\s+artifact-ids: 11763641424/);
  assert.match(workflow, /docker\/setup-docker-action@2bf61fb9464cc67f0cbdeabed6aa0380accd1c70/);
  assert.match(workflow, /version: 'v29\.9\.0'/);
  assert.match(workflow, /DOCKER_CONTEXT: default/);
  assert.doesNotMatch(workflow, /docker (build|buildx|push|login|run)|buildx|packages: write|contents: write|secrets\./);
});

test('describeInspect marks missing fields', () => {
  const { present, imageId } = describeInspect({});
  assert.equal(Object.values(present).some(Boolean), false);
  assert.equal(imageId, null);
});
