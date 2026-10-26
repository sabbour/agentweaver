const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const PROBLEM_CODE = /^[a-z][a-z0-9_]{0,63}$/;
const PROJECT_PATH = '/api/v1/projects/{projectId}';
const ROLE_ASSIGNMENT_PATH = '/api/v1/projects/{projectId}/role-assignments/{assignmentId}';

function problemCode(call) {
  const value = call?.transientResponseBody?.code;
  return PROBLEM_CODE.test(value ?? '') ? value : null;
}

function cleanupError(operationId, call, message) {
  const error = new Error(message);
  error.httpEvidence = {
    operationId,
    status: Number.isInteger(call?.status) ? call.status : null,
    problemCode: problemCode(call),
  };
  return error;
}

function requireOperation(api, operationId, method, expectedPath, queryName) {
  const operation = api.operations?.get(operationId);
  const parameters = operation?.parameters;
  const pathNames = [...expectedPath.matchAll(/\{([^}]+)\}/g)].map(match => match[1]);
  const queryParameters = Array.isArray(parameters)
    ? parameters.filter(parameter => parameter?.in === 'query')
    : [];
  // The Gateway client does not resolve parameter references, so reject them before any cleanup write.
  const hasUnresolvedParameterReference = !Array.isArray(parameters)
    || parameters.some(parameter => !parameter || typeof parameter !== 'object'
      || Array.isArray(parameter) || Object.hasOwn(parameter, '$ref'));
  const hasUnsupportedRequiredParameter = !Array.isArray(parameters)
    || parameters.some(parameter => parameter?.required === true
      && !(parameter.in === 'path' && pathNames.includes(parameter.name))
      && !(queryName && parameter.in === 'query' && parameter.name === queryName));
  if (!operation || operation.method !== method || operation.path !== expectedPath
    || hasUnresolvedParameterReference || hasUnsupportedRequiredParameter
    || (queryName && (queryParameters.length !== 1
      || queryParameters[0].name !== queryName || queryParameters[0].required !== true))) {
    throw new Error(`The P1 cleanup Gateway contract is missing the expected ${operationId} operation.`);
  }
}

function requireAssignment(value, {
  projectId, assignmentId, actorId, role, expectedRevision,
}) {
  if (!UUID.test(value?.assignmentId ?? '')
    || value.assignmentId.toLowerCase() !== assignmentId.toLowerCase()
    || value.projectId !== projectId || value.principalId !== actorId
    || value.role !== role || value.state !== 'revoked'
    || value.revision !== expectedRevision + 1
    || value.grantedBy !== actorId || value.revokedBy !== actorId) {
    throw new Error(`Projects Config did not return the exact revoked ${role} assignment receipt.`);
  }
  return {
    assignmentId: value.assignmentId,
    role: value.role,
    state: value.state,
    revision: value.revision,
  };
}

function requireArchivedProject(value, {
  projectId, projectName, expectedRevision,
}) {
  if (value?.projectId !== projectId || value.name !== projectName
    || value.state !== 'archived' || value.revision !== expectedRevision + 1
    || !Number.isSafeInteger(value.configurationRevision) || value.configurationRevision < 1) {
    throw new Error('Projects Config did not return the exact archived project receipt.');
  }
  return {
    projectId: value.projectId,
    state: value.state,
    revision: value.revision,
  };
}

