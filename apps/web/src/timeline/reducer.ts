import type { RunStreamEvent } from '../api/sse';
import type {
  AgentMessageItem,
  ApprovalRequestItem,
  QuestionRequestItem,
  TimelineReducerState,
  ToolCallItem,
  TurnGroupItem,
  TurnStep,
} from './types';
/**
 * Pure grouping reducer for the run timeline.
 *
 * SECURITY NOTE (Y-3): All text fields stored here are consumed by React
 * components that render them as escaped text nodes (the React default).
 * No HTML interpretation, markdown rendering, or dangerouslySetInnerHTML is
 * used anywhere in the timeline rendering pipeline.
 */
/** Maximum characters stored per content field (Y-1: prevent unbounded DOM growth). */
export const CONTENT_MAX_CHARS = 50_000;

export type TimelineAction =
  | { type: 'event'; event: RunStreamEvent }
  | { type: 'reset' };

export const initialTimelineState: TimelineReducerState = {
  items: [],
  turnCounter: 0,
  currentTurnIndex: null,
  pendingToolCalls: new Map(),
  pendingApprovals: new Map(),
  pendingQuestions: new Map(),
  streamingMessage: null,
};

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------

/** Cap content at CONTENT_MAX_CHARS to keep DOM bounded (Y-1). */
function cap(text: string): string {
  return text.length > CONTENT_MAX_CHARS ? text.slice(0, CONTENT_MAX_CHARS) : text;
}

/**
 * Extract the tool call id from an event payload, accepting BOTH camelCase
 * (`callId`, live SSE wire format) and snake_case (`call_id`, some persisted /
 * replayed sources — the backend's own BoardProjectionService reads both).
 *
 * ROOT CAUSE of the "perpetual clock/spinner" bug: a `tool.result`/`tool.error`
 * whose id key differs from the originating `tool.call` never matched in
 * pendingToolCalls, so `settled` stayed false forever. Normalising the key on
 * BOTH the call and its completion guarantees they pair regardless of casing.
 */
function extractCallId(payload: Record<string, unknown>): unknown {
  const camel = payload['callId'];
  if (camel != null) return camel;
  return payload['call_id'];
}

/**
 * Strip common worktree / home path prefixes from a string for display (Y-2).
 * Full content is still available on expand — this only affects the header label.
 */
export function stripPathPrefix(value: string): string {
  // Remove leading Unix home dirs: /home/xxx/..., /Users/xxx/...
  // Remove leading Windows home dirs: C:\Users\xxx\... or /c/Users/xxx/...
  return value
    .replace(/^\/(?:home|Users)\/[^/\\]+[/\\]/, '')
    .replace(/^[A-Za-z]:[/\\]Users[/\\][^/\\]+[/\\]/, '')
    .replace(/^[/\\][a-zA-Z][/\\]Users[/\\][^/\\]+[/\\]/, '');
}

/**
 * Derive a short, human-readable card title from a tool name + its arguments (Y-2).
 * The path shown in the title has home/worktree prefix stripped.
 */
