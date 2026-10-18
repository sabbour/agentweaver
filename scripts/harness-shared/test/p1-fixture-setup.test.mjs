import test from 'node:test';
import assert from 'node:assert/strict';
import { createP1Fixture, executePrivilegedProjectSetup } from '../p1-fixture-setup.mjs';
import { runP1SurfaceJourney } from '../p1-surface-runner.mjs';

const SOURCE_SHA = 'a'.repeat(40);
const SOURCE_TREE = 'b'.repeat(40);
const INVOCATION_ID = '00000000-0000-4000-8000-000000000001';
const PROJECT_ID = 'fixture-project';
const TENANT_ID = 'fixture-tenant';
const ACTOR_ID = 'fixture-owner';
const ISSUER = 'https://identity.controlled.test/';
const AUDIENCE = 'agentweaver-controlled';

function sourceEvidence() {
  const image = 'ghcr.io/sabbour/agentweaver/projects-config@sha256:' + 'c'.repeat(64);
  const imageID = 'containerd://sha256:' + 'd'.repeat(64);
  const deployment = {
    kind: 'Deployment',
    metadata: {
      name: 'projects-config',
      uid: 'deployment-uid',
      generation: 3,
    },
    spec: {
      replicas: 1,
      template: { spec: { containers: [{ name: 'projects-config', image }] } },
    },
    status: { observedGeneration: 3, readyReplicas: 1, availableReplicas: 1, updatedReplicas: 1 },
  };
  const replicaSet = {
    kind: 'ReplicaSet',
    metadata: {
      name: 'projects-config-abc',
      uid: 'replicaset-uid',
      ownerReferences: [{ kind: 'Deployment', uid: deployment.metadata.uid, controller: true }],
    },
    spec: { replicas: 1 },
  };
  const pod = {
    kind: 'Pod',
    metadata: {
      name: 'projects-config-pod',
      uid: 'pod-uid',
      ownerReferences: [{ kind: 'ReplicaSet', uid: replicaSet.metadata.uid, controller: true }],
    },
    spec: {
      containers: [{ name: 'projects-config', image }],
    },
    status: {
      phase: 'Running',
      conditions: [{ type: 'Ready', status: 'True' }],
      containerStatuses: [{ ready: true, imageID }],
    },
  };
  return {
    services: {
      status: 'passed',
      sourceSha: SOURCE_SHA,
      sourceTree: SOURCE_TREE,
      services: [{
        componentId: 'Agentweaver.Projects.Config',
        deployment: { name: deployment.metadata.name, uid: deployment.metadata.uid, generation: 3, desiredReplicas: 1 },
        replicaSet: { name: replicaSet.metadata.name, uid: replicaSet.metadata.uid },
        pods: [{
          name: pod.metadata.name,
          uid: pod.metadata.uid,
          image,
          imageID,
          registry: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
        }],
      }],
    },
    objects: [deployment, replicaSet, pod],
  };
}

