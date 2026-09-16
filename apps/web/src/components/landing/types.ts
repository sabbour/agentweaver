import type { ComponentType } from 'react';
import type { StepStatus } from '../WorkflowGraphPanel';

/** Stage indices for the five visible beats of every scenario run. */
export const STAGE = {
  TYPING: 0,
  OUTCOME: 1,
  PLAN: 2,
  DISPATCH: 3,
  ARTIFACT: 4,
} as const;

export type StageIndex = (typeof STAGE)[keyof typeof STAGE];

/** A node on the run tree. Positions are derived from `col`/`row` so authored
 *  scenarios stay legible and never hard-code pixel coordinates.
 *
 *  NOTE: scenario nodes intentionally OMIT `startedAt`. The reused
 *  WorkflowGraphPanel renderer only spins an `ElapsedTimer` interval when a node
 *  carries `state.startedAt`; leaving it out keeps the player's single run-token
 *  scheduler the sole owner of time. "Running" is shown by status + a static
 *  authored `duration` string. */
export interface ScenarioNode {
  id: string;
  label: string;
  /** Role key understood by iconForRole / roleDescForRole. */
  role: string;
  agentName?: string;
  agentRoleTitle?: string;
  modelId?: string;
  pod?: string;
  /** Static authored duration text (e.g. "1m 12s"). Never a live timer.
   *  Rendered as the message subtext on completed nodes in the scenario theater
   *  (e.g. "48s" under a done agent node instead of the generic "Finished"). */
  duration?: string;
  /** Grid column (0-based); mapped to an x pixel by the player. */
  col: number;
  /** Grid row (fractional allowed); mapped to a y pixel by the player. */
  row: number;
  /** Human-review gate never auto-completes; it stays a pending gate. */
  isReviewGate?: boolean;
}

export interface OutcomeSpec {
  goal: string;
  scope: string[];
  assumptions: string[];
  review: string[];
}

export interface WorkPlanItem {
  id: string;
  title: string;
  owner: string;
  detail: string;
}

export type ScenarioEdge = [id: string, source: string, target: string];

export interface Scenario {
  id: string;
  /** Short label for the tab strip. */
  tabLabel: string;
  /** One-word category shown under the tab label. */
  tabHint: string;
  /** Theater header title once the run is under way. */
  title: string;
  subtitle: string;
  /** Concrete goal typed into the composer. */
  goal: string;
  outcome: OutcomeSpec;
  plan: WorkPlanItem[];
  nodes: ScenarioNode[];
  edges: ScenarioEdge[];
  /** Static header metric (authored). */
  credits: string;
  /** Label shown on the artifact frame, e.g. "Pull request preview". */
  artifactLabel: string;
  /** One-line caption describing the artifact. */
  artifactCaption: string;
  /** The Stage-5 artifact component. Only the active scenario's artifact mounts. */
  Artifact: ComponentType;
}

/** Derived per-node runtime status for a given stage/dispatch position. */
export interface NodeRuntime {
  status: StepStatus;
  statusLabel: string;
}
