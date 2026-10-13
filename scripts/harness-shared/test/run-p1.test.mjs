import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { runP1Cli } from '../run-p1.mjs';

function validInput() {
  const sourceSha = 'a'.repeat(40);
  const sourceTree = 'b'.repeat(40);
  const registryDigest = `sha256:${'c'.repeat(64)}`;
  const fences = {
    projectId: 'controlled-project',
    runId: 'controlled-run',
    rootSessionId: 'controlled-root',
    sessionId: 'controlled-session',
    executionFence: 1,
  };
  const invocationId = 'controlled-invocation';
  return {
    executionIntent: 'execute',
    journey: 'journal',
    environment: {
      baseUrl: 'https://environment.controlled.test/',
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'controlled-actor',
      tenantId: 'controlled-tenant',
    },
    deploymentEvidence: {
      publicationPath: 'artifacts/p1/publication.json',
      manifestPath: 'releases/foundation.json',
      agentHostArchivePath: 'artifacts/p1/Agentweaver.AgentHost.oci.tar',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver-v1-p0',
      providerReceipt: {
        configMapName: 'provider-catalog-v1',
        sourceCommit: 'd'.repeat(40),
        uid: 'controlled-provider-uid',
        resourceVersion: '12',
      },
      runtimeReceipt: { generation: 1 },
      storageReceipt: { pvUid: 'controlled-pv' },
      sourceReceipt: { sourceSha, sourceTree },
      imageReceipt: {
        registryImage: `ghcr.io/sabbour/agentweaver/agenthost@${registryDigest}`,
        registryDigest,
        archiveSha256: 'e'.repeat(64),
        manifestDigest: `sha256:${'f'.repeat(64)}`,
        configDigest: `sha256:${'1'.repeat(64)}`,
        compressedPullBytes: 1024,
      },
      ownerFences: fences,
      fixtureScope: {
        kind: 'p1-runner-owned',
        status: 'ready',
        invocationId,
        fixtureId: 'controlled-fixture',
        ...fences,
        expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
        allowedJourneys: ['journal'],
        ownershipReceipts: [fences.projectId, fences.runId, fences.rootSessionId, fences.sessionId]
          .map(resourceId => ({
            resourceType: 'owner-resource',
            resourceId,
            invocationId,
            creationReceipt: { resourceId },
            observedReceipt: { resourceId },
            ownerFence: fences.executionFence,
          })),
      },
    },
  };
}

test('P1 CLI help describes the current blocked state without exposing credential arguments', async () => {
  const output = [];
  const code = await runP1Cli(['--help'], {}, { log: text => output.push(text), error() {} });
  assert.equal(code, 0);
  assert.match(output.join('\n'), /--surface api\|mcp\|ui/);
  assert.match(output.join('\n'), /blocked result before reading credentials/);
  assert.doesNotMatch(output.join('\n'), /--token/);
});

test('P1 CLI rejects non-execute input and writes a blocked result without credentials or network activity', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  try {
    await writeFile(inputPath, JSON.stringify({ executionIntent: 'preview' }), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api', '--input', inputPath, '--output', outputPath,
    ], {}, { log: text => output.push(text), error: text => output.push(text) });
    assert.equal(code, 2);
    const lines = (await readFile(outputPath, 'utf8')).trim().split('\n');
    const line = lines.at(-1);
    const result = JSON.parse(line);
    assert.equal(result.surfaceStatus, 'blocked');
    assert.equal(result.failure.message.includes('executionIntent'), true);
    assert.doesNotMatch(`${line}${output.join('\n')}`, /bearer|token/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI parses the UI page module and prepares exact deployment evidence before credentials', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  let prepared = 0;
  let preparation;
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'ui',
      '--input', inputPath,
      '--output', outputPath,
      '--page-module', path.join(directory, 'authorized-page-provider.mjs'),
    ], {}, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence(options) {
        prepared += 1;
        preparation = options;
        throw new Error('Controlled source-evidence stop before credentials or adapters.');
      },
    });
    assert.equal(code, 2);
    assert.equal(prepared, 1, output.at(-1));
    assert.equal(preparation.expectedSourceSha, 'a'.repeat(40));
    assert.equal(preparation.context, 'p1-control');
    assert.equal(preparation.namespace, 'agentweaver-v1-p0');
    assert.equal(preparation.providerCatalog.configMapName, 'provider-catalog-v1');
    assert.equal(path.basename(preparation.publicationPath), 'publication.json');
    assert.equal(path.basename(preparation.manifestPath), 'foundation.json');
    assert.equal(path.basename(preparation.agentHostArchivePath), 'Agentweaver.AgentHost.oci.tar');
    const result = JSON.parse(output.at(-1));
    assert.match(result.failure.message, /Controlled source-evidence stop/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI does not persist a blocked result over an input or deployment artifact', async () => {
  for (const artifact of ['input', 'publication']) {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
    const inputPath = path.join(directory, 'input.json');
    const protectedPath = artifact === 'input' ? inputPath : path.join(directory, 'publication.json');
    const original = artifact === 'input' ? JSON.stringify(validInput()) : 'protected deployment evidence';
    const input = validInput();
    if (artifact === 'publication') input.deploymentEvidence.publicationPath = protectedPath;
    const output = [];
    let prepared = 0;
    try {
      await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
      if (artifact === 'publication') await writeFile(protectedPath, original, { encoding: 'utf8' });
      const code = await runP1Cli([
        '--surface', 'api', '--input', inputPath, '--output', protectedPath,
      ], {}, { log: text => output.push(text), error: text => output.push(text) }, {
        async prepareDeploymentEvidence() {
          prepared += 1;
          return {};
        },
      });

      assert.equal(code, 2);
      assert.equal(prepared, 0);
      assert.equal(await readFile(protectedPath, 'utf8'), original);
      assert.equal(JSON.parse(output.at(-1)).surfaceStatus, 'blocked');
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  }
});

test('P1 CLI blocks forged or pre-existing fixture receipts before credentials or surface adapters', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  let prepared = 0;
  try {
    const input = validInput();
    input.pins.ownerFences.runId = 'pre-existing-run';
    input.pins.fixtureScope.runId = 'pre-existing-run';
    for (const receipt of input.pins.fixtureScope.ownershipReceipts) {
      if (receipt.resourceId === 'controlled-run') {
        receipt.resourceId = 'pre-existing-run';
        receipt.creationReceipt.resourceId = 'pre-existing-run';
        receipt.observedReceipt.resourceId = 'pre-existing-run';
      }
      receipt.claimedCreation = 'caller-supplied';
    }
    await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
    const credentialAccess = new Proxy({}, {
      get() { throw new Error('The blocked runner read a bearer credential.'); },
    });
    const code = await runP1Cli([
      '--surface', 'ui',
      '--input', inputPath,
      '--output', outputPath,
      '--page-module', path.join(directory, 'missing-page-provider.mjs'),
    ], credentialAccess, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() {
        prepared += 1;
        return {};
      },
    });

    assert.equal(code, 2);
    assert.equal(prepared, 1);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /same-invocation setup and cleanup/);
    assert.doesNotMatch(result.failure.message, /bearer credential|missing-page-provider/);
    const evidenceLines = (await readFile(outputPath, 'utf8')).trim().split('\n');
    assert.equal(JSON.parse(evidenceLines.at(-1)).surfaceStatus, 'blocked');
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
