import type { RunStreamEvent } from '../api/sse';

/** Discriminated union output of the grouping reducer. */
export type TimelineItem = TurnGroupItem | LifecycleItem;

export interface TurnGroupItem {
  kind: 'turn-group';
  turnId: unknown;
  /** 1-based display counter — the Nth agent.turn.start seen, regardless of interleaved items. */
  turnIndex: number;
  steps: TurnStep[];
  /** true until agent.turn.end is received for this turn. */
  active: boolean;
}

export interface ApprovalRequestItem {
  kind: 'approval-request';
  requestId: string;
  toolName: string;
  url: string | null;
  resolved: boolean;
  resolvedScope: string | null;
}

export type TurnStep = AgentMessageItem | ToolCallItem | ApprovalRequestItem;

export interface AgentMessageItem {
  kind: 'agent-message';
  messageId: unknown;
  content: string;
  /** true while deltas are still arriving (no settled agent.message yet). */
  streaming: boolean;
}

export interface ToolCallItem {
  kind: 'tool-call';
  callId: unknown;
  toolName: string;
  /** Short human-readable label derived from tool name + key argument. */
  humanTitle: string;
  args: Record<string, unknown>;
  result: { content: string } | null;
  error: { errorMessage: string; isSandboxViolation: boolean } | null;
  /** false until tool.result or tool.error arrives. */
  settled: boolean;
}

export interface LifecycleItem {
  kind: 'lifecycle';
  event: RunStreamEvent;
}

export interface TimelineReducerState {
  items: TimelineItem[];
  /** Dedicated turn counter — incremented on each agent.turn.start (fix RD-3). */
  turnCounter: number;
  /** Index into items[] for the currently open TurnGroupItem. */
  currentTurnIndex: number | null;
  /** callId → [turnItemIndex, stepIndex] for O(1) pairing of tool.result/error. */
  pendingToolCalls: Map<unknown, [number, number]>;
  /** requestId → [turnItemIndex, stepIndex] for pairing tool.result/error with approval cards. */
  pendingApprovals: Map<unknown, [number, number]>;
  /** Location of the currently streaming message bubble. */
  streamingMessage: {
    turnIndex: number;
    stepIndex: number;
    messageId: unknown;
  } | null;
  /** Agent self-assessment from report_outcome tool call. Null if agent never called it. */
  runOutcome?: { achieved: boolean; reason: string };
}