function apiFor({
  bound = false,
  journey = 'gate-answer',
  projectId = PROJECT_ID,
  expectedRunId = 'selected-run',
  actorId = ACTOR_ID,
  tenantId = TENANT_ID,
  platformRuntimeRevision = 7,
} = {}) {
  let selection;
  let root;
  let child;
  let gate;
  let gateResolved = false;
  const calls = [];
  const baseUrl = 'https://environment.controlled.test/';
  const paths = {
    getAuthorizationContext: '/api/v1/authorization/context',
    getProjectConfiguration: '/api/v1/projects/{projectId}/configuration',
    getPlatformRuntimeDefaults: '/api/v1/platform/runtime-defaults',
    acceptRunSelection: '/api/v1/projects/{projectId}/runs/{runId}/selection',
    getRunSelection: '/api/v1/projects/{projectId}/runs/{runId}/selection',
    acceptRunRoot: '/api/v1/projects/{projectId}/runs/{runId}/root',
    readRunStatus: '/api/v1/projects/{projectId}/runs/{runId}/coordination/status',
    readSessionStatus: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/status',
    readDecisions: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions',
    readSessionTree: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/tree',
    registerChild: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/children',
    askQuestion: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/questions',
    requestApproval: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/approvals',
    answerGate: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/answer',
    approveGate: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/approve',
    rejectGate: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/reject',
    sendMessage: '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/messages',
  };
  const api = {
    target: new URL(baseUrl),
    tenantId,
    authProvider: { origin: new URL(baseUrl).origin, issuer: ISSUER, audience: AUDIENCE, actorId },
    operations: new Map(Object.entries(paths).map(([operationId, path]) => [operationId, { path }])),
    calls,
    resolveGate() { gateResolved = true; },
    async discover() {},
    async invoke(operationId, { pathParameters = {}, body } = {}) {
      calls.push({ operationId, pathParameters, body });
      const result = (status, transientResponseBody) => ({
        operationId, status, transientResponseBody,
      });
      switch (operationId) {
        case 'getAuthorizationContext':
          return result(200, {
            contractVersion: 1,
            issuer: ISSUER,
            actorId,
            tenantId,
            membershipRevision: 1,
            boundProjectId: bound ? projectId : null,
            boundRunId: bound ? expectedRunId : null,
            effectiveAuthority: bound ? [{
              resourceType: 'project',
              resourceId: projectId,
              permissions: [{ permission: 'accessPrivateKnowledge', roleRevision: 1 }],
            }] : [],
          });
        case 'getProjectConfiguration':
          return result(200, { projectId, revision: 4, configuration: {} });
        case 'getPlatformRuntimeDefaults':
          return result(403, { code: 'forbidden' });
        case 'acceptRunSelection':
          if (body.expectedPlatformRuntimeRevision !== platformRuntimeRevision) {
            return result(409, { code: 'revision_conflict' });
          }
          selection = {
            projectId: pathParameters.projectId,
            runId: pathParameters.runId,
            projectConfigurationRevision: body.expectedProjectConfigRevision,
            platformRuntimeRevision: body.expectedPlatformRuntimeRevision,
            contextRevision: body.context.revision,
          };
          return result(200, selection);
        case 'getRunSelection':
          return result(200, selection);
        case 'acceptRunRoot':
          root = {
            projectId: pathParameters.projectId,
            runId: pathParameters.runId,
            rootSessionId: body.sessionId,
            executionFence: 11,
            stateVersion: 0,
            logicalTurnOrdinal: 0,
            executionState: 'admitted',
          };
          return result(201, root);
        case 'readRunStatus':
          if (actorId !== ACTOR_ID) return result(403, { code: 'forbidden' });
          return result(200, {
            ...root,
            logicalTurnOrdinal: 0,
            stateVersion: 1,
          });
        case 'readSessionStatus':
          return result(200, {
            identity: {
              projectId: pathParameters.projectId,
              runId: pathParameters.runId,
              sessionId: pathParameters.sessionId,
            },
            rootSessionId: root.rootSessionId,
            executionFence: root.executionFence,
            blockers: gate && !gateResolved ? [{
              requestId: gate.requestId,
              kind: journey === 'gate-answer' ? 'awaitingInput' : 'awaitingApproval',
              choices: journey === 'gate-answer' ? ['continue'] : [],
              allowsFreeform: false,
            }] : [],
          });
        case 'readDecisions':
          return result(200, {
            executionFence: root.executionFence,
            stateVersion: gateResolved ? 2 : gate ? 1 : 0,
            pendingGate: gateResolved ? null : gate,
          });
        case 'askQuestion':
        case 'requestApproval': {
          gate = {
            requestId: body.requestId,
            kind: operationId === 'askQuestion' ? 'question' : 'approval',
            subjectId: body.questionId ?? body.subjectId,
            authorizedActorId: actorId,
            fence: root.executionFence,
            allowedChoices: body.allowedChoices ?? [],
            allowsFreeform: body.allowsFreeform ?? false,
            prompt: body.prompt,
            resolvesOutcomeClarification: false,
          };
          return result(200, {
            accepted: true,
            decisionId: 'decision-id',
            stateVersion: 1,
            executionFence: root.executionFence,
            pendingGate: gate,
            issues: [],
          });
        }
        case 'registerChild':
          child = body.sessionId;
          return result(201, { sessionId: child, parentSessionId: root.rootSessionId });
        case 'answerGate':
        case 'approveGate':
        case 'rejectGate':
          gateResolved = true;
          return result(200, {
            accepted: true,
            decisionId: 'decision-id',
            stateVersion: 2,
            executionFence: root.executionFence,
            issues: [],
          });
        case 'sendMessage':
          return result(202, {
            ownerMessageId: 'owner-message-id',
            recipientSessionId: body.recipientSessionId,
            status: 'accepted',
          });
        case 'readSessionTree':
          return result(200, {
            rootSessionId: root.rootSessionId,
            nodes: [
              { identity: { projectId, runId: root.runId, sessionId: root.rootSessionId }, lifecycle: 'active' },
              ...(child ? [{
                identity: { projectId, runId: root.runId, sessionId: child },
                lifecycle: 'active',
              }] : []),
            ],
          });
        default:
          throw new Error(`Unexpected controlled API operation: ${operationId}`);
      }
    },
  };
  return api;
}

