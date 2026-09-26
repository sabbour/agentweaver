// Generated-artifact SEAM driver (issue #1 expansion, requirement 2).
//
// Where a dynamically-driven persona run (a dispatched PersonaActor sub-agent
// calling the live API directly, guided by a persona brief + the live OpenAPI
// spec — see .github/agents/persona-actor.agent.md) judges a *product outcome*
// (a drafted plan), this driver targets the GENERATION SEAMS themselves:
// it asks the product to
// generate blueprints and workflows, then asserts the generated artifacts are
// STRUCTURALLY CORRECT using the same rules the backend enforces (lib/generation-checks.mjs).
//
// This is the harness's job to catch automatically — the class of bug a human had to
// notice by hand in issue #311 (a generated roster that leaked a reserved system role),
// or a generated workflow with dangling edges / unrouted check branches that would only
// blow up at run time.
//
// Bounded + safe by default: it calls generation endpoints (which return UNSAVED drafts) and a
// throwaway project, then cleans up. With --keep, explicitly selected generated workflows may be
// saved and queued for real provider-backed execution so post-merge acceptance can inspect the
// durable project/run; the harness never fabricates completion.

import {
  analyzeConservativeFan,
  findReservedRoleLeaks,
  validateWorkflowYaml,
  workflowNodeRoles,
} from './generation-checks.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

// Upstream model/provider failures (auth, rate-limit, provider down) are NOT product
// bugs in generation structure — they make the seam un-assessable. We surface them as
// an inconclusive result rather than a false regression.
const PROVIDER_FAIL_STATUS = new Set([401, 402, 429, 500, 502, 503, 504]);
const MODEL_PROVIDER_KEY_HEADER = 'If-Model-Provider-Key';
const TERMINAL_JOB_STATUSES = new Set(['completed', 'failed', 'cancelled']);

export async function prepareAiExecutionContext(client, operation, projectId, options) {
  const body = { operation };
  if (projectId) body.project_id = projectId;
  const response = await client.post('/api/ai/execution-context', body, options);
  const raw = response.transientResponseBody ?? response.responseBody ?? {};
  delete response.transientResponseBody;
  const context = raw.context ?? raw;
  const executionKey = typeof context.execution_key === 'string' && context.execution_key.length > 0
    ? context.execution_key
    : null;
  const aiRequired = context.ai_required === true;
  const providerState = typeof context.effective_model_provider?.state === 'string'
    ? context.effective_model_provider.state
    : null;
  const responseOperation = typeof context.operation === 'string' ? context.operation : null;
  const phase = typeof context.phase === 'string' ? context.phase : null;
  const error = typeof raw.error === 'string' ? raw.error : null;
  const message = typeof raw.message === 'string' ? raw.message : null;
  const ready = response.ok && (!aiRequired || !!executionKey)
    && responseOperation === operation && phase === 'prepared';

  // Replace the captured response before it is persisted. The short-lived key is
  // used only in this call chain and is never included in harness evidence.
  response.responseBody = {
    error,
    message,
    ai_required: aiRequired,
    operation: responseOperation,
    phase,
    has_execution_key: !!executionKey,
    effective_model_provider: providerState ? { state: providerState } : null,
  };

  return {
    ready,
    inconclusive: !ready && PROVIDER_FAIL_STATUS.has(response.status),
    headers: executionKey ? { [MODEL_PROVIDER_KEY_HEADER]: executionKey } : {},
    evidence: {
      status: response.status,
      operation: responseOperation,
      phase,
      aiRequired,
      keyPresent: !!executionKey,
      providerState,
      error,
    },
  };
}

function addExecutionContextCheck(label, context, add) {
  const detail = context.ready
    ? `status ${context.evidence.status}; operation=${context.evidence.operation}, phase=${context.evidence.phase}`
    : `status ${context.evidence.status}${context.evidence.error ? `; ${context.evidence.error}` : ''}`;
  add(
    `Prepared AI execution context for ${label}`,
    context.ready || context.inconclusive,
    detail,
    context.inconclusive ? 'CANNOT_DETERMINE' : 'P0',
  );
}

export function replacementExecutionContext(response, operation) {
  const raw = response.transientResponseBody ?? response.responseBody ?? {};
  delete response.transientResponseBody;
  if (raw.error !== 'model_provider_changed' || !raw.context) return null;

  const context = raw.context;
  const executionKey = typeof context.execution_key === 'string' && context.execution_key.length > 0
    ? context.execution_key
    : null;
  const aiRequired = context.ai_required === true;
  const responseOperation = typeof context.operation === 'string' ? context.operation : null;
  const phase = typeof context.phase === 'string' ? context.phase : null;
  const providerState = typeof context.effective_model_provider?.state === 'string'
    ? context.effective_model_provider.state
    : null;
  const ready = (!aiRequired || !!executionKey) && responseOperation === operation && phase === 'prepared';

  response.responseBody = {
    error: raw.error,
    message: typeof raw.message === 'string' ? raw.message : null,
    context: {
      ai_required: aiRequired,
      operation: responseOperation,
      phase,
      has_execution_key: !!executionKey,
      effective_model_provider: providerState ? { state: providerState } : null,
    },
  };

  return {
    ready,
    headers: executionKey ? { [MODEL_PROVIDER_KEY_HEADER]: executionKey } : {},
    evidence: {
      status: response.status,
      operation: responseOperation,
      phase,
      aiRequired,
      keyPresent: !!executionKey,
      providerState,
      retried: false,
      retryStatus: null,
    },
  };
}

