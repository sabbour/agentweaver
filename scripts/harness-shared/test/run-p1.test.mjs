import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createP1AcceptanceResult, sealP1NativeProbeEnvelope } from '../p1-acceptance-result.mjs';
import { P1_SIGNIN_PROFILE_ROOT } from '../p1-signin.mjs';
import { runP1Cli } from '../run-p1.mjs';

function validInput() {
  const sourceSha = 'a'.repeat(40);
  const sourceTree = 'b'.repeat(40);
  const registryDigest = `sha256:${'c'.repeat(64)}`;
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
      integrationDeploymentReceiptPath: 'artifacts/p1/integration-deployment.json',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver',
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
    },
    fixturePlan: {
      expectedPlatformRuntimeRevision: 7,
      selectionContext: {
        revision: 'provider-catalog-v1',
        availableModelSelectionReferences: ['platform-model'],
        providerRequirements: [],
        requiredEgress: [],
      },
      deniedActor: { actorId: 'controlled-denied-actor', tenantId: 'controlled-tenant' },
      gate: null,
      message: null,
    },
  };
}

function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key =>
      `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

/** Build test-only envelopes for CLI contract checks; these are not live acceptance evidence. */
function probeEnvelope(kind, names) {
  return sealP1NativeProbeEnvelope({
    kind,
    schemaVersion: 1,
    overallStatus: 'passed',
    startedAt: '2026-10-13T00:00:00.000Z',
    completedAt: '2026-10-13T00:00:01.000Z',
    ...(kind === 'p1-dispatch-recovery-acceptance'
      ? { ownerObservation: { status: 'observed' }, liveObservations: { creditDenials: { status: 'passed' } } }
      : {}),
    probes: names.map(name => ({
      name,
      status: 'passed',
      evidence: { kind: 'controlled-source-receipt', receiptHash: name },
      cleanup: kind === 'p1-native-acceptance'
        ? { status: 'not-required', actions: [] }
        : { status: 'not-started', createdResourceIds: [] },
    })),
  });
}

function invocationRecord(input, surface, invocationId, nativeProbeResults = []) {
  const validated = {
    environment: input.environment,
    pins: input.pins,
    deploymentEvidence: input.deploymentEvidence,
    fixturePlan: input.fixturePlan,
    journey: input.journey,
  };
  return {
    kind: 'p1-setup-invocation',
    schemaVersion: 2,
    invocationId,
    executionPlanSha256: createHash('sha256')
      .update(stableJson({ validated, nativeProbeResults })).digest('hex'),
    surface,
  };
}

test('P1 CLI help describes the explicit identity providers without exposing credential arguments', async () => {
  const output = [];
  const code = await runP1Cli(['--help'], {}, { log: text => output.push(text), error() {} });
  assert.equal(code, 0);
  assert.match(output.join('\n'), /--surface api\|mcp\|ui/);
  assert.match(output.join('\n'), /supplied setup token only after source and deployment evidence pass/);
  assert.doesNotMatch(output.join('\n'), /--token/);
});

test('P1 UI surface remains blocked on #1859 before credentials or page providers are used', async () => {
  const outputPath = path.join(P1_SIGNIN_PROFILE_ROOT, `ui-blocked-${randomUUID()}.jsonl`);
  const output = [];
  const calls = [];
  const env = new Proxy({}, {
    get() {
      calls.push('credential');
      throw new Error('The blocked UI runner read a credential.');
    },
  });
  try {
    const code = await runP1Cli([
      '--surface', 'ui',
      '--input', 'unused-input.json',
      '--output', outputPath,
      '--page-module', 'unused-page-provider.mjs',
    ], env, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() {
        calls.push('deployment');
        throw new Error('The blocked UI runner prepared deployment evidence.');
      },
      async createSurfaceAdapters() { calls.push('adapters'); },
    });

    assert.equal(code, 2);
    assert.deepEqual(calls, []);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /blocked on #1859/);
    assert.doesNotMatch(result.failure.message, /credential|deployment evidence|unused-page-provider/);
  } finally {
    await rm(outputPath, { force: true });
  }
});

test('P1 CLI reads exact native envelopes and preserves every supplied probe on later failure', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const nativePath = path.join(directory, 'native.json');
  const recoveryPath = path.join(directory, 'recovery.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  const native = probeEnvelope('p1-native-acceptance', [
    'storage-owner-isolation',
    'network-policy-datapath',
    'sandbox-owner-lifecycle',
    'agenthost-image-readiness',
  ]);
  const recovery = probeEnvelope('p1-dispatch-recovery-acceptance', [
    'numeric-copilot-dispatch-boundaries',
    'checkpoint-restart-duplicate-fence',
  ]);
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    await writeFile(nativePath, JSON.stringify(native), { encoding: 'utf8' });
    await writeFile(recoveryPath, JSON.stringify(recovery), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api',
      '--input', inputPath,
      '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
      '--native-probe-envelope', nativePath,
      '--dispatch-recovery-envelope', recoveryPath,
    ], {}, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() {
        throw new Error('Controlled stop after exact probe envelopes were read.');
      },
    });

    assert.equal(code, 2);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.fullP1Status, 'blocked');
    assert.match(result.failure.message, /Controlled stop/);
    for (const name of [
      'storage-owner-isolation',
      'network-policy-datapath',
      'sandbox-owner-lifecycle',
      'agenthost-image-readiness',
      'numeric-copilot-dispatch-boundaries',
      'checkpoint-restart-duplicate-fence',
    ]) {
      assert.equal(result.probes.find(probe => probe.id === name).status, 'passed', name);
    }
    assert.deepEqual(
      result.probes.find(probe => probe.id === 'numeric-copilot-dispatch-boundaries').evidence,
      recovery.probes[0].evidence,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI rejects a tampered envelope and retains valid envelopes while blocking missing probes', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const nativePath = path.join(directory, 'native.json');
  const recoveryPath = path.join(directory, 'recovery.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  const native = probeEnvelope('p1-native-acceptance', [
    'storage-owner-isolation',
    'network-policy-datapath',
    'sandbox-owner-lifecycle',
    'agenthost-image-readiness',
  ]);
  const recovery = probeEnvelope('p1-dispatch-recovery-acceptance', [
    'numeric-copilot-dispatch-boundaries',
    'checkpoint-restart-duplicate-fence',
  ]);
  recovery.probes[0].scriptedResult = true;
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    await writeFile(nativePath, JSON.stringify(native), { encoding: 'utf8' });
    await writeFile(recoveryPath, JSON.stringify(recovery), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api',
      '--input', inputPath,
      '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
      '--native-probe-envelope', nativePath,
      '--dispatch-recovery-envelope', recoveryPath,
    ], {}, { log: text => output.push(text), error: text => output.push(text) });

    assert.equal(code, 2);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.fullP1Status, 'blocked');
    assert.match(result.failure.message, /checksum does not match/);
    assert.ok(result.probes.filter(probe => [
      'storage-owner-isolation',
      'network-policy-datapath',
      'sandbox-owner-lifecycle',
      'agenthost-image-readiness',
    ].includes(probe.id)).every(probe => probe.status === 'passed'));
    assert.ok(result.probes.filter(probe => [
      'numeric-copilot-dispatch-boundaries',
      'checkpoint-restart-duplicate-fence',
    ].includes(probe.id)).every(probe => probe.status === 'blocked'));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI rejects non-execute input before credentials or network activity', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  try {
    await writeFile(inputPath, JSON.stringify({ executionIntent: 'preview' }), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api', '--input', inputPath, '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
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

test('P1 CLI stops before setup when the setup token is missing', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  const calls = [];
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api', '--input', inputPath, '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
    ], {}, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() {
        calls.push('deployment');
        return {};
      },
      createSetupApi() {
        calls.push('setup-api');
        throw new Error('The setup API must not be created without a token.');
      },
      async createFixture() {
        calls.push('fixture');
      },
      async createSurfaceAdapters() {
        calls.push('adapters');
      },
    });
    assert.equal(code, 2);
    assert.deepEqual(calls, ['deployment']);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /setup token is unavailable/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI fails closed when privileged setup has no verified receipt', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    const setupError = new Error('Controlled privileged setup receipt loss.');
    setupError.p1SetupState = {
      invocationId: '00000000-0000-4000-8000-000000000323',
      setup: null,
      setupAttempted: true,
    };
    setupError.p1SetupCleanup = async () => ({
      invocationId: setupError.p1SetupState.invocationId,
      state: 'indeterminate',
    });
    const code = await runP1Cli([
      '--surface', 'api', '--input', inputPath, '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
    ], { AGENTWEAVER_P1_SETUP_TOKEN: 'controlled-setup-token' },
    { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() { return {}; },
      createSetupApi() { return {}; },
      async createFixture() { throw setupError; },
      async createSurfaceAdapters() {
        throw new Error('The surface adapter must not be created after setup receipt loss.');
      },
    });
    assert.equal(code, 1);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.surfaceStatus, 'failed');
    assert.equal(result.fullP1Status, 'failed');
    assert.equal(result.observations.fixtureSetup.cleanup.state, 'indeterminate');
    assert.match(result.failure.message, /reconcile the invocation before retrying/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI resumes the same invocation, runs the selected journey, and records cleanup state', async () => {
  for (const cleanupState of ['retainedPending', 'cleaned']) {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
    const inputPath = path.join(directory, 'input.json');
    const outputPath = path.join(directory, 'evidence.jsonl');
    const invocationId = '00000000-0000-4000-8000-000000000321';
    const output = [];
    const calls = [];
    const sourceEvidence = {
      services: { status: 'passed' },
      agentHostImageMeasurement: { status: 'passed' },
    };
    try {
      const input = validInput();
      input.journey = 'gate-answer';
      input.fixturePlan.gate = {
        prompt: 'Continue?',
        allowedChoices: ['continue'],
        allowsFreeform: false,
        answer: 'continue',
      };
      await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
      await writeFile(outputPath, `${JSON.stringify(invocationRecord(input, 'api', invocationId))}\n`, { encoding: 'utf8' });
      let fixtureInput;
      const code = await runP1Cli([
        '--surface', 'api', '--input', inputPath, '--output', outputPath,
        '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
      ], { AGENTWEAVER_P1_SETUP_TOKEN: 'controlled-setup-token' },
      { log: text => output.push(text), error: text => output.push(text) }, {
        async prepareDeploymentEvidence() {
          calls.push('deployment');
          return sourceEvidence;
        },
        createSetupApi({ setupToken }) {
          calls.push('setup-api');
          assert.equal(setupToken, 'controlled-setup-token');
          return { marker: 'setup-api' };
        },
        async createFixture(validated, options) {
          calls.push('fixture');
          assert.equal(options.invocationId, invocationId);
          assert.equal(options.setupToken, 'controlled-setup-token');
          assert.deepEqual(options.api, { marker: 'setup-api' });
          const identity = await options.acquireRunIdentity({
            projectId: 'runner-owned-project',
            runId: 'runner-owned-run',
          });
          assert.equal(identity.projectId, 'runner-owned-project');
          assert.equal(identity.runId, 'runner-owned-run');
          fixtureInput = validated;
          const state = {
            invocationId,
            setup: { projectId: 'runner-owned-project', assignmentId: 'runner-owned-assignment' },
            selection: { runId: 'runner-owned-run' },
          };
          return {
            input: fixtureInput,
            identity,
            state,
            async cleanup() {
              calls.push('cleanup');
              return { state: cleanupState, invocationId, projectId: 'runner-owned-project' };
            },
          };
        },
        async acquireRunIdentity(options) {
          calls.push('identity');
          assert.equal(options.projectId, 'runner-owned-project');
          assert.equal(options.runId, 'runner-owned-run');
          return {
            token: 'controlled-run-token',
            deniedToken: 'controlled-denied-token',
            projectId: options.projectId,
            runId: options.runId,
            createClients: () => ({ api: {}, deniedApi: {} }),
          };
        },
        async createSurfaceAdapters({ surface, identity }) {
          calls.push('adapters');
          assert.equal(surface, 'api');
          assert.equal(identity.projectId, 'runner-owned-project');
          return { api: {}, deniedApi: {} };
        },
        async runSurfaceJourney(surface, journeyInput, dependencies) {
          calls.push('journey');
          assert.equal(surface, 'api');
          assert.equal(journeyInput, fixtureInput);
          assert.equal(dependencies.sourceEvidence, sourceEvidence);
          return createP1AcceptanceResult({
            surface,
            surfaceStatus: 'passed',
            sourceEvidence,
          });
        },
      });

      assert.equal(code, 2);
      assert.deepEqual(calls, ['deployment', 'setup-api', 'fixture', 'identity', 'adapters', 'journey', 'cleanup']);
      const records = (await readFile(outputPath, 'utf8')).trim().split(/\r?\n/).map(JSON.parse);
      assert.equal(records.filter(record => record.kind === 'p1-setup-invocation').length, 1);
      assert.equal(records[0].invocationId, invocationId);
      assert.equal(records[0].schemaVersion, 2);
      assert.match(records[0].executionPlanSha256, /^[a-f0-9]{64}$/);
      assert.equal(records[0].surface, 'api');
      const result = records.find(record => record.kind === 'p1-acceptance-result');
      assert.equal(result.surfaceStatus, 'passed');
      assert.equal(result.fullP1Status, 'blocked');
      assert.equal(result.observations.fixtureSetup.cleanup.state, cleanupState);
      assert.equal(result.observations.fixtureSetup.nativeFence.status, 'not-started');
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  }
});

test('P1 CLI rejects changed-plan, changed-surface, and unbound legacy invocation resumes before credentials or setup', async () => {
  for (const mismatch of ['plan', 'surface', 'legacy']) {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
    const inputPath = path.join(directory, 'input.json');
    const outputPath = path.join(directory, 'evidence.jsonl');
    const invocationId = '00000000-0000-4000-8000-000000000325';
    const input = validInput();
    const recordedInput = validInput();
    const surface = mismatch === 'surface' ? 'mcp' : 'api';
    const record = mismatch === 'legacy'
      ? { kind: 'p1-setup-invocation', schemaVersion: 1, invocationId }
      : invocationRecord(recordedInput, 'api', invocationId);
    if (mismatch === 'plan') {
      input.fixturePlan.selectionContext.revision = 'changed-provider-catalog';
    }
    const output = [];
    const calls = [];
    const credentials = new Proxy({}, {
      get(_target, name) {
        if (name === 'AGENTWEAVER_P1_SETUP_TOKEN') calls.push('credential');
        return undefined;
      },
    });
    try {
      await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
      await writeFile(outputPath, `${JSON.stringify(record)}\n`, { encoding: 'utf8' });
      const args = [
        '--surface', surface, '--input', inputPath, '--output', outputPath,
        '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
      ];
      const code = await runP1Cli(
        args,
        credentials,
        { log: text => output.push(text), error: text => output.push(text) },
        {
          async prepareDeploymentEvidence() {
            calls.push('deployment');
            return {};
          },
          createSetupApi() { calls.push('setup-api'); },
          async createFixture() { calls.push('fixture'); },
        },
      );
      assert.equal(code, 2);
      assert.deepEqual(calls, ['deployment']);
      const result = JSON.parse(output.at(-1));
      assert.equal(result.surfaceStatus, 'blocked');
      if (mismatch === 'plan') {
        assert.match(result.failure.message, /different validated execution plan/);
      } else if (mismatch === 'surface') {
        assert.match(result.failure.message, /different selected surface/);
      } else {
        assert.match(result.failure.message, /no complete validated-plan and surface binding/);
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  }
});

test('P1 CLI still cleans up the owned fixture when surface adapter creation fails', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  const calls = [];
  const invocationId = '00000000-0000-4000-8000-000000000324';
  try {
    await writeFile(inputPath, JSON.stringify(validInput()), { encoding: 'utf8' });
    await writeFile(outputPath, `${JSON.stringify(invocationRecord(validInput(), 'api', invocationId))}\n`, { encoding: 'utf8' });
    const code = await runP1Cli([
      '--surface', 'api', '--input', inputPath, '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
    ], { AGENTWEAVER_P1_SETUP_TOKEN: 'controlled-setup-token' },
    { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() { return {}; },
      createSetupApi() { return {}; },
      async createFixture(validated, { invocationId: currentInvocationId }) {
        calls.push('fixture');
        assert.equal(currentInvocationId, invocationId);
        const state = {
          invocationId,
          setup: { projectId: 'runner-owned-project' },
          selection: { runId: 'runner-owned-run' },
        };
        return {
          input: validated,
          state,
          async cleanup() {
            calls.push('cleanup');
            return { state: 'retainedPending', invocationId };
          },
        };
      },
      async createSurfaceAdapters() {
        calls.push('adapters');
        throw new Error('Controlled adapter construction failure.');
      },
      async runSurfaceJourney() {
        calls.push('journey');
        throw new Error('The journey must not run without adapters.');
      },
    });
    assert.equal(code, 2);
    assert.deepEqual(calls, ['fixture', 'adapters', 'cleanup']);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.observations.fixtureSetup.cleanup.state, 'retainedPending');
    assert.match(result.failure.message, /Controlled adapter construction failure/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('P1 CLI does not persist a blocked result over an input, envelope, or deployment artifact', async () => {
  for (const artifact of ['input', 'native-envelope', 'dispatch-envelope', 'publication', 'integration-deployment']) {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
    const inputPath = path.join(directory, 'input.json');
    const protectedPath = artifact === 'input'
      ? inputPath
      : path.join(directory, artifact === 'native-envelope'
        ? 'native.json'
        : artifact === 'dispatch-envelope' ? 'recovery.json'
          : artifact === 'publication' ? 'publication.json' : 'integration-deployment.json');
    const input = validInput();
    const original = artifact === 'input'
      ? JSON.stringify(input)
      : artifact === 'native-envelope'
        ? JSON.stringify(probeEnvelope('p1-native-acceptance', [
          'storage-owner-isolation',
          'network-policy-datapath',
          'sandbox-owner-lifecycle',
          'agenthost-image-readiness',
        ]))
        : artifact === 'dispatch-envelope'
          ? JSON.stringify(probeEnvelope('p1-dispatch-recovery-acceptance', [
            'numeric-copilot-dispatch-boundaries',
            'checkpoint-restart-duplicate-fence',
          ]))
        : 'protected deployment evidence';
    if (artifact === 'publication') input.deploymentEvidence.publicationPath = protectedPath;
    if (artifact === 'integration-deployment') {
      input.deploymentEvidence.integrationDeploymentReceiptPath = protectedPath;
    }
    const output = [];
    let prepared = 0;
    try {
      await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
      if (artifact !== 'input') await writeFile(protectedPath, original, { encoding: 'utf8' });
      const envelopeArgs = artifact === 'native-envelope'
        ? ['--native-probe-envelope', protectedPath]
        : artifact === 'dispatch-envelope'
          ? [
            '--native-probe-envelope', path.join(directory, 'missing-native.json'),
            '--dispatch-recovery-envelope', protectedPath,
          ]
        : [];
      const outputPath = artifact === 'dispatch-envelope' && process.platform === 'win32'
        ? protectedPath.toUpperCase()
        : protectedPath;
      const code = await runP1Cli([
        '--surface', 'api', '--input', inputPath, '--output', outputPath,
        '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
        ...envelopeArgs,
      ], {}, { log: text => output.push(text), error: text => output.push(text) }, {
        async prepareDeploymentEvidence() {
          prepared += 1;
          return {};
        },
      });

      assert.equal(code, 2);
      assert.equal(prepared, 0);
      assert.equal(await readFile(protectedPath, 'utf8'), original);
      const result = JSON.parse(output.at(-1));
      assert.equal(result.surfaceStatus, 'blocked');
      if (artifact === 'dispatch-envelope') {
        assert.match(result.failure.message, /Probe envelope paths must not replace/);
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  }
});

test('P1 CLI rejects caller-supplied fixture receipts before credentials or surface adapters', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-cli-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  const output = [];
  let prepared = 0;
  try {
    const input = validInput();
    input.pins.ownerFences = { runId: 'pre-existing-run' };
    input.pins.fixtureScope = { claimedCreation: 'caller-supplied' };
    await writeFile(inputPath, JSON.stringify(input), { encoding: 'utf8' });
    const credentialAccess = new Proxy({}, {
      get() { throw new Error('The blocked runner read a bearer credential.'); },
    });
    const code = await runP1Cli([
      '--surface', 'api',
      '--input', inputPath,
      '--output', outputPath,
      '--identity-module', path.join(directory, 'authorized-identity-provider.mjs'),
    ], credentialAccess, { log: text => output.push(text), error: text => output.push(text) }, {
      async prepareDeploymentEvidence() {
        prepared += 1;
        return {};
      },
    });

    assert.equal(code, 2);
    assert.equal(prepared, 0);
    const result = JSON.parse(output.at(-1));
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /caller-supplied owner fences or fixture receipts/);
    assert.doesNotMatch(result.failure.message, /bearer credential|missing-page-provider/);
    const evidenceLines = (await readFile(outputPath, 'utf8')).trim().split('\n');
    assert.equal(JSON.parse(evidenceLines.at(-1)).surfaceStatus, 'blocked');
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
