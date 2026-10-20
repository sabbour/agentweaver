import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { redact } from './redaction.mjs';

/** Create invocation-bound API and Projects Config fixtures for a bounded P1 surface journey. */
const SERVICE_COMPONENT = 'Agentweaver.Projects.Config';
const SERVICE_NAME = 'projects-config';
const COMMAND_DLL = 'Agentweaver.Projects.Config.dll';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const runnerOwnedFixtureInputs = new WeakSet();

/** Require a nonempty string field before using it in owner or setup requests. */
function requireString(value, label) {
  if (typeof value !== 'string' || !value.trim()) throw new Error(`The ${label} is required.`);
  return value;
}

/** Derive a stable, valid UUID from the persisted invocation and a resource purpose. */
function stableUuid(invocationId, purpose) {
  const bytes = createHash('sha256').update(`${invocationId}:${purpose}`).digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const value = bytes.toString('hex');
  return `${value.slice(0, 8)}-${value.slice(8, 12)}-${value.slice(12, 16)}-${value.slice(16, 20)}-${value.slice(20)}`;
}

/** Generate a stable idempotency key scoped to one setup invocation. */
export function createP1IdempotencyKey(invocationId) {
  return stableUuid(invocationId, 'surface-idempotency');
}

/** Return whether the exact input object was created by this runner's fixture setup. */
export function isP1RunnerOwnedFixtureInput(input) {
  return runnerOwnedFixtureInputs.has(input);
}

function requireResponse(api, operationId, call, statuses) {
  if (!api.operations.has(operationId) || call?.operationId !== operationId
    || !statuses.includes(call.status)) {
    throw new Error(`The P1 owner ${operationId} call returned an unexpected HTTP result.`);
  }
  return call.transientResponseBody;
}

/** Derive only the path parameter names advertised by the discovered owner operation. */
function parametersFor(api, operationId, values) {
  const operation = api.operations.get(operationId);
  if (!operation?.path) throw new Error(`The P1 owner operation is missing: ${operationId}.`);
  const names = [...operation.path.matchAll(/\{([^}]+)\}/g)].map(match => match[1]);
  const parameters = {};
  for (const name of names) {
    if (typeof values[name] !== 'string' || !values[name]) {
      throw new Error(`The P1 owner operation ${operationId} requires ${name}.`);
    }
    parameters[name] = values[name];
  }
  return parameters;
}

/** Invoke one discovered owner operation and validate its actual status receipt. */
async function invoke(api, operationId, values, body, statuses) {
  const call = await api.invoke(operationId, {
    pathParameters: parametersFor(api, operationId, values),
    ...(body === undefined ? {} : { body }),
  });
  return { call, value: requireResponse(api, operationId, call, statuses) };
}

/** Require privileged setup and readback receipts to agree on the invocation and every created owner revision. */
function requireSetupResult(result, request, actor) {
  const setup = result?.setup;
  const readback = result?.setupReadback;
  if (result?.operationKind !== 'setup' || !setup || !readback
    || setup.invocationId?.toLowerCase() !== request.invocationId.toLowerCase()
    || setup.issuer !== actor.issuer || setup.subject !== actor.actorId
    || setup.tenantId !== actor.tenantId
    || !UUID.test(setup.membershipId ?? '')
    || !UUID.test(setup.assignmentId ?? '')
    || !UUID.test(setup.ownerAssignmentId ?? '')
    || !UUID.test(setup.authorityAuditEventId ?? '')
    || !UUID.test(setup.ownerAuthorityAuditEventId ?? '')
    || !setup.projectId || setup.createdByActorId !== actor.actorId
    || !Number.isSafeInteger(setup.membershipRevision) || setup.membershipRevision < 1
    || !Number.isSafeInteger(setup.projectRevision) || setup.projectRevision < 1
    || !Number.isSafeInteger(setup.projectConfigurationRevision) || setup.projectConfigurationRevision < 1
    || !Number.isSafeInteger(setup.assignmentRevision) || setup.assignmentRevision < 1
    || !Number.isSafeInteger(setup.ownerAssignmentRevision) || setup.ownerAssignmentRevision < 1
    || readback.projectExists !== true || readback.projectState !== 'active'
    || readback.projectRevision !== setup.projectRevision
    || readback.projectConfigurationRevision !== setup.projectConfigurationRevision
    || readback.initialConfigurationExists !== true || readback.assignmentExists !== true
    || readback.assignmentState !== 'active' || readback.assignmentRevision !== setup.assignmentRevision
    || readback.authorityAuditExists !== true
    || readback.ownerAssignmentId !== setup.ownerAssignmentId
    || readback.ownerAssignmentExists !== true || readback.ownerAssignmentState !== 'active'
    || readback.ownerAssignmentRevision !== setup.ownerAssignmentRevision
    || readback.ownerAuthorityAuditExists !== true) {
    throw new Error('Projects Config did not return matching invocation-owned project and grant readbacks.');
  }
  return setup;
}