function plan(journey) {
  return {
    executionIntent: 'execute',
    journey,
    environment: {
      baseUrl: 'https://environment.controlled.test/',
      issuer: ISSUER,
      audience: AUDIENCE,
      actorId: ACTOR_ID,
      tenantId: TENANT_ID,
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
        uid: 'provider-uid',
        resourceVersion: '1',
      },
      runtimeReceipt: { generation: 1 },
      storageReceipt: { pvUid: 'storage-pv' },
      sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
      imageReceipt: {
        registryImage: `ghcr.io/sabbour/agentweaver/agenthost@sha256:${'c'.repeat(64)}`,
        registryDigest: `sha256:${'c'.repeat(64)}`,
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
      deniedActor: { actorId: 'denied-actor', tenantId: TENANT_ID },
      gate: journey.startsWith('gate-') ? {
        prompt: 'Continue the controlled acceptance run?',
        allowedChoices: journey === 'gate-answer' ? ['continue'] : [],
        allowsFreeform: false,
        answer: journey === 'gate-answer' ? 'continue' : 'not-used',
      } : null,
      message: journey === 'message' ? { text: 'Controlled P1 message.' } : null,
    },
  };
}

function journeySourceEvidence(input) {
  const { sourceReceipt, providerReceipt, imageReceipt } = input.pins;
  return {
    source: { sourceSha: sourceReceipt.sourceSha, sourceTree: sourceReceipt.sourceTree },
    integrationDeployment: {
      status: 'deployed',
      sourceSha: sourceReceipt.sourceSha,
      sourceTree: sourceReceipt.sourceTree,
      manifestSha256: '2'.repeat(64),
      publicationSha256: '3'.repeat(64),
      readbackSha256: '4'.repeat(64),
      target: { namespace: input.pins.namespace },
    },
    providerCatalog: {
      name: providerReceipt.configMapName,
      sourceCommit: providerReceipt.sourceCommit,
      uid: providerReceipt.uid,
      resourceVersion: providerReceipt.resourceVersion,
    },
    services: { status: 'passed', sourceSha: sourceReceipt.sourceSha, sourceTree: sourceReceipt.sourceTree },
    agentHostImageMeasurement: {
      status: 'passed',
      sourceSha: sourceReceipt.sourceSha,
      sourceTree: sourceReceipt.sourceTree,
      registryImage: imageReceipt.registryImage,
      registryDigest: imageReceipt.registryDigest,
      archiveSha256: imageReceipt.archiveSha256,
      image: {
        digest: imageReceipt.manifestDigest,
        configDigest: imageReceipt.configDigest,
        compressedPullBytes: imageReceipt.compressedPullBytes,
      },
    },
  };
}

async function createJourneyFixture(journey) {
  const input = plan(journey);
  const fixture = await createP1Fixture(input, {
    api: apiFor({ journey }),
    invocationId: INVOCATION_ID,
    setupToken: 'controlled-secret-token',
    sourceEvidence: sourceEvidence(),
    sourceReceipt: input.pins.sourceReceipt,
    setupContext: input.pins.context,
    namespace: input.pins.namespace,
    runPrivilegedSetup: async request =>
      setupResult(request.request.invocationId, request.request.operationKind),
    acquireRunIdentity: async binding => ({
      token: 'run-token',
      deniedToken: 'denied-token',
      projectId: binding.projectId,
      runId: binding.runId,
      api: apiFor({ bound: true, journey, expectedRunId: binding.runId }),
      deniedApi: apiFor({ actorId: 'denied-actor' }),
    }),
  });
  return { fixture, sourceEvidence: journeySourceEvidence(input) };
}

function mcpFor(journey, events, resolveGate) {
  const operations = journey === 'message'
    ? ['readRunStatus', 'readSessionTree', 'sendMessage']
    : ['readRunStatus', 'readSessionStatus', 'readDecisions', 'answerGate'];
  const tools = new Map(operations.map(operation => [`agentweaver_${operation}`, {
    inputSchema: {
      type: 'object',
      properties: {
        projectId: { type: 'string' },
        runId: { type: 'string' },
        sessionId: { type: 'string' },
        requestId: { type: 'string' },
        tenantSelector: { type: 'string' },
        body: { type: 'object' },
      },
      required: ['projectId', 'runId', 'tenantSelector'],
      additionalProperties: false,
    },
  }]));
  const protocol = [];
  let requestId = 0;
  return {
    target: new URL('https://environment.controlled.test/mcp'),
    tenantId: TENANT_ID,
    authProvider: {
      origin: 'https://environment.controlled.test',
      issuer: ISSUER,
      audience: AUDIENCE,
      actorId: ACTOR_ID,
    },
    tools,
    protocol,
    async discoverTools() { events.push('mcp-discover'); },
    async callTool(name, args) {
      events.push(`mcp:${name}`);
      const id = ++requestId;
      const ownerResponse = name.endsWith('sendMessage')
        ? { ownerMessageId: 'owner-message-id', recipientSessionId: args.body.recipientSessionId, status: 'accepted' }
        : {
          accepted: true,
          decisionId: 'decision-id',
          stateVersion: 2,
          executionFence: 11,
          issues: [],
        };
      if (!name.endsWith('sendMessage')) resolveGate();
      const result = { structuredContent: { status: name.endsWith('sendMessage') ? 202 : 200, ownerResponse } };
      protocol.push({ direction: 'request', message: { id, method: 'tools/call', params: { name, arguments: args } } });
      protocol.push({ direction: 'response', message: { id, result } });
      return {
        isError: false,
        result,
        transientResult: { isError: false, structuredContent: result.structuredContent },
      };
    },
    async close() { events.push('mcp-close'); },
  };
}

