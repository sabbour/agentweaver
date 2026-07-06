import { useCallback, useMemo } from 'react';
import { apiClient } from '../api/apiClient';
import type { RunStreamEvent, StreamStatus } from '../api/sse';
import type { TimelineItem, TurnGroupItem } from '../timeline/types';
import { useTimelineItems } from '../timeline/useTimelineItems';
import { useSeededRunStream } from './useSeededRunStream';
import { deriveRunStatusFromEvents } from '../timeline/deriveRunStatus';
import type { AssemblyReviewDecision, SteerCoordinatorRequest } from '../api/types';

/**
 * Aggregate HITL gate state derived from the timeline/events, so a consumer can
 * surface the FULL gate set without re-scanning (BLOCKING #1 gate integrity).
 * None of this bypasses a gate — the actions below POST to the same endpoints the
 * existing run pages (and MCP tools) use.
 */
export interface CoordinatorGateState {
  /** Coordinator drafted an Outcome plan that has not been confirmed yet. */
  outcomeSpecPending: boolean;
  /** Collective assembly is awaiting human review. */
  assemblyReviewPending: boolean;
  /** Unanswered direct/child questions currently in the timeline. */
  openQuestionCount: number;
  /** Unresolved tool/shell/child approval requests currently in the timeline. */
  openApprovalCount: number;
}

export interface CoordinatorRunModel {
  runId: string;
  events: RunStreamEvent[];
  items: TimelineItem[];
  runOutcome?: { achieved: boolean; reason: string };
  status: StreamStatus;
  error: string | null;
  droppedEventCount: number;
  isLiveRun: boolean;
  /** Lifecycle status derived from events (review cycles handled) — for RunLayout parity. */
  derivedRunStatus: string;
  gates: CoordinatorGateState;
  reconnect: () => void;
  // Gate/steer actions — thin wrappers over apiClient so the TUI does not duplicate wiring.
  steer: (req: SteerCoordinatorRequest) => Promise<unknown>;
  sendMessage: (instruction: string) => Promise<unknown>;
  stop: () => Promise<unknown>;
  confirmOutcomeSpec: () => Promise<unknown>;
  reviseOutcomeSpec: (feedback: string) => Promise<unknown>;
  reviewAssembly: (decision: AssemblyReviewDecision, comment?: string) => Promise<unknown>;
}

/**
 * Packages the run stream + timeline fold + gate model + steer/gate actions for a
 * coordinator (or worker) run, so presentations bind to ONE hook instead of
 * re-wiring useRunStream + seeding + useTimelineItems + gate scanning
 * (rubber-duck finding #7). RunWatcher/CoordinatorRunPage predate this hook and
 * are intentionally left untouched to keep the change surgical; the browser
 * console TUI consumes this so it never copies their reducer/gate wiring.
 *
 * @param runId  run to bind to ('' disables the stream).
 * @param runStatus  lifecycle status, used only to decide whether to seed history
 *   for a parked/terminal run (see useSeededRunStream / SEED_STATUSES).
 */
export function useCoordinatorRunModel(runId: string, runStatus?: string): CoordinatorRunModel {
  const { events, status, error, droppedEventCount, reconnect } = useSeededRunStream(runId, runStatus);
  const { items, runOutcome } = useTimelineItems(events, runId, droppedEventCount);
  const isLiveRun = status === 'connecting' || status === 'streaming';
  const derivedRunStatus = deriveRunStatusFromEvents(events, isLiveRun);

  const gates = useMemo<CoordinatorGateState>(() => {
    // Outcome-spec: drafted but not confirmed. Assembly review: requested but not resolved.
    let outcomeSpecDrafted = false;
    let outcomeSpecConfirmed = false;
    let assemblyReviewRequested = false;
    let assemblyReviewResolved = false;
    for (const e of events) {
      switch (e.type) {
        case 'coordinator.outcome_spec': outcomeSpecDrafted = true; break;
        case 'coordinator.outcome_spec.confirmed': outcomeSpecConfirmed = true; break;
        case 'coordinator.assembly_review_requested': assemblyReviewRequested = true; assemblyReviewResolved = false; break;
        case 'coordinator.assembly_review_approved':
        case 'coordinator.assembly_review_preserved':
        case 'coordinator.assembly_changes_requested':
        case 'coordinator.assembly_declined':
          assemblyReviewResolved = true; break;
        default: break;
      }
    }
    let openQuestionCount = 0;
    let openApprovalCount = 0;
    for (const item of items) {
      if (item.kind === 'question-request' && !item.resolved) openQuestionCount += 1;
      else if (item.kind === 'turn-group') {
        for (const step of (item as TurnGroupItem).steps) {
          if (step.kind === 'approval-request' && !step.resolved) openApprovalCount += 1;
        }
      } else if (item.kind === 'lifecycle') {
        const t = item.event.type;
        if (t === 'tool.approval_required' || t === 'coordinator.child_approval_required' || t === 'shell.approval_required') {
          openApprovalCount += 1; // conservative; Timeline pairs resolution for display
        }
      }
    }
    return {
      outcomeSpecPending: outcomeSpecDrafted && !outcomeSpecConfirmed,
      assemblyReviewPending: assemblyReviewRequested && !assemblyReviewResolved,
      openQuestionCount,
      openApprovalCount,
    };
  }, [events, items]);

  const steer = useCallback((req: SteerCoordinatorRequest) => apiClient.steerCoordinator(runId, req), [runId]);
  const sendMessage = useCallback((instruction: string) => apiClient.steerCoordinator(runId, { kind: 'send', instruction }), [runId]);
  const stop = useCallback(() => apiClient.steerCoordinator(runId, { kind: 'stop' }), [runId]);
  const confirmOutcomeSpec = useCallback(() => apiClient.confirmOutcomeSpec(runId), [runId]);
  const reviseOutcomeSpec = useCallback((feedback: string) => apiClient.reviseOutcomeSpec(runId, feedback), [runId]);
  const reviewAssembly = useCallback(
    (decision: AssemblyReviewDecision, comment?: string) => apiClient.reviewAssembly(runId, decision, comment),
    [runId],
  );

  return {
    runId, events, items, runOutcome, status, error, droppedEventCount, isLiveRun,
    derivedRunStatus, gates, reconnect,
    steer, sendMessage, stop, confirmOutcomeSpec, reviseOutcomeSpec, reviewAssembly,
  };
}
