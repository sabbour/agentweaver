import { createHash } from 'node:crypto';
import { redact } from './redaction.mjs';
import { runP1CleanupOperations } from './p1-cleanup-operations.mjs';

/** Create invocation-bound API and Projects Config fixtures for a bounded P1 surface journey. */
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const runnerOwnedFixtureInputs = new WeakSet();
const PROBLEM_CODE = /^[a-z][a-z0-9_]{0,63}$/;

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

/** Return the project name that binds a P1 fixture project to its persisted invocation. */
export function p1FixtureProjectName(invocationId) {
  return `p1-${invocationId.toLowerCase()}`;
}

/** Return whether the exact input object was created by this runner's fixture setup. */
export function isP1RunnerOwnedFixtureInput(input) {
  return runnerOwnedFixtureInputs.has(input);
}

function requireResponse(api, operationId, call, statuses) {
  if (!api.operations.has(operationId) || call?.operationId !== operationId
    || !statuses.includes(call.status)) {
    const failure = new Error(`The P1 owner ${operationId} call returned an unexpected HTTP result.`);
    // Keep only the HTTP status and a well-formed problem code; never keep a raw response body.
    failure.httpEvidence = {
      operationId,
      status: Number.isInteger(call?.status) ? call.status : null,
      problemCode: PROBLEM_CODE.test(call?.transientResponseBody?.code ?? '')
        ? call.transientResponseBody.code : null,
    };
    throw failure;
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
async function invoke(api, operationId, values, body, statuses, query) {
  const call = await api.invoke(operationId, {
    pathParameters: parametersFor(api, operationId, values),
    ...(query === undefined ? {} : { query }),
    ...(body === undefined ? {} : { body }),
  });
  return { call, value: requireResponse(api, operationId, call, statuses) };
}

/** Require a role-assignment receipt for the exact project, actor, role, state, and revision. */
function requireAssignment(assignment, projectId, actor, role, state, revision, assignmentId) {
  if (!UUID.test(assignment?.assignmentId ?? '')
    || (assignmentId && assignment.assignmentId !== assignmentId)
    || assignment.projectId !== projectId || assignment.principalId !== actor.actorId
    || assignment.role !== role || assignment.state !== state
    || assignment.revision !== revision || assignment.grantedBy !== actor.actorId
    || (state === 'revoked' && assignment.revokedBy !== actor.actorId)) {
    throw new Error(`Projects Config did not return the exact ${state} ${role} assignment receipt.`);
  }
  return assignment;
}

/** Require a project summary for the exact invocation-owned project, state, and revision. */
function requireProject(project, invocationId, state, projectId = null, revision = null) {
  if (typeof project?.projectId !== 'string' || !project.projectId
    || (projectId && project.projectId !== projectId)
    || project.name !== p1FixtureProjectName(invocationId) || project.state !== state
    || !Number.isSafeInteger(project.revision) || project.revision < 1
    || (revision !== null && project.revision !== revision)
    || !Number.isSafeInteger(project.configurationRevision) || project.configurationRevision < 1) {
    throw new Error(`Projects Config did not return the exact ${state} fixture project.`);
  }
  return project;
}

/** Keep only allowlisted failure evidence for a mutating setup or cleanup request. */
function recordRequestFailure(state, step, error) {
  const evidence = error?.httpEvidence;
  state.requestFailures.push({
    step,
    operationId: typeof evidence?.operationId === 'string' ? evidence.operationId : null,
    status: Number.isInteger(evidence?.status) ? evidence.status : null,
    problemCode: typeof evidence?.problemCode === 'string' ? evidence.problemCode : null,
  });
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
 * Setup and cleanup use only the public Projects API with the signed-in tenant admin's setup client.
 * Any failure after a mutating setup or cleanup request is sent is indeterminate: it is never retried
 * and no cleanup runs, so an operator can review the invocation-named project first.
 */
export async function createP1Fixture(plan, {
  api,
  invocationId,
  acquireRunIdentity,
  now = () => Date.now(),
  createId,
}) {
  if (!UUID.test(invocationId ?? '')) throw new Error('A persisted P1 setup invocation ID is required.');
  if (typeof acquireRunIdentity !== 'function') {
    throw new Error('An already-authorized run identity provider is required.');
  }
  const environment = plan.environment;
  const state = {
    invocationId,
    projectName: p1FixtureProjectName(invocationId),
    setup: null,
    setupAttempted: false,
    setupIndeterminate: false,
    selection: null,
    cleanup: null,
    requestFailures: [],
  };
  const actor = {
    actorId: environment.actorId,
    tenantId: environment.tenantId,
    issuer: environment.issuer,
  };
  const idFor = purpose => createId ? createId(purpose) : stableUuid(invocationId, purpose);
  const runId = idFor('run');
  const rootSessionId = idFor('root-session');
  const childSessionId = plan.journey === 'message' ? idFor('child-session') : null;

  /** Send one setup mutation; a failure after the request is sent makes setup indeterminate. */
  const setupStep = async (step, send) => {
    state.setupAttempted = true;
    try {
      return await send();
    } catch (error) {
      state.setupIndeterminate = true;
      recordRequestFailure(state, step, error);
      throw error;
    }
  };

  /** Create the invocation-named project and grant the signed-in actor Owner, then Orchestrator. */
  const runSetup = async () => {
    const created = await setupStep('create-project', async () => requireProject((await invoke(
      api, 'createProject', {}, { name: state.projectName }, [201])).value, invocationId, 'active'));
    state.setup = {
      invocationId,
      tenantId: actor.tenantId,
      projectId: created.projectId,
      projectRevision: created.revision,
      projectConfigurationRevision: created.configurationRevision,
      ownerAssignmentId: null,
      ownerAssignmentRevision: null,
      assignmentId: null,
      assignmentRevision: null,
      complete: false,
    };
    const owner = await setupStep('assign-owner', async () => requireAssignment((await invoke(
      api, 'assignProjectRole', { projectId: created.projectId },
      { principalId: actor.actorId, role: 'owner' }, [201])).value,
    created.projectId, actor, 'owner', 'active', 1));
    state.setup.ownerAssignmentId = owner.assignmentId;
    state.setup.ownerAssignmentRevision = owner.revision;
    const orchestrator = await setupStep('assign-orchestrator', async () => requireAssignment((await invoke(
      api, 'assignProjectRole', { projectId: created.projectId },
      { principalId: actor.actorId, role: 'orchestrator' }, [201])).value,
    created.projectId, actor, 'orchestrator', 'active', 1));
    state.setup.assignmentId = orchestrator.assignmentId;
    state.setup.assignmentRevision = orchestrator.revision;
    state.setup.complete = true;
    return { created, owner, orchestrator };
  };

  /**
   * Revoke Orchestrator, archive the project, then revoke Owner. The last Owner of an active project
   * cannot be revoked, so the project is archived first. Any failure after the first request is sent
   * leaves cleanup indeterminate and is not retried.
   */
  const runCleanup = async () => {
    if (state.setupIndeterminate || !state.setup?.complete) {
      return state.cleanup = {
        invocationId,
        state: 'indeterminate',
        reason: 'Setup did not return every expected receipt, so cleanup was not attempted.',
      };
    }
    if (state.cleanup) {
      throw new Error('P1 fixture cleanup was already attempted for this invocation and is never retried.');
    }
    const setup = state.setup;
    state.cleanup = {
      invocationId,
      state: 'indeterminate',
      reason: 'Cleanup was attempted, but not every cleanup receipt was verified.',
    };
    const cleanup = await runP1CleanupOperations({
      api,
      invocationId,
      projectName: state.projectName,
      projectId: setup.projectId,
      projectRevision: setup.projectRevision,
      ownerAssignmentId: setup.ownerAssignmentId,
      ownerAssignmentRevision: setup.ownerAssignmentRevision,
      orchestratorAssignmentId: setup.assignmentId,
      orchestratorAssignmentRevision: setup.assignmentRevision,
      actorId: actor.actorId,
      onFailure(step, error) { recordRequestFailure(state, step, error); },
    });
    return state.cleanup = {
      invocationId,
      state: 'cleaned',
      tenantId: actor.tenantId,
      projectId: setup.projectId,
      projectRevision: cleanup.archived.revision,
      projectState: cleanup.archived.state,
      projectConfigurationRevision: setup.projectConfigurationRevision,
      assignmentId: cleanup.revokedOrchestrator.assignmentId,
      assignmentRevision: cleanup.revokedOrchestrator.revision,
      ownerAssignmentId: cleanup.revokedOwner.assignmentId,
      ownerAssignmentRevision: cleanup.revokedOwner.revision,
    };
  };

  try {
    await api.discover();
    const { value: initialContext } = await invoke(api, 'getAuthorizationContext', {}, undefined, [200]);
    requireContext(initialContext, actor);

    const setupResult = await runSetup();
    const setup = state.setup;
    const projectId = setup.projectId;

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
        setupResult.created, configuration),
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
        return runCleanup();
      },
      root,
    };
  } catch (error) {
    error.p1SetupState = state;
    error.p1SetupApi = api;
    error.p1SetupCleanup = async () => {
      if (!state.setupAttempted) return { invocationId, state: 'not-created' };
      return runCleanup();
    };
    throw error;
  }
}

/** Redact and summarize fixture ownership, setup, selection, dispatch fence, and cleanup outcome. */
export function summarizeP1FixtureCleanup(state, cleanup, nativeFence) {
  return redact({
    invocationId: state?.invocationId ?? null,
    setup: state?.setup ?? null,
    requestFailures: state?.requestFailures ?? [],
    diagnosis: state?.setupIndeterminate || state?.cleanup?.state === 'indeterminate'
      || (cleanup?.state === 'indeterminate') ? {
        state: 'required',
        projectName: state.projectName ?? null,
        reason: 'Find the project with this name through listProjects and review it and the setup actor\'s effective authority through getAuthorizationContext. Do not retry setup or cleanup before that review.',
      } : null,
    selection: state?.selection ?? null,
    nativeFence: nativeFence ?? { status: 'not-started', reason: 'This setup path never invokes dispatch.' },
    cleanup: cleanup ?? state?.cleanup ?? { state: 'not-started' },
  });
}