function uiFor(events, journey, resolveGate = () => {}) {
  const origin = 'https://environment.controlled.test';
  return {
    target: new URL(`${origin}/`),
    gateway: new URL(`${origin}/api/v1`),
    tenantId: TENANT_ID,
    identity: { issuer: ISSUER, audience: AUDIENCE, actorId: ACTOR_ID },
    async openRun() { events.push('ui-open'); },
    async gate() {
      events.push('ui-gate');
      resolveGate();
      return {
        status: 200,
        transientResponseBody: {
          accepted: true,
          decisionId: 'decision-id',
          stateVersion: 2,
          executionFence: 11,
          issues: [],
        },
      };
    },
    async sendMessage({ recipientSessionId }) {
      events.push('ui-message');
      return {
        status: 202,
        transientResponseBody: {
          ownerMessageId: 'owner-message-id',
          recipientSessionId,
          status: 'accepted',
        },
      };
    },
    async replayJournal() {
      if (journey !== 'journal') throw new Error('Unexpected journal read.');
    },
    async close() { events.push('ui-close'); },
  };
}

function setupResult(invocationId, operationKind, cleanupState = 'retainedPending') {
  const setup = {
    invocationId,
    issuer: ISSUER,
    subject: ACTOR_ID,
    tenantId: TENANT_ID,
    membershipId: '00000000-0000-4000-8000-000000000011',
    membershipRevision: 1,
    projectId: PROJECT_ID,
    createdByActorId: ACTOR_ID,
    projectRevision: 1,
    projectConfigurationRevision: 4,
    assignmentId: '00000000-0000-4000-8000-000000000012',
    assignmentRevision: 1,
    ownerAssignmentId: '00000000-0000-4000-8000-000000000013',
    ownerAssignmentRevision: 1,
    authorityAuditEventId: '00000000-0000-4000-8000-000000000014',
    ownerAuthorityAuditEventId: '00000000-0000-4000-8000-000000000015',
  };
  const cleaned = cleanupState === 'cleaned';
  if (operationKind === 'setup') {
    return {
      operationKind,
      replayed: false,
      setup,
      setupReadback: {
        projectExists: true,
        projectState: 'active',
        projectRevision: 1,
        projectConfigurationRevision: 4,
        initialConfigurationExists: true,
        assignmentExists: true,
        assignmentState: 'active',
        assignmentRevision: 1,
        authorityAuditExists: true,
        ownerAssignmentId: setup.ownerAssignmentId,
        ownerAssignmentExists: true,
        ownerAssignmentState: 'active',
        ownerAssignmentRevision: 1,
        ownerAuthorityAuditExists: true,
      },
      cleanup: null,
    };
  }
  return {
    operationKind,
    replayed: false,
    setup: null,
    setupReadback: null,
    cleanup: {
      invocationId,
      tenantId: TENANT_ID,
      projectId: PROJECT_ID,
      assignmentId: setup.assignmentId,
      state: cleanupState,
      projectRevision: cleaned ? 2 : 1,
      projectConfigurationRevision: 4,
      assignmentRevision: cleaned ? 2 : 1,
      authorityAuditEventId: cleaned ? '00000000-0000-4000-8000-000000000016' : null,
      ownerAssignmentId: setup.ownerAssignmentId,
      ownerAssignmentRevision: cleaned ? 2 : 1,
      ownerRevocationAuditEventId: cleaned ? '00000000-0000-4000-8000-000000000017' : null,
    },
  };
}