export function deriveHumanTitle(toolName: string, args: Record<string, unknown>): string {
  // report_intent: show the intent text directly — it IS the label
  if (toolName === 'report_intent') {
    const intent = args['intent'];
    if (intent != null) return String(intent).slice(0, 120);
    return 'Intent';
  }

  // run_command: always show the command, never the injected working directory
  if (toolName === 'run_command') {
    const cmd = args['command'] ?? args['cmd'];
    if (cmd != null) return `Run command \u00b7 ${String(cmd).slice(0, 80)}`;
    return 'Run command';
  }

  // For file/search tools, derive a display path from known path argument keys.
  // Exclude 'directory' to avoid showing working directory injected for governance.
  const pathArg = args['path'] ?? args['file'] ?? args['dir'];
  const pathStr = pathArg != null ? String(pathArg) : null;
  const displayPath = pathStr != null ? stripPathPrefix(pathStr) : null;

  const knownTools: Record<string, string> = {
    read_file: 'Read file',
    write_file: 'Write file',
    create_file: 'Create file',
    create: 'Create file',
    delete_file: 'Delete file',
    list_directory: 'List directory',
    search_files: 'Search files',
    grep_search: 'Search',
    file_search: 'Find files',
    edit_file: 'Edit file',
    edit: 'Edit file',
    str_replace_editor: 'Edit file',
    apply_patch: 'Apply patch',
    move_file: 'Move file',
  };

  const label = knownTools[toolName] ?? toolName.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());

  if (displayPath) return `${label} \u00b7 ${displayPath}`;
  // For run_command, show the command arg instead
  const cmdArg = args['command'] ?? args['cmd'];
  if (cmdArg != null) return `${label} \u00b7 ${String(cmdArg).slice(0, 80)}`;
  // For search tools, show the pattern
  const patternArg = args['pattern'] ?? args['query'];
  if (patternArg != null) return `${label} \u00b7 ${String(patternArg).slice(0, 60)}`;
  return label;
}

// ---------------------------------------------------------------------------
// Helpers that produce new state immutably
// ---------------------------------------------------------------------------

/**
 * Return the current open TurnGroupItem, or auto-create a synthetic one if
 * no turn is open (Fix #4 — never crash on orphaned events).
 */
function ensureOpenTurn(state: TimelineReducerState): TimelineReducerState {
  if (state.currentTurnIndex !== null) return state;
  const turnCounter = state.turnCounter + 1;
  const syntheticTurn: TurnGroupItem = {
    kind: 'turn-group',
    turnId: `synthetic-${turnCounter}`,
    turnIndex: turnCounter,
    steps: [],
    active: true,
  };
  const items = [...state.items, syntheticTurn];
  return { ...state, items, turnCounter, currentTurnIndex: items.length - 1 };
}

function addStepToCurrentTurn(
  state: TimelineReducerState,
  step: TurnStep,
): TimelineReducerState {
  const s = ensureOpenTurn(state);
  const ti = s.currentTurnIndex!;
  const turn = s.items[ti] as TurnGroupItem;
  const stepIndex = turn.steps.length;
  const newTurn: TurnGroupItem = { ...turn, steps: [...turn.steps, step] };
  const items = [...s.items.slice(0, ti), newTurn, ...s.items.slice(ti + 1)];

  let pendingToolCalls = s.pendingToolCalls;
  if (step.kind === 'tool-call') {
    pendingToolCalls = new Map(pendingToolCalls);
    pendingToolCalls.set(step.callId, [ti, stepIndex]);
  }

  let streamingMessage = s.streamingMessage;
  if (step.kind === 'agent-message' && step.streaming) {
    streamingMessage = { turnIndex: ti, stepIndex, messageId: step.messageId };
  }

  return { ...s, items, pendingToolCalls, streamingMessage };
}

function appendDeltaToMessage(state: TimelineReducerState, delta: string): TimelineReducerState {
  const sm = state.streamingMessage!;
  const turn = state.items[sm.turnIndex] as TurnGroupItem;
  const msg = turn.steps[sm.stepIndex] as AgentMessageItem;
  const updated: AgentMessageItem = {
    ...msg,
    content: cap(msg.content + delta),
  };
  const newSteps = [...turn.steps.slice(0, sm.stepIndex), updated, ...turn.steps.slice(sm.stepIndex + 1)];
  const newTurn: TurnGroupItem = { ...turn, steps: newSteps };
  const items = [
    ...state.items.slice(0, sm.turnIndex),
    newTurn,
    ...state.items.slice(sm.turnIndex + 1),
  ];
  return { ...state, items };
}

