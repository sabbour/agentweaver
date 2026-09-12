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
// Bounded + safe: it only calls generation endpoints (which return UNSAVED drafts) and a
// throwaway project for project-scoped workflow generation, then cleans up. Nothing is
// deployed, merged, saved to a catalog, or run.

import {
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

async function prepareAiExecutionContext(client, operation, projectId) {
  const body = { operation };
  if (projectId) body.project_id = projectId;
  const response = await client.post('/api/ai/execution-context', body);
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

function replacementExecutionContext(response, operation) {
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
  response, operation, path, body, time, timingKey,
}) {
  const replacement = replacementExecutionContext(response, operation);
  if (!replacement?.ready) return { response, replacement: replacement?.evidence ?? null };

  const retried = await time(`${timingKey}RetryMs`, () =>
    client.post(path, body, { headers: replacement.headers }),
  );
  return {
    response: retried,
    replacement: { ...replacement.evidence, retried: true, retryStatus: retried.status },
  };
}

/**
 * @param {import('./client.mjs').AgentweaverClient} client
 * @param {any} scenario  generation-seam scenario (kind: 'generation-seam')
 * @param {Object} opts
 * @param {boolean} [opts.keep]
 */
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
  };
  /** @type {{name:string, pass:boolean, detail:string, category:string, skipped?:boolean}[]} */
  const checks = [];
  // Taxonomy: P0 = platform-correctness, P1 = output-quality, CANNOT_DETERMINE =
  // unobservable (e.g. the generator's model provider was down). CANNOT_DETERMINE is
  // excluded from pass/fail scoring rather than guessed.
  const add = (name, pass, detail = '', category = 'P0') =>
    checks.push({ name, pass: !!pass, detail, category, skipped: category === 'CANNOT_DETERMINE' });
  let inconclusive = false;

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
  add(
    'Deployment exposes its authentication configuration (/api/auth/config)',
    authConfig.ok && authMode === 'Entra',
    authMode ? `server auth mode ${authMode}` : `status ${authConfig.status}`,
  );
  if (!authConfig.ok || authMode !== 'Entra') return finalize();

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
    ? 'authenticated Entra session'
    : `status ${auth.status}; a valid Entra bearer token is required (GitHub CLI tokens are not accepted)`;
  add('Authenticated Entra bearer token accepted (/api/auth/session)', signedIn, authDetail);
  if (!signedIn) return finalize();

  // ── SEAM 1: blueprint generation ────────────────────────────────────────────
  const blueprintContext = await time('blueprintExecutionContextMs', () =>
    prepareAiExecutionContext(client, 'blueprint_generation'),
  );
  addExecutionContextCheck('blueprint generation', blueprintContext, add);
  if (blueprintContext.inconclusive) inconclusive = true;

  const blueprintRequest = blueprintContext.ready
    ? await time('blueprintGenerateMs', () =>
      client.post(
        '/api/blueprints/generate',
        { description: scenario.blueprintDescription },
        { headers: blueprintContext.headers },
      ),
    )
    : null;
  const blueprintResult = blueprintRequest
    ? await retryWithReplacementContext(client, {
      response: blueprintRequest,
      operation: 'blueprint_generation',
      path: '/api/blueprints/generate',
      body: { description: scenario.blueprintDescription },
      time,
      timingKey: 'blueprintGenerate',
    })
    : { response: null, replacement: null };
  evidence.aiExecutionContexts.blueprintGeneration = {
    ...blueprintContext.evidence,
    replacement: blueprintResult.replacement,
  };
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
    const bp = genBp.responseBody?.blueprint ?? {};
    const genWfYaml = genBp.responseBody?.generated_workflow_yaml ?? null;
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
      evidence.generatedBlueprintWorkflowValid = { valid: v.valid, errors: v.errors, nodeCount: v.nodeCount };
      add(
        "Blueprint's inline generated workflow passes backend structural validation",
        v.valid,
        v.valid ? `${v.nodeCount} nodes, structurally valid` : `${v.errors.length} error(s): ${v.errors.slice(0, 3).join('; ')}`,
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
    const workflowContext = await time('workflowExecutionContextMs', () =>
      prepareAiExecutionContext(client, 'workflow_generation', evidence.projectId),
    );
    addExecutionContextCheck('workflow generation', workflowContext, add);
    if (workflowContext.inconclusive) inconclusive = true;

    const workflowRequest = workflowContext.ready
      ? await time('workflowGenerateMs', () =>
        client.post(
          `/api/projects/${evidence.projectId}/workflows/generate`,
          { description: scenario.workflowDescription },
          { headers: workflowContext.headers },
        ),
      )
      : null;
    const workflowResult = workflowRequest
      ? await retryWithReplacementContext(client, {
        response: workflowRequest,
        operation: 'workflow_generation',
        path: `/api/projects/${evidence.projectId}/workflows/generate`,
        body: { description: scenario.workflowDescription },
        time,
        timingKey: 'workflowGenerate',
      })
      : { response: null, replacement: null };
    evidence.aiExecutionContexts.workflowGeneration = {
      ...workflowContext.evidence,
      replacement: workflowResult.replacement,
    };
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
      const yaml = genWf.responseBody?.yaml ?? '';
      const workflowId = genWf.responseBody?.workflowId ?? null;
      const v = validateWorkflowYaml(yaml);
      const yamlDocumentId = v.documentId;
      const nodeRoles = workflowNodeRoles(yaml);
      const roleLeaks = findReservedRoleLeaks({ workflowRoles: nodeRoles });
      evidence.generatedWorkflow = { workflowId, yamlDocumentId, wasCorrected: genWf.responseBody?.wasCorrected, nodeRoles };
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
    }

    // ── SEAM 3: backend round-trip — prove our local validator mirror agrees with
    // the LIVE backend guard (and that the guard is actually deployed). Save a
    // deliberately-BROKEN workflow (a check node declaring a 'fail' verdict with no
    // outgoing edge for it) via PUT and assert the backend rejects it with a 4xx;
    // then save a VALID one as a positive control and assert it is accepted.
    await runBackendGuardRoundTrip(client, evidence.projectId, add, time);
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
