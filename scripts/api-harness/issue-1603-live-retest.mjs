import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createRecorderSessionAuthProvider } from './lib/auth-providers/recorder-session.mjs';
import {
  deploymentShaMatches,
  requireDeploymentShaMatch,
  requireExpectedDeploymentSha,
} from './lib/deployment-sha.mjs';
import { appendRedactedJsonLine } from '../harness-shared/safe-jsonl.mjs';
import { redact } from '../harness-shared/redaction.mjs';

function usage() {
  return [
    'Usage: node scripts/api-harness/issue-1603-live-retest.mjs',
    '  --expected-deployment-sha <git-sha>',
    '  [--recorder-auth-root <path>]',
    '',
    'The expected SHA must identify the deployment under test. /api/version may',
    'report its documented 7-character prefix, which must prefix that expected SHA.',
  ].join('\n');
}

function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') {
      options.help = true;
      continue;
    }
    const [name, inlineValue] = argument.split('=', 2);
    if (name === '--expected-deployment-sha' || name === '--recorder-auth-root') {
      const value = inlineValue ?? argv[++index];
      if (!value || value.startsWith('--')) {
        throw new Error(`Missing value for ${name}.\n\n${usage()}`);
      }
      if (name === '--expected-deployment-sha') options.expectedDeploymentSha = value;
      else options.recorderAuthRoot = value;
      continue;
    }
    throw new Error(`Unknown option: ${argument}\n\n${usage()}`);
  }
  return options;
}

const options = parseArgs(process.argv.slice(2));
if (options.help) {
  console.log(usage());
  process.exit(0);
}
const expectedDeploymentSha = requireExpectedDeploymentSha(options.expectedDeploymentSha);
const baseUrl = 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io';
const projectId = 'cdb7fb5a-094f-4886-8eb2-2f8c43023999';
const workflowId = 'pm-discovery';
const stamp = new Date().toISOString().replaceAll(':', '').replaceAll('.', '-');
const transcriptPath = path.resolve(`scripts/api-harness/transcripts/issue-1603-live-retest-${stamp}.jsonl`);
const evidencePath = path.resolve(`scripts/api-harness/evidence/issue-1603-live-retest-${stamp}.json`);
const authorization = await createRecorderSessionAuthProvider({
  authRoot: options.recorderAuthRoot,
  baseUrl,
}).getAuthorization();
const terminalStatuses = new Set(['completed', 'failed', 'cancelled', 'blocked', 'rejected', 'assemble_ready']);
const sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

await mkdir(path.dirname(transcriptPath), { recursive: true });
await mkdir(path.dirname(evidencePath), { recursive: true });