function settleStreamingMessage(
  state: TimelineReducerState,
  content: string,
): TimelineReducerState {
  if (!state.streamingMessage) return state;
  const sm = state.streamingMessage;
  const turn = state.items[sm.turnIndex] as TurnGroupItem;
  const msg = turn.steps[sm.stepIndex] as AgentMessageItem;
  const settled: AgentMessageItem = { ...msg, content: cap(content), streaming: false };
  const newSteps = [...turn.steps.slice(0, sm.stepIndex), settled, ...turn.steps.slice(sm.stepIndex + 1)];
  const newTurn: TurnGroupItem = { ...turn, steps: newSteps };
  const items = [
    ...state.items.slice(0, sm.turnIndex),
    newTurn,
    ...state.items.slice(sm.turnIndex + 1),
  ];
  return { ...state, items, streamingMessage: null };
}

function settleToolCall(
  state: TimelineReducerState,
  callId: unknown,
  patch: Pick<ToolCallItem, 'result' | 'error' | 'settled'>,
): TimelineReducerState {
  const loc = state.pendingToolCalls.get(callId);
  if (!loc) return state; // unknown callId — ignore gracefully
  const [ti, si] = loc;
  const turn = state.items[ti] as TurnGroupItem;
  const call = turn.steps[si] as ToolCallItem;
  const updated: ToolCallItem = { ...call, ...patch };
  const newSteps = [...turn.steps.slice(0, si), updated, ...turn.steps.slice(si + 1)];
  const newTurn: TurnGroupItem = { ...turn, steps: newSteps };
  let items = [...state.items.slice(0, ti), newTurn, ...state.items.slice(ti + 1)];
  const pendingToolCalls = new Map(state.pendingToolCalls);
  pendingToolCalls.delete(callId);

  // Resolve any pending approval card for the same callId/requestId
  const pendingApprovals = new Map(state.pendingApprovals);
  const approvalLoc = pendingApprovals.get(callId);
  if (approvalLoc) {
    const [ati, asi] = approvalLoc;
    const aTurn = items[ati] as TurnGroupItem;
    const approvalStep = aTurn.steps[asi] as ApprovalRequestItem;
    const resolvedApproval: ApprovalRequestItem = {
      ...approvalStep,
      resolved: true,
      resolvedScope: patch.result ? 'once' : 'deny',
    };
    const newSteps2 = [...aTurn.steps.slice(0, asi), resolvedApproval, ...aTurn.steps.slice(asi + 1)];
    const newTurn2 = { ...aTurn, steps: newSteps2 };
    items = [...items.slice(0, ati), newTurn2, ...items.slice(ati + 1)];
    pendingApprovals.delete(callId);
  }

  return { ...state, items, pendingToolCalls, pendingApprovals };
}

/**
 * Grace-settle every still-pending tool call that belongs to the given turn.
 *
 * A tool call stays `settled:false` if the backend never emits a matching
 * completion (e.g. the SDK provided no ToolCallId, so the runner minted a fresh
 * random id for the completion that cannot pair with the call). Once the turn
 * has ended or the run has terminated, the agent has demonstrably moved on, so
 * any such call IS finished — leaving it unsettled shows a perpetual running
 * spinner for work that is actually done. We mark it settled with no error, so
 * the row resolves to a normal completed (checkmark) state rather than spinning
 * forever. This is the intentional fallback for genuinely-missing completions;
 * real completions still settle immediately via their callId match.
 */
function settlePendingCallsInTurn(
  state: TimelineReducerState,
  turnIndex: number,
): TimelineReducerState {
  const stale: unknown[] = [];
  for (const [callId, [ti]] of state.pendingToolCalls) {
    if (ti === turnIndex) stale.push(callId);
  }
  if (stale.length === 0) return state;

  const turn = state.items[turnIndex] as TurnGroupItem;
  const steps = [...turn.steps];
  const pendingToolCalls = new Map(state.pendingToolCalls);
  for (const callId of stale) {
    const loc = pendingToolCalls.get(callId);
    if (!loc) continue;
    const si = loc[1];
    const call = steps[si] as ToolCallItem;
    if (call && call.kind === 'tool-call' && !call.settled) {
      steps[si] = { ...call, settled: true };
    }
    pendingToolCalls.delete(callId);
  }
  const newTurn: TurnGroupItem = { ...turn, steps };
  const items = [...state.items.slice(0, turnIndex), newTurn, ...state.items.slice(turnIndex + 1)];
  return { ...state, items, pendingToolCalls };
}