/** Require a single source-attested Projects Config Deployment, ReplicaSet, and Pod receipt. */
function sourceProjectsConfigEvidence(sourceEvidence, sourceReceipt) {
  const services = sourceEvidence?.services?.services;
  const matches = services?.filter(service => service.componentId === SERVICE_COMPONENT) ?? [];
  if (sourceEvidence?.services?.status !== 'passed' || matches.length !== 1) {
    throw new Error('The attested Projects Config Deployment and Pod evidence is missing.');
  }
  const service = matches[0];
  const pod = service.pods?.length === 1 ? service.pods[0] : null;
  if (!service.deployment?.uid || !service.replicaSet?.uid || !pod?.uid || !pod?.name
    || service.deployment.desiredReplicas !== 1
    || pod.registry?.sourceSha !== sourceReceipt.sourceSha
    || pod.registry?.sourceTree !== sourceReceipt.sourceTree
    || !pod.imageID) {
    throw new Error('The Projects Config evidence is not bound to one current source-attested Pod.');
  }
  return { service, pod };
}

/** Find controller references that bind a workload object to the expected owner UID. */
function ownerReference(resource, kind, uid) {
  return (resource?.metadata?.ownerReferences ?? []).filter(reference =>
    reference.kind === kind && reference.uid === uid && reference.controller === true);
}

/** Parse a successful kubectl list response and reject missing or malformed item arrays. */
function requireListResult(result, label) {
  if (result.error || result.status !== 0) throw new Error(`Could not attest the current Projects Config ${label}.`);
  let value;
  try {
    value = JSON.parse(result.stdout);
  } catch {
    throw new Error(`Kubernetes returned invalid Projects Config ${label}.`);
  }
  if (!Array.isArray(value?.items)) throw new Error(`Kubernetes returned no Projects Config ${label} list.`);
  return value.items;
}