test('privileged Projects Config input is sent only after the exact source-attested Pod is re-read', async () => {
  const evidence = sourceEvidence();
  const calls = [];
  const token = 'controlled-secret-token';
  const result = await executePrivilegedProjectSetup({
    context: 'p1-control',
    namespace: 'agentweaver',
    sourceEvidence: evidence,
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    accessToken: token,
    request: {
      invocationId: INVOCATION_ID,
      operationKind: 'setup',
      tenantId: TENANT_ID,
      expectedProjectRevision: null,
      expectedAssignmentRevision: null,
    },
    spawnSyncImpl(_executable, args, options) {
      calls.push({ args, options });
      if (args.includes('get') && args.includes('deployments,replicasets,pods')) {
        return { status: 0, stdout: JSON.stringify({ items: evidence.objects }), stderr: '' };
      }
      if (args.includes('get') && args.includes('pod')) {
        return { status: 0, stdout: JSON.stringify(evidence.objects[2]), stderr: '' };
      }
      return { status: 0, stdout: JSON.stringify({ operationKind: 'setup' }), stderr: '' };
    },
  });
  assert.equal(result.operationKind, 'setup');
  assert.equal(calls.length, 3);
  assert.ok(calls[1].args.includes('get'));
  assert.ok(calls[1].args.includes('projects-config-pod'));
  assert.deepEqual(calls[2].args.slice(-3), ['dotnet', 'Agentweaver.Projects.Config.dll', '--privileged-project-setup']);
  assert.ok(calls[2].args.includes('-i'));
  assert.ok(calls[2].args.includes('--container=projects-config'));
  assert.ok(!calls[2].args.includes(token));
  assert.match(calls[2].options.input, new RegExp(token));
  assert.equal(calls[2].options.stdio[0], 'pipe');
});

test('a changed Projects Config Pod blocks before privileged stdin is sent', async () => {
  const evidence = sourceEvidence();
  let calls = 0;
  await assert.rejects(executePrivilegedProjectSetup({
    context: 'p1-control',
    namespace: 'agentweaver',
    sourceEvidence: evidence,
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    accessToken: 'controlled-secret-token',
    request: { invocationId: INVOCATION_ID, operationKind: 'setup', tenantId: TENANT_ID },
    spawnSyncImpl(_executable, args) {
      calls += 1;
      if (calls === 1) return { status: 0, stdout: JSON.stringify({ items: evidence.objects }), stderr: '' };
      return {
        status: 0,
        stdout: JSON.stringify({ ...evidence.objects[2], metadata: { ...evidence.objects[2].metadata, uid: 'changed-pod' } }),
        stderr: '',
      };
    },
  }), /freshly observed Projects Config Pod/);
  assert.equal(calls, 2);
});

test('same-invocation fixture creation uses owner readbacks and retains accepted selections on cleanup', async () => {
  const events = [];
  const setupApi = apiFor();
  const deniedApi = apiFor();
  const fixture = await createP1Fixture(plan('gate-answer'), {
    api: setupApi,
    invocationId: INVOCATION_ID,
    setupToken: 'controlled-secret-token',
    sourceEvidence: sourceEvidence(),
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    setupContext: 'p1-control',
    namespace: 'agentweaver',
    runPrivilegedSetup: async request => {
      events.push(`setup:${request.request.operationKind}`);
      return setupResult(request.request.invocationId, request.request.operationKind);
    },
    acquireRunIdentity: async binding => {
      events.push('identity');
      assert.equal(binding.projectId, PROJECT_ID);
      assert.equal(binding.runId, '00000000-0000-4000-8000-000000000021');
      return {
        token: 'run-token',
        deniedToken: 'denied-token',
        projectId: binding.projectId,
        runId: binding.runId,
        api: apiFor({
          bound: true,
          journey: 'gate-answer',
          expectedRunId: binding.runId,
        }),
        deniedApi,
      };
    },
    createId: purpose => ({
      run: '00000000-0000-4000-8000-000000000021',
      'root-session': '00000000-0000-4000-8000-000000000022',
      'gate-request': '00000000-0000-4000-8000-000000000023',
      'gate-idempotency': '00000000-0000-4000-8000-000000000024',
      'gate-question': '00000000-0000-4000-8000-000000000025',
      fixture: '00000000-0000-4000-8000-000000000026',
    }[purpose] ?? '00000000-0000-4000-8000-000000000027'),
  });
  assert.equal(fixture.input.pins.ownerFences.projectId, PROJECT_ID);
  assert.equal(fixture.input.pins.ownerFences.runId, '00000000-0000-4000-8000-000000000021');
  assert.equal(fixture.input.pins.fixtureScope.gate.requestId, '00000000-0000-4000-8000-000000000023');
  assert.equal(fixture.input.pins.fixtureScope.ownershipReceipts.every(receipt =>
    receipt.invocationId === INVOCATION_ID
    && receipt.creationReceipt.resourceId === receipt.resourceId
    && receipt.observedReceipt.resourceId === receipt.resourceId), true);
  assert.ok(events.indexOf('identity') > -1);
  const cleanup = await fixture.cleanup();
  assert.equal(cleanup.state, 'retainedPending');
  assert.deepEqual(events, ['setup:setup', 'identity', 'setup:cleanup']);
});