/**
 * Settle any still-streaming message and close the open turn.
 * Safe no-op when no turn is open and no streaming message exists.
 * Always settle BEFORE closing — mirrors the agent.turn.end pattern.
 */
function closeOpenTurn(state: TimelineReducerState): TimelineReducerState {
  // 1. Settle any still-streaming message with its accumulated content.
  let s = state;
  if (s.streamingMessage) {
    const sm = s.streamingMessage;
    const accumulatedContent = (
      (s.items[sm.turnIndex] as TurnGroupItem).steps[sm.stepIndex] as AgentMessageItem
    ).content;
    s = settleStreamingMessage(s, accumulatedContent);
  }
  // 2. Close the open turn (no-op when already closed).
  if (s.currentTurnIndex === null) return s;
  const ti = s.currentTurnIndex;
  // Grace-settle any tool calls whose completion never arrived (no perpetual spinner).
  s = settlePendingCallsInTurn(s, ti);
  const turn = s.items[ti] as TurnGroupItem;
  const closedTurn: TurnGroupItem = { ...turn, active: false };
  const items = [...s.items.slice(0, ti), closedTurn, ...s.items.slice(ti + 1)];
  return { ...s, items, currentTurnIndex: null, streamingMessage: null };
}

// ---------------------------------------------------------------------------
// Main reducer
// ---------------------------------------------------------------------------