/** Run one bounded kubectl read or pod-exec command with the selected cluster and namespace. */
function kubectl({ executable, spawnSyncImpl, context, namespace, args }) {
  return spawnSyncImpl(executable, [
    '--context', context,
    '--namespace', namespace,
    '--request-timeout=15s',
    ...args,
  ], {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 20000,
    maxBuffer: 8 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

/** Match the attested Projects Config Deployment, ReplicaSet, and sole Pod to a fresh Kubernetes list. */
function verifyLiveProjectsConfig(items, expected) {
  const deployments = items.filter(item =>
    item.kind === 'Deployment' && item.metadata?.name === SERVICE_NAME);
  const replicaSets = items.filter(item =>
    item.kind === 'ReplicaSet' && ownerReference(item, 'Deployment', expected.service.deployment.uid).length === 1
    && (item.spec?.replicas ?? 0) > 0);
  const pods = items.filter(item =>
    item.kind === 'Pod' && ownerReference(item, 'ReplicaSet', expected.service.replicaSet.uid).length === 1);
  const deployment = deployments[0];
  const replicaSet = replicaSets[0];
  const pod = pods[0];
  const container = deployment?.spec?.template?.spec?.containers?.[0];
  if (deployments.length !== 1 || replicaSets.length !== 1 || pods.length !== 1
    || deployment.metadata?.uid !== expected.service.deployment.uid
    || deployment.metadata?.generation !== expected.service.deployment.generation
    || deployment.spec?.replicas !== 1
    || deployment.status?.observedGeneration !== deployment.metadata.generation
    || deployment.status?.readyReplicas !== 1 || deployment.status?.availableReplicas !== 1
    || deployment.status?.updatedReplicas !== 1
    || container?.name !== SERVICE_NAME || container.image !== expected.pod.image
    || replicaSet.metadata?.uid !== expected.service.replicaSet.uid
    || replicaSet.metadata?.name !== expected.service.replicaSet.name
    || pod.metadata?.uid !== expected.pod.uid || pod.metadata?.name !== expected.pod.name) {
    throw new Error('The live Projects Config Deployment, ReplicaSet, or sole Pod differs from its source attestation.');
  }
  return { deployment, replicaSet, pod };
}

/** Require a fresh Pod read to preserve its identity, owner, readiness, and image ID. */
function verifyFreshPod(pod, expected, replicaSet) {
  const container = pod?.spec?.containers?.[0];
  const containerStatus = pod?.status?.containerStatuses?.[0];
  const ready = pod?.status?.conditions?.some(condition =>
    condition.type === 'Ready' && condition.status === 'True');
  if (pod?.kind !== 'Pod' || pod.metadata?.uid !== expected.pod.uid
    || pod.metadata?.name !== expected.pod.name || pod.status?.phase !== 'Running' || ready !== true
    || ownerReference(pod, 'ReplicaSet', replicaSet.metadata.uid).length !== 1
    || container?.name !== SERVICE_NAME || container.image !== expected.pod.image
    || containerStatus?.ready !== true || containerStatus.imageID !== expected.pod.imageID) {
    throw new Error('The freshly observed Projects Config Pod no longer matches its source attestation.');
  }
}

/**
 * Verify the attested Projects Config workload, then send a setup or cleanup request to its pod.
 * A command or receipt failure after execution begins is marked indeterminate so callers reconcile it.
 */
export async function executePrivilegedProjectSetup({
  context,
  namespace,
  sourceEvidence,
  sourceReceipt,
  accessToken,
  request,
  executable = 'kubectl',
  spawnSyncImpl = spawnSync,
}) {
  requireString(context, 'Kubernetes context');
  requireString(namespace, 'Kubernetes namespace');
  requireString(accessToken, 'privileged Projects Config access token');
  if (!request || !['setup', 'cleanup'].includes(request.operationKind)
    || !UUID.test(request.invocationId ?? '')) {
    throw new Error('The privileged Projects Config operation is invalid.');
  }
  const expected = sourceProjectsConfigEvidence(sourceEvidence, sourceReceipt);
  const listed = requireListResult(kubectl({
    executable,
    spawnSyncImpl,
    context,
    namespace,
    args: ['get', 'deployments,replicasets,pods', '--output=json'],
  }), 'workload list');
  const live = verifyLiveProjectsConfig(listed, expected);

  const freshResult = kubectl({
    executable,
    spawnSyncImpl,
    context,
    namespace,
    args: ['get', 'pod', expected.pod.name, '--output=json'],
  });
  if (freshResult.error || freshResult.status !== 0) {
    throw new Error('Could not freshly re-read the attested Projects Config Pod before setup.');
  }
  let freshPod;
  try {
    freshPod = JSON.parse(freshResult.stdout);
  } catch {
    throw new Error('Kubernetes returned invalid fresh Projects Config Pod evidence.');
  }
  verifyFreshPod(freshPod, expected, live.replicaSet);

  const stdin = JSON.stringify({
    version: 1,
    accessToken,
    invocationId: request.invocationId,
    operationKind: request.operationKind,
    tenantId: request.tenantId,
    expectedProjectRevision: request.expectedProjectRevision ?? null,
    expectedAssignmentRevision: request.expectedAssignmentRevision ?? null,
  });
  const args = [
    '--context', context,
    '--namespace', namespace,
    '--request-timeout=30s',
    'exec', '-i', expected.pod.name,
    '--container=projects-config',
    '--',
    'dotnet', COMMAND_DLL, '--privileged-project-setup',
  ];
  const result = spawnSyncImpl(executable, args, {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 30000,
    maxBuffer: 1024 * 1024,
    stdio: ['pipe', 'pipe', 'pipe'],
    input: stdin,
  });
  if (result.error || result.status !== 0) {
    const failure = new Error('Projects Config privileged setup did not return a successful command result.');
    failure.indeterminate = true;
    throw failure;
  }
  try {
    return JSON.parse(result.stdout);
  } catch {
    const failure = new Error('Projects Config privileged setup returned no valid JSON receipt.');
    failure.indeterminate = true;
    throw failure;
  }
}

/** Retry an operation once only when its result is indeterminate and invocation-bound reconciliation is safe. */
async function runIdempotentOperation(operation, runCommand) {
  let lastError;
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      return await runCommand(operation);
    } catch (error) {
      lastError = error;
      if (error?.indeterminate !== true || attempt === 1) throw error;
    }
  }
  throw lastError;
}

/** Require a live Projects authorization context for the exact actor and optional run binding. */
function requireContext(context, actor, binding = null) {
  if (context?.contractVersion !== 1 || context.issuer !== actor.issuer
    || context.actorId !== actor.actorId || context.tenantId !== actor.tenantId
    || context.boundProjectId !== (binding?.projectId ?? null)
    || context.boundRunId !== (binding?.runId ?? null)
    || !Array.isArray(context.effectiveAuthority)) {
    throw new Error('The live Projects authorization context does not match the exact selected actor and run.');
  }
  if (binding) {
    const project = context.effectiveAuthority.find(authority =>
      authority.resourceType === 'project' && authority.resourceId === binding.projectId);
    const permissions = new Set(project?.permissions?.map(value => value.permission) ?? []);
    if (!permissions.has('accessPrivateKnowledge')) {
      throw new Error('The run-bound actor has no current Owner and Orchestrator authority for this project.');
    }
  }
}

/** Verify that an accepted and observed run selection has the exact resource revisions. */
function requireSelection(selection, projectId, runId, projectRevision, platformRevision, contextRevision) {
  if (selection?.projectId !== projectId || selection.runId !== runId
    || selection.projectConfigurationRevision !== projectRevision
    || selection.platformRuntimeRevision !== platformRevision
    || selection.contextRevision !== contextRevision) {
    throw new Error('The owner selection readback does not match the exact fixture project, run, and revisions.');
  }
  return selection;
}

/** Match the accepted root-session receipt to the expected run and positive execution fence. */
function requireRoot(root, projectId, runId, rootSessionId) {
  if (root?.projectId !== projectId || root.runId !== runId
    || root.rootSessionId !== rootSessionId || !Number.isSafeInteger(root.executionFence)
    || root.executionFence < 1 || !Number.isSafeInteger(root.stateVersion) || root.stateVersion < 0) {
    throw new Error('The owner did not return the exact accepted root and execution fence.');
  }
  return root;
}

/** Read run and root-session status concurrently and verify the new owner fence. */
async function readRootEvidence(api, projectId, runId, rootSessionId, executionFence) {
  const [runResult, sessionResult] = await Promise.all([
    invoke(api, 'readRunStatus', { projectId, runId }, undefined, [200]),
    invoke(api, 'readSessionStatus', { projectId, runId, sessionId: rootSessionId }, undefined, [200]),
  ]);
  const run = runResult.value;
  const session = sessionResult.value;
  if (run?.projectId !== projectId || run.runId !== runId || run.rootSessionId !== rootSessionId
    || run.executionFence !== executionFence || session?.identity?.projectId !== projectId
    || session.identity.runId !== runId || session.identity.sessionId !== rootSessionId
    || session.rootSessionId !== rootSessionId || session.executionFence !== executionFence) {
    throw new Error('The owner root readback does not match the exact newly accepted session.');
  }
  return { run, session };
}

/** Read the owner session tree and require each named fixture session to be active exactly once. */
async function readTree(api, projectId, runId, rootSessionId, requiredSessionIds) {
  const { value: tree } = await invoke(
    api, 'readSessionTree', { projectId, runId, sessionId: rootSessionId }, undefined, [200]);
  if (tree?.rootSessionId !== rootSessionId || !Array.isArray(tree.nodes)
    || tree.nodes.some(node => node.identity?.projectId !== projectId || node.identity?.runId !== runId)
    || requiredSessionIds.some(sessionId => tree.nodes.filter(node =>
      node.identity?.sessionId === sessionId && node.lifecycle === 'active').length !== 1)) {
    throw new Error('The live owner session tree does not contain the exact active fixture sessions.');
  }
  return tree;
}

/** Create or recognize the planned pending gate and verify its exact request and owner readback. */
async function createGate(api, plan, input, root, actor, createId) {
  const gatePlan = plan.fixturePlan.gate;
  const { value: currentDecisions } = await invoke(api, 'readDecisions', {
    projectId: root.projectId, runId: root.runId, sessionId: root.rootSessionId,
  }, undefined, [200]);
  if (currentDecisions?.executionFence !== root.executionFence
    || !Number.isSafeInteger(currentDecisions.stateVersion) || currentDecisions.stateVersion < 0) {
    throw new Error('The owner did not return current decision state for gate setup.');
  }
  const requestId = createId('gate-request');
  const request = input.journey === 'gate-answer'
    ? {
      expectedStateVersion: currentDecisions.stateVersion,
      idempotencyKey: createId('gate-idempotency'),
      requestId,
      questionId: createId('gate-question'),
      prompt: gatePlan.prompt,
      allowedChoices: gatePlan.allowedChoices,
      allowsFreeform: gatePlan.allowsFreeform,
    }
    : {
      expectedStateVersion: currentDecisions.stateVersion,
      idempotencyKey: createId('gate-idempotency'),
      requestId,
      subjectId: createId('gate-subject'),
      prompt: gatePlan.prompt,
    };
  const operationId = input.journey === 'gate-answer' ? 'askQuestion' : 'requestApproval';
  const expectedKind = operationId === 'askQuestion' ? 'question' : 'approval';
  const expectedSubjectId = operationId === 'askQuestion' ? request.questionId : request.subjectId;
  const expectedAllowedChoices = operationId === 'askQuestion'
    ? request.allowedChoices ?? []
    : ['approve', 'reject'];
  const matchesPlannedGate = gate => gate?.requestId === requestId
    && gate.kind === expectedKind
    && gate.subjectId === expectedSubjectId
    && gate.authorizedActorId === actor.actorId
    && gate.fence === root.executionFence
    && Array.isArray(gate.allowedChoices)
    && gate.allowedChoices.length === expectedAllowedChoices.length
    && gate.allowedChoices.every((choice, index) => choice === expectedAllowedChoices[index])
    && gate.allowsFreeform === (request.allowsFreeform ?? false)
    && gate.prompt === request.prompt
    && gate.resolvesOutcomeClarification === false;
  let gateRequest;
  let expectedReadbackVersion;
  if (currentDecisions.pendingGate?.requestId === requestId) {
    gateRequest = currentDecisions.pendingGate;
    if (!matchesPlannedGate(gateRequest)) {
      throw new Error('The existing owner gate does not match the exact fixture actor, fence, kind, and planned payload.');
    }
    expectedReadbackVersion = currentDecisions.stateVersion;
  } else if (currentDecisions.pendingGate) {
    throw new Error('A different decision gate already exists for this fixture run.');
  } else {
    const result = await invoke(api, operationId, {
      projectId: root.projectId, runId: root.runId, sessionId: root.rootSessionId,
    }, request, [200]);
    const receipt = result.value;
    if (receipt?.accepted !== true || receipt.executionFence !== root.executionFence
      || !Number.isSafeInteger(receipt.stateVersion)
      || receipt.stateVersion !== currentDecisions.stateVersion + 1
      || !matchesPlannedGate(receipt.pendingGate)) {
      throw new Error('The owner did not accept the exact fixture-owned gate request.');
    }
    gateRequest = receipt.pendingGate;
    expectedReadbackVersion = receipt.stateVersion;
  }
  const [decisionResult, sessionResult] = await Promise.all([
    invoke(api, 'readDecisions', {
      projectId: root.projectId, runId: root.runId, sessionId: root.rootSessionId,
    }, undefined, [200]),
    invoke(api, 'readSessionStatus', {
      projectId: root.projectId, runId: root.runId, sessionId: root.rootSessionId,
    }, undefined, [200]),
  ]);
  const decisions = decisionResult.value;
  const blocker = sessionResult.value?.blockers?.find(item => item.requestId === requestId);
  if (decisions?.executionFence !== root.executionFence
    || decisions.stateVersion !== expectedReadbackVersion
    || !matchesPlannedGate(decisions.pendingGate)
    || !blocker || input.journey === 'gate-answer' && blocker.kind !== 'awaitingInput'
    || input.journey !== 'gate-answer' && blocker.kind !== 'awaitingApproval'
    || !Array.isArray(blocker.choices)
    || blocker.choices.length !== expectedAllowedChoices.length
    || blocker.choices.some((choice, index) => choice !== expectedAllowedChoices[index])
    || blocker.allowsFreeform !== (request.allowsFreeform ?? false)) {
    throw new Error('The owner gate readback does not match the created fixture request.');
  }
  return {
    requestId,
    request: gateRequest,
    readback: { decisions, blocker },
  };
}

/** Bind a resource creation receipt and its owner readback to an invocation and fence. */
function creationReceipt(resourceType, resourceId, invocationId, ownerFence, creationReceipt, observedReceipt) {
  return {
    resourceType,
    resourceId,
    invocationId,
    ownerFence,
    creationReceipt: { resourceId, receipt: creationReceipt },
    observedReceipt: { resourceId, receipt: observedReceipt },
  };
}

/**
 * Create a setup project, accepted run selection, root session, and journey-specific owned resources.
 * Returns a runner-marked input and cleanup handle; setup failures retain enough state for reconciliation.
 */
export async function createP1Fixture(plan, {
  api,
  invocationId,
  setupToken,
  sourceEvidence,
  sourceReceipt,
  runPrivilegedSetup = executePrivilegedProjectSetup,
  acquireRunIdentity,
  now = () => Date.now(),
  createId,
  setupContext,
  namespace,
  kubectl = 'kubectl',
  spawnSyncImpl = spawnSync,
}) {
  if (!UUID.test(invocationId ?? '')) throw new Error('A persisted P1 setup invocation ID is required.');
  if (typeof acquireRunIdentity !== 'function') {
    throw new Error('An already-authorized run identity provider is required.');
  }
  const environment = plan.environment;
  const state = { invocationId, setup: null, setupAttempted: false, selection: null, cleanup: null };
  const actor = {
    actorId: environment.actorId,
    tenantId: environment.tenantId,
    issuer: environment.issuer,
  };
  const idFor = purpose => createId ? createId(purpose) : stableUuid(invocationId, purpose);
  const runId = idFor('run');
  const rootSessionId = idFor('root-session');
  const childSessionId = plan.journey === 'message' ? idFor('child-session') : null;

  const runSetup = async operationKind => {
    if (!UUID.test(operationKind.invocationId ?? '')
      || operationKind.invocationId !== invocationId) {
      throw new Error('The setup operation changed its persisted invocation ID.');
    }
    const request = {
      version: 1,
      invocationId,
      operationKind: operationKind.operationKind,
      tenantId: actor.tenantId,
      expectedProjectRevision: operationKind.expectedProjectRevision ?? null,
      expectedAssignmentRevision: operationKind.expectedAssignmentRevision ?? null,
    };
    const result = await runIdempotentOperation(request, setupRequest => {
      if (setupRequest.operationKind === 'setup') state.setupAttempted = true;
      return runPrivilegedSetup({
        context: setupContext,
        namespace,
        sourceEvidence,
        sourceReceipt,
        accessToken: setupToken,
        request: setupRequest,
        executable: kubectl,
        spawnSyncImpl,
      });
    });
    if (operationKind.operationKind === 'setup') {
      state.setup = requireSetupResult(result, request, actor);
    } else {
      const cleanup = result?.cleanup;
      const cleaned = cleanup?.state === 'cleaned';
      const retained = cleanup?.state === 'retainedPending';
      if (result?.operationKind !== 'cleanup' || !cleanup
        || result.cleanup.invocationId?.toLowerCase() !== invocationId.toLowerCase()
        || result.cleanup.tenantId !== actor.tenantId
        || result.cleanup.projectId !== state.setup?.projectId
        || result.cleanup.assignmentId !== state.setup?.assignmentId
        || !cleaned && !retained
        || cleanup.ownerAssignmentId !== state.setup.ownerAssignmentId
        || cleanup.projectConfigurationRevision !== state.setup.projectConfigurationRevision
        || cleanup.projectRevision !== state.setup.projectRevision + (cleaned ? 1 : 0)
        || cleanup.assignmentRevision !== state.setup.assignmentRevision + (cleaned ? 1 : 0)
        || cleanup.ownerAssignmentRevision !== state.setup.ownerAssignmentRevision + (cleaned ? 1 : 0)
        || (cleaned && (!UUID.test(cleanup.authorityAuditEventId ?? '')
          || !UUID.test(cleanup.ownerRevocationAuditEventId ?? '')))
        || (retained && (cleanup.authorityAuditEventId !== null
          || cleanup.ownerRevocationAuditEventId !== null))) {
        throw new Error('Projects Config did not return the exact setup cleanup receipt.');
      }
      state.cleanup = cleanup;
    }
    return result;
  };

  try {
    await api.discover();
    const { value: initialContext } = await invoke(api, 'getAuthorizationContext', {}, undefined, [200]);
    requireContext(initialContext, actor);

    const setupResult = await runSetup({ invocationId, operationKind: 'setup' });
    const setup = state.setup;
    const projectId = setup.projectId;
    if (setup.projectRevision !== setupResult.setupReadback.projectRevision) {
      throw new Error('The initial project revision does not match Projects Config setup readback.');
    }

    const configurationResult = await invoke(
      api, 'getProjectConfiguration', { projectId }, undefined, [200]);
    const configuration = configurationResult.value;
    if (configuration?.projectId !== projectId
      || configuration.revision !== setup.projectConfigurationRevision) {
      throw new Error('The current project configuration revision does not match setup.');
    }

    const platformRevision = plan.fixturePlan.expectedPlatformRuntimeRevision;
    const selectionRequest = {
      expectedProjectConfigRevision: configuration.revision,
      expectedPlatformRuntimeRevision: platformRevision,
      context: plan.fixturePlan.selectionContext,
    };
    const acceptedSelection = (await invoke(api, 'acceptRunSelection', {
      projectId, runId,
    }, selectionRequest, [200])).value;
    state.selection = acceptedSelection;
    const observedSelection = (await invoke(api, 'getRunSelection', { projectId, runId }, undefined, [200])).value;
    requireSelection(acceptedSelection, projectId, runId, configuration.revision,
      platformRevision, plan.fixturePlan.selectionContext.revision);
    requireSelection(observedSelection, projectId, runId, configuration.revision,
      platformRevision, plan.fixturePlan.selectionContext.revision);

    const identity = await acquireRunIdentity({
      projectId,
      runId,
      actorId: actor.actorId,
      tenantId: actor.tenantId,
      issuer: actor.issuer,
      audience: environment.audience,
      baseUrl: environment.baseUrl,
    });
    const hasDeniedActor = Boolean(plan.fixturePlan.deniedActor);
    if (!identity || typeof identity.token !== 'string'
      || hasDeniedActor && typeof identity.deniedToken !== 'string') {
      throw new Error(hasDeniedActor
        ? 'The identity provider did not return run-bound and distinct denied-actor broker tokens.'
        : 'The identity provider did not return a run-bound broker token.');
    }
    if (!identity.api && typeof identity.createClients === 'function') {
      Object.assign(identity, identity.createClients(hasDeniedActor
        ? { deniedActor: plan.fixturePlan.deniedActor }
        : {}));
    }
    const runApi = identity.api;
    const deniedApi = identity.deniedApi;
    if (!runApi || hasDeniedActor && !deniedApi || !hasDeniedActor && deniedApi) {
      throw new Error(hasDeniedActor
        ? 'The identity provider did not create both planned API clients.'
        : 'The identity provider created an unplanned denied-actor API client.');
    }
    if (identity.projectId !== projectId || identity.runId !== runId) {
      throw new Error('The identity provider did not retain the exact selected project and run binding.');
    }
    if (!runApi.operations?.size) await runApi.discover();
    if (deniedApi && !deniedApi.operations?.size) await deniedApi.discover();
    const { value: runAuthorization } = await invoke(
      runApi, 'getAuthorizationContext', {}, undefined, [200]);
    requireContext(runAuthorization, actor, { projectId, runId });

    const acceptedRoot = (await invoke(runApi, 'acceptRunRoot', { projectId, runId }, {
      sessionId: rootSessionId,
    }, [201])).value;
    const root = requireRoot(acceptedRoot, projectId, runId, rootSessionId);
    const rootEvidence = await readRootEvidence(
      runApi, projectId, runId, rootSessionId, root.executionFence);

    let childReceipt;
    let childTree;
    if (childSessionId) {
      childReceipt = (await invoke(runApi, 'registerChild', {
        projectId, runId, sessionId: rootSessionId,
      }, { sessionId: childSessionId }, [201])).value;
      childTree = await readTree(runApi, projectId, runId, rootSessionId,
        [rootSessionId, childSessionId]);
    }

    let gate;
    if (plan.journey.startsWith('gate-')) {
      gate = await createGate(runApi, plan, {
        journey: plan.journey,
      }, root, actor, idFor);
    }
    const tree = childTree ?? await readTree(runApi, projectId, runId, rootSessionId, [rootSessionId]);
    const sessionId = rootSessionId;
    const expiresAt = new Date(now() + 15 * 60 * 1000).toISOString();
    const fixtureId = idFor('fixture');
    const ownershipReceipts = [
      creationReceipt('project', projectId, invocationId, root.executionFence,
        setupResult.setup, setupResult.setupReadback),
      creationReceipt('run-selection', runId, invocationId, root.executionFence,
        acceptedSelection, observedSelection),
      creationReceipt('root-session', rootSessionId, invocationId, root.executionFence,
        acceptedRoot, rootEvidence.session),
      creationReceipt('session', sessionId, invocationId, root.executionFence,
        acceptedRoot, rootEvidence.session),
      ...(childReceipt ? [creationReceipt('child-session', childSessionId, invocationId,
        root.executionFence, childReceipt, tree)] : []),
      ...(gate ? [creationReceipt('gate', gate.requestId, invocationId, root.executionFence,
        gate.request, gate.readback)] : []),
    ];
    const fixtureScope = {
      kind: 'p1-runner-owned',
      status: 'ready',
      invocationId,
      fixtureId,
      projectId,
      runId,
      rootSessionId,
      sessionId,
      executionFence: root.executionFence,
      expiresAt,
      allowedJourneys: [plan.journey],
      ownershipReceipts,
      ...(plan.fixturePlan.deniedActor ? { deniedActor: plan.fixturePlan.deniedActor } : {}),
      ...(gate ? {
        gate: {
          requestId: gate.requestId,
          answer: plan.fixturePlan.gate.answer,
        },
      } : {}),
      ...(childSessionId ? {
        message: {
          senderSessionId: rootSessionId,
          recipientSessionId: childSessionId,
          text: plan.fixturePlan.message.text,
        },
      } : {}),
    };
    const runtimeInput = {
      ...plan,
      pins: {
        ...plan.pins,
        ownerFences: {
          projectId, runId, rootSessionId, sessionId, executionFence: root.executionFence,
        },
        fixtureScope,
      },
    };
    runnerOwnedFixtureInputs.add(runtimeInput);
    return {
      input: runtimeInput,
      api: runApi,
      deniedApi,
      identity,
      state,
      async cleanup() {
        const cleanupResult = await runSetup({
          invocationId,
          operationKind: 'cleanup',
          expectedProjectRevision: setup.projectRevision,
          expectedAssignmentRevision: setup.assignmentRevision,
        });
        return cleanupResult.cleanup;
      },
      root,
    };
  } catch (error) {
    error.p1SetupState = state;
    error.p1SetupApi = api;
    error.p1SetupCleanup = async () => {
      if (!state.setup) {
        return state.setupAttempted
          ? {
            invocationId,
            state: 'indeterminate',
            reason: 'Privileged setup may have completed, but no matching setup receipt was verified.',
          }
          : { invocationId, state: 'not-created' };
      }
      const cleanup = await runSetup({
        invocationId,
        operationKind: 'cleanup',
        expectedProjectRevision: state.setup.projectRevision,
        expectedAssignmentRevision: state.setup.assignmentRevision,
      });
      return cleanup.cleanup;
    };
    throw error;
  }
}

/** Redact and summarize fixture ownership, setup, selection, dispatch fence, and cleanup outcome. */
export function summarizeP1FixtureCleanup(state, cleanup, nativeFence) {
  return redact({
    invocationId: state?.invocationId ?? null,
    setup: state?.setup ?? null,
    selection: state?.selection ?? null,
    nativeFence: nativeFence ?? { status: 'not-started', reason: 'This setup path never invokes dispatch.' },
    cleanup: cleanup ?? state?.cleanup ?? { state: 'not-started' },
  });
}