test('TenantAdmin setup with Owner and Orchestrator grants accepts selection without platform defaults', async () => {
  const api = apiFor();
  const fixturePlan = plan('journal');
  const fixture = await createP1Fixture(fixturePlan, {
    api,
    invocationId: INVOCATION_ID,
    setupToken: 'controlled-secret-token',
    sourceEvidence: sourceEvidence(),
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    runPrivilegedSetup: async request => {
      assert.equal(request.request.tenantId, TENANT_ID);
      const result = setupResult(request.request.invocationId, request.request.operationKind);
      if (request.request.operationKind === 'setup') {
        assert.equal(result.setup.tenantId, TENANT_ID);
        assert.equal(result.setupReadback.assignmentExists, true);
        assert.equal(result.setupReadback.ownerAssignmentExists, true);
      }
      return result;
    },
    acquireRunIdentity: async binding => ({
      token: 'run-token',
      deniedToken: 'denied-token',
      projectId: binding.projectId,
      runId: binding.runId,
      api: apiFor({ bound: true, journey: 'journal', expectedRunId: binding.runId }),
      deniedApi: apiFor({ actorId: 'denied-actor' }),
    }),
  });
  try {
    assert.ok(api.calls.some(call => call.operationId === 'getProjectConfiguration'));
    assert.equal(api.calls.some(call => call.operationId === 'getPlatformRuntimeDefaults'), false);
    const selectionRequest = api.calls.find(call => call.operationId === 'acceptRunSelection');
    assert.equal(selectionRequest.body.expectedPlatformRuntimeRevision,
      fixturePlan.fixturePlan.expectedPlatformRuntimeRevision);
    assert.equal(fixture.state.selection.platformRuntimeRevision,
      fixturePlan.fixturePlan.expectedPlatformRuntimeRevision);
    assert.ok(api.calls.some(call => call.operationId === 'getRunSelection'));
  } finally {
    await fixture.cleanup();
  }
});

test('stale expected platform revision is denied before run identity or root/session creation', async () => {
  const api = apiFor({ platformRuntimeRevision: 8 });
  const invoke = api.invoke.bind(api);
  let selectionStatus;
  api.invoke = async (operationId, options) => {
    const result = await invoke(operationId, options);
    if (operationId === 'acceptRunSelection') selectionStatus = result.status;
    return result;
  };
  let identityAcquired = false;
  let setupFailure;
  try {
    await createP1Fixture(plan('message'), {
      api,
      invocationId: INVOCATION_ID,
      setupToken: 'controlled-secret-token',
      sourceEvidence: sourceEvidence(),
      sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
      runPrivilegedSetup: async request =>
        setupResult(request.request.invocationId, request.request.operationKind,
          request.request.operationKind === 'cleanup' ? 'cleaned' : 'retainedPending'),
      acquireRunIdentity: async () => {
        identityAcquired = true;
        throw new Error('Identity must not be requested after stale selection denial.');
      },
    });
  } catch (error) {
    setupFailure = error;
  }
  assert.match(setupFailure?.message ?? '', /acceptRunSelection call returned an unexpected HTTP result/);
  assert.equal(setupFailure.p1SetupState.selection, null);
  const selectionRequest = api.calls.find(call => call.operationId === 'acceptRunSelection');
  assert.equal(selectionRequest.body.expectedPlatformRuntimeRevision,
    plan('message').fixturePlan.expectedPlatformRuntimeRevision);
  assert.equal(selectionStatus, 409);
  assert.equal(api.calls.some(call => call.operationId === 'getProjectConfiguration'), true);
  assert.equal(api.calls.some(call => call.operationId === 'getRunSelection'), false);
  assert.equal(identityAcquired, false);
  assert.equal(api.calls.some(call => call.operationId === 'acceptRunRoot'), false);
  assert.equal(api.calls.some(call => call.operationId === 'registerChild'), false);
  assert.equal(await setupFailure.p1SetupCleanup().then(result => result.state), 'cleaned');
});