async function retryWithReplacementContext(client, {
  response, operation, path, body, time, timingKey, extraHeaders = {},
}) {
  const replacement = replacementExecutionContext(response, operation);
  if (!replacement?.ready) return { response, replacement: replacement?.evidence ?? null };

  const retried = await time(`${timingKey}RetryMs`, () =>
    client.post(path, body, { headers: { ...extraHeaders, ...replacement.headers } }),
  );
  return {
    response: retried,
    replacement: { ...replacement.evidence, retried: true, retryStatus: retried.status },
  };
}

function durableIdempotencyKey(prefix) {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function jobStatus(response) {
  return typeof response?.responseBody?.status === 'string'
    ? response.responseBody.status.toLowerCase()
    : null;
}

function jobId(response) {
  return response?.responseBody?.job_id ?? response?.responseBody?.jobId ?? null;
}

function jobUrl(response, name) {
  return response?.responseBody?.[name] ?? null;
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForDurableJob(client, initial, { time, timingKey, timeoutMs }) {
  let current = initial;
  const statusUrl = jobUrl(initial, 'status_url') ?? jobUrl(initial, 'statusUrl');
  if (!statusUrl) return current;
  const deadline = Date.now() + (timeoutMs ?? 300_000);
  while (!TERMINAL_JOB_STATUSES.has(jobStatus(current) ?? '') && Date.now() < deadline) {
    await sleep(1000);
    current = await time(`${timingKey}StatusMs`, () => client.get(statusUrl));
  }
  return current;
}

async function submitDurableJob(client, {
  path,
  body,
  headers,
  idempotencyKey,
  time,
  timingKey,
  timeoutMs,
}) {
  const requestHeaders = { ...headers, 'Idempotency-Key': idempotencyKey };
  const accepted = await time(`${timingKey}AcceptMs`, () =>
    client.post(path, body, { headers: requestHeaders }),
  );
  if (accepted.status !== 202) {
    return { durable: false, accepted, duplicate: null, final: accepted, result: null };
  }
  const duplicate = await time(`${timingKey}IdempotencyMs`, () =>
    client.post(path, body, { headers: requestHeaders }),
  );
  const final = await waitForDurableJob(client, accepted, { time, timingKey, timeoutMs });
  const resultUrl = jobUrl(final, 'result_url') ?? jobUrl(final, 'resultUrl') ?? jobUrl(accepted, 'result_url') ?? jobUrl(accepted, 'resultUrl');
  const result = jobStatus(final) === 'completed' && resultUrl
    ? await time(`${timingKey}ResultMs`, () => client.get(resultUrl))
    : null;
  return { durable: true, accepted, duplicate, final, result };
}

async function exerciseCancelRetry(client, {
  path,
  body,
  headers,
  idempotencyKey,
  time,
  timingKey,
}) {
  const accepted = await time(`${timingKey}AcceptMs`, () =>
    client.post(path, body, { headers: { ...headers, 'Idempotency-Key': idempotencyKey } }),
  );
  if (accepted.status !== 202) return { accepted, cancel: null, retry: null };
  const cancelUrl = jobUrl(accepted, 'cancel_url') ?? jobUrl(accepted, 'cancelUrl');
  const cancel = cancelUrl
    ? await time(`${timingKey}CancelMs`, () => client.post(cancelUrl, {}))
    : null;
  const retryUrl = jobUrl(cancel, 'retry_url') ?? jobUrl(cancel, 'retryUrl') ?? jobUrl(accepted, 'retry_url') ?? jobUrl(accepted, 'retryUrl');
  const retry = retryUrl
    ? await time(`${timingKey}RetryMs`, () => client.post(retryUrl, {}))
    : null;
  return { accepted, cancel, retry };
}

/**
 * @param {import('./client.mjs').AgentweaverClient} client
 * @param {any} scenario  generation-seam scenario (kind: 'generation-seam')
 * @param {Object} opts
 * @param {boolean} [opts.keep]
 */
export function verifyPmDiscovery(response) {
  const workflow = response.responseBody ?? {};
  const nodes = Array.isArray(workflow.nodes) ? workflow.nodes : [];
  const edges = Array.isArray(workflow.edges) ? workflow.edges : [];
  const branchIds = edges
    .filter((edge) => edge.from === 'discovery-fan-out')
    .map((edge) => edge.to);
  const customer = nodes.find((node) => node.id === 'customer-signal-research');
  const technical = nodes.find((node) => node.id === 'technical-feasibility-research');
  const valid = response.ok
    && branchIds.join(',') === 'customer-signal-research,technical-feasibility-research'
    && customer?.independent === true
    && technical?.independent === true
    && customer?.declared_output_paths?.join(',') === 'customer-signals.md'
    && technical?.declared_output_paths?.join(',') === 'technical-feasibility.md'
    && edges.some((edge) => edge.from === 'customer-signal-research' && edge.to === 'discovery-fan-in')
    && edges.some((edge) => edge.from === 'technical-feasibility-research' && edge.to === 'discovery-fan-in')
    && edges.some((edge) => edge.from === 'discovery-fan-in' && edge.to === 'synthesis');
  return {
    valid,
    branchIds,
    customerOutputPaths: customer?.declared_output_paths ?? [],
    technicalOutputPaths: technical?.declared_output_paths ?? [],
  };
}

export async function runGenerationSeams(client, scenario, opts = {}) {
  const lifecycle = { projectId: null, cleanupAttempted: false };
  const cleanup = async () => {
    if (opts.keep || !lifecycle.projectId || lifecycle.cleanupAttempted) return;
    lifecycle.cleanupAttempted = true;
    const response = await client.del(`/api/projects/${lifecycle.projectId}?confirm=true`);
    if (!response.ok) {
      throw new Error(`throwaway project cleanup failed with status ${response.status}`);
    }
  };

  try {
    const result = await executeGenerationSeams(client, scenario, { ...opts, lifecycle });
    result.cleanup = cleanup;
    return result;
  } catch (primaryError) {
    try {
      await cleanup();
    } catch (cleanupError) {
      primaryError.cleanupErrors = [String(cleanupError?.message ?? cleanupError)];
    }
    throw primaryError;
  }
}

async function executeGenerationSeams(client, scenario, opts = {}) {
  const started = Date.now();
  const timings = {};
  const evidence = {
    deployment: null,
    authentication: null,
    aiExecutionContexts: {
      blueprintGeneration: null,
      workflowGeneration: null,
    },
    projectId: null,
    generatedBlueprint: null,
    generatedBlueprintWorkflowValid: null,
    generatedWorkflow: null,
    generatedWorkflowValidation: null,
    conservativeFanCases: [],
    retainedWorkflowIds: [],
    retainedRunTriggers: [],
    retainedRuntimeProofs: scenario.retainedRuntimeProofs ?? [],
    pmDiscovery: null,
  };
  /** @type {{name:string, pass:boolean, detail:string, category:string, skipped?:boolean}[]} */
  const checks = [];
  // Taxonomy: P0 = platform-correctness, P1 = output-quality, CANNOT_DETERMINE =
  // unobservable (e.g. the generator's model provider was down). CANNOT_DETERMINE is
  // excluded from pass/fail scoring rather than guessed.
  const add = (name, pass, detail = '', category = 'P0') =>
    checks.push({ name, pass: !!pass, detail, category, skipped: category === 'CANNOT_DETERMINE' });
  let inconclusive = false;
  let blueprintGeneratedWorkflowYaml = null;

  const time = async (key, fn) => {
    const t0 = Date.now();
    try {
      return await fn();
    } finally {
      timings[key] = Date.now() - t0;
    }
  };

  // --- Deployment and auth preflight ---
  // Version is public and confirms this is a current deployed API before the harness
  // sends an authenticated request or creates its throwaway project.
  const version = await client.get('/api/version', { authenticated: false });
  const deployedVersion = version.ok && typeof version.responseBody?.version === 'string'
    ? version.responseBody.version.trim()
    : '';
  const gitSha = version.ok && typeof version.responseBody?.gitSha === 'string'
    ? version.responseBody.gitSha.trim()
    : '';
  evidence.deployment = {
    version: deployedVersion || null,
    gitSha: gitSha || null,
    isRelease: version.ok && typeof version.responseBody?.isRelease === 'boolean'
      ? version.responseBody.isRelease
      : null,
  };
  add(
    'Deployment reports its current version (/api/version)',
    !!deployedVersion,
    deployedVersion
      ? `${deployedVersion}${gitSha ? ` (${gitSha})` : ''}`
      : `status ${version.status}`,
  );
  if (!deployedVersion) return finalize();

  // /api/auth/config describes the identity system without exposing its client
  // configuration in evidence. /api/auth/session is the authenticated preflight.
  const authConfig = await client.get('/api/auth/config', { authenticated: false });
  const authMode = authConfig.ok && typeof authConfig.responseBody?.mode === 'string'
    ? authConfig.responseBody.mode
    : null;
  evidence.authentication = {
    configStatus: authConfig.status,
    serverMode: authMode,
    sessionStatus: null,
  };
  authConfig.responseBody = authMode ? { mode: authMode } : null;
  const supportedAuthMode = authMode === 'Entra' || authMode === 'LocalTest';
  add(
    'Deployment exposes its authentication configuration (/api/auth/config)',
    authConfig.ok && supportedAuthMode,
    authMode ? `server auth mode ${authMode}` : `status ${authConfig.status}`,
  );
  if (!authConfig.ok || !supportedAuthMode) return finalize();

  const auth = await client.get('/api/auth/session');
  const signedIn = auth.ok && auth.responseBody?.authenticated === true;
  evidence.authentication.sessionStatus = auth.status;
  const authModeFromSession = typeof auth.responseBody?.auth_mode === 'string'
    ? auth.responseBody.auth_mode
    : null;
  auth.responseBody = {
    authenticated: signedIn,
    auth_mode: authModeFromSession,
  };
  const authDetail = signedIn
    ? `authenticated ${authMode} session`
    : `status ${auth.status}; a valid ${authMode} bearer token is required (GitHub CLI tokens are not accepted)`;
  add(`Authenticated ${authMode} bearer token accepted (/api/auth/session)`, signedIn, authDetail);
  if (!signedIn) return finalize();

  // ── SEAM 1: blueprint generation ────────────────────────────────────────────
  const blueprintContext = await time('blueprintExecutionContextMs', () =>
    prepareAiExecutionContext(client, 'blueprint_generation'),
  );
  addExecutionContextCheck('blueprint generation', blueprintContext, add);
  if (blueprintContext.inconclusive) inconclusive = true;

  const blueprintBody = { description: scenario.blueprintDescription };
  const blueprintIdempotencyKey = durableIdempotencyKey('api-harness-blueprint');
  const blueprintRequest = blueprintContext.ready
    ? await submitDurableJob(client, {
      path: '/api/blueprints/generate',
      body: blueprintBody,
      headers: blueprintContext.headers,
      idempotencyKey: blueprintIdempotencyKey,
      time,
      timingKey: 'blueprintGenerate',
      timeoutMs: opts.timeoutMs,
    })
    : null;
  const blueprintResult = blueprintRequest?.durable === false
    ? await retryWithReplacementContext(client, {
      response: blueprintRequest.accepted,
      operation: 'blueprint_generation',
      path: '/api/blueprints/generate',
      body: blueprintBody,
      time,
      timingKey: 'blueprintGenerate',
      extraHeaders: { 'Idempotency-Key': blueprintIdempotencyKey },
    })
    : { response: blueprintRequest?.result ?? blueprintRequest?.final ?? null, replacement: null };
  evidence.aiExecutionContexts.blueprintGeneration = {
    ...blueprintContext.evidence,
    replacement: blueprintResult.replacement,
    ...(blueprintRequest?.durable ? {
      job: {
        acceptedStatus: blueprintRequest.accepted.status,
        duplicateStatus: blueprintRequest.duplicate?.status ?? null,
        jobId: jobId(blueprintRequest.accepted),
        duplicateJobId: jobId(blueprintRequest.duplicate),
        terminalStatus: jobStatus(blueprintRequest.final),
        resultStatus: blueprintRequest.result?.status ?? null,
      },
    } : {}),
  };
  if (blueprintRequest?.durable) {
    add(
      'Blueprint generation job is accepted durably (202)',
      blueprintRequest.accepted.status === 202 && !!jobId(blueprintRequest.accepted),
      `status ${blueprintRequest.accepted.status}; job=${jobId(blueprintRequest.accepted) ?? '(missing)'}`,
    );
    add(
      'Blueprint generation idempotency reuses the same job',
      blueprintRequest.duplicate?.status === 202
        && !!jobId(blueprintRequest.accepted)
        && jobId(blueprintRequest.accepted) === jobId(blueprintRequest.duplicate),
      `first=${jobId(blueprintRequest.accepted) ?? '(missing)'}, duplicate=${jobId(blueprintRequest.duplicate) ?? '(missing)'}`,
    );
    add(
      'Blueprint generation reaches terminal completed status through the hosted worker',
      jobStatus(blueprintRequest.final) === 'completed',
      `terminal status=${jobStatus(blueprintRequest.final) ?? '(missing)'}`,
    );
    add(
      'Blueprint generation exposes a result artifact',
      blueprintRequest.result?.ok === true
        && !!blueprintRequest.result.responseBody?.artifact_id
        && !!blueprintRequest.result.responseBody?.blueprint,
      `result status=${blueprintRequest.result?.status ?? '(not fetched)'}`,
    );
  } else if (scenario.requireDurableJobs && blueprintRequest?.accepted) {
    add(
      'Blueprint generation uses the durable job contract (202)',
      false,
      `status ${blueprintRequest.accepted.status}`,
    );
  }
  if (blueprintResult.replacement?.retried) {
    add(
      'Blueprint generator accepts replacement AI execution context after provider change',
      blueprintResult.response.status === 200,
      `retry status ${blueprintResult.response.status}`,
    );
  }
  const genBp = blueprintResult.response;

  if (genBp && genBp.status !== 200) {
    if (PROVIDER_FAIL_STATUS.has(genBp.status)) {
      inconclusive = true;
      add('Blueprint generator reachable', true, `provider unavailable (status ${genBp.status}) — seam not assessed`, 'CANNOT_DETERMINE');
    } else if (genBp) {
      add('Blueprint generation returned a usable draft', false, `status ${genBp.status}: ${JSON.stringify(redact(genBp.responseBody)).slice(0, 300)}`);
    }
  } else {
    const bp = genBp?.responseBody?.blueprint ?? {};
    const genWfYaml = genBp?.responseBody?.generated_workflow_yaml ?? null;
    blueprintGeneratedWorkflowYaml = genWfYaml;
    evidence.generatedBlueprint = {
      id: bp.id,
      name: bp.name,
      roster: bp.roster ?? [],
      bespokeRoles: (bp.bespoke_roles ?? []).map((b) => b?.id ?? b?.title),
      workflows: bp.workflows ?? (bp.workflow ? [bp.workflow] : []),
      hasGeneratedWorkflowYaml: !!genWfYaml,
    };

    const roster = bp.roster ?? [];
    const minRoster = scenario.minRosterSize ?? 2;
    add(
      `Generated roster is a real multi-role team (≥${minRoster})`,
      Array.isArray(roster) && roster.length >= minRoster,
      `${Array.isArray(roster) ? roster.length : 0} role(s): ${(roster ?? []).join(', ')}`,
      'P0',
    );

    // The issue #311 seam: a generated roster must exclude reserved system roles.
    const leaks = findReservedRoleLeaks({
      roster,
      bespoke_roles: bp.bespoke_roles ?? [],
      workflowRoles: genWfYaml ? workflowNodeRoles(genWfYaml) : [],
    });
    add(
      'Generated roster excludes reserved system roles (Scribe/Work Monitor/Rai/Coordinator — issue #311)',
      leaks.offenders.length === 0,
      leaks.offenders.length === 0 ? 'no reserved-role leakage' : `LEAKED reserved role(s): ${leaks.offenders.join(', ')}`,
    );

    add(
      'Generated blueprint bundles at least one workflow',
      (bp.workflows?.length ?? 0) > 0 || !!bp.workflow || !!genWfYaml,
      `workflows=[${(bp.workflows ?? []).join(', ')}]${genWfYaml ? ' + inline generated_workflow_yaml' : ''}`,
    );

    // If the generator produced a custom workflow inline, it must pass structural validation.
    if (genWfYaml) {
      const v = validateWorkflowYaml(genWfYaml);
      const fan = analyzeConservativeFan(genWfYaml);
      evidence.generatedBlueprintWorkflowValid = {
        valid: v.valid,
        errors: v.errors,
        nodeCount: v.nodeCount,
        conservativeFan: fan,
      };
      add(
        "Blueprint's inline generated workflow passes backend structural validation",
        v.valid,
        v.valid ? `${v.nodeCount} nodes, structurally valid` : `${v.errors.length} error(s): ${v.errors.slice(0, 3).join('; ')}`,
      );
      add(
        "Blueprint's inline generated workflow obeys conservative fan safety",
        fan.safe,
        fan.safe ? `mode=${fan.mode}` : fan.errors.slice(0, 3).join('; '),
      );
    }
  }

  // ── SEAM 2: project-scoped workflow generation ──────────────────────────────
  // Needs an owned project. Create a throwaway one seeded with a base blueprint so
  // the generator can constrain nodes to the cast roles (FR-061).
  const slug = `${scenario.projectPrefix}-${Date.now().toString(36)}`;
  const create = await time('projectCreateMs', () =>
    client.post('/api/projects', {
      name: slug,
      origin: 'blank',
      working_directory: slug,
      blueprint_id: scenario.baseBlueprintId,
    }),
  );
  evidence.projectId = create.responseBody?.project_id ?? null;
  opts.lifecycle.projectId = evidence.projectId;
  add(
    'Throwaway project created to host workflow generation',
    create.status === 201 && !!evidence.projectId,
    evidence.projectId ? `project ${evidence.projectId}` : `status ${create.status}`,
  );

  if (evidence.projectId) {
    if (scenario.verifyPmDiscovery) {
      await inspectPmDiscovery(client, evidence.projectId, evidence, add);
    }
    if (opts.keep && blueprintGeneratedWorkflowYaml) {
      await retainGeneratedWorkflow(
        client,
        evidence.projectId,
        blueprintGeneratedWorkflowYaml,
        'blueprint custom workflow',
        evidence,
        add,
      );
    }

    const workflowContext = await time('workflowExecutionContextMs', () =>
      prepareAiExecutionContext(client, 'workflow_generation', evidence.projectId),
    );
    addExecutionContextCheck('workflow generation', workflowContext, add);
    if (workflowContext.inconclusive) inconclusive = true;

    const workflowBody = { description: scenario.workflowDescription };
    const workflowPath = `/api/projects/${evidence.projectId}/workflows/generate`;
    const workflowIdempotencyKey = durableIdempotencyKey('api-harness-workflow');
    const workflowRequest = workflowContext.ready
      ? await submitDurableJob(client, {
        path: workflowPath,
        body: workflowBody,
        headers: workflowContext.headers,
        idempotencyKey: workflowIdempotencyKey,
        time,
        timingKey: 'workflowGenerate',
        timeoutMs: opts.timeoutMs,
      })
      : null;
    const workflowResult = workflowRequest?.durable === false
      ? await retryWithReplacementContext(client, {
        response: workflowRequest.accepted,
        operation: 'workflow_generation',
        path: workflowPath,
        body: workflowBody,
        time,
        timingKey: 'workflowGenerate',
        extraHeaders: { 'Idempotency-Key': workflowIdempotencyKey },
      })
      : { response: workflowRequest?.result ?? workflowRequest?.final ?? null, replacement: null };
    const cancelRetryContext = workflowContext.ready && workflowRequest?.durable
      ? await time('workflowCancelRetryExecutionContextMs', () =>
        prepareAiExecutionContext(client, 'workflow_generation', evidence.projectId),
      )
      : null;
    if (cancelRetryContext) {
      addExecutionContextCheck('workflow cancellation/retry probe', cancelRetryContext, add);
      if (cancelRetryContext.inconclusive) inconclusive = true;
    }
    const cancelRetry = cancelRetryContext?.ready
      ? await exerciseCancelRetry(client, {
        path: workflowPath,
        body: { description: `${scenario.workflowDescription}\n\nCancellation/retry probe.` },
        headers: cancelRetryContext.headers,
        idempotencyKey: durableIdempotencyKey('api-harness-workflow-cancel'),
        time,
        timingKey: 'workflowCancelRetry',
      })
      : null;
    evidence.aiExecutionContexts.workflowGeneration = {
      ...workflowContext.evidence,
      replacement: workflowResult.replacement,
      ...(workflowRequest?.durable ? {
        job: {
          acceptedStatus: workflowRequest.accepted.status,
          duplicateStatus: workflowRequest.duplicate?.status ?? null,
          jobId: jobId(workflowRequest.accepted),
          duplicateJobId: jobId(workflowRequest.duplicate),
          terminalStatus: jobStatus(workflowRequest.final),
          resultStatus: workflowRequest.result?.status ?? null,
        },
      } : {}),
      ...(cancelRetry ? {
        cancelRetry: {
          contextStatus: cancelRetryContext?.evidence.status ?? null,
          acceptedStatus: cancelRetry.accepted?.status ?? null,
          cancelStatus: cancelRetry.cancel?.status ?? null,
          cancelledJobStatus: jobStatus(cancelRetry.cancel),
          retryStatus: cancelRetry.retry?.status ?? null,
          retriedJobStatus: jobStatus(cancelRetry.retry),
        },
      } : {}),
    };
    if (workflowRequest?.durable) {
      add(
        'Advanced workflow generation job is accepted durably (202)',
        workflowRequest.accepted.status === 202 && !!jobId(workflowRequest.accepted),
        `status ${workflowRequest.accepted.status}; job=${jobId(workflowRequest.accepted) ?? '(missing)'}`,
      );
      add(
        'Advanced workflow generation idempotency reuses the same job',
        workflowRequest.duplicate?.status === 202
          && !!jobId(workflowRequest.accepted)
          && jobId(workflowRequest.accepted) === jobId(workflowRequest.duplicate),
        `first=${jobId(workflowRequest.accepted) ?? '(missing)'}, duplicate=${jobId(workflowRequest.duplicate) ?? '(missing)'}`,
      );
      add(
        'Advanced workflow generation reaches terminal completed status through the hosted worker',
        jobStatus(workflowRequest.final) === 'completed',
        `terminal status=${jobStatus(workflowRequest.final) ?? '(missing)'}`,
      );
      add(
        'Advanced workflow generation exposes a result artifact',
        workflowRequest.result?.ok === true
          && !!workflowRequest.result.responseBody?.artifact_id
          && !!workflowRequest.result.responseBody?.yaml,
        `result status=${workflowRequest.result?.status ?? '(not fetched)'}`,
      );
    } else if (scenario.requireDurableJobs && workflowRequest?.accepted) {
      add(
        'Advanced workflow generation uses the durable job contract (202)',
        false,
        `status ${workflowRequest.accepted.status}`,
      );
    }
    if (cancelRetryContext?.ready) {
      add(
        'Advanced workflow generation cancellation is accepted',
        cancelRetry?.accepted?.status === 202 && cancelRetry.cancel?.status === 200 && jobStatus(cancelRetry.cancel) === 'cancelled',
        `accept status=${cancelRetry?.accepted?.status ?? '(missing)'}; cancel status=${cancelRetry?.cancel?.status ?? '(missing)'}; job status=${jobStatus(cancelRetry?.cancel) ?? '(missing)'}`,
      );
      add(
        'Advanced workflow generation retry is accepted after cancellation',
        cancelRetry?.retry?.status === 202,
        `retry status=${cancelRetry?.retry?.status ?? '(missing)'}; job status=${jobStatus(cancelRetry?.retry) ?? '(missing)'}`,
      );
    }
    if (workflowResult.replacement?.retried) {
      add(
        'Workflow generator accepts replacement AI execution context after provider change',
        workflowResult.response.status === 200,
        `retry status ${workflowResult.response.status}`,
      );
    }
    const genWf = workflowResult.response;

    if (genWf && genWf.status !== 200) {
      if (PROVIDER_FAIL_STATUS.has(genWf.status)) {
        inconclusive = true;
        add('Workflow generator reachable', true, `provider unavailable (status ${genWf.status}) — seam not assessed`, 'CANNOT_DETERMINE');
      } else if (genWf) {
        add('Workflow generation returned a usable draft', false, `status ${genWf.status}: ${JSON.stringify(redact(genWf.responseBody)).slice(0, 300)}`);
      }
    } else {
      const yaml = genWf?.responseBody?.yaml ?? '';
      const workflowId = genWf?.responseBody?.workflowId ?? genWf?.responseBody?.workflow_id ?? null;
      const v = validateWorkflowYaml(yaml);
      const yamlDocumentId = v.documentId;
      const nodeRoles = workflowNodeRoles(yaml);
      const roleLeaks = findReservedRoleLeaks({ workflowRoles: nodeRoles });
      const fan = analyzeConservativeFan(yaml);
      evidence.generatedWorkflow = {
        workflowId,
        yamlDocumentId,
        wasCorrected: genWf.responseBody?.wasCorrected,
        nodeRoles,
        conservativeFan: fan,
      };
      evidence.generatedWorkflowValidation = { valid: v.valid, errors: v.errors, warnings: v.warnings, nodeCount: v.nodeCount };

      add(
        'Generated workflow passes backend structural validation (no dangling edges / unrouted check branches)',
        v.valid,
        v.valid ? `${v.nodeCount} nodes, structurally valid` : `${v.errors.length} error(s): ${v.errors.slice(0, 3).join('; ')}`,
      );
      add(
        'Generated workflow id matches the YAML document id',
        !!workflowId && !!yamlDocumentId && workflowId === yamlDocumentId,
        `workflowId=${workflowId ?? '(missing)'}, yaml.id=${yamlDocumentId ?? '(missing)'}`,
      );
      add(
        'Generated workflow assigns no work to reserved system roles',
        roleLeaks.offenders.length === 0,
        roleLeaks.offenders.length === 0 ? `roles: ${nodeRoles.join(', ') || '(none declared)'}` : `LEAKED: ${roleLeaks.offenders.join(', ')}`,
      );
      add(
        'Generated workflow obeys conservative fan safety',
        fan.safe,
        fan.safe ? `mode=${fan.mode}` : fan.errors.slice(0, 3).join('; '),
      );
      if (opts.keep && v.valid && fan.safe) {
        await retainGeneratedWorkflow(client, evidence.projectId, yaml, 'primary generated workflow', evidence, add);
      }
    }

    for (const fanCase of scenario.conservativeFanCases ?? []) {
      const fanResult = await runConservativeFanGenerationCase(
        client,
        evidence.projectId,
        fanCase,
        opts,
        time,
      );
      evidence.conservativeFanCases.push(fanResult.evidence);
      if (fanResult.inconclusive) inconclusive = true;
      add(
        `Conservative generation case '${fanCase.id}' returns ${fanCase.expectedMode}`,
        fanResult.pass,
        fanResult.detail,
        fanResult.inconclusive ? 'CANNOT_DETERMINE' : 'P0',
      );
      if (opts.keep && fanResult.yaml && fanResult.analysis?.safe) {
        const retainedWorkflowId = await retainGeneratedWorkflow(
          client,
          evidence.projectId,
          fanResult.yaml,
          `conservative generation case '${fanCase.id}'`,
          evidence,
          add,
        );
        if (retainedWorkflowId && fanCase.startRetainedRun) {
          await queueRetainedWorkflowRun(
            client,
            evidence.projectId,
            retainedWorkflowId,
            fanCase.id,
            evidence,
            add,
          );
        }
      }
    }

    // ── SEAM 3: backend round-trip — prove our local validator mirror agrees with
    // the LIVE backend guard (and that the guard is actually deployed). Save a
    // deliberately-BROKEN workflow (a check node declaring a 'fail' verdict with no
    // outgoing edge for it) via PUT and assert the backend rejects it with a 4xx;
    // then save a VALID one as a positive control and assert it is accepted.
    await runBackendGuardRoundTrip(client, evidence.projectId, add, time);
  }

  async function runConservativeFanGenerationCase(client, projectId, fanCase, opts, time) {
    const timingKey = `conservativeFan-${fanCase.id.replace(/[^a-z0-9]+/gi, '-')}`;
    const context = await time(`${timingKey}ContextMs`, () =>
      prepareAiExecutionContext(client, 'workflow_generation', projectId),
    );
    if (!context.ready) {
      return {
        pass: false,
        inconclusive: context.inconclusive,
        detail: `AI execution context unavailable (status ${context.evidence.status})`,
        yaml: null,
        analysis: null,
        evidence: { id: fanCase.id, expectedMode: fanCase.expectedMode, context: context.evidence },
      };
    }

    const path = `/api/projects/${projectId}/workflows/generate`;
    const idempotencyKey = durableIdempotencyKey(`api-harness-${fanCase.id}`);
    const request = await submitDurableJob(client, {
      path,
      body: { description: fanCase.description, content_only: true },
      headers: context.headers,
      idempotencyKey,
      time,
      timingKey,
      timeoutMs: opts.timeoutMs,
    });
    const result = request.durable === false
      ? await retryWithReplacementContext(client, {
        response: request.accepted,
        operation: 'workflow_generation',
        path,
        body: { description: fanCase.description, content_only: true },
        time,
        timingKey,
        extraHeaders: { 'Idempotency-Key': idempotencyKey },
      })
      : { response: request.result ?? request.final, replacement: null };
    const response = result.response;
    if (!response || response.status !== 200) {
      const status = response?.status ?? request.accepted?.status ?? 0;
      const providerFailure = PROVIDER_FAIL_STATUS.has(status);
      return {
        pass: false,
        inconclusive: providerFailure,
        detail: providerFailure
          ? `provider unavailable (status ${status})`
          : `generation failed with status ${status}`,
        yaml: null,
        analysis: null,
        evidence: {
          id: fanCase.id,
          expectedMode: fanCase.expectedMode,
          status,
          jobId: jobId(request.accepted),
          terminalStatus: jobStatus(request.final),
        },
      };
    }

    const yaml = response.responseBody?.yaml ?? '';
    const validation = validateWorkflowYaml(yaml);
    const analysis = analyzeConservativeFan(yaml);
    const pass = validation.valid && analysis.safe && analysis.mode === fanCase.expectedMode;
    return {
      pass,
      inconclusive: false,
      detail: pass
        ? `mode=${analysis.mode}; ${validation.nodeCount} nodes`
        : `mode=${analysis.mode}; expected=${fanCase.expectedMode}; ${[...validation.errors, ...analysis.errors].slice(0, 3).join('; ')}`,
      yaml,
      analysis,
      evidence: {
        id: fanCase.id,
        expectedMode: fanCase.expectedMode,
        status: response.status,
        jobId: jobId(request.accepted),
        terminalStatus: jobStatus(request.final),
        workflowId: response.responseBody?.workflow_id ?? response.responseBody?.workflowId ?? null,
        validation,
        analysis,
      },
    };
  }

  async function retainGeneratedWorkflow(client, projectId, yaml, label, evidence, add) {
    const validation = validateWorkflowYaml(yaml);
    const workflowId = validation.documentId;
    if (!validation.valid || !workflowId) {
      add(`Retain ${label}`, false, 'generated YAML is not valid enough to save');
      return null;
    }
    const response = await client.put(
      `/api/projects/${projectId}/workflows/${encodeURIComponent(workflowId)}`,
      { yaml },
    );
    if (response.ok) evidence.retainedWorkflowIds.push(workflowId);
    add(
      `Retain ${label}`,
      response.ok,
      response.ok ? `saved workflow ${workflowId}` : `save status ${response.status}`,
    );
    return response.ok ? workflowId : null;
  }

  async function queueRetainedWorkflowRun(client, projectId, workflowId, caseId, evidence, add) {
    const context = await prepareAiExecutionContext(client, 'orchestration', projectId);
    if (!context.ready) {
      add(
        `Queue retained run for '${caseId}'`,
        false,
        `orchestration context unavailable (status ${context.evidence.status})`,
        context.inconclusive ? 'CANNOT_DETERMINE' : 'P0',
      );
      return;
    }
    const response = await client.post(
      `/api/projects/${projectId}/workflows/${encodeURIComponent(workflowId)}/run`,
      {},
      { headers: context.headers },
    );
    const taskId = response.responseBody?.task_id ?? null;
    if (response.ok && taskId) {
      evidence.retainedRunTriggers.push({ caseId, workflowId, taskId });
    }
    add(
      `Queue retained run for '${caseId}'`,
      response.status === 201 && !!taskId,
      taskId ? `task ${taskId} will execute against retained workflow ${workflowId}` : `status ${response.status}`,
    );
  }

  async function inspectPmDiscovery(client, projectId, evidence, add) {
    const response = await client.get(`/api/projects/${projectId}/workflows/pm-discovery`);
    const verification = verifyPmDiscovery(response);
    evidence.pmDiscovery = {
      status: response.status,
      branchIds: verification.branchIds,
      customerOutputPaths: verification.customerOutputPaths,
      technicalOutputPaths: verification.technicalOutputPaths,
    };
    add(
      'PM Discovery exposes two ordered independent research branches before synthesis',
      verification.valid,
      verification.valid
        ? `${verification.branchIds.join(' -> join, ')} -> join -> synthesis`
        : `status ${response.status}; branches=${verification.branchIds.join(',')}`,
    );
  }

  return finalize();

  function finalize() {
    // CANNOT_DETERMINE checks are unobservable — excluded from pass/fail scoring.
    const scored = checks.filter((c) => c.category !== 'CANNOT_DETERMINE' && !c.skipped);
    const pass = scored.length > 0 && scored.every((c) => c.pass);
    return {
      pass,
      inconclusive,
      checks,
      timings,
      triggeredFailureSignals: [],
      evidence,
      durationMs: Date.now() - started,
      cleanup: async () => {},
    };
  }
}

// A deliberately-broken workflow: the check node 'gate' declares a 'fail' verdict but
// no outgoing edge routes it — exactly the FR-016 rule the backend enforces.
const BROKEN_WORKFLOW_YAML = `id: seam-roundtrip-guard
name: Seam Round-trip Guard
start: work
nodes:
  - id: work
    type: prompt
    role: backend-engineer
  - id: gate
    type: check
    branches: [pass, fail]
  - id: done
    type: terminal
edges:
  - { from: work, to: gate }
  - { from: gate, to: done, when: pass }
`;

const VALID_WORKFLOW_YAML = `id: seam-roundtrip-guard
name: Seam Round-trip Guard
start: work
nodes:
  - id: work
    type: prompt
    role: backend-engineer
  - id: done
    type: terminal
edges:
  - { from: work, to: done }
`;

/**
 * Round-trip a broken and a valid workflow through the live PUT guard to prove the
 * harness's local validator mirror matches the deployed backend rules.
 */
async function runBackendGuardRoundTrip(client, projectId, add, time) {
  const wfId = 'seam-roundtrip-guard';
  // Our local mirror must agree the broken one is invalid (sanity on the fixture).
  const localBroken = validateWorkflowYaml(BROKEN_WORKFLOW_YAML);

  const putBroken = await time('backendGuardRejectMs', () =>
    client.put(`/api/projects/${projectId}/workflows/${wfId}`, { yaml: BROKEN_WORKFLOW_YAML }),
  );
  add(
    'Backend REJECTS a structurally-broken workflow with 4xx (guard is live; mirror agrees)',
    !localBroken.valid && putBroken.status >= 400 && putBroken.status < 500,
    `local mirror invalid=${!localBroken.valid}, backend status=${putBroken.status} — ${
      (putBroken.responseBody?.error ?? '').toString().slice(0, 120)
    }`,
  );

  const putValid = await time('backendGuardAcceptMs', () =>
    client.put(`/api/projects/${projectId}/workflows/${wfId}`, { yaml: VALID_WORKFLOW_YAML }),
  );
  add(
    'Backend ACCEPTS a valid workflow (positive control — rejection above is specific, not blanket)',
    putValid.status >= 200 && putValid.status < 300,
    `backend status=${putValid.status}`,
  );
}