/** Revoke Orchestrator, archive the project, then revoke its last Owner exactly once per write. */
export async function runP1CleanupOperations({
  api,
  invocationId,
  projectName,
  projectId,
  projectRevision,
  ownerAssignmentId,
  ownerAssignmentRevision,
  orchestratorAssignmentId,
  orchestratorAssignmentRevision,
  actorId,
  signal,
  onFailure = () => {},
}) {
  if (!api || typeof api.invoke !== 'function' || !(api.operations instanceof Map)
    || typeof invocationId !== 'string' || typeof projectName !== 'string'
    || typeof projectId !== 'string' || !projectId
    || typeof actorId !== 'string' || !actorId
    || !Number.isSafeInteger(projectRevision) || projectRevision < 1
    || !Number.isSafeInteger(ownerAssignmentRevision) || ownerAssignmentRevision < 1
    || !Number.isSafeInteger(orchestratorAssignmentRevision) || orchestratorAssignmentRevision < 1
    || !UUID.test(ownerAssignmentId ?? '') || !UUID.test(orchestratorAssignmentId ?? '')
    || typeof onFailure !== 'function') {
    throw new Error('The P1 cleanup target or API client is incomplete.');
  }

  requireOperation(api, 'revokeProjectRoleAssignment', 'DELETE', ROLE_ASSIGNMENT_PATH, 'expectedRevision');
  requireOperation(api, 'updateProject', 'PATCH', PROJECT_PATH);
  const updateProjectRequestBody = api.operations.get('updateProject').requestBody;
  const updateProjectSchema = api.schemas instanceof Map
    ? api.schemas.get('UpdateProjectRequest')
    : null;
  const updateProjectProperties = updateProjectSchema?.properties;
  const requiredUpdateProjectProperties = new Set(
    Array.isArray(updateProjectSchema?.required) ? updateProjectSchema.required : [],
  );
  // Resolve the advertised schema and confirm that archive requests remain revision guarded.
  if (updateProjectRequestBody?.required !== true
    || updateProjectRequestBody.content?.['application/json']?.schema?.$ref
      !== '#/components/schemas/UpdateProjectRequest'
    || updateProjectSchema?.type !== 'object'
    || !requiredUpdateProjectProperties.has('expectedRevision')
    || !requiredUpdateProjectProperties.has('name')
    || !requiredUpdateProjectProperties.has('state')
    || updateProjectProperties?.expectedRevision?.type !== 'integer'
    || updateProjectProperties?.name?.type !== 'string'
    || updateProjectProperties?.state?.type !== 'string'
    || !Array.isArray(updateProjectProperties?.state?.enum)
    || !updateProjectProperties.state.enum.includes('archived')) {
    throw new Error('The P1 cleanup Gateway contract does not advertise the revision-guarded project update schema.');
  }

  const steps = [];
  const runStep = async (step, operationId, expectedRevision, invoke, verify) => {
    let call;
    try {
      call = await invoke();
      if (call?.operationId !== operationId || call.status !== 200) {
        throw cleanupError(
          operationId,
          call,
          `The P1 owner ${operationId} call returned an unexpected HTTP result.`,
        );
      }
      let receipt;
      try {
        receipt = verify(call.transientResponseBody);
      } catch (error) {
        error.httpEvidence ??= {
          operationId,
          status: Number.isInteger(call.status) ? call.status : null,
          problemCode: problemCode(call),
        };
        throw error;
      }
      steps.push({ step, operationId, expectedRevision, outcome: 'confirmed', status: call.status, receipt });
      return receipt;
    } catch (error) {
      onFailure(step, error);
      steps.push({
        step,
        operationId,
        expectedRevision,
        outcome: 'indeterminate',
        status: Number.isInteger(error?.httpEvidence?.status) ? error.httpEvidence.status : null,
        problemCode: typeof error?.httpEvidence?.problemCode === 'string'
          ? error.httpEvidence.problemCode : null,
      });
      error.p1CleanupSteps = steps;
      throw error;
    }
  };

  // Keep the last Owner until the project is archived; the API rejects revoking it while active.
  const revokedOrchestrator = await runStep(
    'revoke-orchestrator',
    'revokeProjectRoleAssignment',
    orchestratorAssignmentRevision,
    () => api.invoke('revokeProjectRoleAssignment', {
      pathParameters: { projectId, assignmentId: orchestratorAssignmentId },
      query: { expectedRevision: orchestratorAssignmentRevision },
      signal,
    }),
    value => requireAssignment(value, {
      projectId,
      assignmentId: orchestratorAssignmentId,
      actorId,
      role: 'orchestrator',
      expectedRevision: orchestratorAssignmentRevision,
    }),
  );
  const archived = await runStep(
    'archive-project',
    'updateProject',
    projectRevision,
    () => api.invoke('updateProject', {
      pathParameters: { projectId },
      body: { expectedRevision: projectRevision, name: projectName, state: 'archived' },
      signal,
    }),
    value => requireArchivedProject(value, { projectId, projectName, expectedRevision: projectRevision }),
  );
  const revokedOwner = await runStep(
    'revoke-owner',
    'revokeProjectRoleAssignment',
    ownerAssignmentRevision,
    () => api.invoke('revokeProjectRoleAssignment', {
      pathParameters: { projectId, assignmentId: ownerAssignmentId },
      query: { expectedRevision: ownerAssignmentRevision },
      signal,
    }),
    value => requireAssignment(value, {
      projectId,
      assignmentId: ownerAssignmentId,
      actorId,
      role: 'owner',
      expectedRevision: ownerAssignmentRevision,
    }),
  );
  return { steps, archived, revokedOrchestrator, revokedOwner };
}
