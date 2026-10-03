import { createHash } from 'node:crypto';
import { appendRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';
import { writeLifecycleJson } from '../../harness-shared/persona-lifecycle.mjs';
import { verifyRenderedPreview } from '../../harness-shared/preview-browser.mjs';

export const DEFAULT_BUDGETS = Object.freeze({
  planning: 6, claimProvisioning: 6, implementation: 18, initialPreview: 5,
  buildTestReview: 10, revisionProvisioning: 12, correctedPreview: 5, terminalCompletion: 8,
});

export const MAX_ASSEMBLY_CORRECTIONS = 3;
const TERMINAL = new Set(['failed', 'cancelled', 'canceled', 'blocked', 'assembly_blocked', 'assembly_failed', 'assembly_declined', 'merge_failed', 'rai_blocked', 'needs_resolution', 'assembly_unknown']);
const DISPATCH_REENTRY_PHASES = new Set(['implementation', 'buildTestReview']);
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
    this.previewEvents = new Map();
    this.podBindings = new Map();
    this.buildTests = new Map();
    this.assemblyChangesRequests = [];
    this.subtaskDispatches = [];
    this.subtaskStatuses = new Map();
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
        if (event.type === 'sandbox.execution_pod.bound' || event.type === 'sandbox.execution_pod.unbound') {
          this.podBindings.set(runId, { sequence: cursor, type: event.type, podName: event.payload?.podName });
        }
        if (event.type === 'coordinator.assembly_build_test_completed') {
          this.buildTests.set(runId, { sequence: cursor, ...event.payload });
        }
        if (event.type === 'coordinator.assembly_changes_requested') {
          this.assemblyChangesRequests.push({
            runId, sequence: cursor, workPlanId: event.payload?.workPlanId,
            redispatchedSubtaskIds: event.payload?.redispatchedSubtaskIds,
          });
        }
        if (event.type === 'subtask.dispatched') {
          this.subtaskDispatches.push({
            runId, sequence: cursor, subtaskId: event.payload?.subtaskId,
            childRunId: event.payload?.childRunId, assignedAgent: event.payload?.assignedAgent,
          });
        }
        if (event.type?.startsWith('subtask.') && nonempty(event.payload?.childRunId)) {
          this.subtaskStatuses.set(event.payload.childRunId, {
            childRunId: event.payload.childRunId, subtaskId: event.payload.subtaskId,
            assignedAgent: event.payload?.assignedAgent,
            status: event.type.slice('subtask.'.length), sequence: cursor,
          });
        }
        if (['sandbox.preview_ready', 'sandbox.preview_failed', 'sandbox.preview_skipped_not_applicable'].includes(event.type)) {
          const events = this.previewEvents.get(runId) ?? [];
          events.push(event);
          this.previewEvents.set(runId, events);
        }
        if (event.type === 'coordinator.assembly_review_requested' && event.payload?.outputRevisionId) {
          this.reviewRequests.set(runId, {
            id: event.payload.outputRevisionId, sequence: cursor,
            treeHash: event.payload.treeHash, workPlanId: event.payload.workPlanId,
          });
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

const nonempty = (value) => typeof value === 'string' && value.trim().length > 0;
const agentIdentity = (value) => nonempty(value) ? value.trim().toLowerCase() : '';
const matchesRevisionWorkPlanId = (revisionId, planId) => Number.isSafeInteger(planId) && planId > 0
  && (typeof revisionId === 'number' ? Number.isSafeInteger(revisionId) && revisionId > 0
    : typeof revisionId === 'string' && /^[1-9]\d*$/.test(revisionId)
      && Number.isSafeInteger(Number(revisionId)))
  && Number(revisionId) === planId;

export function selectCurrentAutomaticPreview({ runId, plan, run, revision, deltas, sessions }) {
  if (!Array.isArray(sessions)) throw new AcceptanceFailure('Preview sessions response must be an array.');
  const review = deltas.reviewRequests.get(runId);
  if (!Number.isSafeInteger(plan?.workPlanId) || plan.workPlanId < 1
    || plan.coordinatorRunId !== runId || normalize(plan.status) !== 'in_review' || !review
    || review.workPlanId !== plan.workPlanId || !nonempty(review.treeHash)
    || revision?.tree_hash !== review.treeHash || !matchesRevisionWorkPlanId(revision.work_plan_id, plan.workPlanId)) {
    throw new AcceptanceFailure('Current review, work plan and revision tree do not match.');
  }
  const binding = deltas.podBindings.get(runId);
  const current = run?.sandbox?.current_binding;
  if (binding?.type !== 'sandbox.execution_pod.bound' || !nonempty(binding.podName)
    || current?.state !== 'verified'
    || current.run_id !== runId
    || current.provisioner !== 'kubernetes-sandbox-claim'
    || !nonempty(current.claim_name) || !nonempty(current.claim_uid)
    || !nonempty(current.pod_uid) || !nonempty(current.namespace)
    || !Number.isInteger(run.lifecycle_generation) || run.lifecycle_generation < 1
    || current.lifecycle_generation !== run.lifecycle_generation
    || !nonempty(current.assembly_attempt)
    || !nonempty(current.source_repository) || !nonempty(current.source_ref)
    || !nonempty(current.source_base_commit) || !nonempty(current.source_worktree)
    || current.source_tree !== review.treeHash
    || current.pod_name !== binding.podName) {
    throw new AcceptanceFailure('Current run has no verified claim and source binding matching the preview.');
  }
  const build = deltas.buildTests.get(runId);
  if (!build || build.workPlanId !== plan.workPlanId || build.treeHash !== review.treeHash
    || build.sequence >= review.sequence) {
    throw new AcceptanceFailure('Current review has no completed Build & Test for its tree.');
  }
  const events = (deltas.previewEvents.get(runId) ?? [])
    .filter((event) => event.payload?.run_id === runId
      && event.payload?.work_plan_id === plan.workPlanId
      && event.payload?.tree_hash === review.treeHash
      && event.payload?.source === 'preview-step'
      && event.sequence < review.sequence);
  const latest = events.at(-1);
  if (latest && latest.type !== 'sandbox.preview_ready') {
    throw new AcceptanceFailure('Current automatic preview failed or was skipped.');
  }
  if (sessions.length > 1) throw new AcceptanceFailure('Ambiguous preview sessions for current run.');
  if (!latest && !sessions.length) return null;
  if (!latest) throw new AcceptanceFailure('Preview session has no current automatic preview-ready event.');
  const event = latest.payload;
  if (!nonempty(event.session_id) || !nonempty(event.preview_runner_session_id)
    || !nonempty(event.preview_url) || !nonempty(event.pod_name)
    || !Number.isInteger(event.target_port) || event.target_port < 1 || event.target_port > 65535
    || event.pod_name !== binding.podName || binding.sequence >= latest.sequence) {
    throw new AcceptanceFailure('Automatic preview event has no active matching pod, runner, port or URL.');
  }
  if (!sessions.length) return null;
  const session = sessions[0];
  if (session.session_id !== event.session_id || session.preview_runner_session_id !== event.preview_runner_session_id
    || session.pod_name !== event.pod_name || session.target_port !== event.target_port
    || session.preview_url !== event.preview_url) {
    throw new AcceptanceFailure('Preview session does not match the current automatic preview event.');
  }
  return { sessionId: event.session_id, previewUrl: event.preview_url, targetPort: event.target_port };
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
  expectedText, correctedText, feedback, targetFiles,
  budgets = DEFAULT_BUDGETS, pollMs = 5000, browser = verifyRenderedPreview,
  clock = () => Date.now(), pause = sleep, approveShell = false,
}) {
  const result = {
    schema: 'agentweaver.oracle-assembly-acceptance/v1', verdict: 'fail',
    phase: 'preflight', projectId, parentRunId: suppliedRunId ?? null,
    childRunIds: [], revisionIds: [], previews: [], decisions: [],
    shellApprovalReconciliations: [],
    lastEvents: [], terminalDiagnostic: null, transcriptPath, resultPath, cleanup: [],
    phaseTimingsMs: {}, phaseAttempts: [], assemblyCorrections: [],
    lastWorkPlan: null, lastChildStatuses: [],
  };
  const owned = [];
  const submittedShellApprovals = new Set();
  const deltas = new EventDeltas(request);
  const runStartedAt = clock();
  let phaseStarted = runStartedAt;
  let phaseDeadline = Infinity;
  let latest = {};
  const phaseAttemptCounts = new Map([['preflight', 1]]);
  const usedPhaseDispatches = new Map();
  let activePhaseAttempt = { phase: 'preflight', attempt: 1, startedAtElapsedMs: 0 };
  let assemblyReviewDeadline = null;
  let assemblyReviewComplete = false;
  let nextAssemblyChangesRequest = 0;
  const phaseBudgetMinutes = (name) => budgets[name] ?? 2;
  const finishPhase = () => {
    if (!activePhaseAttempt) return;
    const durationMs = Math.max(0, clock() - phaseStarted);
    activePhaseAttempt.durationMs = durationMs;
    result.phaseTimingsMs[activePhaseAttempt.phase] = (result.phaseTimingsMs[activePhaseAttempt.phase] ?? 0) + durationMs;
    result.phaseAttempts.push(activePhaseAttempt);
    activePhaseAttempt = null;
  };
  const phase = (name, { dispatchIds } = {}) => {
    if (result.phase === name && activePhaseAttempt) return;
    let dispatchKey;
    if (dispatchIds !== undefined) {
      if (!Array.isArray(dispatchIds) || !dispatchIds.length || dispatchIds.some((id) => !nonempty(id))) {
        throw new AcceptanceFailure(`Phase ${name} requires a verified dispatched child generation.`, 'invalid_dispatch_generation');
      }
      dispatchKey = JSON.stringify([...dispatchIds].sort());
    }
    const previousAttempts = phaseAttemptCounts.get(name) ?? 0;
    if (DISPATCH_REENTRY_PHASES.has(name) && previousAttempts > 0) {
      const used = usedPhaseDispatches.get(name) ?? new Set();
      if (!dispatchKey || used.has(dispatchKey)) {
        throw new AcceptanceFailure(`Phase ${name} cannot restart without a new child dispatch.`, 'phase_restart_without_dispatch');
      }
      used.add(dispatchKey);
      usedPhaseDispatches.set(name, used);
    } else if (DISPATCH_REENTRY_PHASES.has(name) && dispatchKey) {
      usedPhaseDispatches.set(name, new Set([dispatchKey]));
    }
    finishPhase();
    result.phase = name;
    phaseStarted = clock();
    phaseDeadline = phaseStarted + phaseBudgetMinutes(name) * 60_000;
    const attempt = previousAttempts + 1;
    phaseAttemptCounts.set(name, attempt);
    activePhaseAttempt = {
      phase: name, attempt, startedAtElapsedMs: phaseStarted - runStartedAt,
      ...(dispatchIds ? { dispatchChildRunIds: [...dispatchIds].sort() } : {}),
    };
    if (name === 'buildTestReview' && assemblyReviewDeadline === null) {
      const lifecycleMinutes = phaseBudgetMinutes('buildTestReview')
        + MAX_ASSEMBLY_CORRECTIONS * (
          phaseBudgetMinutes('revisionProvisioning')
          + phaseBudgetMinutes('implementation')
          + phaseBudgetMinutes('buildTestReview')
        );
      assemblyReviewDeadline = phaseStarted + lifecycleMinutes * 60_000;
    }
  };
  const remaining = () => {
    let ms = phaseDeadline - clock();
    if (assemblyReviewDeadline !== null && !assemblyReviewComplete) {
      const lifecycleRemaining = assemblyReviewDeadline - clock();
      if (lifecycleRemaining <= 0) {
        throw new AcceptanceFailure('Assembly review lifecycle exceeded its fixed overall deadline.', 'phase_timeout');
      }
      ms = Math.min(ms, lifecycleRemaining);
    }
    if (ms <= 0) throw new AcceptanceFailure(`Phase ${result.phase} exceeded its budget.`, 'phase_timeout');
    return ms;
  };
  const checkedRequest = async (method, url, body, options = {}) => {
    const maxAttempts = method === 'GET' ? 3 : 1;
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      const signal = AbortSignal.timeout(Math.max(1, Math.min(remaining(), method === 'POST' ? 180_000 : 30_000)));
      let response;
      try {
        response = await request(method, url, body, { ...options, signal });
      } catch (error) {
        if (method !== 'GET' || !(error instanceof AcceptanceFailure)
          || !['request_timeout', 'transport_error'].includes(error.code)
          || (signal.aborted && signal.reason?.name !== 'TimeoutError')) throw error;
        remaining();
        if (attempt === maxAttempts) throw error;
        await pause(Math.min(250 * attempt, remaining()));
        continue;
      }
      remaining();
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
    if (detail.tree_hash !== requested.treeHash || !matchesRevisionWorkPlanId(detail.work_plan_id, latest.plan?.workPlanId)
      || !nonempty(detail.tree_content_sha256) || !Array.isArray(detail.files) || !detail.files.length) {
      throw new AcceptanceFailure('Review revision tree does not match current work plan.');
    }
    return { ...requested, contentIdentity, detail };
  };
  const verifyRevisionSource = async (revision, text) => {
    let matched = false;
    for (const file of revision.detail.files) {
      if (!nonempty(file.path) || !/^[a-f0-9]{64}$/i.test(file.sha256 ?? '')) {
        throw new AcceptanceFailure('Review revision file has no valid source identity.');
      }
      const data = requireResponse(await checkedRequest('GET', path(
        `/output-revisions/${encodeURIComponent(revision.id)}/files/${file.path.split('/').map(encodeURIComponent).join('/')}`,
      )), 'revision source bytes');
      if (data.revision_id !== revision.id || data.path !== file.path || data.sha256 !== file.sha256
        || typeof data.content_base64 !== 'string'
        || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(data.content_base64)) {
        throw new AcceptanceFailure('Review revision source bytes do not match their manifest.');
      }
      const bytes = Buffer.from(data.content_base64, 'base64');
      if (bytes.length !== file.size || createHash('sha256').update(bytes).digest('hex') !== file.sha256) {
        throw new AcceptanceFailure('Review revision source hash or size mismatch.');
      }
      if (bytes.toString('utf8').includes(text)) matched = true;
    }
    if (!matched) throw new AcceptanceFailure('Expected application text is absent from the reviewed source bytes.');
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
  const recordLatestStatuses = () => {
    const subtasks = Array.isArray(latest.plan?.subtasks) ? latest.plan.subtasks : [];
    result.lastWorkPlan = latest.plan ? {
      workPlanId: latest.plan.workPlanId ?? null,
      status: latest.plan.status ?? null,
      assemblyStage: latest.plan.assemblyStage ?? null,
      subtasks: subtasks.map((subtask) => ({
        subtaskId: subtask.subtaskId ?? null, status: subtask.status ?? null,
      })),
    } : null;
    result.lastChildStatuses = (Array.isArray(latest.children) ? latest.children : []).map((child) => {
      const subtask = subtasks.find((entry) => entry.subtaskId === child.subtaskId);
      const eventStatus = deltas.subtaskStatuses.get(child.childRunId);
      return {
        childRunId: child.childRunId,
        subtaskId: child.subtaskId ?? eventStatus?.subtaskId ?? null,
        status: child.status ?? eventStatus?.status ?? subtask?.status ?? null,
        latestEventStatus: eventStatus?.status ?? null,
      };
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
    recordLatestStatuses();
    const ids = [result.parentRunId, ...(Array.isArray(children.body) ? children.body.map((c) => c.childRunId).filter(Boolean) : [])];
    result.childRunIds = [...new Set([...result.childRunIds, ...ids.slice(1)])];
    for (const id of ids) {
      await deltas.poll(id);
    }
    recordLatestStatuses();
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
  const wait = async (name, predicate, options = {}) => {
    phase(name, options);
    for (;;) {
      const state = await snapshot();
      if (await predicate(state)) { remaining(); return state; }
      await pause(Math.min(pollMs, remaining()));
    }
  };
  const nextDispatchedCorrection = (state) => {
    const requests = deltas.assemblyChangesRequests;
    for (let index = nextAssemblyChangesRequest; index < requests.length; index++) {
      const correction = requests[index];
      if (correction.runId !== result.parentRunId) {
        nextAssemblyChangesRequest = index + 1;
        continue;
      }
      if (!Number.isSafeInteger(correction.workPlanId) || correction.workPlanId < 1
        || !Array.isArray(correction.redispatchedSubtaskIds)
        || !correction.redispatchedSubtaskIds.length
        || correction.redispatchedSubtaskIds.some((id) => !Number.isSafeInteger(id) || id < 1)) {
        throw new AcceptanceFailure('Assembly correction event has no valid work plan or redispatched subtasks.', 'assembly_correction_unverified');
      }
      if (state.plan?.workPlanId !== correction.workPlanId) {
        throw new AcceptanceFailure('Assembly correction event does not match the current work plan.', 'assembly_correction_unverified');
      }
      const subtaskIds = [...new Set(correction.redispatchedSubtaskIds)];
      const nextCorrection = requests.slice(index + 1).find((entry) => entry.runId === result.parentRunId);
      const children = Array.isArray(state.children) ? state.children : [];
      const dispatchedChildren = subtaskIds.map((subtaskId) => {
        const candidates = deltas.subtaskDispatches.filter((dispatch) => dispatch.runId === result.parentRunId
          && dispatch.subtaskId === subtaskId
          && dispatch.sequence > correction.sequence
          && (!nextCorrection || dispatch.sequence < nextCorrection.sequence)
          && nonempty(dispatch.childRunId));
        const childRunIds = [...new Set(candidates.map((dispatch) => dispatch.childRunId))];
        if (childRunIds.length > 1) {
          throw new AcceptanceFailure(`Assembly correction dispatched multiple children for subtask ${subtaskId}.`, 'assembly_correction_unverified');
        }
        const childRunId = childRunIds[0];
        if (!childRunId) return null;
        if (candidates.length !== 1) {
          throw new AcceptanceFailure(`Assembly correction dispatch for subtask ${subtaskId} is ambiguous.`, 'assembly_correction_unverified');
        }
        const dispatch = candidates[0];
        if (deltas.subtaskDispatches.some((prior) => prior.runId === result.parentRunId
          && prior.childRunId === childRunId && prior.sequence < correction.sequence)) {
          throw new AcceptanceFailure(`Assembly correction reused an existing child for subtask ${subtaskId}.`, 'assembly_correction_unverified');
        }
        if (!children.some((child) => child.childRunId === childRunId)) return null;
        const previousDispatch = deltas.subtaskDispatches.findLast((prior) => prior.runId === result.parentRunId
          && prior.subtaskId === subtaskId && prior.sequence < correction.sequence);
        if (!agentIdentity(previousDispatch?.assignedAgent) || !agentIdentity(dispatch.assignedAgent)) {
          throw new AcceptanceFailure(`Assembly correction omitted an assigned author for subtask ${subtaskId}.`, 'assembly_correction_unverified');
        }
        const subtask = state.plan?.subtasks?.find((entry) => entry.subtaskId === subtaskId);
        if (agentIdentity(subtask?.assignedAgent) !== agentIdentity(dispatch.assignedAgent)) return null;
        return {
          subtaskId, childRunId, dispatchSequence: dispatch.sequence,
          assignedAgent: dispatch.assignedAgent,
        };
      });
      if (dispatchedChildren.some((child) => child === null)) return null;
      nextAssemblyChangesRequest = index + 1;
      return {
        sequence: correction.sequence, workPlanId: correction.workPlanId, subtaskIds,
        children: dispatchedChildren,
        childRunIds: dispatchedChildren.map((child) => child.childRunId),
      };
    }
    return null;
  };
  const hasPendingAssemblyCorrection = () => deltas.assemblyChangesRequests
    .slice(nextAssemblyChangesRequest)
    .some((correction) => correction.runId === result.parentRunId);
  const waitForAssemblyReview = async (dispatchIds) => {
    phase('buildTestReview', { dispatchIds });
    for (;;) {
      const state = await snapshot();
      const correction = nextDispatchedCorrection(state);
      if (correction) {
        result.assemblyCorrections.push({
          attempt: result.assemblyCorrections.length + 1,
          workPlanId: correction.workPlanId, sequence: correction.sequence,
          subtaskIds: correction.subtaskIds, childRunIds: correction.childRunIds,
          children: correction.children,
        });
        if (result.assemblyCorrections.length > MAX_ASSEMBLY_CORRECTIONS) {
          throw new AcceptanceFailure(`Assembly correction retry cap (${MAX_ASSEMBLY_CORRECTIONS}) exceeded.`, 'assembly_correction_limit');
        }
        return { correction };
      }
      if (!hasPendingAssemblyCorrection()
        && (normalize(state.plan?.status) === 'in_review' || normalize(state.plan?.assemblyStage).includes('review'))) {
        remaining();
        assemblyReviewComplete = true;
        return { review: state };
      }
      await pause(Math.min(pollMs, remaining()));
    }
  };
  const preview = async (label, text, revision, { keepPhase = false } = {}) => {
    if (!keepPhase) phase(label);
    for (;;) {
      await snapshot();
      const sessions = requireResponse(await checkedRequest('GET', path('/sandbox/port-forward')), 'automatic preview sessions');
      const selected = selectCurrentAutomaticPreview({
        runId: result.parentRunId, plan: latest.plan, run: latest.run, revision: revision.detail, deltas, sessions,
      });
      if (selected) {
        const checked = await browser(selected.previewUrl, text, { timeoutMs: remaining() });
        remaining();
        if (!checked.ready) throw new AcceptanceFailure(`${label}: rendered preview failed acceptance.`);
        await snapshot();
        const current = requireResponse(await checkedRequest('GET', path('/sandbox/port-forward')), 'preview still active');
        const rechecked = selectCurrentAutomaticPreview({
          runId: result.parentRunId, plan: latest.plan, run: latest.run, revision: revision.detail, deltas, sessions: current,
        });
        if (!rechecked || rechecked.sessionId !== selected.sessionId) {
          throw new AcceptanceFailure(`${label}: automatic preview changed or stopped during browser verification.`);
        }
        result.previews.push({ phase: label, sessionId: selected.sessionId, targetPort: selected.targetPort, ...checked });
        return checked;
      }
      await pause(Math.min(pollMs, remaining()));
    }
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
    let reviewDispatchIds;
    for (;;) {
      const lifecycle = await waitForAssemblyReview(reviewDispatchIds);
      if (lifecycle.review) break;
      const correction = lifecycle.correction;
      const dispatchIds = correction.children.map((child) => child.childRunId);
      const dispatch = { dispatchIds };
      await wait('revisionProvisioning',
        () => dispatchIds.every((id) => deltas.boundClaims.has(id)), dispatch);
      await wait('implementation', (s) => correction.children.every(({ subtaskId, childRunId, dispatchSequence }) => {
        const subtask = s.plan?.subtasks?.find((entry) => entry.subtaskId === subtaskId);
        const childStatus = deltas.subtaskStatuses.get(childRunId);
        return normalize(subtask?.status) === 'assemble_ready'
          && childStatus?.sequence > dispatchSequence
          && normalize(childStatus?.status) === 'assemble_ready';
      }), dispatch);
      reviewDispatchIds = dispatchIds;
    }
    const firstFiles = requireResponse(await checkedRequest('GET', path('/assembly/files')), 'initial assembly files');
    if (!Array.isArray(firstFiles) || !firstFiles.length) throw new AcceptanceFailure('No assembled files at initial review.');
    const firstRevision = await currentReviewRevision();
    result.initialRevision = firstRevision;
    await verifyRevisionSource(firstRevision, expectedText);
    await preview('initialPreview', expectedText, firstRevision);
    await collectRevisions();
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
    await verifyRevisionSource(correctedRevision, correctedText);
    const corrected = await preview('correctedPreview', correctedText, correctedRevision, { keepPhase: true });
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
    finishPhase();
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
