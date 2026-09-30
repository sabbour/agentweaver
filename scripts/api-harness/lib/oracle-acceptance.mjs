import { appendRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';
import { writeLifecycleJson } from '../../harness-shared/persona-lifecycle.mjs';
import { verifyRenderedPreview } from '../../harness-shared/preview-browser.mjs';

export const DEFAULT_BUDGETS = Object.freeze({
  planning: 6, claimProvisioning: 6, implementation: 18, initialPreview: 5,
  buildTestReview: 10, revisionProvisioning: 12, correctedPreview: 5, terminalCompletion: 8,
});

const TERMINAL = new Set(['failed', 'cancelled', 'canceled', 'blocked', 'assembly_blocked', 'assembly_failed', 'assembly_declined', 'merge_failed', 'rai_blocked', 'needs_resolution', 'assembly_unknown']);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const normalize = (value) => String(value ?? '').toLowerCase().replaceAll(' ', '_');
const bodyOf = (call) => call.transientResponseBody ?? call.responseBody;
const coordinatorStreams = ['-coordinator-draft', '-coordinator-decompose', '-coordinator-orchestrate'];

export class AcceptanceFailure extends Error {
  constructor(message, code = 'acceptance_failed') {
    super(message);
    this.code = code;
  }
}

export function createAcceptanceTransport(client, transcriptPath) {
  return async (method, path, body, options = {}) => {
    const timeoutMs = method === 'POST' && path.endsWith('/sandbox/port-forward')
      ? 180_000 : method === 'POST' ? 120_000 : 30_000;
    const signal = options.signal ?? AbortSignal.timeout(timeoutMs);
    let call;
    try {
      call = await client.call(method, path, body, { ...options, signal });
    } catch (error) {
      const code = signal.aborted
        ? signal.reason?.name === 'TimeoutError' ? 'request_timeout' : 'request_cancelled'
        : 'transport_error';
      await appendRedactedJsonLine(transcriptPath, {
        at: new Date().toISOString(), request: { method, path, body },
        error: { code, message: String(error.message) },
      });
      throw new AcceptanceFailure(`${method} ${path}: ${error.message}`, code);
    }
    const evidence = path === '/openapi/v1.json'
      ? { ...call, responseBody: { paths: Object.keys(bodyOf(call)?.paths ?? {}) } }
      : call;
    await appendRedactedJsonLine(transcriptPath, { at: new Date().toISOString(), request: { method, path, body }, response: evidence });
    client.calls.length = 0;
    return { status: call.status, body: bodyOf(call) };
  };
}

export function requireResponse(response, context, statuses = [200]) {
  if (!statuses.includes(response.status)) throw new AcceptanceFailure(`${context}: HTTP ${response.status}`);
  return response.body;
}

export class EventDeltas {
  constructor(request, { limit = 250 } = {}) {
    this.request = request;
    this.limit = limit;
    this.cursors = new Map();
    this.recent = [];
    this.boundClaims = new Set();
    this.reviewRequests = new Map();
  }

  async poll(runId) {
    const added = [];
    let cursor = this.cursors.get(runId) ?? 0;
    for (;;) {
      const body = requireResponse(
        await this.request('GET', `/api/runs/${encodeURIComponent(runId)}/events?after=${cursor}&limit=${this.limit}`),
        'event delta',
      );
      if (!Array.isArray(body)) throw new AcceptanceFailure('Event delta response must be an array.');
      for (const event of body) {
        if (!Number.isInteger(event.sequence) || event.sequence <= cursor) {
          throw new AcceptanceFailure('Non-monotonic event cursor.');
        }
        cursor = event.sequence;
        added.push(event);
        if (event.type === 'sandbox.execution_pod.bound') this.boundClaims.add(runId);
        if (event.type === 'coordinator.assembly_review_requested' && event.payload?.outputRevisionId) {
          this.reviewRequests.set(runId, { id: event.payload.outputRevisionId, sequence: cursor });
        }
        if (/^(run\.|assembly\.|coordinator\.|sandbox\.|shell\.|preview\.|workflow\.)/.test(event.type ?? '')) {
          this.recent.push({ runId, sequence: cursor, type: event.type, payload: event.payload });
        }
      }
      this.cursors.set(runId, cursor);
      this.recent = this.recent.slice(-20);
      if (body.length < this.limit) return added;
    }
  }
}

export async function cleanupOwnedPreviews(request, owned) {
  const outcomes = [];
  for (const { runId, sessionId } of [...owned].reverse()) {
    try {
      const response = await request('DELETE', `/api/runs/${encodeURIComponent(runId)}/sandbox/port-forward/${encodeURIComponent(sessionId)}`);
      const list = await request('GET', `/api/runs/${encodeURIComponent(runId)}/sandbox/port-forward`);
      const sessions = Array.isArray(list.body) ? list.body : [];
      outcomes.push({ runId, sessionId, deleted: response.status === 200 && list.status === 200 && !sessions.some((s) => s.session_id === sessionId) });
    } catch (error) {
      outcomes.push({ runId, sessionId, deleted: false, error: String(error.message) });
    }
  }
  return outcomes;
}

export async function runOracleAcceptance({
  request, transcriptPath, resultPath, projectId, runId: suppliedRunId, goal, workflowId,
  expectedText, correctedText, feedback, targetFiles, port = 3000,
  budgets = DEFAULT_BUDGETS, pollMs = 5000, browser = verifyRenderedPreview,
  clock = () => Date.now(), pause = sleep, approveShell = false,
}) {
  const result = {
    schema: 'agentweaver.oracle-assembly-acceptance/v1', verdict: 'fail',
    phase: 'preflight', projectId, parentRunId: suppliedRunId ?? null,
    childRunIds: [], revisionIds: [], previews: [], decisions: [],
    shellApprovalReconciliations: [],
    lastEvents: [], terminalDiagnostic: null, transcriptPath, resultPath, cleanup: [],
    phaseTimingsMs: {},
  };
  const owned = [];
  const submittedShellApprovals = new Set();
  const deltas = new EventDeltas(request);
  let phaseStarted = clock();
  let phaseDeadline = Infinity;
  let latest = {};
  const phase = (name) => {
    result.phaseTimingsMs[result.phase] = clock() - phaseStarted;
    result.phase = name;
    phaseStarted = clock();
    phaseDeadline = phaseStarted + (budgets[name] ?? 2) * 60_000;
  };
  const remaining = () => {
    const ms = phaseDeadline - clock();
    if (ms <= 0) throw new AcceptanceFailure(`Phase ${result.phase} exceeded its budget.`, 'phase_timeout');
    return ms;
  };
  const checkedRequest = async (method, url, body, options = {}) => {
    const { allowLateResponse = false, ...requestOptions } = options;
    const maxAttempts = method === 'GET' ? 3 : 1;
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      const signal = AbortSignal.timeout(Math.max(1, Math.min(remaining(), method === 'POST' ? 180_000 : 30_000)));
      let response;
      try {
        response = await request(method, url, body, { ...requestOptions, signal });
      } catch (error) {
        if (method !== 'GET' || !(error instanceof AcceptanceFailure)
          || !['request_timeout', 'transport_error'].includes(error.code)
          || (signal.aborted && signal.reason?.name !== 'TimeoutError')) throw error;
        remaining();
        if (attempt === maxAttempts) throw error;
        await pause(Math.min(250 * attempt, remaining()));
        continue;
      }
      if (!allowLateResponse) remaining();
      const transientRead = method === 'GET' && [0, 502, 503, 504].includes(response.status);
      if (!transientRead) return response;
      if (attempt === maxAttempts) {
        const message = response.body?.message ?? `HTTP ${response.status}`;
        throw new AcceptanceFailure(`${method} ${url}: ${message}`, 'transport_error');
      }
      await pause(Math.min(250 * attempt, remaining()));
    }
    throw new AcceptanceFailure(`${method} ${url}: retry budget exhausted`, 'transport_error');
  };
  deltas.request = checkedRequest;
  const path = (suffix) => `/api/runs/${encodeURIComponent(result.parentRunId)}${suffix}`;
  const collectRevisions = async () => {
    for (const id of [result.parentRunId, ...result.childRunIds]) {
      const response = await checkedRequest('GET', `/api/runs/${encodeURIComponent(id)}/output-revisions`);
      if (response.status === 410) throw new AcceptanceFailure(`Output revision content unavailable for run ${id}.`);
      const revisions = requireResponse(response, 'output revisions');
      if (!Array.isArray(revisions)) throw new AcceptanceFailure('Output revisions response must be an array.');
      for (const revision of revisions) {
        const revisionId = revision.revision_id ?? revision.revisionId;
        if (revisionId && !result.revisionIds.includes(revisionId)) result.revisionIds.push(revisionId);
      }
    }
  };
  const currentReviewRevision = async () => {
    const requested = deltas.reviewRequests.get(result.parentRunId);
    if (!requested?.id) throw new AcceptanceFailure('Current assembly review has no output revision event.');
    const revisions = requireResponse(await checkedRequest('GET', path('/output-revisions')), 'parent output revisions');
    if (!Array.isArray(revisions) || !revisions.some((entry) => entry.revision_id === requested.id)) {
      throw new AcceptanceFailure('Review revision missing from parent output revisions.');
    }
    const detail = requireResponse(
      await checkedRequest('GET', path(`/output-revisions/${encodeURIComponent(requested.id)}`)),
      'review revision detail',
    );
    if (detail?.revision_id !== requested.id || detail.manifest_incomplete === true) {
      throw new AcceptanceFailure('Current review revision is missing or incomplete.');
    }
    const contentIdentity = detail.tree_content_sha256 ?? detail.tree_hash;
    if (typeof contentIdentity !== 'string' || !contentIdentity) {
      throw new AcceptanceFailure('Review revision has no artifact content identity.');
    }
    return { ...requested, contentIdentity };
  };
  const submitReview = async (decision, revisionId) => {
    if (!revisionId) throw new AcceptanceFailure('A current output revision is required for review.');
    const context = requireResponse(await checkedRequest('POST', '/api/ai/execution-context', {
      operation: 'orchestration', project_id: result.projectId,
    }), 'review execution context');
    if (!context?.execution_key) throw new AcceptanceFailure('Review execution key unavailable.');
    return requireResponse(await checkedRequest('POST', path('/assembly/review'), {
      ...decision, output_revision_id: revisionId,
    }, { headers: { 'If-Model-Provider-Key': context.execution_key } }), 'assembly review', [200, 202]);
  };
  const pendingShellApprovals = async (runIds) => {
    const body = requireResponse(await checkedRequest('GET', path('/pending-approvals')), 'pending approvals');
    if (!body || body.run_id !== result.parentRunId || !Array.isArray(body.approvals)
      || !Number.isInteger(body.count) || body.count !== body.approvals.length) {
      throw new AcceptanceFailure('Invalid pending approvals response for the tested root run.');
    }
    const scope = new Set(runIds);
    const seen = new Set();
    return body.approvals.filter((entry) => {
      if (!entry || typeof entry.root_run_id !== 'string' || entry.root_run_id !== result.parentRunId
        || typeof entry.owning_run_id !== 'string' || !entry.owning_run_id
        || typeof entry.action_run_id !== 'string' || !entry.action_run_id
        || typeof entry.request_id !== 'string' || !entry.request_id.trim()
        || typeof entry.is_shell !== 'boolean') {
        throw new AcceptanceFailure('Invalid pending approval identity or classification.');
      }
      const key = `${entry.owning_run_id}\0${entry.request_id}`;
      if (seen.has(key)) throw new AcceptanceFailure('Duplicate pending approval identity.');
      seen.add(key);
      if (!scope.has(entry.action_run_id)) {
        throw new AcceptanceFailure('Pending approval action run is outside the tested run tree.');
      }
      if (!entry.is_shell) {
        if (entry.tool_name === 'run_command') throw new AcceptanceFailure('Conflicting pending shell classification.');
        return false;
      }
      if (entry.tool_name !== 'run_command'
        || !(entry.owning_run_id === entry.action_run_id
          || (entry.action_run_id === result.parentRunId
            && coordinatorStreams.some((suffix) => entry.owning_run_id === result.parentRunId + suffix)))) {
        throw new AcceptanceFailure('Pending shell approval is outside the tested run tree or has conflicting classification.');
      }
      return true;
    });
  };
  const snapshot = async () => {
    const [run, plan, children] = await Promise.all([
      checkedRequest('GET', path('')), checkedRequest('GET', path('/work-plan')), checkedRequest('GET', path('/children')),
    ]);
    requireResponse(run, 'run status');
    if (plan.status !== 200 && plan.status !== 404) requireResponse(plan, 'work plan');
    requireResponse(children, 'children');
    latest = { run: run.body, plan: plan.status === 200 ? plan.body : null, children: children.body };
    const ids = [result.parentRunId, ...(Array.isArray(children.body) ? children.body.map((c) => c.childRunId).filter(Boolean) : [])];
    result.childRunIds = [...new Set([...result.childRunIds, ...ids.slice(1)])];
    for (const id of ids) {
      await deltas.poll(id);
    }
    for (const approval of await pendingShellApprovals(ids)) {
      const key = `${approval.action_run_id}\0${approval.request_id}`;
      if (!approveShell) {
        throw new AcceptanceFailure(`Shell approval required for ${approval.action_run_id}; rerun with --approve-shell only for a disposable project.`);
      }
      if (submittedShellApprovals.has(key)) {
        throw new AcceptanceFailure(`Shell approval still pending after submission for ${approval.action_run_id}; refusing to replay the write.`);
      }
      submittedShellApprovals.add(key);
      const response = await checkedRequest('POST', `/api/runs/${encodeURIComponent(approval.action_run_id)}/shell-approvals`, {
        command_hash: approval.request_id,
      });
      if (response.status === 409 && response.body?.error === 'Run is not active.') {
        const current = await pendingShellApprovals(ids);
        if (current.some((entry) => entry.action_run_id === approval.action_run_id && entry.request_id === approval.request_id)) {
          throw new AcceptanceFailure(`Shell approval conflict for ${approval.action_run_id}: request is still pending.`);
        }
        result.shellApprovalReconciliations.push({
          actionRunId: approval.action_run_id, requestId: approval.request_id,
          reason: 'Request no longer actionable after conflict; confirmed absent from fresh pending approvals.',
        });
      } else {
        requireResponse(response, 'shell approval', [200, 201, 202]);
      }
    }
    result.lastEvents = deltas.recent;
    result.terminalDiagnostic = run.body?.failureReason ?? run.body?.failure_reason
      ?? latest.plan?.statusReason ?? deltas.recent.findLast((e) => /error|failed|blocked/.test(e.type ?? '')) ?? null;
    const terminal = [run.body?.status, latest.plan?.status, latest.plan?.assemblyTerminalStage].find((s) => TERMINAL.has(normalize(s)));
    if (terminal) throw new AcceptanceFailure(`Terminal run state: ${terminal}`);
    return latest;
  };
  const wait = async (name, predicate) => {
    phase(name);
    for (;;) {
      const state = await snapshot();
      if (await predicate(state)) { remaining(); return state; }
      await pause(Math.min(pollMs, remaining()));
    }
  };
  const preview = async (label, text, { keepPhase = false } = {}) => {
    if (!keepPhase) phase(label);
    let last;
    while (remaining() > 0) {
      const prior = requireResponse(await checkedRequest('GET', path('/sandbox/port-forward')), 'existing preview sessions');
      if (!Array.isArray(prior) || prior.length > 0) {
        throw new AcceptanceFailure(`${label}: pre-existing preview session; refusing to adopt or delete it.`);
      }
      const response = await checkedRequest('POST', path('/sandbox/port-forward'), { target_port: port }, { allowLateResponse: true });
      const value = response.body;
      if (response.status === 200 && value?.session_id && value?.preview_url) {
        owned.push({ runId: result.parentRunId, sessionId: value.session_id });
        remaining();
        const listed = requireResponse(await checkedRequest('GET', path('/sandbox/port-forward')), 'preview readiness');
        if (!Array.isArray(listed) || !listed.some((s) => s.session_id === value.session_id)) {
          throw new AcceptanceFailure(`${label}: created preview not listed as ready.`);
        }
        remaining();
        const checked = await browser(value.preview_url, text, { timeoutMs: remaining() });
        remaining();
        result.previews.push({ phase: label, sessionId: value.session_id, ...checked });
        if (!checked.ready) throw new AcceptanceFailure(`${label}: rendered preview failed acceptance.`);
        return checked;
      }
      last = `HTTP ${response.status}`;
      if (response.status >= 400 && ![404, 409, 503].includes(response.status)) break;
      await pause(Math.min(pollMs, remaining()));
    }
    throw new AcceptanceFailure(`${label}: preview not ready (${last ?? 'deadline'}).`, 'phase_timeout');
  };
  try {
    const [version, spec, auth] = await Promise.all([
      checkedRequest('GET', '/api/version', undefined, { authenticated: false }),
      checkedRequest('GET', '/openapi/v1.json', undefined, { authenticated: false }),
      checkedRequest('GET', '/api/auth/session'),
    ]);
    result.version = requireResponse(version, 'version');
    const openapi = requireResponse(spec, 'OpenAPI');
    if (!openapi?.paths || !auth.body?.authenticated || auth.status !== 200) {
      throw new AcceptanceFailure('OpenAPI or authenticated session preflight failed.');
    }
    const essential = ['/api/runs/{id}/events', '/api/runs/{coordinatorRunId}/assembly/review'];
    result.openapiPathsChecked = essential.map((p) => ({ path: p, present: Boolean(openapi.paths[p]) }));
    if (result.parentRunId) {
      const attached = requireResponse(await checkedRequest('GET', path('')), 'attached run');
      if (!attached?.project_id || (projectId && attached.project_id !== projectId)) {
        throw new AcceptanceFailure('Attached run must have a matching project_id.');
      }
      result.projectId = attached.project_id;
    }
    if (result.projectId) {
      requireResponse(await checkedRequest('GET', `/api/projects/${encodeURIComponent(result.projectId)}`), 'project');
    }
    if (!result.parentRunId) {
      if (!projectId || !goal) throw new AcceptanceFailure('Provide --run-id or --project-id and --goal.');
      const execution = requireResponse(await checkedRequest('POST', '/api/ai/execution-context', { operation: 'orchestration', project_id: projectId }), 'execution context');
      if (!execution?.execution_key) throw new AcceptanceFailure('Execution key unavailable.');
      const started = requireResponse(await checkedRequest('POST', `/api/projects/${encodeURIComponent(projectId)}/orchestrations`, {
        goal, workflow_override_id: workflowId, start_mode: 'direct', auto_approve_tools: true, autopilot: true,
      }, { headers: { 'If-Model-Provider-Key': execution.execution_key } }), 'orchestration start', [201]);
      result.parentRunId = started.runId ?? started.run_id;
      if (!result.parentRunId) throw new AcceptanceFailure('Orchestration did not return runId.');
    }
    await wait('planning', (s) => Boolean(s.plan));
    await wait('claimProvisioning', (s) => s.children.some((c) => deltas.boundClaims.has(c.childRunId)));
    await wait('implementation', (s) => /assembl|review|complet/.test(normalize(s.plan?.status)));
    await wait('buildTestReview', (s) => normalize(s.plan?.status) === 'in_review' || normalize(s.plan?.assemblyStage).includes('review'));
    const firstFiles = requireResponse(await checkedRequest('GET', path('/assembly/files')), 'initial assembly files');
    if (!Array.isArray(firstFiles) || !firstFiles.length) throw new AcceptanceFailure('No assembled files at initial review.');
    await preview('initialPreview', expectedText);
    await collectRevisions();
    const firstRevision = await currentReviewRevision();
    result.initialRevision = firstRevision;
    if (!feedback?.trim() || !correctedText?.trim() || !targetFiles?.length) {
      throw new AcceptanceFailure('Grounded request_changes requires feedback, target files, and corrected application evidence.');
    }
    result.childRunIdsAtReview = [...result.childRunIds];
    phase('reviewDecision');
    await submitReview({
      approved: false, request_changes: true, feedback, target_files: targetFiles,
    }, firstRevision.id);
    result.decisions.push({ decision: 'request_changes', outputRevisionId: firstRevision.id, feedback, targetFiles });
    const stopped = await cleanupOwnedPreviews(request, owned);
    result.cleanup.push(...stopped);
    if (stopped.some((entry) => !entry.deleted)) throw new AcceptanceFailure('Initial preview cleanup could not be confirmed.');
    owned.length = 0;
    await wait('revisionProvisioning', (s) => s.children.some((c) => c.childRunId
      && !result.childRunIdsAtReview.includes(c.childRunId) && deltas.boundClaims.has(c.childRunId)));
    let correctedRevision;
    await wait('correctedPreview', async (s) => {
      if (normalize(s.plan?.status) !== 'in_review' && !normalize(s.plan?.assemblyStage).includes('review')) return false;
      const requested = deltas.reviewRequests.get(result.parentRunId);
      if (!requested?.id || requested.id === firstRevision.id || requested.sequence <= firstRevision.sequence) return false;
      correctedRevision = await currentReviewRevision();
      if (correctedRevision.contentIdentity === firstRevision.contentIdentity) {
        throw new AcceptanceFailure('Corrected output revision has unchanged artifact content.');
      }
      return true;
    });
    const revisedFiles = requireResponse(await checkedRequest('GET', path('/assembly/files')), 'revised assembly files');
    if (!Array.isArray(revisedFiles) || !revisedFiles.length) throw new AcceptanceFailure('No revised assembly artifacts.');
    const corrected = await preview('correctedPreview', correctedText, { keepPhase: true });
    if (result.previews[0].bodySha256 === corrected.bodySha256) throw new AcceptanceFailure('Corrected preview is identical to initial render.');
    result.correctedRevision = correctedRevision;
    await collectRevisions();
    phase('reviewDecision');
    await submitReview({ approved: true }, correctedRevision.id);
    result.decisions.push({ decision: 'approve', outputRevisionId: correctedRevision.id });
    await wait('terminalCompletion', (s) => normalize(s.run?.status) === 'completed' || normalize(s.plan?.status) === 'complete');
    result.verdict = 'pass';
  } catch (error) {
    result.error = { code: error.code ?? 'acceptance_failed', message: String(error.message) };
    try {
      if (result.parentRunId) await snapshot();
    } catch { /* Preserve original failure and last known state. */ }
  } finally {
    result.phaseTimingsMs[result.phase] = clock() - phaseStarted;
    result.lastEvents = deltas.recent;
    result.cleanup.push(...await cleanupOwnedPreviews(request, owned));
    if (result.cleanup.some((entry) => !entry.deleted)) {
      result.verdict = 'fail';
      result.error ??= { code: 'cleanup_failed', message: 'Owned preview cleanup could not be confirmed.' };
    }
    await appendRedactedJsonLine(transcriptPath, { at: new Date().toISOString(), result });
    await writeLifecycleJson(resultPath, result);
  }
  return result;
}
