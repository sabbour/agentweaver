/**
 * Pure grouping reducer for the run timeline.
 *
 * SECURITY NOTE (Y-3): All text fields stored here are consumed by React
 * components that render them as escaped text nodes (the React default).
 * No HTML interpretation, markdown rendering, or dangerouslySetInnerHTML is
 * used anywhere in the timeline rendering pipeline.
 */
import type { RunStreamEvent } from '../api/sse';
import type {
  TimelineReducerState,
  TurnGroupItem,
  AgentMessageItem,
  ToolCallItem,
} from './types';

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
  const pathArg = args['path'] ?? args['file'] ?? args['directory'] ?? args['dir'];
  const pathStr = pathArg != null ? String(pathArg) : null;
  const displayPath = pathStr != null ? stripPathPrefix(pathStr) : null;

  const knownTools: Record<string, string> = {
    read_file: 'Read file',
    write_file: 'Write file',
    create_file: 'Create file',
    delete_file: 'Delete file',
    list_directory: 'List directory',
    run_command: 'Run command',
    search_files: 'Search files',
    edit_file: 'Edit file',
    move_file: 'Move file',
  };

  const label = knownTools[toolName] ?? toolName.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());

  if (displayPath) return `${label} \u00b7 ${displayPath}`;
  // For run_command, show the command arg instead
  const cmdArg = args['command'] ?? args['cmd'];
  if (cmdArg != null) return `${label} \u00b7 ${String(cmdArg).slice(0, 60)}`;
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
  step: AgentMessageItem | ToolCallItem,
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
  const items = [...state.items.slice(0, ti), newTurn, ...state.items.slice(ti + 1)];
  const pendingToolCalls = new Map(state.pendingToolCalls);
  pendingToolCalls.delete(callId);
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
      const turn = smState.items[ti] as TurnGroupItem;
      const closedTurn: TurnGroupItem = { ...turn, active: false };
      const items = [...smState.items.slice(0, ti), closedTurn, ...smState.items.slice(ti + 1)];
      return { ...smState, items, currentTurnIndex: null, streamingMessage: null };
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
      const callId = event.payload['callId'];
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
      const callId = event.payload['callId'];
      const content = cap(String(event.payload['content'] ?? ''));
      return settleToolCall(state, callId, {
        result: { content },
        error: null,
        settled: true,
      });
    }

    case 'tool.error': {
      const callId = event.payload['callId'];
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

    case 'run.completed': {
      // The watch loop emits run.completed at the workflow terminal; close any lingering
      // open turn defensively (should already be closed by agent.turn.end from the runner).
      const s = closeOpenTurn(state);
      return { ...s, items: [...s.items, { kind: 'lifecycle', event }] };
    }

    case 'review.requested':
    case 'review.approved':
    case 'review.declined':
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };

    case 'merge.completed':
    case 'merge.failed': {
      // Defensive close — a no-op when the turn is already closed via agent.turn.end.
      const s = closeOpenTurn(state);
      return { ...s, items: [...s.items, { kind: 'lifecycle', event }] };
    }

    case 'tool.output':
    case 'tool.exec_result':
    case 'shell.approval_required':
    case 'sandbox.selected':
    case 'sandbox.warning':
      return { ...state, items: [...state.items, { kind: 'lifecycle', event }] };

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