async function request(method, pathname, body, extraHeaders = {}) {
  let lastError;
  for (let attempt = 1; attempt <= 4; attempt++) {
    try {
      const response = await fetch(`${baseUrl}${pathname}`, {
        method,
        headers: {
          Authorization: authorization,
          ...extraHeaders,
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        redirect: 'error',
      });
      const text = await response.text();
      let parsed = text;
      try {
        parsed = text ? JSON.parse(text) : null;
      } catch {
        // Preserve non-JSON response text.
      }
      await appendRedactedJsonLine(transcriptPath, {
        ts: new Date().toISOString(),
        request: { method, path: pathname, body },
        response: { status: response.status, body: parsed },
      });
      return { status: response.status, body: parsed };
    } catch (error) {
      lastError = error;
      if (attempt < 4) await sleep(attempt * 1_000);
    }
  }
  throw lastError;
}

function eventRows(body) {
  return Array.isArray(body) ? body : body?.events ?? body?.items ?? [];
}

function eventTimestamp(event) {
  return event.timestamp_utc ?? event.timestampUtc ?? event.created_at ?? event.createdAt ?? null;
}

function childId(child) {
  return child.childRunId ?? child.child_run_id ?? null;
}

function childStatus(child) {
  return String(child.childRunStatus ?? child.child_run_status ?? child.subtaskStatus ?? child.subtask_status ?? '').toLowerCase();
}

function isTerminal(status) {
  return terminalStatuses.has(String(status ?? '').toLowerCase());
}

function downstreamNodeIds(workflow, joinNodeId) {
  if (!joinNodeId || !Array.isArray(workflow?.edges)) return [];
  const result = [joinNodeId];
  const queued = [joinNodeId];
  const seen = new Set(result);
  while (queued.length > 0) {
    const from = queued.shift();
    for (const edge of workflow.edges.filter((candidate) => candidate.from === from)) {
      if (!seen.has(edge.to)) {
        seen.add(edge.to);
        result.push(edge.to);
        queued.push(edge.to);
      }
    }
  }
  return result;
}

async function approveShellRequests(runId, seen) {
  const result = await request('GET', `/api/runs/${runId}/events?limit=1000`);
  if (result.status !== 200) return;
  for (const event of eventRows(result.body)) {
    if ((event.type ?? event.event_type) !== 'shell.approval_required') continue;
    const hash = event.payload?.commandHash ?? event.payload?.command_hash;
    const key = `${runId}:${hash}`;
    if (!hash || seen.has(key)) continue;
    const approved = await request('POST', `/api/runs/${runId}/shell-approvals`, { command_hash: hash });
    if (approved.status >= 200 && approved.status < 300) seen.add(key);
  }
}

async function snapshot(runId) {
  const [run, plan, children, events] = await Promise.all([
    request('GET', `/api/runs/${runId}`),
    request('GET', `/api/runs/${runId}/work-plan`),
    request('GET', `/api/runs/${runId}/children`),
    request('GET', `/api/runs/${runId}/events?limit=1000`),
  ]);
  return {
    run: run.body,
    plan: plan.status === 200 ? plan.body : null,
    children: children.status === 200 && Array.isArray(children.body) ? children.body : [],
    events: events.status === 200 ? eventRows(events.body) : [],
  };
}

const version = await request('GET', '/api/version');
if (version.status !== 200) {
  throw new Error(`Deployment version lookup failed: HTTP ${version.status}`);
}
requireDeploymentShaMatch(expectedDeploymentSha, version.body?.gitSha);
const session = await request('GET', '/api/auth/session');
if (session.status !== 200 || session.body?.authenticated !== true) {
  throw new Error(`Recorder authentication failed: HTTP ${session.status}`);
}
const workflow = await request('GET', `/api/projects/${projectId}/workflows/${workflowId}`);
if (workflow.status !== 200 || !Array.isArray(workflow.body?.nodes)) {
  throw new Error(`Workflow lookup failed: HTTP ${workflow.status}`);
}

const executionContext = await request(
  'POST',
  '/api/ai/execution-context',
  { operation: 'orchestration', project_id: projectId },
);
if (executionContext.status !== 200 || !executionContext.body?.execution_key) {
  throw new Error(`Execution context failed: HTTP ${executionContext.status}`);
}

const launch = await request(
  'POST',
  `/api/projects/${projectId}/orchestrations`,
  {
    goal: 'Validate Agentweaver v0.34.0 parent cancellation for one concrete initiative: reliable cancellation of active static-fan child runs. Execute the built-in PM discovery workflow as an explicit five-subtask plan. Subtask 1 must independently research customer signals and write only docs/planning/research-customer-signal-parent-cancellation.md. Subtask 2 must independently research technical feasibility and write only docs/planning/research-technical-feasibility-parent-cancellation.md. Dispatch subtasks 1 and 2 concurrently. Subtask 3 must depend on both research subtasks and synthesize them. Subtask 4 must review the synthesis for stakeholders. Subtask 5 must produce the final recommendation. Do not collapse the two independent research subtasks into one task.',
    workflow_override_id: workflowId,
    start_mode: 'direct',
    auto_approve_tools: false,
    autopilot: false,
  },
  { 'If-Model-Provider-Key': executionContext.body.execution_key },
);
if (launch.status !== 201 || !launch.body?.runId) {
  throw new Error(`Orchestration launch failed: HTTP ${launch.status}`);
}

const parentRunId = launch.body.runId;
console.log(JSON.stringify({ phase: 'launched', parentRunId, transcriptPath }));
const seenApprovals = new Set();
let beforeCancel;
const readyDeadline = Date.now() + 5 * 60_000;

while (Date.now() < readyDeadline) {
  beforeCancel = await snapshot(parentRunId);
  await approveShellRequests(parentRunId, seenApprovals);
  for (const child of beforeCancel.children) {
    if (childId(child)) await approveShellRequests(childId(child), seenApprovals);
  }
  const active = beforeCancel.children.filter((child) =>
    childId(child) && childStatus(child) === 'inprogress');
  const pending = downstreamNodeIds(
    workflow.body,
    beforeCancel.plan?.parentJoinNodeId ?? beforeCancel.plan?.parent_join_node_id,
  );
  console.log(JSON.stringify({
    phase: 'waiting',
    parentRunId,
    planStatus: beforeCancel.plan?.status ?? null,
    activeChildren: active.map(childId),
    childStatuses: beforeCancel.children.map((child) => ({
      runId: childId(child),
      status: childStatus(child),
    })),
    pendingWorkflowNodes: pending,
  }));
  if (active.length >= 2 && pending.length >= 1) break;
  if (isTerminal(beforeCancel.run?.status)) throw new Error(`Parent terminalized before cancellation: ${beforeCancel.run?.status}`);
  await sleep(1_000);
}

const activeChildren = beforeCancel.children
  .filter((child) => childId(child) && !isTerminal(childStatus(child)))
  .map((child) => ({
    subtaskId: child.subtaskId ?? child.subtask_id,
    runId: childId(child),
    statusAtCancellation: childStatus(child),
  }));
const pendingWorkflowNodeIds = downstreamNodeIds(
  workflow.body,
  beforeCancel.plan?.parentJoinNodeId ?? beforeCancel.plan?.parent_join_node_id,
);
if (activeChildren.length < 2 || pendingWorkflowNodeIds.length < 1) {
  throw new Error('Did not observe the required top-level fan shape before the deadline');
}

const requestedAt = new Date().toISOString();
const cancel = await request('POST', `/api/runs/${parentRunId}/cancel`);
if (cancel.status !== 200) throw new Error(`Parent cancellation failed: HTTP ${cancel.status}`);
console.log(JSON.stringify({ phase: 'cancelled', parentRunId, requestedAt, activeChildren, pendingWorkflowNodeIds }));

const observations = [];
const terminalDeadline = Date.now() + 60_000;
let finalSnapshot;
let childEvidence = [];
while (Date.now() < terminalDeadline) {
  finalSnapshot = await snapshot(parentRunId);
  childEvidence = [];
  for (const activeChild of activeChildren) {
    const [runResult, eventsResult] = await Promise.all([
      request('GET', `/api/runs/${activeChild.runId}`),
      request('GET', `/api/runs/${activeChild.runId}/events?limit=1000`),
    ]);
    const cancellationEvent = eventRows(eventsResult.body).find((event) => {
      const type = event.type ?? event.event_type;
      return type === 'run.cancelled'
        && event.payload?.reason === 'parent_cancelled'
        && event.payload?.requested === true
        && event.payload?.requestedByRunId === parentRunId;
    }) ?? null;
    childEvidence.push({
      ...activeChild,
      status: runResult.body?.status ?? null,
      endedAt: runResult.body?.ended_at ?? runResult.body?.endedAt ?? null,
      cancellationEvent,
    });
  }
  const observation = {
    at: new Date().toISOString(),
    parentStatus: finalSnapshot.run?.status ?? null,
    planStatus: finalSnapshot.plan?.status ?? null,
    parentResumeState: finalSnapshot.plan?.parentResumeState ?? finalSnapshot.plan?.parent_resume_state ?? null,
    childStatuses: childEvidence.map((child) => ({
      runId: child.runId,
      status: child.status,
      cancellationEvent: Boolean(child.cancellationEvent),
    })),
  };
  observations.push(observation);
  console.log(JSON.stringify({ phase: 'observing', ...observation }));
  if (childEvidence.every((child) => isTerminal(child.status) && child.cancellationEvent)) break;
  await sleep(2_000);
}

const finalObservedAt = new Date().toISOString();
const finalChildren = finalSnapshot.children;
const dispatchedWorkflowNodeIds = new Set((finalSnapshot.plan?.subtasks ?? [])
  .map((subtask) => subtask.workflowBranchNodeId ?? subtask.workflow_branch_node_id)
  .filter(Boolean));
const pendingAfter = pendingWorkflowNodeIds.filter((nodeId) => !dispatchedWorkflowNodeIds.has(nodeId));
const parentCancelEvent = finalSnapshot.events.find((event) => (event.type ?? event.event_type) === 'run.cancelled') ?? null;
const postCancelResumeEvents = finalSnapshot.events.filter((event) => {
  const timestamp = eventTimestamp(event);
  const type = event.type ?? event.event_type ?? '';
  return timestamp && Date.parse(timestamp) >= Date.parse(requestedAt)
    && /(resume|join|children_complete|assembly_started)/i.test(type);
});

const normalizedChildren = childEvidence.map((child) => {
  const cancellationTimestamp = child.cancellationEvent ? eventTimestamp(child.cancellationEvent) : null;
  const terminalTimestamp = child.endedAt ?? cancellationTimestamp;
  return {
    child_run_id: child.runId,
    subtask_id: child.subtaskId,
    status_at_cancellation: child.statusAtCancellation,
    final_status: child.status,
    ended_at: child.endedAt,
    cancellation_event: child.cancellationEvent,
    cancellation_delta_ms: cancellationTimestamp ? Date.parse(cancellationTimestamp) - Date.parse(requestedAt) : null,
    terminal_delta_ms: terminalTimestamp ? Date.parse(terminalTimestamp) - Date.parse(requestedAt) : null,
    run_url: `${baseUrl}/projects/${projectId}/runs/${child.runId}`,
  };
});
const assertions = {
  deployed_sha_matches: deploymentShaMatches(expectedDeploymentSha, version.body?.gitSha),
  authenticated: session.body?.authenticated === true,
  parent_cancel_returned_200: cancel.status === 200,
  required_top_level_shape_observed: activeChildren.length >= 2 && pendingWorkflowNodeIds.length >= 1,
  every_active_child_has_attributable_event: normalizedChildren.every((child) => Boolean(child.cancellation_event)),
  every_active_child_terminal_promptly: normalizedChildren.every((child) =>
    isTerminal(child.final_status) && child.terminal_delta_ms !== null && child.terminal_delta_ms <= 30_000),
  pending_subtasks_not_dispatched: pendingAfter.length === pendingWorkflowNodeIds.length,
  work_plan_cancelled_and_suppressed:
    String(finalSnapshot.plan?.status ?? '').toLowerCase() === 'cancelled'
    && String(finalSnapshot.plan?.parentResumeState ?? finalSnapshot.plan?.parent_resume_state ?? '').toLowerCase() === 'suppressed',
  parent_resume_join_suppressed: postCancelResumeEvents.length === 0,
};
const verdict = Object.values(assertions).every(Boolean) ? 'pass' : 'fail';

const evidence = redact({
  schema: 'agentweaver.issue-1603-live-retest/v1',
  captured_at: finalObservedAt,
  verdict,
  target: {
    base_url: baseUrl,
    expected_deployment_sha: expectedDeploymentSha,
    version: version.body,
    authenticated: session.body?.authenticated === true,
  },
  project: {
    project_id: projectId,
    workflow_id: workflowId,
    project_url: `${baseUrl}/projects/${projectId}`,
  },
  trigger: {
    parent_run_id: parentRunId,
    parent_run_url: `${baseUrl}/projects/${projectId}/runs/${parentRunId}`,
  },
  cancellation: {
    requested_at: requestedAt,
    final_observation_at: finalObservedAt,
    final_observation_delta_ms: Date.parse(finalObservedAt) - Date.parse(requestedAt),
    response: cancel.body,
    parent_status: finalSnapshot.run?.status ?? null,
    parent_ended_at: finalSnapshot.run?.ended_at ?? finalSnapshot.run?.endedAt ?? null,
    parent_cancelled_event: parentCancelEvent,
    active_children: normalizedChildren,
    pending_workflow_node_ids: pendingWorkflowNodeIds,
    pending_subtasks_after: pendingAfter,
    children_after: finalChildren,
    work_plan_status_after: finalSnapshot.plan?.status ?? null,
    parent_resume_state_after: finalSnapshot.plan?.parentResumeState ?? finalSnapshot.plan?.parent_resume_state ?? null,
    post_cancel_resume_events: postCancelResumeEvents,
    observations,
  },
  assertions,
  transcript_path: path.relative(process.cwd(), transcriptPath),
});

await writeFile(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`, 'utf8');
console.log(JSON.stringify({ phase: 'complete', verdict, parentRunId, evidencePath, transcriptPath, assertions }));
if (verdict !== 'pass') process.exitCode = 2;