function processEvent(
  state: TimelineReducerState,
  event: RunStreamEvent,
): TimelineReducerState {
  switch (event.type) {
    case 'agent.turn.start': {
      const turnCounter = state.turnCounter + 1;
      const newTurn: TurnGroupItem = {
        kind: 'turn-group',
        turnId: event.payload['turnId'],
        turnIndex: turnCounter,
        steps: [],
        active: true,
      };
      const items = [...state.items, newTurn];
      return { ...state, items, turnCounter, currentTurnIndex: items.length - 1 };
    }

    case 'agent.turn.end': {
      // Settle any still-streaming message first
      const smState = state.streamingMessage ? settleStreamingMessage(state, (state.items[state.streamingMessage.turnIndex] as TurnGroupItem).steps[state.streamingMessage.stepIndex].kind === 'agent-message' ? (((state.items[state.streamingMessage.turnIndex] as TurnGroupItem).steps[state.streamingMessage.stepIndex]) as AgentMessageItem).content : '') : state;

      if (smState.currentTurnIndex === null) return { ...smState, streamingMessage: null };

      // Targeted single-item update — do NOT use items.map() (fix RD-7)
      const ti = smState.currentTurnIndex;
      // Grace-settle any tool calls whose completion never arrived so finished
      // work never shows a perpetual running spinner (see settlePendingCallsInTurn).
      const settledState = settlePendingCallsInTurn(smState, ti);
      const turn = settledState.items[ti] as TurnGroupItem;
      const closedTurn: TurnGroupItem = { ...turn, active: false };
      const items = [...settledState.items.slice(0, ti), closedTurn, ...settledState.items.slice(ti + 1)];
      return { ...settledState, items, currentTurnIndex: null, streamingMessage: null };
    }

    case 'agent.message.delta': {
      const delta = String(event.payload['delta'] ?? '');
      const messageId = event.payload['messageId'];
      if (state.streamingMessage && state.streamingMessage.messageId === messageId) {
        return appendDeltaToMessage(state, delta);
      }
      // Different messageId — auto-settle any orphaned streaming message before starting the new one.
      // Without this, a message whose agent.message never arrives stays streaming:true forever.
      let s = state;
      if (s.streamingMessage) {
        const sm = s.streamingMessage;
        const accumulatedContent = (
          (s.items[sm.turnIndex] as TurnGroupItem).steps[sm.stepIndex] as AgentMessageItem
        ).content;
        s = settleStreamingMessage(s, accumulatedContent);
      }
      // New streaming message
      const msg: AgentMessageItem = {
        kind: 'agent-message',
        messageId,
        content: cap(delta),
        streaming: true,
      };
      return addStepToCurrentTurn(s, msg);
    }

    case 'agent.message': {
      const messageId = event.payload['messageId'];
      const content = cap(String(event.payload['content'] ?? ''));
      if (state.streamingMessage?.messageId === messageId) {
        // Settle the existing streaming bubble
        return settleStreamingMessage(state, content);
      }
      // No prior streaming bubble (replay path or final-fallback) — add settled
      const msg: AgentMessageItem = {
        kind: 'agent-message',
        messageId,
        content,
        streaming: false,
      };
      return addStepToCurrentTurn(state, msg);
    }

    case 'tool.call': {
      const callId = extractCallId(event.payload);
      const toolName = String(event.payload['toolName'] ?? 'tool');
      const args = (event.payload['arguments'] as Record<string, unknown>) ?? {};
      const callItem: ToolCallItem = {
        kind: 'tool-call',
        callId,
        toolName,
        humanTitle: deriveHumanTitle(toolName, args),
        args,
        result: null,
        error: null,
        settled: false,
      };
      return addStepToCurrentTurn(state, callItem);
    }

    case 'tool.result': {
      const callId = extractCallId(event.payload);
      const content = cap(String(event.payload['content'] ?? ''));
      return settleToolCall(state, callId, {
        result: { content },
        error: null,
        settled: true,
      });
    }

    case 'tool.error': {
      const callId = extractCallId(event.payload);
      const errorMessage = String(event.payload['errorMessage'] ?? '');
      // RD-B2: derive isSandboxViolation from errorMessage — there is NO errorCode field.
      const lower = errorMessage.toLowerCase();
      const isSandboxViolation =
        lower.includes('sandbox') ||
        lower.includes('outside the sandbox boundary') ||
        lower.includes('denied');
      return settleToolCall(state, callId, {
        result: null,
        error: { errorMessage, isSandboxViolation },
        settled: true,
      });
    }

    case 'run.failed': {
      const s = closeOpenTurn(state);
      return { ...s, items: [...s.items, { kind: 'lifecycle', event }] };
    }

    case 'run.error': {
      // Non-terminal: run was reverted to AwaitingReview and is retryable.
      // Add a visible error card without closing or completing the stream.
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };
    }

    case 'run.completed': {
      // The watch loop emits run.completed at the workflow terminal; close any lingering
      // open turn defensively (should already be closed by agent.turn.end from the runner).
      const s = closeOpenTurn(state);
      return { ...s, items: [...s.items, { kind: 'lifecycle', event }] };
    }

    // Workflow-lifecycle events: surface as lifecycle cards in the timeline so every
    // event type is rendered live (Constitution Principle V).
    case 'review.requested':
    case 'review.approved':
    case 'review.declined':
    case 'review.changes_requested':
    case 'revision.started':
    case 'merge.completed':
    case 'merge.failed':
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };

    // Internal orchestration step — not surfaced as a user-visible card.
    case 'workflow.step':
      return state;

    case 'tool.output':
    case 'tool.exec_result':
    case 'shell.approval_required':
    case 'sandbox.selected':
    case 'sandbox.warning':
    case 'agent.system_prompt':
    case 'agent.task':
    case 'agent.tools':
    case 'agent.intent':
    case 'tool.auto_approved':
    // NOTE: coordinator.child_approval_required / _resolved are intentionally NOT
    // handled here. They have dedicated cases below that build an ApprovalRequestItem
    // carrying the owning childRunId so approve/deny routes to the child subtask run
    // (issue #196). Listing them in this fall-through group would shadow those cases
    // (first-match-wins) and lose childRunId routing.
    // Coordinator orchestration milestones — surface as lifecycle cards so the
    // coordinator session timeline narrates the run (spec → plan → dispatch →
    // assembly) even when no agent-turn content is streamed. Normal runs never
    // emit these types, so this is inert for the per-run timeline. High-frequency
    // topology/graph snapshots and subtask.running ticks are deliberately omitted
    // (the workflow graph already visualizes those) to keep the narrative readable.
    // falls through
    case 'coordinator.recovered':
    case 'coordinator.outcome_spec':
    case 'coordinator.outcome_spec.confirmed':
    case 'coordinator.work_plan':
    case 'subtask.dispatched':
    case 'subtask.rai_flagged':
    case 'subtask.assemble_ready':
    case 'subtask.completed':
    case 'subtask.failed':
    case 'coordinator.children_complete':
    case 'coordinator.steering':
    case 'coordinator.assembly_started':
    case 'coordinator.integration_conflict_auto_resolved':
    case 'coordinator.assembly_rai_started':
    case 'coordinator.assembly_rai_completed':
    case 'coordinator.assembly_review_requested':
    case 'coordinator.assembly_review_approved':
    case 'coordinator.assembly_review_preserved':
    case 'coordinator.assembly_changes_requested':
    case 'coordinator.assembly_merge_started':
    case 'coordinator.assembly_merge_completed':
    case 'coordinator.assembly_merge_failed':
    case 'coordinator.assembly_scribe_started':
    case 'coordinator.assembly_scribe_completed':
    case 'coordinator.assembly_completed':
    case 'coordinator.assembly_blocked':
    case 'coordinator.assembly_declined':
    case 'coordinator.assembly_failed':
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };

    case 'run.outcome': {
      const achieved = event.payload['achieved'] as boolean;
      const reason = String(event.payload['reason'] ?? '');
      return { ...state, runOutcome: { achieved, reason } };
    }

    // A coordinator bubbles a child subtask's tool approval as this type; its payload carries the
    // owning child run id so approve/deny can target it (issue #196). Handled identically here.
    case 'tool.approval_required':
    // falls through
    case 'coordinator.child_approval_required': {
      // Server emits camelCase (requestId, toolName); accept both for resilience.
      const requestId = String(event.payload['request_id'] ?? event.payload['requestId'] ?? '');
      const toolName = String(event.payload['tool_name'] ?? event.payload['toolName'] ?? '');
      const url = event.payload['url'] != null ? String(event.payload['url']) : null;
      const childRunId = event.payload['childRunId'] ?? event.payload['child_run_id'];
      const approvalItem: ApprovalRequestItem = {
        kind: 'approval-request',
        requestId,
        toolName,
        url,
        childRunId: childRunId != null && String(childRunId).trim() !== '' ? String(childRunId) : null,
        resolved: false,
        resolvedScope: null,
      };
      if (state.currentTurnIndex !== null) {
        const s = addStepToCurrentTurn(state, approvalItem);
        const ti = s.currentTurnIndex!;
        const stepIndex = (s.items[ti] as TurnGroupItem).steps.length - 1;
        const pendingApprovals = new Map(s.pendingApprovals);
        pendingApprovals.set(requestId, [ti, stepIndex]);
        return { ...s, pendingApprovals };
      }
      // Fallback: no open turn → lifecycle
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };
    }

    // Coordinator mirror of tool.approval_resolved for a bubbled child approval (issue #196).
    case 'tool.approval_resolved':
    // falls through
    case 'coordinator.child_approval_resolved': {
      // Server notifies that a HITL gate closed (operator action or timeout). Find the pending
      // approval by requestId and mark it resolved so the card disables immediately.
      const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
      const expired = Boolean(event.payload['expired']);
      const approved = Boolean(event.payload['approved']);
      const loc = state.pendingApprovals.get(requestId);
      if (!loc) return state; // unknown or already resolved — ignore
      const [ti, si] = loc;
      const turn = state.items[ti] as TurnGroupItem;
      const approvalStep = turn.steps[si] as ApprovalRequestItem;
      const resolvedScope = expired ? 'expired' : approved ? 'once' : 'deny';
      const resolved: ApprovalRequestItem = { ...approvalStep, resolved: true, resolvedScope };
      const newSteps = [...turn.steps.slice(0, si), resolved, ...turn.steps.slice(si + 1)];
      const newTurn: TurnGroupItem = { ...turn, steps: newSteps };
      const items = [...state.items.slice(0, ti), newTurn, ...state.items.slice(ti + 1)];
      const pendingApprovals = new Map(state.pendingApprovals);
      pendingApprovals.delete(requestId);
      return { ...state, items, pendingApprovals };
    }

    // ---- HITL question gates (BLOCKING #1) --------------------------------
    // Questions often arrive with no open turn (esp. bubbled child questions),
    // so they are folded as dedicated top-level items paired by requestId and
    // rendered via the reusable QuestionAnswerCard (see Timeline).
    case 'agent.question_asked':
    case 'coordinator.child_question': {
      const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
      if (!requestId) return state;
      // Ignore a duplicate asked for a question we already track.
      if (state.pendingQuestions.has(requestId)) return state;
      const isChild = event.type === 'coordinator.child_question';
      const askingRunId = isChild
        ? (event.payload['childRunId'] != null || event.payload['child_run_id'] != null
            ? String(event.payload['childRunId'] ?? event.payload['child_run_id'])
            : undefined)
        : undefined;
      const sourceLabel = isChild
        ? (event.payload['sourceLabel'] != null || event.payload['agentName'] != null || event.payload['label'] != null
            ? String(event.payload['sourceLabel'] ?? event.payload['agentName'] ?? event.payload['label'])
            : 'Subtask')
        : undefined;
      const item: QuestionRequestItem = {
        kind: 'question-request',
        requestId,
        question: cap(String(event.payload['question'] ?? '')),
        askingRunId,
        sourceLabel,
        resolved: false,
      };
      const items = [...state.items, item];
      const pendingQuestions = new Map(state.pendingQuestions);
      pendingQuestions.set(requestId, items.length - 1);
      return { ...state, items, pendingQuestions };
    }

    case 'agent.question_answered': {
      const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
      const idx = requestId ? state.pendingQuestions.get(requestId) : undefined;
      if (idx == null) return state; // unknown or already-folded — ignore
      const existing = state.items[idx] as QuestionRequestItem;
      const resolved: QuestionRequestItem = {
        ...existing,
        answer: cap(String(event.payload['answer'] ?? '')),
        timedOut: Boolean(event.payload['timedOut'] ?? event.payload['timed_out'] ?? false),
        resolved: true,
      };
      const items = [...state.items.slice(0, idx), resolved, ...state.items.slice(idx + 1)];
      const pendingQuestions = new Map(state.pendingQuestions);
      pendingQuestions.delete(requestId);
      return { ...state, items, pendingQuestions };
    }

    case 'coordinator.autopilot_answered': {
      // Autopilot auto-answered a (child) question via the coordinator model.
      // Resolve the paired question item if we are tracking it, and always keep
      // the muted audit line (lifecycle card) for provenance.
      const requestId = String(event.payload['requestId'] ?? event.payload['request_id'] ?? '');
      const idx = requestId ? state.pendingQuestions.get(requestId) : undefined;
      let items = state.items;
      let pendingQuestions = state.pendingQuestions;
      if (idx != null) {
        const existing = state.items[idx] as QuestionRequestItem;
        const resolved: QuestionRequestItem = {
          ...existing,
          answer: cap(String(event.payload['answer'] ?? '')),
          timedOut: false,
          resolved: true,
        };
        items = [...state.items.slice(0, idx), resolved, ...state.items.slice(idx + 1)];
        pendingQuestions = new Map(state.pendingQuestions);
        pendingQuestions.delete(requestId);
      }
      return { ...state, items: [...items, { kind: 'lifecycle', event }], pendingQuestions };
    }

    default:
      return state;
  }
}

export function timelineReducer(
  state: TimelineReducerState,
  action: TimelineAction,
): TimelineReducerState {
  if (action.type === 'reset') return initialTimelineState;
  return processEvent(state, action.event);
}