test('same-invocation gate retry verifies and reuses the owner gate after losing the first receipt', async () => {
  const setupApi = apiFor();
  const runApi = apiFor({
    bound: true,
    journey: 'gate-answer',
    expectedRunId: '00000000-0000-4000-8000-000000000021',
  });
  const invoke = runApi.invoke.bind(runApi);
  let lostGateReceipt = false;
  runApi.invoke = async (operationId, options) => {
    const result = await invoke(operationId, options);
    if (operationId === 'askQuestion' && !lostGateReceipt) {
      lostGateReceipt = true;
      throw new Error('Controlled lost gate mutation receipt.');
    }
    return result;
  };
  const createFixture = () => createP1Fixture(plan('gate-answer'), {
    api: setupApi,
    invocationId: INVOCATION_ID,
    setupToken: 'controlled-secret-token',
    sourceEvidence: sourceEvidence(),
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    setupContext: 'p1-control',
    namespace: 'agentweaver',
    runPrivilegedSetup: async request =>
      setupResult(request.request.invocationId, request.request.operationKind),
    createId: purpose => ({
      run: '00000000-0000-4000-8000-000000000021',
      'root-session': '00000000-0000-4000-8000-000000000022',
      'gate-request': '00000000-0000-4000-8000-000000000023',
      'gate-idempotency': '00000000-0000-4000-8000-000000000024',
      'gate-question': '00000000-0000-4000-8000-000000000025',
      fixture: '00000000-0000-4000-8000-000000000026',
    }[purpose] ?? '00000000-0000-4000-8000-000000000027'),
    acquireRunIdentity: async binding => ({
      token: 'run-token',
      deniedToken: 'denied-token',
      projectId: binding.projectId,
      runId: binding.runId,
      api: runApi,
      deniedApi: apiFor({ actorId: 'denied-actor' }),
    }),
  });

  let firstError;
  try {
    await createFixture();
  } catch (error) {
    firstError = error;
  }
  assert.match(firstError?.message ?? '', /Controlled lost gate mutation receipt/);
  await firstError.p1SetupCleanup();

  const retried = await createFixture();
  try {
    const gateReceipt = retried.input.pins.fixtureScope.ownershipReceipts
      .find(receipt => receipt.resourceType === 'gate');
    assert.equal(gateReceipt.resourceId, '00000000-0000-4000-8000-000000000023');
    assert.equal(gateReceipt.creationReceipt.receipt.kind, 'question');
    assert.equal(gateReceipt.creationReceipt.receipt.subjectId, '00000000-0000-4000-8000-000000000025');
    assert.equal(gateReceipt.creationReceipt.receipt.authorizedActorId, ACTOR_ID);
    assert.equal(gateReceipt.creationReceipt.receipt.fence, 11);
    assert.equal(gateReceipt.creationReceipt.receipt.prompt, plan('gate-answer').fixturePlan.gate.prompt);
    assert.deepEqual(gateReceipt.creationReceipt.receipt.allowedChoices, ['continue']);
    assert.equal(gateReceipt.creationReceipt.receipt.allowsFreeform, false);
    assert.equal(Object.hasOwn(gateReceipt.creationReceipt.receipt, 'accepted'), false);
    assert.equal(runApi.calls.filter(call => call.operationId === 'askQuestion').length, 1);
  } finally {
    await retried.cleanup();
  }
});

test('message setup registers and reads back a same-run child without invoking dispatch', async () => {
  const setupApi = apiFor();
  const deniedApi = apiFor();
  const fixture = await createP1Fixture(plan('message'), {
    api: setupApi,
    invocationId: INVOCATION_ID,
    setupToken: 'controlled-secret-token',
    sourceEvidence: sourceEvidence(),
    sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
    runPrivilegedSetup: async request =>
      setupResult(request.request.invocationId, request.request.operationKind),
    acquireRunIdentity: async binding => ({
      token: 'run-token',
      deniedToken: 'denied-token',
      projectId: binding.projectId,
      runId: binding.runId,
      api: apiFor({
        bound: true,
        journey: 'message',
        expectedRunId: binding.runId,
      }),
      deniedApi,
    }),
  });
  const children = fixture.input.pins.fixtureScope.ownershipReceipts
    .filter(receipt => receipt.resourceType === 'child-session');
  assert.equal(children.length, 1);
  assert.equal(fixture.input.pins.fixtureScope.message.senderSessionId,
    fixture.input.pins.ownerFences.rootSessionId);
  assert.equal(fixture.input.pins.fixtureScope.message.recipientSessionId, children[0].resourceId);
  assert.equal(fixture.input.pins.fixtureScope.message.text, 'Controlled P1 message.');
});

test('same-invocation fixtures reach the selected API, MCP, and UI gate and message methods', async () => {
  for (const journey of ['gate-answer', 'message']) {
    for (const surface of ['api', 'mcp', 'ui']) {
      const { fixture, sourceEvidence: evidence } = await createJourneyFixture(journey);
      const events = [];
      const mcp = surface === 'mcp' ? mcpFor(journey, events, fixture.api.resolveGate) : undefined;
      const ui = surface === 'ui' ? uiFor(events, journey, fixture.api.resolveGate) : undefined;
      try {
        const result = await runP1SurfaceJourney(surface, fixture.input, {
          api: fixture.api,
          deniedApi: fixture.deniedApi,
          mcp,
          ui,
          sourceEvidence: evidence,
          clock: () => Date.parse('2026-10-13T00:00:00.000Z'),
          sleep: async () => {},
          beforeUiAction: async ({ journey: selectedJourney }) => {
            events.push(`before-ui:${selectedJourney}`);
          },
        });
        assert.equal(result.surfaceStatus, 'passed', JSON.stringify(result));
        if (surface === 'api') {
          assert.ok(fixture.api.calls.some(call =>
            call.operationId === (journey === 'message' ? 'sendMessage' : 'answerGate')));
        } else if (surface === 'mcp') {
          assert.ok(events.includes(`mcp:agentweaver_${journey === 'message' ? 'sendMessage' : 'answerGate'}`));
        } else {
          assert.ok(events.indexOf(`before-ui:${journey}`) < events.indexOf(journey === 'message' ? 'ui-message' : 'ui-gate'));
          assert.ok(events.includes(journey === 'message' ? 'ui-message' : 'ui-gate'));
          assert.ok(events.includes('ui-close'));
        }
      } finally {
        await fixture.cleanup();
      }
    }
  }
});

