import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { allowedFields, archivePins as pins, describeInspect, runAgentHostArchiveDiagnostic } from '../agenthost-archive-diagnostic.mjs';

const helper = 'b'.repeat(40);
const bytes = Buffer.from('synthetic archive');
const sha = createHash('sha256').update(bytes).digest('hex');
const env = {
  GITHUB_REPOSITORY: 'sabbour/agentweaver', GITHUB_REF: 'refs/heads/v1', GITHUB_EVENT_NAME: 'workflow_dispatch',
  EXPECTED_SOURCE: helper, GITHUB_RUN_ATTEMPT: '1', GH_TOKEN: 'secret', GITHUB_TOKEN: 'secret',
};
const image = (overrides = {}) => ({
  Id: pins.configDigest, Os: 'linux', Architecture: 'amd64', Env: ['SECRET=raw'],
  Config: { User: pins.user, Entrypoint: pins.entrypoint, Labels: {
    'org.opencontainers.image.revision': pins.imageSourceSha, 'io.agentweaver.source-tree': pins.sourceTree,
    'org.opencontainers.image.version': pins.version, 'arbitrary.label': 'raw-label',
  } }, ...overrides,
});

function run(inspect, { hash = sha, nested = false, envOverrides = {}, git = {}, rawInspect } = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'ah-diag-'));
  const archiveDir = nested ? path.join(dir, 'v1-release-pack-x-1') : dir;
  if (nested) mkdirSync(archiveDir);
  writeFileSync(path.join(archiveDir, pins.archive), bytes);
  const calls = [];
  const spawn = (bin, args, options) => {
    calls.push([bin, ...args]);
    assert.equal(options.env.GH_TOKEN, undefined);
    if (bin === 'git') return { status: 0, stdout: args[0] === 'status' ? (git.status ?? '') : (git.head ?? helper), stderr: '' };
    if (args[0] === 'load') return { status: 0, stdout: 'raw load output', stderr: '' };
    return { status: 0, stdout: rawInspect ?? JSON.stringify(inspect), stderr: '' };
  };
  try {
    return { calls, dir, result: runAgentHostArchiveDiagnostic(dir, { env: { ...env, ...envOverrides }, spawn, expectedArchiveSha256: hash }) };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const failure = (options, code) => {
  const calls = [];
  const dir = mkdtempSync(path.join(tmpdir(), 'ah-diag-'));
  writeFileSync(path.join(dir, pins.archive), bytes);
  try {
    assert.throws(() => runAgentHostArchiveDiagnostic(dir, {
      env: { ...env, ...options.envOverrides }, expectedArchiveSha256: sha,
      spawn: (bin, args) => {
        calls.push([bin, ...args]);
        if (options.spawn) return options.spawn(bin, args);
        if (bin === 'git') return { status: 0, stdout: args[0] === 'status' ? (options.status ?? '') : (options.head ?? helper), stderr: '' };
        return { status: 0, stdout: args[0] === 'load' ? '' : (options.rawInspect ?? '{}'), stderr: '' };
      },
    }), error => { assert.equal(error.diagnostic.failureCode, code); assert.equal(error.diagnostic.status, 'failed'); return true; });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
  return calls;
};

test('refuses wrong repository, ref, event and run attempt before any command', () => {
  for (const envOverrides of [{ GITHUB_REPOSITORY: 'other/repo' }, { GITHUB_REF: 'refs/heads/dev' },
    { GITHUB_EVENT_NAME: 'push' }, { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_RUN_ATTEMPT: undefined }])
    assert.deepEqual(failure({ envOverrides }, 'DIAGNOSTIC_SCOPE_INVALID'), []);
});

test('refuses a wrong helper SHA or dirty source before any docker command', () => {
  for (const options of [{ head: 'c'.repeat(40) }, { status: ' M file.txt' }])
    assert.equal(failure(options, 'HELPER_SOURCE_MISMATCH').some(call => call[0] === 'docker'), false);
});

test('malformed inspect JSON is a clear failure, not valid evidence', () => {
  for (const rawInspect of ['not json', '[]', 'null', '{"Config":5,"Id":"x"}', '{}'])
    assert.equal(failure({ rawInspect }, 'INSPECT_OUTPUT_INVALID').at(-1)[1], 'image');
});

test('secret-bearing command failure output is sanitized', () => {
  const secret = 'ghp_SECRETTOKENVALUE';
  const spawn = (bin, args) => bin === 'git' ? { status: 0, stdout: args[0] === 'status' ? '' : helper, stderr: '' }
    : { status: 1, stdout: secret, stderr: `unauthorized ${secret}` };
  const calls = [];
  const dir = mkdtempSync(path.join(tmpdir(), 'ah-diag-'));
  writeFileSync(path.join(dir, pins.archive), bytes);
  try {
    assert.throws(() => runAgentHostArchiveDiagnostic(dir, { env, expectedArchiveSha256: sha,
      spawn: (...args) => { calls.push(args[0]); return spawn(...args); } }), error => {
      const text = `${error.message}${JSON.stringify(error.diagnostic)}`;
      assert.equal(text.includes(secret), false);
      assert.equal(error.diagnostic.failureCode, 'AUTHORIZATION_DENIED');
      return true;
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('finds the archive in the real download nesting layout', () => {
  const { calls, result } = run(image(), { nested: true });
  assert.equal(result.archiveHashVerifiedBeforeLoad, true);
  assert.equal(calls.some(call => call[1] === 'load' && call.at(-1).includes('v1-release-pack-x-1')), true);
});

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

test('release workflow routes the archive diagnostic alone, read-only, before Docker setup, with no build or publish', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8').replaceAll('\r\n', '\n');
  const job = workflow.slice(workflow.indexOf('\n  diagnose-agenthost-archive:'), workflow.indexOf('\n  publish:'));
  assert.match(workflow, /agenthost_archive_diagnostic:[\s\S]*?default: false/);
  for (const name of ['pack', 'diagnose-probe-sampler', 'publish', 'publish-probe-sampler']) {
    const start = workflow.indexOf(`\n  ${name}:`);
    assert.match(workflow.slice(start, start + 400), /!inputs\.agenthost_archive_diagnostic/);
  }
  assert.match(job, /if: \$\{\{ inputs\.agenthost_archive_diagnostic \}\}/);
  for (const guard of ['GITHUB_REPOSITORY', 'GITHUB_REF" = "refs/heads/v1', 'GITHUB_RUN_ATTEMPT" = "1',
    'EXPECTED_SOURCE" = "$GITHUB_SHA', '"$PUBLISH" = "false"', '"$PLATFORM_ONLY" = "false"', '"$PROBE_SAMPLER_REPLACEMENT" = "false"'])
    assert.ok(job.includes(guard), guard);
  assert.ok(job.indexOf('GITHUB_RUN_ATTEMPT') < job.indexOf('setup-docker-action'));
  assert.ok(job.indexOf('setup-docker-action') < job.indexOf('download-artifact'));
  assert.match(job, /run-id: 38286624230\n\s+artifact-ids: 11763641424/);
  assert.match(job, /docker\/setup-docker-action@2bf61fb9464cc67f0cbdeabed6aa0380accd1c70/);
  assert.match(job, /version: 'v29\.9\.0'/);
  assert.match(job, /DOCKER_CONTEXT: default/);
  assert.doesNotMatch(job, /docker (build|buildx|push|login|run)|buildx|: write|environment:|secrets\.|release:pack|publish\.mjs/);
});

test('describeInspect marks missing fields', () => {
  const { present, imageId } = describeInspect({});
  assert.equal(Object.values(present).some(Boolean), false);
  assert.equal(imageId, null);
});
