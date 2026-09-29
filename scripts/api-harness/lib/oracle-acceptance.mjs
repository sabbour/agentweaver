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
      const code = signal.aborted ? 'request_timeout' : 'transport_error';
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
    lastEvents: [], terminalDiagnostic: null, transcriptPath, resultPath, cleanup: [],
    phaseTimingsMs: {},
  };
  const owned = [];
  const deltas = new EventDeltas(request);
  let phaseStarted = clock();
  let latest = {};
  const phase = (name) => {
    result.phaseTimingsMs[result.phase] = clock() - phaseStarted;
    result.phase = name;
    phaseStarted = clock();
  };
  const path = (suffix) => `/api/runs/${encodeURIComponent(result.parentRunId)}${suffix}`;
  const collectRevisions = async () => {
    for (const id of [result.parentRunId, ...result.childRunIds]) {
      const response = await request('GET', `/api/runs/${encodeURIComponent(id)}/output-revisions`);
      if (response.status === 410) throw new AcceptanceFailure(`Output revision content unavailable for run ${id}.`);
      const revisions = requireResponse(response, 'output revisions');
      if (!Array.isArray(revisions)) throw new AcceptanceFailure('Output revisions response must be an array.');
      for (const revision of revisions) {
        const revisionId = revision.revision_id ?? revision.revisionId;
        if (revisionId && !result.revisionIds.includes(revisionId)) result.revisionIds.push(revisionId);
      }
    }
  };
  const snapshot = async () => {
    const [run, plan, children] = await Promise.all([
      request('GET', path('')), request('GET', path('/work-plan')), request('GET', path('/children')),
    ]);
    requireResponse(run, 'run status');
    if (plan.status !== 200 && plan.status !== 404) requireResponse(plan, 'work plan');
    requireResponse(children, 'children');
    latest = { run: run.body, plan: plan.status === 200 ? plan.body : null, children: children.body };
    const ids = [result.parentRunId, ...(Array.isArray(children.body) ? children.body.map((c) => c.childRunId).filter(Boolean) : [])];
    result.childRunIds = [...new Set([...result.childRunIds, ...ids.slice(1)])];
    for (const id of ids) {
      const events = await deltas.poll(id);
      for (const event of events) {
        if (event.type !== 'shell.approval_required') continue;
        const hash = event.payload?.commandHash ?? event.payload?.command_hash;
        if (!approveShell || !hash) throw new AcceptanceFailure(`Shell approval required for ${id}; rerun with --approve-shell only for a disposable project.`);
        requireResponse(await request('POST', `/api/runs/${encodeURIComponent(id)}/shell-approvals`, { command_hash: hash }), 'shell approval', [200, 201, 202]);
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
    const deadline = clock() + budgets[name] * 60_000;
    for (;;) {
      const state = await snapshot();
      if (await predicate(state)) return state;
      if (clock() >= deadline) throw new AcceptanceFailure(`Phase ${name} exceeded ${budgets[name]} minute budget.`, 'phase_timeout');
      await pause(Math.min(pollMs, Math.max(1, deadline - clock())));
    }
  };
  const preview = async (label, text) => {
    phase(label);
    const deadline = clock() + budgets[label] * 60_000;
    let last;
    while (clock() < deadline) {
      const prior = requireResponse(await request('GET', path('/sandbox/port-forward')), 'existing preview sessions');
      if (!Array.isArray(prior) || prior.length > 0) {
        throw new AcceptanceFailure(`${label}: pre-existing preview session; refusing to adopt or delete it.`);
      }
      const response = await request('POST', path('/sandbox/port-forward'), { target_port: port });
      const value = response.body;
      if (response.status === 200 && value?.session_id && value?.preview_url) {
        owned.push({ runId: result.parentRunId, sessionId: value.session_id });
        const listed = requireResponse(await request('GET', path('/sandbox/port-forward')), 'preview readiness');
        if (!Array.isArray(listed) || !listed.some((s) => s.session_id === value.session_id)) {
          throw new AcceptanceFailure(`${label}: created preview not listed as ready.`);
        }
        const checked = await browser(value.preview_url, text, { timeoutMs: Math.max(1, deadline - clock()) });
        result.previews.push({ phase: label, sessionId: value.session_id, ...checked });
        if (!checked.ready) throw new AcceptanceFailure(`${label}: rendered preview failed acceptance.`);
        return checked;
      }
      last = `HTTP ${response.status}`;
      if (response.status >= 400 && ![404, 409, 503].includes(response.status)) break;
      await pause(Math.min(pollMs, Math.max(1, deadline - clock())));
    }
    throw new AcceptanceFailure(`${label}: preview not ready (${last ?? 'deadline'}).`, 'phase_timeout');
  };
  try {
    const [version, spec, auth] = await Promise.all([
      request('GET', '/api/version', undefined, { authenticated: false }),
      request('GET', '/openapi/v1.json', undefined, { authenticated: false }),
      request('GET', '/api/auth/session'),
    ]);
    result.version = requireResponse(version, 'version');
    const openapi = requireResponse(spec, 'OpenAPI');
    if (!openapi?.paths || !auth.body?.authenticated || auth.status !== 200) {
      throw new AcceptanceFailure('OpenAPI or authenticated session preflight failed.');
    }
    const essential = ['/api/runs/{id}/events', '/api/runs/{coordinatorRunId}/assembly/review'];
    result.openapiPathsChecked = essential.map((p) => ({ path: p, present: Boolean(openapi.paths[p]) }));
    if (projectId) requireResponse(await request('GET', `/api/projects/${encodeURIComponent(projectId)}`), 'project');
    if (!result.parentRunId) {
      if (!projectId || !goal) throw new AcceptanceFailure('Provide --run-id or --project-id and --goal.');
      const execution = requireResponse(await request('POST', '/api/ai/execution-context', { operation: 'orchestration', project_id: projectId }), 'execution context');
      if (!execution?.execution_key) throw new AcceptanceFailure('Execution key unavailable.');
      const started = requireResponse(await request('POST', `/api/projects/${encodeURIComponent(projectId)}/orchestrations`, {
        goal, workflow_override_id: workflowId, start_mode: 'direct', auto_approve_tools: true, autopilot: true,
      }, { headers: { 'If-Model-Provider-Key': execution.execution_key } }), 'orchestration start', [201]);
      result.parentRunId = started.runId ?? started.run_id;
      if (!result.parentRunId) throw new AcceptanceFailure('Orchestration did not return runId.');
    }
    await wait('planning', (s) => Boolean(s.plan));
    await wait('claimProvisioning', (s) => s.children.some((c) => deltas.boundClaims.has(c.childRunId)));
    await wait('implementation', (s) => /assembl|review|complet/.test(normalize(s.plan?.status)));
    await wait('buildTestReview', (s) => normalize(s.plan?.status) === 'in_review' || normalize(s.plan?.assemblyStage).includes('review'));
    const firstFiles = requireResponse(await request('GET', path('/assembly/files')), 'initial assembly files');
    if (!Array.isArray(firstFiles) || !firstFiles.length) throw new AcceptanceFailure('No assembled files at initial review.');
    await preview('initialPreview', expectedText);
    await collectRevisions();
    if (!feedback?.trim() || !correctedText?.trim() || !targetFiles?.length) {
      throw new AcceptanceFailure('Grounded request_changes requires feedback, target files, and corrected application evidence.');
    }
    result.childRunIdsAtReview = [...result.childRunIds];
    phase('reviewDecision');
    requireResponse(await request('POST', path('/assembly/review'), {
      approved: false, request_changes: true, feedback, target_files: targetFiles,
    }), 'request_changes', [200, 202]);
    result.decisions.push({ decision: 'request_changes', feedback, targetFiles });
    const stopped = await cleanupOwnedPreviews(request, owned);
    result.cleanup.push(...stopped);
    if (stopped.some((entry) => !entry.deleted)) throw new AcceptanceFailure('Initial preview cleanup could not be confirmed.');
    owned.length = 0;
    await wait('revisionProvisioning', (s) => s.children.some((c) => c.childRunId
      && !result.childRunIdsAtReview.includes(c.childRunId) && deltas.boundClaims.has(c.childRunId)));
    await wait('correctedPreview', async (s) => {
      if (normalize(s.plan?.status) !== 'in_review' && !normalize(s.plan?.assemblyStage).includes('review')) return false;
      const files = await request('GET', path('/assembly/files'));
      return files.status === 200 && Array.isArray(files.body) && files.body.length > 0
        && JSON.stringify(files.body) !== JSON.stringify(firstFiles);
    });
    const revisedFiles = requireResponse(await request('GET', path('/assembly/files')), 'revised assembly files');
    if (!Array.isArray(revisedFiles) || !revisedFiles.length) throw new AcceptanceFailure('No revised assembly artifacts.');
    if (JSON.stringify(firstFiles) === JSON.stringify(revisedFiles)) throw new AcceptanceFailure('Revised assembly diff is unchanged.');
    const corrected = await preview('correctedPreview', correctedText);
    if (result.previews[0].bodySha256 === corrected.bodySha256) throw new AcceptanceFailure('Corrected preview is identical to initial render.');
    await collectRevisions();
    requireResponse(await request('POST', path('/assembly/review'), { approved: true }), 'final approval', [200, 202]);
    result.decisions.push({ decision: 'approve' });
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