test('setup failures without a verified receipt report indeterminate only after a privileged attempt', async () => {
  const indeterminate = Object.assign(new Error('controlled missing receipt'), { indeterminate: true });
  let attempts = 0;
  let setupError;
  try {
    await createP1Fixture(plan('journal'), {
      api: apiFor(),
      invocationId: INVOCATION_ID,
      setupToken: 'controlled-secret-token',
      sourceEvidence: sourceEvidence(),
      sourceReceipt: plan('journal').pins.sourceReceipt,
      setupContext: 'p1-control',
      namespace: 'agentweaver',
      runPrivilegedSetup: async () => {
        attempts += 1;
        throw indeterminate;
      },
      acquireRunIdentity: async () => { throw new Error('Identity must not be requested before setup.'); },
    });
  } catch (error) {
    setupError = error;
  }
  assert.ok(setupError);
  assert.equal(attempts, 2);
  assert.equal(setupError.p1SetupState.setupAttempted, true);
  assert.equal((await setupError.p1SetupCleanup()).state, 'indeterminate');

  const beforeAttempt = apiFor();
  beforeAttempt.discover = async () => { throw new Error('controlled discovery failure'); };
  let discoveryError;
  try {
    await createP1Fixture(plan('journal'), {
      api: beforeAttempt,
      invocationId: INVOCATION_ID,
      setupToken: 'controlled-secret-token',
      sourceEvidence: sourceEvidence(),
      sourceReceipt: plan('journal').pins.sourceReceipt,
      setupContext: 'p1-control',
      namespace: 'agentweaver',
      runPrivilegedSetup: async () => {
        throw new Error('Privileged setup must not be called.');
      },
      acquireRunIdentity: async () => { throw new Error('Identity must not be requested before setup.'); },
    });
  } catch (error) {
    discoveryError = error;
  }
  assert.ok(discoveryError);
  assert.equal(discoveryError.p1SetupState.setupAttempted, false);
  assert.equal((await discoveryError.p1SetupCleanup()).state, 'not-created');
});

test('setup cleanup verifies archive and revocation when selection was not accepted', async () => {
  const api = apiFor();
  const invoke = api.invoke.bind(api);
  api.invoke = async (operationId, options) => operationId === 'getProjectConfiguration'
    ? { operationId, status: 503, transientResponseBody: { code: 'unavailable' } }
    : invoke(operationId, options);
  const operations = [];
  let setupFailure;
  try {
    await createP1Fixture(plan('journal'), {
      api,
      invocationId: INVOCATION_ID,
      setupToken: 'controlled-secret-token',
      sourceEvidence: sourceEvidence(),
      sourceReceipt: plan('journal').pins.sourceReceipt,
      setupContext: 'p1-control',
      namespace: 'agentweaver',
      runPrivilegedSetup: async request => {
        operations.push(request.request.operationKind);
        return setupResult(request.request.invocationId, request.request.operationKind, 'cleaned');
      },
      acquireRunIdentity: async () => { throw new Error('Identity must not be requested before selection.'); },
    });
  } catch (error) {
    setupFailure = error;
  }
  assert.ok(setupFailure);
  assert.ok(setupFailure.p1SetupState.setup);
  const cleanup = await setupFailure.p1SetupCleanup();
  assert.equal(cleanup.state, 'cleaned');
  assert.deepEqual(operations, ['setup', 'cleanup']);
});

test('a failed durable UI action marker prevents the UI write', async () => {
  const { fixture, sourceEvidence: evidence } = await createJourneyFixture('message');
  const events = [];
  const ui = uiFor(events, 'message');
  try {
    const result = await runP1SurfaceJourney('ui', fixture.input, {
      api: fixture.api,
      deniedApi: fixture.deniedApi,
      ui,
      sourceEvidence: evidence,
      clock: () => Date.parse('2026-10-13T00:00:00.000Z'),
      beforeUiAction: async () => { throw new Error('controlled evidence append failure'); },
    });
    assert.equal(result.surfaceStatus, 'failed');
    assert.match(result.failure.message, /controlled evidence append failure/);
    assert.equal(events.includes('ui-message'), false);
    assert.ok(events.includes('ui-close'));
  } finally {
    await fixture.cleanup();
  }
});
