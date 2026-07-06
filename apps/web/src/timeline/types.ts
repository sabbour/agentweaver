import type { RunStreamEvent } from '../api/sse';

/** Discriminated union output of the grouping reducer. */
export type TimelineItem = TurnGroupItem | LifecycleItem | WorkflowStepItem | QuestionRequestItem;

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
  /**
   * The run id that OWNS this pending approval. For a coordinator orchestration this is the child
   * subtask run id (carried on the bubbled event payload), which differs from the run whose stream
   * is being rendered. Approve/deny must POST here, not the parent/coordinator run id (issue #196).
   * Null when the approval belongs to the currently rendered run.
   */
  childRunId: string | null;
  resolved: boolean;
  /** 'once'|'run'|'tool'|'always' = operator approved; 'deny' = operator denied; 'expired' = server timeout */
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

/**
 * A top-level HITL question gate (agent.question_asked / coordinator.child_question).
 *
 * BLOCKING #1: questions frequently arrive with NO open turn (especially bubbled
 * child questions on the coordinator stream), so they cannot live as a TurnStep.
 * They are folded into a dedicated top-level item, paired asked↔answered by
 * requestId, and rendered via the reusable QuestionAnswerCard.
 *
 * `askingRunId` is the run that must receive the answer. For a bubbled coordinator
 * child question this is the childRunId (NOT the coordinator run); it is undefined
 * for a direct question, in which case the renderer answers against the watched run.
 */
export interface QuestionRequestItem {
  kind: 'question-request';
  requestId: string;
  question: string;
  /** childRunId for a bubbled child question; undefined ⇒ answer the watched run. */
  askingRunId?: string;
  /** Provenance label for a bubbled child question, e.g. "Subtask 2". */
  sourceLabel?: string;
  /** Present once resolved (agent.question_answered / coordinator.autopilot_answered). */
  answer?: string;
  timedOut?: boolean;
  resolved: boolean;
}

export interface WorkflowStepItem {
  kind: 'workflow_step';
  /** "agent" | "rai" | "review" | "merge" | "scribe" */
  step: string;
  status: 'started' | 'completed' | 'skipped' | 'failed';
  label: string;
  /** Agent name for the "agent" step — e.g. "Trinity" */
  agentName?: string;
  timestamp: number;
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
  /** requestId → items[] index of the QuestionRequestItem, for pairing asked↔answered (BLOCKING #1). */
  pendingQuestions: Map<string, number>;
  /** Location of the currently streaming message bubble. */
  streamingMessage: {
    turnIndex: number;
    stepIndex: number;
    messageId: unknown;
  } | null;
  /** Agent self-assessment from report_outcome tool call. Null if agent never called it. */
  runOutcome?: { achieved: boolean; reason: string };
}
