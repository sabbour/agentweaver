import '@xyflow/react/dist/style.css';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiError, formatApiErrorMessage } from '../api/errors';
import { useRunStream } from '../api/sse';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  Tooltip,
  Tree,
  TreeItem,
  TreeItemLayout,
  makeStyles,
  mergeClasses,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  tokens,
} from '@fluentui/react-components';
import { Display, EmptyState, TitleText } from '../components/ui';
import { AgentStepList } from '../components/ui/agentic';
import type { AgentArtifact, AgentStep, AgentStepStatus } from '../components/ui/agentic';
import { AgentAvatar } from '../components/AgentAvatar';
import { AgentSessionPanel } from '../components/AgentSessionPanel';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';
import { CoordinatorArtifactsPanel } from '../components/CoordinatorArtifactsPanel';
import { CostChip, formatAic } from '../components/CostChip';
import { OutcomePlanPanel } from '../components/OutcomePlanPanel';
import { AgentTokenBreakdown } from '../components/runs/AgentTokenBreakdown';
import { SlidePanel } from '../components/SlidePanel';
import {
  accentClass,
  ActiveEdgeContext,
  BrowseFilesContext,
  coordinatorLoopbackLabel,
  CoordinatorSessionContext,
  ElapsedTimer,
  ExecutionModalContext,
  forwardEdge,
  iconForRole,
  loopbackEdge,
  roleDescForRole,
  StatusBadge,
  useNodeStyles,
  workflowEdgeTypes,
  workflowNodeTypes,
} from '../components/WorkflowGraphPanel';
import { useSeededRunStream } from '../hooks/useSeededRunStream';
import { buildTopologyState, initialTopologyState, seedTopologyFromWorkPlan } from '../state/topologyReducer';
import { formatModelLabel } from '../utils/agentIdentity';
import { layoutDagColumns, NODE_H, NODE_TYPE_H, NODE_TYPE_W, NODE_W } from '../utils/dagLayout';
import {
  ArrowRepeatAllRegular,
  BotRegular,
  CheckmarkRegular,
  CircleRegular,
  ClockRegular,
  DismissRegular,
  DocumentRegular,
  FlowchartRegular,
  FolderRegular,
  OpenRegular,
} from '@fluentui/react-icons';
import { Handle, MiniMap, Position, ReactFlow } from '@xyflow/react';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type { FormattedApiError } from '../api/errors';
import type { RunStreamEvent } from '../api/sse';
import type {
  GraphDescriptor,
  PortForwardSessionDto,
  RunAgentTokenBreakdownDto,
  RunStatus,
  WorkPlanResponse,
} from '../api/types';
import type { RunSessionTree } from '../components/AgentSessionPanel';
import type { ExecutorDef, ExecutorState, StepStatus, WorkflowNodeData } from '../components/WorkflowGraphPanel';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import type { CoordinatorTopologyState, TopologyNodeState } from '../state/topologyReducer';
import type { NodeSizeHint } from '../utils/dagLayout';
import type { FluentIcon } from '@fluentui/react-icons';
import type { Edge, Node, NodeProps } from '@xyflow/react';
// ---------------------------------------------------------------------------
// Subtask pipeline expansion is controlled at the page level so the graph container height can grow
// to fit expanded child pipelines (instead of clipping them inside the fixed-height canvas).
interface CoordExpandValue { expanded: Set<string>; toggle: (key: string) => void; }
const CoordExpandContext = createContext<CoordExpandValue | undefined>(undefined);

// Subtask-card clicks open the docked agent-session panel instead of navigating away.
const CoordPanelContext = createContext<((nodeId: string) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Topology status helpers
// ---------------------------------------------------------------------------

function topoStatusToStepStatus(status: string): StepStatus {
  switch (status) {
    case 'dispatching':
    case 'assembling':
    case 'in_review':
    case 'dispatched':     return 'started';
    case 'running':        return 'started';
    case 'pending_capacity': return 'started';
    case 'awaiting_assembly': return 'started';
    case 'assemble_ready': return 'completed';
    case 'rai_flagged':    return 'revise';
    case 'completed':      return 'completed';
    case 'complete':       return 'completed';
    case 'blocked':
    case 'assembly_blocked':
    case 'assembly_failed':
    case 'assembly_declined':
    case 'needs_resolution':
    case 'rai_blocked':
    case 'failed':         return 'failed';
    default:               return 'pending';
  }
}

function graphNodeSize(node: Node): { width: number; height: number } {
  const nt = (node.data as { nodeType?: string } | undefined)?.nodeType;
  return {
    width: node.measured?.width ?? node.initialWidth ?? NODE_TYPE_W[nt ?? ''] ?? NODE_W,
    height: node.measured?.height ?? node.initialHeight ?? NODE_TYPE_H[nt ?? ''] ?? NODE_H,
  };
}

function routeGridEdges(edges: Edge[], nodes: Node[]): Edge[] {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const center = (node: Node) => {
    const size = graphNodeSize(node);
    return {
      x: node.position.x + size.width / 2,
      y: node.position.y + size.height / 2,
    };
  };
  return edges.map((edge) => {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) return edge;
    const sourceCenter = center(source);
    const targetCenter = center(target);
    if (edge.type === 'loopback') {
      const rowPeers = (node: Node, nodeCenter: { x: number; y: number }) =>
        nodes
          .filter((peer) => peer.id !== node.id)
          .map((peer) => center(peer))
          .filter((peerCenter) => Math.abs(peerCenter.y - nodeCenter.y) <= 1);
      const rightCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x > sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x > targetCenter.x),
      ].length;
      const leftCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x < sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x < targetCenter.x),
      ].length;
      const side = leftCrossings < rightCrossings ? 'left' : 'right';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: { ...(edge.data ?? {}), returnSide: side },
      };
    }
    if (edge.type !== 'spine') return edge;
    const forward = targetCenter.x >= sourceCenter.x;
    return {
      ...edge,
      sourceHandle: forward ? 'source-right' : 'source-left',
      targetHandle: forward ? 'target-left' : 'target-right',
      data: { ...(edge.data ?? {}), flowDirection: 'horizontal' },
    };
  });
}

function topoStatusToLabel(status: string): string {
  switch (status) {
    case 'dispatching':     return 'Dispatching';
    case 'assembling':      return 'Assembling';
    case 'in_review':       return 'In review';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'dispatched':     return 'Dispatched';
    case 'running':        return 'Running';
    case 'pending_capacity': return 'Waiting for capacity';
    case 'assemble_ready': return 'Ready for assembly';
    case 'rai_flagged':    return 'RAI flagged';
    case 'completed':      return 'Completed';
    case 'complete':       return 'Complete';
    case 'blocked':        return 'Blocked';
    case 'assembly_blocked': return 'Assembly blocked';
    case 'assembly_failed': return 'Assembly failed';
    case 'assembly_declined': return 'Assembly declined';
    case 'needs_resolution': return 'Needs resolution';
    case 'rai_blocked':    return 'RAI blocked';
    case 'failed':         return 'Failed';
    case 'unknown':        return 'Unknown';
    default:               return 'Pending';
  }
}

/** Map a coordinator graph node id (e.g. 'plan:subtask-1') to its topology node. */
function resolveSubtaskTopoNode(
  graphNodeId: string,
  topology: CoordinatorTopologyState,
): TopologyNodeState | undefined {
  if (topology.nodes[graphNodeId]) return topology.nodes[graphNodeId];
  // Strip 'plan:' prefix: 'plan:subtask-1' -> 'subtask-1'
  const stripped = graphNodeId.replace(/^plan:/, '');
  return topology.nodes[stripped];
}

// ---------------------------------------------------------------------------
// Orchestration lifecycle derivation (issues 3 & 4)
// ---------------------------------------------------------------------------

// Canonical orchestration phases. Backend strings (coordinator_status / work-plan
// status) and assembly_* events are normalized into these so the UI degrades
// gracefully whether or not Tank's in-flight fields are present.
type OrchPhase =
  | 'drafting_outcome'
  | 'dispatching'
  | 'awaiting_assembly'
  | 'assembling'
  | 'rai'
  | 'in_review'
  | 'merge'
  | 'scribe'
  | 'complete'
  | 'failed'
  | 'blocked'
  | 'needs_resolution'
  | 'declined'
  | 'unknown';

interface OrchState {
  phase: OrchPhase;
  reason?: string;
  diff?: string;
  conflictFiles?: string[];
  conflictBranch?: string;
  sourceLabel: string;
  updatedAt?: string;
}

type CoordinatorRunBucket =
  | 'pending'
  | 'running'
  | 'waiting'
  | 'blocked'
  | 'failed'
  | 'completed'
  | 'unknown';

interface CoordinatorRunViewState {
  bucket: CoordinatorRunBucket;
  label: string;
  reason?: string;
  sourceLabel: string;
  terminal: boolean;
  canRetry: boolean;
  canStop: boolean;
  canToggleAutomation: boolean;
}

const RUN_LEVEL_TERMINAL = new Set<string>(['completed', 'failed', 'blocked', 'declined', 'merged', 'merge_failed']);
const RUN_LEVEL_RETRYABLE = new Set<string>(['failed', 'merge_failed']);

// coordinator.assembly_* event type -> phase. These event types may not be emitted
// yet; absence simply means we fall through to the status field / work-plan status.
const ASSEMBLY_EVENT_PHASE: Record<string, { phase: OrchPhase; priority?: number }> = {
  'coordinator.assembly_started': { phase: 'assembling' },
  'coordinator.assembly_rai_started': { phase: 'rai' },
  'coordinator.assembly_rai_completed': { phase: 'assembling' },
  'coordinator.assembly_review_requested': { phase: 'in_review' },
  'coordinator.assembly_review_approved': { phase: 'merge' },
  // The run failed while the review gate was still open, but the gate was DELIBERATELY preserved so
  // the human can still view the changes. Keep the orchestration in the review phase (emitted after
  // assembly_failed) so the UI shows the "review still available" message instead of kicking the
  // operator out. Combined with a terminal run status this drives the preserved-review branch.
  'coordinator.assembly_review_preserved': { phase: 'in_review', priority: 4 },
  'coordinator.assembly_changes_requested': { phase: 'dispatching', priority: 3 }, // re-dispatch resets the phase
  'coordinator.assembly_merge_started': { phase: 'merge' },
  'coordinator.assembly_merge_completed': { phase: 'scribe' },
  'coordinator.assembly_merge_failed': { phase: 'failed', priority: 3 },
  'merge.conflicted': { phase: 'needs_resolution', priority: 3 },
  'coordinator.assembly_scribe_started': { phase: 'scribe' },
  'coordinator.assembly_scribe_completed': { phase: 'scribe' },
  'coordinator.assembly_completed': { phase: 'complete', priority: 3 },
  'coordinator.assembly_failed': { phase: 'failed', priority: 3 },
  'coordinator.assembly_blocked': { phase: 'blocked', priority: 3 },
  'coordinator.assembly_declined': { phase: 'declined', priority: 3 },
};

function normalizePhase(raw: string | undefined | null): OrchPhase {
  if (!raw) return 'unknown';
  const k = raw.toLowerCase().replace(/[^a-z]/g, '');
  if (k.includes('outcomespecdraft') || k.includes('draftingoutcome') || k.includes('defineoutcome') || k.includes('planning') || k === 'drafting') return 'drafting_outcome';
  if (k.includes('awaitingassembly')) return 'awaiting_assembly';
  if (k.includes('needsresolution')) return 'needs_resolution';
  if (k.includes('reviewpreserved')) return 'in_review';
  if (k.includes('reviewapproved')) return 'merge';
  if (k.includes('assembling')) return 'assembling';
  if (k.includes('inreview')) return 'in_review';
  if (k.includes('drafting') || k.includes('awaitingconfirmation')) return 'drafting_outcome';
  if (k.includes('complete')) return 'complete';
  if (k.includes('fail')) return 'failed';
  if (k.includes('block')) return 'blocked';
  if (k.includes('pendingcapacity')) return 'blocked';
  if (k.includes('declin')) return 'declined';
  if (k.includes('dispatch')) return 'dispatching';
  return 'unknown';
}

function readStr(p: Record<string, unknown>, keys: string[]): string | undefined {
  for (const k of keys) {
    const v = p[k];
    if (v != null && String(v).trim() !== '') return String(v);
  }
  return undefined;
}

function readEventTimestamp(p: Record<string, unknown>): string | undefined {
  return readStr(p, ['timestamp_utc', 'timestampUtc', 'updated_at', 'updatedAt', 'timestamp']);
}

function readChildRunId(node: GraphDescriptor['nodes'][number]): string | undefined {
  return node.child_run_id
    ?? readStr(node.data ?? {}, ['child_run_id', 'childRunId'])
    ?? (node.child_graph_ref?.startsWith('run:') ? node.child_graph_ref.slice(4) : undefined);
}

// Priority: live assembly_* events (last wins) > coordinator_status field > work-plan status.
function deriveOrchState(
  events: RunStreamEvent[],
  statusField: string | undefined,
  reasonField: string | undefined,
  workPlanStatus: string | undefined,
): OrchState {
  let winner: { phase: OrchPhase; payload: Record<string, unknown>; type: string; sequence: number; priority: number } | undefined;
  let latestOutcomeDrafting: RunStreamEvent | undefined;
  let latestOutcomeSupersedingSeq = -1;
  for (const evt of events) {
    if (evt.type === 'coordinator.outcome_spec.drafting') {
      if (!latestOutcomeDrafting || evt.sequence >= latestOutcomeDrafting.sequence) latestOutcomeDrafting = evt;
    } else if (
      evt.type === 'coordinator.outcome_spec'
      || evt.type === 'coordinator.outcome_spec.confirmed'
      || evt.type === 'coordinator.work_plan'
      || evt.type === 'subtask.dispatched'
      || evt.type === 'subtask.running'
    ) {
      latestOutcomeSupersedingSeq = Math.max(latestOutcomeSupersedingSeq, evt.sequence ?? -1);
    }
    const mapped = ASSEMBLY_EVENT_PHASE[evt.type as string];
    if (!mapped) continue;
    const priority = mapped.priority ?? 1;
    if (!winner || priority > winner.priority || (priority === winner.priority && evt.sequence >= winner.sequence)) {
      winner = { phase: mapped.phase, payload: evt.payload, type: evt.type, sequence: evt.sequence, priority };
    }
  }
  if (winner) {
    const rawFiles = winner.payload['conflictingFiles'] ?? winner.payload['conflicting_files'];
    const conflictFiles = Array.isArray(rawFiles)
      ? rawFiles.map((f) => String(f)).filter((f) => f.trim() !== '')
      : undefined;
    return {
      phase: winner.phase,
      reason: readStr(winner.payload, ['reason', 'message', 'error', 'detail']),
      diff: readStr(winner.payload, ['diff', 'summary', 'integrationDiff', 'integration_diff', 'treeHash', 'tree_hash']),
      conflictFiles: conflictFiles && conflictFiles.length > 0 ? conflictFiles : undefined,
      conflictBranch: readStr(winner.payload, ['conflictingBranch', 'conflicting_branch']),
      sourceLabel: `${winner.type} event #${winner.sequence}`,
      updatedAt: readEventTimestamp(winner.payload),
    };
  }
  if (latestOutcomeDrafting && (latestOutcomeDrafting.sequence ?? -1) > latestOutcomeSupersedingSeq) {
    return {
      phase: 'drafting_outcome',
      reason: readStr(latestOutcomeDrafting.payload, ['message', 'reason', 'detail']),
      sourceLabel: `${latestOutcomeDrafting.type} event #${latestOutcomeDrafting.sequence}`,
      updatedAt: readEventTimestamp(latestOutcomeDrafting.payload),
    };
  }
  const fieldPhase = normalizePhase(statusField);
  if (fieldPhase !== 'unknown') {
    return { phase: fieldPhase, reason: reasonField ?? undefined, sourceLabel: 'run status field' };
  }
  const wpPhase = normalizePhase(workPlanStatus);
  if (wpPhase !== 'unknown') return { phase: wpPhase, sourceLabel: 'work plan status' };
  return { phase: 'unknown', sourceLabel: 'no phase source yet' };
}

// Coordinator graph node status (so it never shows a stale "Pending").
function orchPhaseToTopoStatus(phase: OrchPhase): string | undefined {
  switch (phase) {
    case 'complete': return 'completed';
    case 'failed':
    case 'declined': return 'failed';
    case 'blocked': return 'blocked';
    case 'unknown': return undefined;
    default: return 'running';
  }
}

// Collective-assembly stage node status, derived from the orchestration phase. Assembly is
// automated EXCEPT the Human Review gate, which waits on the user: during `in_review` the review
// node becomes 'started' so WorkflowNode renders it action-required ("Awaiting your review").
// Returns undefined for stages not yet reached so the backend planned/live kind is preserved.
// role ∈ {rai, review, merge, scribe}.
function assemblyTerminalStageMatchesRole(role: string, terminalStage: string | undefined): boolean {
  if (!terminalStage) return false;
  const stage = terminalStage.toLowerCase().replace(/[^a-z0-9]/g, '');
  const normalizedRole = role.toLowerCase().replace(/[^a-z0-9]/g, '');
  if (stage === normalizedRole) return true;
  if (normalizedRole === 'review') return stage.includes('review');
  if (normalizedRole === 'rai') return stage.includes('rai');
  if (normalizedRole === 'merge') return stage.includes('merge');
  if (normalizedRole === 'scribe') return stage.includes('scribe');
  return false;
}

function assemblyNodeStatus(role: string, phase: OrchPhase, terminalStage?: string): StepStatus | undefined {
  switch (phase) {
    case 'assembling':
    case 'rai':
      return role === 'rai' ? 'started' : undefined;
    case 'in_review':
      if (role === 'rai')    return 'completed';
      if (role === 'review') return 'started';
      return undefined;
    case 'merge':
      if (role === 'rai' || role === 'review') return 'completed';
      if (role === 'merge') return 'started';
      return undefined;
    case 'scribe':
      if (role === 'rai' || role === 'review' || role === 'merge') return 'completed';
      if (role === 'scribe') return 'started';
      return undefined;
    case 'complete':
      return 'completed';
    case 'needs_resolution':
      if (role === 'rai' || role === 'review') return 'completed';
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      if (role === 'merge') return 'failed';
      return undefined;
    case 'failed':
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      return role === 'merge' ? 'failed' : undefined;
    case 'declined':
      if (terminalStage) return assemblyTerminalStageMatchesRole(role, terminalStage) ? 'failed' : undefined;
      if (role === 'review') return 'failed';
      if (role === 'rai')    return 'completed';
      return undefined;
    default:
      return undefined;
  }
}

function orchPhaseLabel(phase: OrchPhase): string {
  switch (phase) {
    case 'drafting_outcome':    return 'Drafting outcome plan';
    case 'dispatching':       return 'Dispatching';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'assembling':        return 'Assembling';
    case 'rai':               return 'RAI review';
    case 'in_review':         return 'In review';
    case 'merge':             return 'Merging';
    case 'scribe':            return 'Scribing';
    case 'complete':          return 'Complete';
    case 'failed':            return 'Failed';
    case 'blocked':           return 'Blocked';
    case 'needs_resolution':  return 'Needs resolution';
    case 'declined':          return 'Declined';
    default:                  return 'Unknown';
  }
}

function runStatusLabel(status: string | undefined): string {
  switch (status) {
    case 'pending': return 'Pending';
    case 'in_progress': return 'In progress';
    case 'completed': return 'Completed';
    case 'failed': return 'Failed';
    case 'blocked': return 'Blocked';
    case 'awaiting_review': return 'Awaiting review';
    case 'merging': return 'Merging';
    case 'merged': return 'Merged';
    case 'declined': return 'Declined';
    case 'merge_failed': return 'Merge failed';
    case 'needs_resolution': return 'Needs resolution';
    case 'assemble_ready': return 'Ready for assembly';
    default: return 'Unknown';
  }
}

function bucketForRunStatus(status: string | undefined): CoordinatorRunBucket {
  switch (status) {
    case 'pending': return 'pending';
    case 'in_progress':
    case 'merging': return 'running';
    case 'awaiting_review': return 'waiting';
    case 'assemble_ready': return 'completed';
    case 'blocked': return 'blocked';
    case 'failed':
    case 'declined':
    case 'merge_failed': return 'failed';
    case 'completed':
    case 'merged': return 'completed';
    default: return 'unknown';
  }
}

function bucketForOrchPhase(phase: OrchPhase): CoordinatorRunBucket {
  switch (phase) {
    case 'drafting_outcome':
    case 'dispatching':
    case 'awaiting_assembly':
    case 'assembling':
    case 'rai':
    case 'merge':
    case 'scribe': return 'running';
    case 'in_review': return 'waiting';
    case 'complete': return 'completed';
    case 'failed':
    case 'declined': return 'failed';
    case 'needs_resolution':
    case 'blocked': return 'blocked';
    default: return 'unknown';
  }
}

function deriveCoordinatorRunViewState(
  runLevelStatus: RunStatus | undefined,
  orch: OrchState,
  loadError: FormattedApiError | null,
): CoordinatorRunViewState {
  if (loadError && runLevelStatus === undefined) {
    const label =
      loadError.kind === 'not-found' ? 'Run not found'
      : loadError.kind === 'unauthorized' ? 'Authentication required'
      : loadError.kind === 'forbidden' ? 'Permission required'
      : 'Run unavailable';
    return {
      bucket: 'unknown',
      label,
      reason: loadError.detail ?? loadError.message,
      sourceLabel: `GET /runs failed${loadError.status ? ` (${loadError.status})` : ''}`,
      terminal: true,
      canRetry: false,
      canStop: false,
      canToggleAutomation: false,
    };
  }

  const status = runLevelStatus ? String(runLevelStatus) : undefined;
  if (status && RUN_LEVEL_TERMINAL.has(status)) {
    const reviewPreserved = orch.sourceLabel.includes('coordinator.assembly_review_preserved');
    return {
      bucket: bucketForRunStatus(status),
      label: reviewPreserved ? 'Review preserved' : runStatusLabel(status),
      reason: orch.reason ?? (reviewPreserved ? 'The run ended, but the assembly review artifact is still available to inspect.' : undefined),
      sourceLabel: reviewPreserved ? orch.sourceLabel : 'run status field',
      terminal: true,
      canRetry: RUN_LEVEL_RETRYABLE.has(status),
      canStop: false,
      canToggleAutomation: false,
    };
  }

  const orchBucket = bucketForOrchPhase(orch.phase);
  if (orchBucket !== 'unknown') {
    return {
      bucket: orchBucket,
      label: orchPhaseLabel(orch.phase),
      reason: orch.reason,
      sourceLabel: orch.sourceLabel,
      terminal: orchBucket === 'completed' || orchBucket === 'failed' || orchBucket === 'blocked',
      canRetry: false,
      canStop: status === 'in_progress',
      canToggleAutomation: status === 'in_progress',
    };
  }

  const runBucket = bucketForRunStatus(status);
  return {
    bucket: runBucket,
    label: runStatusLabel(status),
    reason: orch.reason,
    sourceLabel: status ? 'run status field' : orch.sourceLabel,
    terminal: false,
    canRetry: false,
    canStop: status === 'in_progress',
    canToggleAutomation: status === 'in_progress',
  };
}

// ---------------------------------------------------------------------------
// Session timeline derivation (issue 6)
// ---------------------------------------------------------------------------

function fmtTotal(ms: number): string {
  const secs = Math.floor(ms / 1000);
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  const s = secs % 60;
  if (mins < 60) return `${mins}m ${s}s`;
  const hrs = Math.floor(mins / 60);
  return `${hrs}h ${mins % 60}m`;
}

// Parent subtask elapsed = sum of the child pipeline steps' durations (issue 2).
// Ticks live while any child step is still running.
function useTickingNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

function AggregateElapsed({ states }: { states: Record<string, ExecutorState> }) {
  const hasRunning = Object.values(states).some((st) => st.startedAt !== undefined && st.completedAt === undefined);
  const now = useTickingNow(hasRunning);
  let total = 0;
  for (const st of Object.values(states)) {
    if (st.startedAt === undefined) continue;
    total += Math.max(0, (st.completedAt ?? now) - st.startedAt);
  }
  if (total <= 0) return null;
  return <span aria-label="Total child elapsed">{fmtTotal(total)}</span>;
}

// ---------------------------------------------------------------------------
// Subtask node data + custom React Flow node
// ---------------------------------------------------------------------------

interface SubtaskNodeData extends Record<string, unknown> {
  graphNodeId: string;
  label: string;
  topoStatus: string;
  topoNode: TopologyNodeState | undefined;
  childGraphRef: string | undefined;
  childRunId: string | undefined;
  agent: string | undefined;
  agentRole: string | undefined;
  model: string | undefined;
  phase: string | undefined;
  projectId: string;
  startedAt?: number;
  completedAt?: number;
  totalNanoAiu?: number | null;
  totalTokens?: number | null;
  executionPodName?: string | null;
  /** Layout direction for handle placement. 'LR' = left/right; 'TB' = top/bottom; 'GRID' exposes all sides for routed grid edges. */
  dir?: 'LR' | 'TB' | 'GRID';
}

// Vertical space (px) a subtask node reserves below its body when its child pipeline is expanded,
// so dagre spaces sibling subtasks apart instead of letting the expansion overlap neighbours.
const EXPANDED_PIPELINE_RESERVE = 188;

// Dagre's nodesep is the vertical gap between sibling nodes in LR layout. Subtask cards can be
// taller than the generic hints because their titles/metadata wrap, so keep a generous separation
// for fan-out columns.
const COORDINATOR_GRAPH_NODE_SEP = 96;

// Renders column depth labels (L0 Coordinator, L1 Research…) inside the React Flow canvas
// using ViewportPortal so they pan/zoom with the graph and stay aligned over each column.
// A compact pipeline step row rendered inline inside a SubtaskNode expansion panel. Laid out as a
// narrow VERTICAL strip (icon + label/role + status/timer) so the expansion stays within the card
// width and only grows downward — avoiding the horizontal overflow that overlapped neighbour nodes.
// Does not use React Flow Handles (rendered outside a ReactFlow canvas).
function ChildStepRow({ def, state, isLast }: { def: ExecutorDef; state: ExecutorState; isLast: boolean }) {
  const { key, label, Icon } = def;
  const { status, startedAt, completedAt } = state;
  return (
    <div
      style={{ display: 'flex', flexDirection: 'column', alignItems: 'stretch' }}
      data-testid={`child-node-${key}`}
    >
      <div
        role="article"
        aria-label={`${label}: ${status}`}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '6px 8px',
          border: '1px solid var(--colorNeutralStroke2)',
          borderRadius: 6,
          background: status === 'started'
            ? 'var(--colorBrandBackground2)'
            : 'var(--colorNeutralBackground1)',
        }}
      >
        <span aria-hidden="true" style={{ display: 'inline-flex', color: 'var(--colorNeutralForeground3)', flexShrink: 0 }}>
          <Icon fontSize={16} />
        </span>
        <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1 }}>
          <span style={{ fontSize: 'var(--fontSizeBase200)', fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {label}
          </span>
          <span style={{ fontSize: 'var(--fontSizeBase100)', color: 'var(--colorNeutralForeground3)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {def.roleDescription}
          </span>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 2, flexShrink: 0 }}>
          <StatusBadge status={status} />
          {startedAt !== undefined && (
            <span style={{ fontSize: 'var(--fontSizeBase100)', color: 'var(--colorNeutralForeground3)' }}>
              <ElapsedTimer startedAt={startedAt} completedAt={completedAt} />
            </span>
          )}
        </div>
      </div>
      {!isLast && (
        <span aria-hidden="true" style={{ alignSelf: 'center', color: 'var(--colorNeutralForeground4)', lineHeight: 1, fontSize: 12, height: 14, display: 'flex', alignItems: 'center' }}>
          ↓
        </span>
      )}
    </div>
  );
}

function SubtaskNode({ id, data, selected }: NodeProps) {
  const s = useNodeStyles();
  const d = data as SubtaskNodeData;
  const expandCtx = useContext(CoordExpandContext);
  const openPanel = useContext(CoordPanelContext);
  const expanded = expandCtx?.expanded.has(id) ?? false;
  const [childDescriptor, setChildDescriptor] = useState<GraphDescriptor | null>(null);
  const [childDescriptorError, setChildDescriptorError] = useState<string | null>(null);
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  // Fetch the child run's graph descriptor only when expanded.
  useEffect(() => {
    if (!expanded || !d.childRunId) {
      queueMicrotask(() => setChildDescriptorError(null));
      return;
    }
    let cancelled = false;
    queueMicrotask(() => setChildDescriptorError(null));
    apiClient.getRunGraph(d.childRunId as string)
      .then((desc) => {
        if (!cancelled) {
          setChildDescriptor(desc);
          setChildDescriptorError(null);
        }
      })
      .catch((err) => {
        if (!cancelled) setChildDescriptorError(formatApiErrorMessage(err, 'Child pipeline is not available yet.'));
      });
    return () => { cancelled = true; };
  }, [expanded, d.childRunId]);

  // Subscribe to the child run's live SSE events only while expanded; tear down on collapse.
  const childStreamRunId = expanded && d.childRunId ? (d.childRunId as string) : '';
  const { events: childEvents } = useRunStream(childStreamRunId);

  const childFallbackNow = useTickingNow(childEvents.some((evt) => evt.type === 'workflow.step' && String(evt.payload['status'] ?? 'started') === 'started'));

  // Map workflow.step events from the child run to executor states.
  const childStepStates = useMemo<Record<string, ExecutorState>>(() => {
    const map: Record<string, ExecutorState> = {};
    for (const evt of childEvents) {
      if (evt.type === 'workflow.step') {
        const step      = String(evt.payload['step'] ?? '');
        const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
        const tsStr     = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs      = tsStr ? new Date(tsStr).getTime() : NaN;
        const evtMsg    = evt.payload['message'] != null ? String(evt.payload['message']) : undefined;
        const prev      = map[step];
        map[step] = {
          status:      evtStatus,
          agentName:   prev?.agentName,
          message:     evtMsg,
          startedAt:   evtStatus === 'started' ? (!isNaN(tsMs) ? tsMs : undefined) : prev?.startedAt,
          completedAt: evtStatus !== 'started' && !isNaN(tsMs) ? tsMs : prev?.completedAt,
        };
      } else if (evt.type === 'run.assemble_ready' || evt.type === 'subtask.assemble_ready') {
        const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs  = tsStr ? new Date(tsStr).getTime() : NaN;
        map['assemble-ready'] = { status: 'completed', completedAt: !isNaN(tsMs) ? tsMs : childFallbackNow };
      }
    }
    return map;
  }, [childEvents, childFallbackNow]);

  // Build the ordered list of child pipeline nodes from the descriptor when available. Avoid
  // painting a fake success pipeline when the child graph fetch fails.
  const childNodes = useMemo<Array<{ def: ExecutorDef; state: ExecutorState }>>(() => {
    const defs = childDescriptor
      ? childDescriptor.nodes.map((n) => ({
          key:             n.id,
          label:           n.label,
          roleDescription: roleDescForRole(n.role),
          Icon:            iconForRole(n.role),
        }))
      : [];
    return defs.map((def) => ({
      def,
      state: childStepStates[def.key] ?? { status: 'pending' },
    }));
  }, [childDescriptor, childStepStates]);

  const stepStatus = topoStatusToStepStatus(d.topoStatus as string);
  const statusLabel = topoStatusToLabel(d.topoStatus as string);
  const podName = d.executionPodName as string | null | undefined;

  const handleCardClick = useCallback(() => {
    openPanel?.(id);
  }, [id, openPanel]);

  return (
    <>
      <Tooltip
        content={podName ? `Pod: ${podName}` : ''}
        relationship="description"
        positioning="above"
        withArrow
      >
      <div
        className={`${s.card} ${s.cardSubtask}${stepStatus === 'started' ? ` ${s.cardActive}` : ''}${selected ? ` ${s.cardSelected}` : ''}`}
        data-node-type="subtask"
        role="article"
        aria-label={`${d.label as string}: ${d.topoStatus as string}`}
        aria-current={selected ? 'true' : undefined}
        onClick={handleCardClick}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            handleCardClick();
          }
        }}
        tabIndex={0}
        style={{ cursor: 'pointer' }}
      >
      {d.dir === 'GRID' ? (
        <>
          <Handle id="target-left" type="target" position={Position.Left} style={handleStyle} />
          <Handle id="target-right" type="target" position={Position.Right} style={handleStyle} />
          <Handle id="target-top" type="target" position={Position.Top} style={handleStyle} />
          <Handle id="target-bottom" type="target" position={Position.Bottom} style={handleStyle} />
          <Handle id="source-left" type="source" position={Position.Left} style={handleStyle} />
          <Handle id="source-right" type="source" position={Position.Right} style={handleStyle} />
          <Handle id="source-top" type="source" position={Position.Top} style={handleStyle} />
          <Handle id="source-bottom" type="source" position={Position.Bottom} style={handleStyle} />
        </>
      ) : (
        <>
          <Handle type="target" position={d.dir === 'TB' ? Position.Top : Position.Left} style={handleStyle} />
          <Handle type="source" position={d.dir === 'TB' ? Position.Bottom : Position.Right} style={handleStyle} />
        </>
      )}

      <span className={`${s.accentBar} ${accentClass(s, stepStatus)}`} aria-hidden="true" />

      {/* Top row: status chip left, cost right */}
      <div className={s.cardHeader}>
        <StatusBadge status={stepStatus} label={statusLabel} />
        <CostChip totalNanoAiu={d.totalNanoAiu as number | null | undefined} totalTokens={d.totalTokens as number | null | undefined} />
      </div>

      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          {d.agent
            ? <AgentAvatar name={d.agent as string} size={28} circle badgeIcon={d.Icon as FluentIcon} badgeTitle={(d.agentRole as string | undefined) ?? 'Subtask Agent'} />
            : <BotRegular fontSize={22} />}
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{d.label as string}</span>
          <span className={s.cardRole}>{(d.agentRole as string | undefined) ?? 'Subtask Agent'}</span>
          {d.agent && <span className={s.cardSubText}>{d.agent as string}</span>}
          {d.model && <span className={s.cardModel}>{formatModelLabel(d.model as string)}</span>}
          {d.phase && <span className={s.cardSubText}>{d.phase as string}</span>}
        </div>
      </div>

      {/* Inline child pipeline — compact vertical strip of step rows. Stays within the card width
          (grows only downward) so the expansion never overflows into neighbouring subtask columns. */}
      {expanded && (
        <div
          className="nopan nodrag"
          style={{
            marginTop: 10,
            display: 'flex',
            flexDirection: 'column',
            gap: 0,
          }}
        >
          {childDescriptorError ? (
            <Text style={{ color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300 }}>{childDescriptorError}</Text>
          ) : !d.childRunId ? (
            <Text style={{ color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300 }}>Child run has not been dispatched yet.</Text>
          ) : childNodes.length === 0 ? (
            <Text style={{ color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300 }}>Child pipeline has not been emitted yet.</Text>
          ) : childNodes.map((node, i) => (
              <ChildStepRow
                key={node.def.key}
                def={node.def}
                state={node.state}
                isLast={i === childNodes.length - 1}
              />
            ))}
        </div>
      )}

      {d.startedAt !== undefined ? (
        <div className={s.cardFooter}>
          <span className={s.cardTimer}>
            <ElapsedTimer startedAt={d.startedAt as number} completedAt={d.completedAt as number | undefined} />
          </span>
        </div>
      ) : (expanded && Object.keys(childStepStates).length > 0 && (
        <div className={s.cardFooter}>
          <span className={s.cardTimer}>
            <AggregateElapsed states={childStepStates} />
          </span>
        </div>
      ))}
    </div>
      </Tooltip>
    </>
  );
}

/** Combined node types: generic workflow nodes + subtask expandable node. */
const coordinatorNodeTypes = { ...workflowNodeTypes, subtask: SubtaskNode };

// ---------------------------------------------------------------------------
// Page styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    width: '100%',
    minWidth: 0,
    height: '100%',
    minHeight: 0,
    overflow: 'hidden',
  },
  breadcrumb: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecorationLine: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  statusBannerStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  console: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
    flex: 1,
    minHeight: 0,
  },
  // ---- Run header (identity / actions grid) --------------------------------
  runHeader: {
    display: 'grid',
    gridTemplateColumns: '1fr',
    gridTemplateAreas: '"identity" "actions"',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  identityArea: {
    gridArea: 'identity',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
  },
  actionsArea: {
    gridArea: 'actions',
    paddingTop: tokens.spacingVerticalM,
    borderTopWidth: tokens.strokeWidthThin,
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    minWidth: 0,
  },
  topTitleRow: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  identityLead: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
    flex: '1 1 auto',
  },
  titleText: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    margin: 0,
    maxWidth: '100%',
    minWidth: 0,
  },
  liveDot: {
    display: 'inline-flex',
    alignItems: 'center',
  },
  statusChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'nowrap',
  },
  statusChipStrong: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  statsStrip: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  compactChromeActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexShrink: 0,
  },
  metaRail: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  metaItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
  },
  metaItemStrong: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  metaValue: {
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    maxWidth: '240px',
  },
  metaSeparator: {
    color: tokens.colorNeutralForeground4,
  },
  executionContext: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    paddingTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  executionKicker: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  executionSeparator: {
    color: tokens.colorNeutralForeground4,
  },
  executionValue: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
    maxWidth: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  executionReason: {
    color: tokens.colorNeutralForeground3,
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  statusDetails: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  statusDetailsSummary: {
    cursor: 'pointer',
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightMedium,
  },
  statusDetailsBody: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingTop: tokens.spacingVerticalXS,
  },
  // ---- Run actions toolbar --------------------------------------------------
  runToolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
    minWidth: 0,
  },
  toolbarSection: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  toolbarLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightMedium,
  },
  riskToggleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  toolbarDivider: {
    width: tokens.strokeWidthThin,
    alignSelf: 'stretch',
    minHeight: '20px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  phaseSource: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  stateReason: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  creditsSurface: {
    width: '360px',
    maxHeight: '420px',
    overflowY: 'auto',
    padding: tokens.spacingVerticalM,
  },
  // ---- Error / not-found ----------------------------------------------------
  pageError: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalXXL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  pageErrorActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    paddingTop: tokens.spacingVerticalS,
  },
  // ---- Body: tree | center --------------------------------------------------
  bodyGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(220px, 300px) minmax(0, 1fr)',
    gridTemplateRows: 'minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    minWidth: 0,
    flex: 1,
    minHeight: 0,
    '@media (max-width: 960px)': {
      gridTemplateColumns: '1fr',
      gridTemplateRows: 'auto minmax(0, 1fr)',
    },
  },
  treeRail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
    minHeight: 0,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 960px)': {
      maxHeight: '320px',
    },
  },
  treeRailHeader: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  treeScroll: {
    minHeight: 0,
    overflowY: 'auto',
    overflowX: 'hidden',
  },
  treeEmpty: {
    padding: tokens.spacingVerticalS,
  },
  runTreeItem: {
    minWidth: 0,
  },
  runTreeItemSelected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    borderRadius: tokens.borderRadiusMedium,
  },
  runTreeStatusIcon: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '18px',
    height: '18px',
    flexShrink: 0,
  },
  runTreeStatusRunning: { color: tokens.colorBrandForeground1 },
  runTreeStatusSuccess: { color: tokens.colorStatusSuccessForeground1 },
  runTreeStatusDanger: { color: tokens.colorStatusDangerForeground1 },
  runTreeStatusInput: { color: tokens.colorStatusWarningForeground1 },
  runTreeStatusQueued: { color: tokens.colorNeutralForeground3 },
  treeText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  treeNode: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalSNudge,
    minWidth: 0,
  },
  // Task-first tree row: bold PRIMARY = task title (full width, ellipsis); one muted
  // SECONDARY line = [dot] statusLabel · agentName (role). No pills, no side-stripes.
  treePrimary: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    minWidth: 0,
  },
  treeMetaRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minWidth: 0,
    overflow: 'hidden',
    whiteSpace: 'nowrap',
  },
  treeStatusText: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    flexShrink: 0,
  },
  treeStatusDot: {
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    backgroundColor: 'currentColor',
    flexShrink: 0,
  },
  treeIdentity: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    minWidth: 0,
  },
  stateTextRunning: { color: tokens.colorBrandForeground1 },
  stateTextSuccess: { color: tokens.colorStatusSuccessForeground1 },
  stateTextDanger: { color: tokens.colorStatusDangerForeground1 },
  stateTextInput: { color: tokens.colorStatusWarningForeground1 },
  stateTextQueued: { color: tokens.colorNeutralForeground3 },
  // ---- Center: tabs + minimap ----------------------------------------------
  centerZone: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
    minHeight: 0,
  },
  centerHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  centerHeaderTitles: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  centerTabRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  minimapButton: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingHorizontalXS,
    borderRadius: tokens.borderRadiusMedium,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    flexShrink: 0,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  minimapCanvas: {
    position: 'relative',
    width: '180px',
    height: '110px',
    overflow: 'hidden',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    pointerEvents: 'none',
    '& .react-flow__minimap': {
      position: 'absolute',
      top: 0,
      left: 0,
      right: 0,
      bottom: 0,
      width: '100%',
      height: '100%',
      margin: 0,
    },
    '& .react-flow__minimap-svg': {
      width: '100%',
      height: '100%',
    },
  },
  minimapCaption: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
  },
  centerTabBody: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    minHeight: 0,
    flex: 1,
  },
  readoutBody: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minHeight: 0,
    minWidth: 0,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    overflow: 'hidden',
  },
  tabPanelCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minHeight: '520px',
    minWidth: 0,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  approvalGateWrap: {
    minWidth: 0,
  },
  runChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalSNudge,
    minHeight: '32px',
    padding: `${tokens.spacingVerticalSNudge} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusCircular,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground2,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  runChipLabel: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  runChipCount: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground3,
  },
  runChipAdded: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorPaletteGreenForeground1,
  },
  runChipRemoved: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorPaletteRedForeground1,
  },
  scopeHint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  // ---- Topology (preserved graph plumbing) ---------------------------------
  dagContainer: {
    minHeight: '200px',
    width: '100%',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'auto',
    '& .react-flow__renderer': { borderRadius: '8px' },
    '& .react-flow__minimap': {
      opacity: 0,
      transform: 'scale(0.92)',
      transformOrigin: 'bottom right',
      transition: 'opacity 120ms ease, transform 120ms ease',
      pointerEvents: 'none',
    },
    '&:hover .react-flow__minimap': {
      opacity: 1,
      transform: 'scale(1)',
      pointerEvents: 'auto',
    },
  },
  topologyDag: {
    flex: 1,
    height: 'auto',
    minHeight: '480px',
    overscrollBehavior: 'contain',
    '@media (max-width: 640px)': {
      minHeight: '360px',
    },
  },
  topologyPanelBody: {
    minHeight: 0,
    overflowY: 'hidden',
  },
  topologyInspector: {
    flex: 1,
    minHeight: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  topologyInspectorSummary: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
});

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

// Flatten the run tree into display rows. Depth is derived from the ACTUAL parent/child
// nesting level (recursion depth), NOT from graph column position. This prevents the
// cascading-staircase regression where sequential sibling rows each indented one level
// deeper than the last.
function flattenRunTree(nodes: RunSessionTree[], depth = 0): RunSessionTree[] {
  return nodes.flatMap((node) => [
    { ...node, depth },
    ...flattenRunTree(node.children, depth + 1),
  ]);
}

function runTreeStatusIcon(status: string) {
  const color = semanticStateColorForStatus(status);
  if (color === 'success') return <CheckmarkRegular aria-hidden="true" />;
  if (color === 'danger') return <DismissRegular aria-hidden="true" />;
  if (color === 'running') return <Spinner size="extra-tiny" aria-label="Running" />;
  if (color === 'input') return <ClockRegular aria-hidden="true" />;
  return <CircleRegular aria-hidden="true" />;
}

type SemanticStateColor = 'running' | 'success' | 'danger' | 'input' | 'queued';

function semanticStateColorForStatus(status: string | undefined): SemanticStateColor {
  switch (status) {
    case 'drafting_outcome':
    case 'planning':
    case 'running':
    case 'dispatched':
    case 'dispatching':
    case 'in_progress':
    case 'awaiting_assembly':
    case 'assembling':
    case 'merging':
      return 'running';
    case 'completed':
    case 'complete':
    case 'merged':
    case 'assemble_ready':
    case 'confirmed':
    case 'done':
    case 'success':
      return 'success';
    case 'failed':
    case 'merge_failed':
    case 'declined':
    case 'error':
      return 'danger';
    case 'awaiting_confirmation':
    case 'awaiting_review':
    case 'in_review':
    case 'needs_clarification':
    case 'needs_resolution':
    case 'rai_flagged':
    case 'blocked':
    case 'manual_gate':
    case 'approval_required':
      return 'input';
    default:
      return 'queued';
  }
}

function semanticStateColorToAgentStatus(color: SemanticStateColor): AgentStepStatus {
  switch (color) {
    case 'running': return 'running';
    case 'success': return 'complete';
    case 'danger': return 'blocked';
    case 'input': return 'warning';
    default: return 'pending';
  }
}

function semanticStateColorForBucket(bucket: CoordinatorRunBucket): SemanticStateColor {
  switch (bucket) {
    case 'running': return 'running';
    case 'completed': return 'success';
    case 'failed': return 'danger';
    case 'waiting':
    case 'blocked': return 'input';
    default: return 'queued';
  }
}

function runTreeStatusLabel(status: string, confirmedBy?: string): string {
  switch (status) {
    case 'drafting_outcome': return 'Drafting outcome plan';
    case 'planning': return 'Planning';
    case 'awaiting_confirmation': return 'Awaiting confirmation';
    case 'confirmed': return confirmedBy ? `Confirmed by ${confirmedBy}` : 'Confirmed';
    case 'needs_clarification': return 'Needs clarification';
    case 'needs_resolution': return 'Needs resolution';
    case 'pending_capacity': return 'Waiting for capacity';
    case 'blocked': return 'Blocked';
    case 'completed': return 'Completed';
    case 'assemble_ready': return 'Ready for assembly';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'failed': return 'Failed';
    case 'merge_failed': return 'Merge failed';
    case 'running': return 'Running';
    case 'pending': return 'Pending';
    default: return status ? status.replace(/_/g, ' ') : 'Pending';
  }
}

const FAILED_TASK_STATUSES = new Set(['failed', 'merge_failed', 'declined']);
const BLOCKED_TASK_STATUSES = new Set(['blocked', 'rai_flagged', 'needs_clarification', 'pending_capacity', 'needs_resolution']);
const WAITING_TASK_STATUSES = new Set(['waiting', 'awaiting_confirmation']);
const PENDING_TASK_STATUSES = new Set(['pending']);
const EXECUTING_TASK_STATUSES = new Set(['drafting_outcome', 'planning', 'running', 'dispatched', 'dispatching', 'in_progress', 'awaiting_assembly', 'assembling']);

function formatPhaseUpdated(timestamp: string | undefined): string {
  if (!timestamp) return 'No timestamp from source yet';
  const parsed = new Date(timestamp);
  if (Number.isNaN(parsed.getTime())) return `Updated ${timestamp}`;
  return `Updated ${parsed.toLocaleString()}`;
}

function graphEmptyCopy(
  isConnecting: boolean,
  noWorkPlan: boolean,
  graphError: FormattedApiError | null,
  viewState: CoordinatorRunViewState,
) {
  if (graphError) {
    return {
      title: graphError.kind === 'not-found' ? 'Graph has not been emitted yet' : 'Run graph could not be loaded',
      body: graphError.kind === 'not-found'
        ? 'The coordinator run exists, but the saved graph endpoint has not produced a descriptor yet. Keep the stream open or refresh after the coordinator emits the graph.'
        : `${graphError.message}${graphError.detail ? ` ${graphError.detail}` : ''}`,
    };
  }
  if (isConnecting) {
    return {
      title: 'Connecting to the coordinator stream',
      body: 'Agentweaver is opening the live event feed. The graph will populate from the stream or the saved run graph as soon as either source responds.',
    };
  }
  if (noWorkPlan) {
    return {
      title: viewState.terminal ? 'No saved work plan for this run' : 'Work plan is not available yet',
      body: viewState.terminal
        ? 'The run is no longer active and no work plan was saved. Review the run status and coordinator messages for the failure reason.'
        : 'The coordinator has not produced a saved work plan for this run yet. The page will keep retrying while the run is in progress.',
    };
  }
  return {
    title: 'Waiting for the run graph',
    body: 'The page is listening for coordinator graph events and checking the saved graph. If this does not resolve, refresh the run or inspect the coordinator messages for an error.',
  };
}

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();
  const navigate = useNavigate();
  // Actual run-level RunStatus (distinct from the WorkPlan/orchestration phase). A run can be
  // terminally Failed/Declined at the run level while its WorkPlan.Status is still `in_review`
  // (e.g. a run interrupted by an old build before the durability fix): the in-memory assembly
  // gate is NOT armed, so showing an actionable review bar would 409. We use this to suppress the
  // review affordance for a terminal run and show its failure reason instead.
  const [runLevelStatusState, setRunLevelStatusState] = useState<{ runId: string; status: RunStatus | undefined }>({
    runId: '',
    status: undefined,
  });
  const runLevelStatus = runLevelStatusState.runId === (runId ?? '') ? runLevelStatusState.status : undefined;
  const setRunLevelStatus = useCallback((status: RunStatus | undefined) => {
    setRunLevelStatusState({ runId: runId ?? '', status });
  }, [runId]);

  const {
    events,
    droppedEventCount,
    status: streamStatus,
    error: streamError,
    reconnect: reconnectStream,
  } = useSeededRunStream(runId ?? '', runLevelStatus);

  // Ctrl+Scroll zoom for the orchestration graph.
  const { zoom, zoomIn, zoomOut, resetZoom, viewportRef, maxZoom } = useCtrlScrollZoom({ maxZoom: 2 });

  // Responsive DAG reflow: observe the graph viewport so the topology can choose
  // an appropriate row/column count instead of relying on a giant CSS scale.
  const [dagScrollNode, setDagScrollNode] = useState<HTMLElement | null>(null);
  const [dagContainerSize, setDagContainerSize] = useState({ width: 0, height: 0 });
  const setDagViewportRef = useCallback((node: HTMLElement | null) => {
    viewportRef(node);
    setDagScrollNode(node);
    if (node) setDagContainerSize({ width: node.clientWidth, height: node.clientHeight });
  }, [viewportRef]);
  useEffect(() => {
    if (!dagScrollNode || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) {
        setDagContainerSize({ width: entry.contentRect.width, height: entry.contentRect.height });
      }
    });
    ro.observe(dagScrollNode);
    return () => ro.disconnect();
  }, [dagScrollNode]);

  // REST seed: coordinator GraphDescriptor (GET /api/runs/{id}/graph, coordinator variant).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);
  const [graphError, setGraphError] = useState<FormattedApiError | null>(null);
  const [runLoadError, setRunLoadError] = useState<FormattedApiError | null>(null);
  const [workPlanError, setWorkPlanError] = useState<FormattedApiError | null>(null);

  // Topology seed from work plan + children (for subtask status projection).
  const [topoSeed, setTopoSeed] = useState(initialTopologyState);

  // Agent name -> role title, fetched from the project team roster, so a subtask card can show the
  // assigned agent's ROLE (e.g. "Repo Auditor") and not just their cast name (e.g. "Deckard").
  const [roleByAgent, setRoleByAgent] = useState<Record<string, string>>({});
  const [projectName, setProjectName] = useState('Project');

  // ---------------------------------------------------------------------------
  // Orchestration lifecycle poll (issues 3 & 4). Reads the coordinator_status field
  // (added by the backend concurrently — optional) plus the work-plan status, both
  // tolerated as absent. Polls until the orchestration reaches a terminal phase.
  // ---------------------------------------------------------------------------
  const [coordStatusField, setCoordStatusField] = useState<string | undefined>(undefined);
  const [coordStatusReason, setCoordStatusReason] = useState<string | undefined>(undefined);
  const [workPlanStatus, setWorkPlanStatus] = useState<string | undefined>(undefined);
  const [retriedFrom, setRetriedFrom] = useState<string | null>(null);
  // Per-run work-plan snapshot.
  const [workPlanData, setWorkPlanData] = useState<WorkPlanResponse | null>(null);

  // Sandbox preview port-forward state.
  const [previewDialogOpen, setPreviewDialogOpen] = useState(false);
  const [previewTargetPort, setPreviewTargetPort] = useState('3000');
  const [previewSession,    setPreviewSession]    = useState<PortForwardSessionDto | undefined>(undefined);
  const [previewBusy,       setPreviewBusy]       = useState(false);
  const [previewError,      setPreviewError]      = useState<string | undefined>(undefined);

  // True once the work-plan endpoint has confirmed a 404 (run has no plan yet / is stuck).
  // Used to render a graceful empty state and to back off the lifecycle poll so the page
  // doesn't hammer the 404 endpoint on a tight loop.
  const [noWorkPlan, setNoWorkPlan] = useState(false);
  // True when the run detail confirms this is a child run (parent_run_id non-null). Child runs
  // never have a work-plan or outcome-plan; skip coordinator-only artifact fetches entirely.
  const [isChildRun, setIsChildRun] = useState(false);
  // Retry state for the header button.
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [stopError, setStopError] = useState<string | null>(null);
  // Per-run option toggles (autopilot + auto-approve-tools). Seeded once from the run detail,
  // then driven by user toggles (optimistic). Both cascade to the coordinator's children.
  const [autopilot, setAutopilot] = useState(false);
  const [autoApprove, setAutoApprove] = useState(false);
  const [autopilotBusy, setAutopilotBusy] = useState(false);
  const [autoApproveBusy, setAutoApproveBusy] = useState(false);
  const [automationError, setAutomationError] = useState<string | null>(null);
  const [tokenBreakdown, setTokenBreakdown] = useState<RunAgentTokenBreakdownDto | null>(null);
  const seededToggles = useRef(false);


  useEffect(() => {
    if (!projectId) {
      queueMicrotask(() => setProjectName('Project'));
      return;
    }
    let cancelled = false;
    queueMicrotask(() => setProjectName('Project'));
    apiClient.getProject(projectId)
      .then((project) => {
        if (!cancelled) setProjectName(project.name?.trim() || 'Project');
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [projectId]);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    // Fetch graph descriptor for REST seed (so finished coordinator runs still render).
    queueMicrotask(() => {
      setRestDescriptor(null);
      setGraphError(null);
    });
    apiClient.getRunGraph(runId)
      .then((desc) => {
        if (cancelled) return;
        setRestDescriptor(desc);
        setGraphError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        setGraphError(formatApiError(err, 'The saved run graph could not be loaded.'));
      });

    // Fetch work plan + children for topology status seed. Skip for child runs —
    // work-plan is a coordinator-only artifact and child runs will never have one.
    void (async () => {
      const runDetail = await apiClient.getRun(runId).catch((err) => {
        if (!cancelled) setRunLoadError(formatApiError(err, 'The run could not be loaded.'));
        return null;
      });
      if (cancelled) return;
      if (runDetail) setRunLoadError(null);
      if (runDetail?.parent_run_id != null) {
        setIsChildRun(true);
        return;
      }
      const [workPlan, children] = await Promise.all([
        apiClient.getWorkPlan(runId).catch((err) => {
          if (!(err instanceof ApiError && err.status === 404) && !cancelled) {
            setWorkPlanError(formatApiError(err, 'The work plan could not be loaded.'));
          }
          return null;
        }),
        apiClient.getCoordinatorChildren(runId).catch(() => null),
      ]);
      if (cancelled) return;
      if (workPlan) {
        setWorkPlanError(null);
        setNoWorkPlan(false);
        setTopoSeed(seedTopologyFromWorkPlan(workPlan, children));
        setWorkPlanData(workPlan);
      }
    })();

    return () => { cancelled = true; };
  }, [runId]);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let consecutiveFailures = 0;
    const loadBreakdown = async () => {
      try {
        const next = await apiClient.getRunTokenBreakdown(runId);
        if (!cancelled) {
          consecutiveFailures = 0;
          setTokenBreakdown(next);
        }
      } catch {
        if (!cancelled) {
          consecutiveFailures += 1;
          setTokenBreakdown(null);
          if (consecutiveFailures >= 3) {
            clearInterval(handle);
          }
        }
      }
    };
    const handle: ReturnType<typeof setInterval> = setInterval(() => { void loadBreakdown(); }, 30000);
    void loadBreakdown();
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [runId]);


  // Fetch the project team once to resolve each assigned agent's role title for the subtask cards.
  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getTeam(projectId)
      .then((team) => {
        if (cancelled) return;
        const map: Record<string, string> = {};
        for (const m of team.members ?? []) {
          if (m.name && m.role_title) map[m.name] = m.role_title;
        }
        setRoleByAgent(map);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [projectId]);



  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const TERMINAL = new Set<OrchPhase>(['complete', 'failed', 'blocked', 'declined']);
    queueMicrotask(() => {
      setRunLoadError(null);
      setWorkPlanError(null);
      setNoWorkPlan(false);
      setRunLevelStatus(undefined);
      setCoordStatusField(undefined);
      setCoordStatusReason(undefined);
      setWorkPlanStatus(undefined);
      setWorkPlanData(null);
      setIsChildRun(false);
      seededToggles.current = false;
    });

    const tick = async () => {
      let detail: Awaited<ReturnType<typeof apiClient.getRun>>;
      try {
        detail = await apiClient.getRun(runId);
      } catch (err) {
        if (cancelled) return;
        setRunLoadError(formatApiError(err, 'The run could not be loaded.'));
        if (err instanceof ApiError && (err.status === 404 || err.status === 401 || err.status === 403)) return;
        timer = setTimeout(() => { void tick(); }, 8000);
        return;
      }
      if (cancelled) return;
      setRunLoadError(null);
      // Child runs (parent_run_id non-null) are not coordinator runs and will never have a
      // work-plan or outcome-plan. Skip coordinator-only artifact fetches to avoid 404 noise.
      const childRun = detail?.parent_run_id != null;
      setIsChildRun(childRun);
      let wp: WorkPlanResponse | null = null;
      let workPlanFailed = false;
      if (!childRun) {
        try {
          wp = await apiClient.getWorkPlan(runId);
          const children = await apiClient.getCoordinatorChildren(runId).catch(() => null);
          if (cancelled) return;
          setNoWorkPlan(false);
          setWorkPlanError(null);
          setWorkPlanData(wp);
          setTopoSeed(seedTopologyFromWorkPlan(wp, children));
        } catch (err) {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 404) {
            setNoWorkPlan(true);
            setWorkPlanError(null);
          } else {
            workPlanFailed = true;
            setWorkPlanError(formatApiError(err, 'The work plan could not be loaded.'));
          }
          wp = null;
        }
      } else {
        setNoWorkPlan(false);
        setWorkPlanError(null);
      }
      if (cancelled) return;
      const statusField = detail?.coordinator_status ?? undefined;
      const reasonField = detail?.coordinator_status_reason ?? undefined;
      const wpStatus = wp?.status ?? undefined;
      setCoordStatusField(statusField);
      setCoordStatusReason(reasonField);
      setWorkPlanStatus(wpStatus);
      setRunLevelStatus(detail?.status ?? undefined);
      if (detail?.retried_from) setRetriedFrom(detail.retried_from);
      // Seed the option toggles once from the run detail; subsequent user toggles own the state.
      if (!seededToggles.current && detail) {
        setAutopilot(Boolean(detail.autopilot));
        setAutoApprove(Boolean(detail.auto_approve_tools));
        seededToggles.current = true;
      }
      // Stop polling when the run-level status is already terminal even if the orchestration
      // coordinator_status field is absent (e.g., a run interrupted before emitting a terminal status).
      if (detail?.status && RUN_LEVEL_TERMINAL.has(String(detail.status))) return;
      const phase = normalizePhase(statusField) !== 'unknown'
        ? normalizePhase(statusField)
        : normalizePhase(wpStatus);
      if (!TERMINAL.has(phase)) timer = setTimeout(() => { void tick(); }, workPlanFailed ? 8000 : 4000);
    };

    void tick();
    return () => { cancelled = true; if (timer) clearTimeout(timer); };
  }, [runId, setRunLevelStatus]);

  // Goal is carried by the coordinator.started event.
  const goal = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'coordinator.started' && typeof evt.payload['goal'] === 'string') {
        return evt.payload['goal'] as string;
      }
    }
    return undefined;
  }, [events]);

  // The workflow the coordinator selected/planned this orchestration against. Carried by the
  // coordinator.workflow_selected event, which is persisted to the run event log and replayed on
  // reconnect — so it survives page reloads. Latest event wins.
  const selectedWorkflow = useMemo<{ name: string; auto: boolean; rationale?: string } | undefined>(() => {
    let picked: { name: string; auto: boolean; rationale?: string } | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.workflow_selected') {
        const name = evt.payload['selectedName'] ?? evt.payload['selectedId'];
        if (name != null && String(name).trim() !== '') {
          const rationale = evt.payload['rationale']
            ?? evt.payload['reason']
            ?? evt.payload['selectionReason']
            ?? evt.payload['selection_reason']
            ?? evt.payload['why'];
          picked = {
            name: String(name),
            auto: evt.payload['wasAutoSelected'] === true,
            rationale: rationale != null && String(rationale).trim() !== '' ? String(rationale) : undefined,
          };
        }
      }
    }
    return picked;
  }, [events]);

  // coordinator.graph SSE: highest-seq-wins over REST seed (same pattern as run.workflow_graph).
  const sseDescriptor = useMemo<GraphDescriptor | undefined>(() => {
    let best: { seq: number; desc: GraphDescriptor } | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.graph') {
        const seq = typeof evt.payload['seq'] === 'number' ? evt.payload['seq'] : 0;
        if (!best || seq >= best.seq) {
          best = { seq, desc: evt.payload as unknown as GraphDescriptor };
        }
      }
    }
    return best?.desc;
  }, [events]);

  const effectiveDescriptor: GraphDescriptor | null = sseDescriptor ?? restDescriptor;

  const latestOutcomePlanEvent = useMemo<RunStreamEvent | undefined>(() => {
    let latest: RunStreamEvent | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec' && (!latest || evt.sequence >= latest.sequence)) latest = evt;
    }
    return latest;
  }, [events]);

  const latestOutcomePlanDraftingEvent = useMemo<RunStreamEvent | undefined>(() => {
    let latest: RunStreamEvent | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec.drafting' && (!latest || evt.sequence >= latest.sequence)) latest = evt;
    }
    return latest;
  }, [events]);

  const specConfirmed = useMemo(
    () => events.some((e) => e.type === 'coordinator.outcome_spec.confirmed'),
    [events],
  );

  const outcomePlanConfirmedBy = useMemo(() => {
    let confirmedBy: string | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec.confirmed' && typeof evt.payload['confirmedBy'] === 'string') {
        confirmedBy = evt.payload['confirmedBy'] as string;
      }
    }
    return confirmedBy;
  }, [events]);

  const workPlanSeen = useMemo(
    () => workPlanData != null || events.some((e) => e.type === 'coordinator.work_plan'),
    [events, workPlanData],
  );

  const planningDescriptor = useMemo<GraphDescriptor | null>(() => {
    if (!effectiveDescriptor) return null;
    const base = effectiveDescriptor;

    const nodesById = new Map(base.nodes.map((node) => [node.id, node]));
    const coordinator = nodesById.get('coordinator') ?? base.nodes[0];
    if (!coordinator) return base;

    const outcomeNode: GraphDescriptor['nodes'][number] = {
      id: 'outcome-plan',
      label: 'Outcome plan',
      role: 'outcome_plan',
      kind: latestOutcomePlanDraftingEvent || latestOutcomePlanEvent || specConfirmed || coordStatusField === 'drafting'
        ? 'live'
        : 'planned',
      node_type: 'action',
    };
    const workNode: GraphDescriptor['nodes'][number] = {
      id: 'work-plan',
      label: 'Work plan',
      role: 'work_plan',
      kind: workPlanSeen ? 'live' : 'planned',
      node_type: 'action',
    };

    const originalNodes = base.nodes.filter((node) => node.id !== 'outcome-plan' && node.id !== 'work-plan');
    const planningUnlocked = specConfirmed || workPlanSeen || (!latestOutcomePlanEvent && originalNodes.some((node) => node.node_type === 'subtask'));
    const downstreamNodes = planningUnlocked ? originalNodes.filter((node) => node.id !== coordinator.id) : [];
    const originalDownstreamIds = new Set(downstreamNodes.map((node) => node.id));
    const originalEdges = planningUnlocked
      ? base.edges.filter((edge) => edge.from !== coordinator.id && edge.to !== coordinator.id && originalDownstreamIds.has(edge.from) && originalDownstreamIds.has(edge.to))
      : [];
    const firstDownstreamIds = planningUnlocked
      ? new Set(base.edges.filter((edge) => !edge.loopback && edge.from === coordinator.id).map((edge) => edge.to))
      : new Set<string>();
    if (planningUnlocked && firstDownstreamIds.size === 0) {
      for (const node of downstreamNodes) firstDownstreamIds.add(node.id);
    }

    const nodes = planningUnlocked
      ? [coordinator, outcomeNode, workNode, ...downstreamNodes]
      : [coordinator, outcomeNode];
    const edges: GraphDescriptor['edges'] = [
      { from: coordinator.id, to: 'outcome-plan', cardinality: 'direct', loopback: false },
    ];
    if (planningUnlocked) {
      edges.push({ from: 'outcome-plan', to: 'work-plan', cardinality: 'direct', loopback: false });
      for (const id of firstDownstreamIds) {
        if (id !== 'outcome-plan' && id !== 'work-plan') {
          edges.push({ from: 'work-plan', to: id, cardinality: firstDownstreamIds.size > 1 ? 'fanout' : 'direct', loopback: false });
        }
      }
      edges.push(...originalEdges);
    }

    return { ...base, start_node_id: coordinator.id, nodes, edges };
  }, [effectiveDescriptor, latestOutcomePlanDraftingEvent, latestOutcomePlanEvent, specConfirmed, workPlanSeen, coordStatusField]);

  // Derived orchestration lifecycle (issues 3 & 4).
  const orch = useMemo<OrchState>(
    () => deriveOrchState(events, coordStatusField, coordStatusReason, workPlanStatus),
    [events, coordStatusField, coordStatusReason, workPlanStatus],
  );
  const viewState = useMemo(
    () => deriveCoordinatorRunViewState(runLevelStatus, orch, runLoadError),
    [runLevelStatus, orch, runLoadError],
  );
  // Derive sandbox backend from sandbox.selected events for the Preview Sandbox button.
  const sandboxBackend = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'sandbox.selected') {
        const backend = evt.payload['backend'] ?? evt.payload['Backend'];
        if (backend) return String(backend);
      }
    }
    return undefined;
  }, [events]);

  // Coordinator graph node status override so it never shows a stale "Pending".
  const coordNodeStatusOverride = orchPhaseToTopoStatus(orch.phase)
    ?? (viewState.bucket === 'failed' || viewState.bucket === 'blocked'
      ? 'failed'
      : viewState.bucket === 'completed'
        ? 'completed'
        : undefined);

  // Topology state for subtask status projection.
  const topology = useMemo(
    () => buildTopologyState(events, topoSeed),
    [events, topoSeed],
  );

  // Per-subtask elapsed timing, derived from the subtask.* coordinator events (which carry a
  // timestamp_utc). Keyed by the raw subtaskId string. startedAt = first dispatched/running;
  // completedAt = first terminal (completed/failed/assemble_ready/rai_flagged). Drives a live counter
  // on each subtask card so the user can see how long it has been running.
  const subtaskTiming = useMemo<Record<string, { startedAt?: number; completedAt?: number }>>(() => {
    const STARTED = new Set(['subtask.dispatched', 'subtask.running', 'subtask.pending_capacity']);
    const TERMINAL = new Set(['subtask.completed', 'subtask.failed', 'subtask.assemble_ready', 'subtask.rai_flagged']);
    const map: Record<string, { startedAt?: number; completedAt?: number }> = {};
    for (const evt of events) {
      if (!STARTED.has(evt.type) && !TERMINAL.has(evt.type)) continue;
      const sid = evt.payload['subtaskId'];
      if (sid == null) continue;
      const key = String(sid);
      const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
      const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
      if (isNaN(tsMs)) continue;
      const cur = map[key] ?? {};
      if (STARTED.has(evt.type)) {
        cur.startedAt = cur.startedAt === undefined ? tsMs : Math.min(cur.startedAt, tsMs);
      } else {
        cur.completedAt = cur.completedAt === undefined ? tsMs : Math.max(cur.completedAt, tsMs);
      }
      map[key] = cur;
    }
    return map;
  }, [events]);

  // Per-assembly-stage elapsed timing (RAI / Review / Merge / Scribe), derived from the
  // coordinator.assembly_* events (which now carry timestamp_utc). Keyed by node ROLE so it can be
  // injected into the generic assembly node state the same way subtaskTiming feeds subtask cards —
  // giving each collective-assembly stage a live count-up timer that survives SSE replay/restart.
  const assemblyTiming = useMemo<Record<string, { startedAt?: number; completedAt?: number }>>(() => {
    const STARTED: Record<string, string> = {
      'coordinator.assembly_rai_started': 'rai',
      'coordinator.assembly_review_requested': 'review',
      'coordinator.assembly_merge_started': 'merge',
      'coordinator.assembly_scribe_started': 'scribe',
    };
    const COMPLETED: Record<string, string> = {
      'coordinator.assembly_rai_completed': 'rai',
      'coordinator.assembly_review_approved': 'review',
      'coordinator.assembly_changes_requested': 'review',
      'coordinator.assembly_declined': 'review',
      'coordinator.assembly_merge_completed': 'merge',
      'coordinator.assembly_merge_failed': 'merge',
      'merge.conflicted': 'merge',
      'coordinator.assembly_scribe_completed': 'scribe',
    };
    const map: Record<string, { startedAt?: number; completedAt?: number }> = {};
    for (const evt of events) {
      const startRole = STARTED[evt.type];
      const doneRole = COMPLETED[evt.type];
      const role = startRole ?? doneRole;
      if (!role) continue;
      const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
      const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
      if (isNaN(tsMs)) continue;
      const cur = map[role] ?? {};
      if (startRole) {
        cur.startedAt = cur.startedAt === undefined ? tsMs : Math.min(cur.startedAt, tsMs);
      } else {
        cur.completedAt = cur.completedAt === undefined ? tsMs : Math.max(cur.completedAt, tsMs);
      }
      map[role] = cur;
    }
    return map;
  }, [events]);
  // reserve room for expanded child pipelines and the container can grow to fit them.
  const [expandedKeys, setExpandedKeys] = useState<Set<string>>(new Set());
  const toggleExpand = useCallback((key: string) => {
    setExpandedKeys((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }, []);
  const expandValue = useMemo<CoordExpandValue>(
    () => ({ expanded: expandedKeys, toggle: toggleExpand }),
    [expandedKeys, toggleExpand],
  );

  // Which coordinator loopback arc (if any) is currently "lit" blue: the review->coordinator
  // "Request changes" arc while a human-review request-changes wave is re-dispatching, or the
  // rai->coordinator "RAI flags" arc while an RAI flag is looping back. Mirrors the graph's
  // active-edge highlight (ActiveEdgeContext). A loop is active when its triggering event is the
  // most recent one that has not yet been superseded by a fresh assembly review / terminal.
  const activeLoopbackId = useMemo<string | undefined>(() => {
    let changesSeq = -1;
    let raiSeq = -1;
    let supersedeSeq = -1;
    for (const e of events) {
      const seq = e.sequence ?? -1;
      const t = e.type as string;
      if (t === 'coordinator.assembly_changes_requested') {
        changesSeq = Math.max(changesSeq, seq);
      } else if (t === 'subtask.rai_flagged') {
        raiSeq = Math.max(raiSeq, seq);
      } else if (
        t === 'coordinator.assembly_review_requested' ||
        t === 'coordinator.assembly_review_approved' ||
        t === 'coordinator.assembly_completed' ||
        t === 'coordinator.assembly_declined' ||
        t === 'coordinator.assembly_failed' ||
        t === 'coordinator.assembly_blocked' ||
        t === 'merge.conflicted'
      ) {
        supersedeSeq = Math.max(supersedeSeq, seq);
      }
    }
    const reviewActive = changesSeq > supersedeSeq && changesSeq >= raiSeq;
    const raiActive = raiSeq > supersedeSeq && raiSeq > changesSeq;
    if (!reviewActive && !raiActive) return undefined;
    if (!effectiveDescriptor) return undefined;
    const wantRole = reviewActive ? 'review' : 'rai';
    const roleById: Record<string, string> = {};
    for (const n of effectiveDescriptor.nodes) roleById[n.id] = (n.role ?? '').toLowerCase();
    const edge = effectiveDescriptor.edges.find(
      (e) => e.loopback && roleById[e.from] === wantRole,
    );
    return edge ? `${edge.from}-${edge.to}` : undefined;
  }, [events, effectiveDescriptor]);


  const { rfNodes, displayEdges } = useMemo<{ rfNodes: Node[]; displayEdges: Edge[] }>(() => {
    if (!planningDescriptor) return { rfNodes: [], displayEdges: [] };

    const fwdEdges: Edge[] = [];
    const allEdges: Edge[] = [];
    // Role lookup so loopback labels are derived from the SOURCE node's role rather than its
    // exact id (robust across descriptor id schemes). Tank adds two coordinator-level loopbacks:
    // rai->coordinator and review->coordinator (loopback:true, no label field on GraphEdge). Render
    // them as labelled back-edges matching the per-run loopback styling. Falls back gracefully when
    // a descriptor has zero loopbacks (older runs) — the loop simply produces no loopback edges.
    const roleById: Record<string, string> = {};
    for (const n of planningDescriptor.nodes) roleById[n.id] = (n.role ?? '').toLowerCase();
    for (const edge of planningDescriptor.edges) {
      const edgeId = `${edge.from}-${edge.to}`;
      if (edge.loopback) {
        allEdges.push(loopbackEdge(edgeId, edge.from, edge.to, coordinatorLoopbackLabel(roleById[edge.from], edge.from)));
      } else {
        const e = forwardEdge(edgeId, edge.from, edge.to);
        fwdEdges.push(e);
        allEdges.push(e);
      }
    }

    const nodeSizeHints: Record<string, NodeSizeHint> = {};
    const raw: Node[] = planningDescriptor.nodes.map((node) => {
      const nt = node.node_type;
      // Subtask cards render taller than the generic hint (multi-line title + role + agent + model +
      // phase + the Expand-pipeline / View-run buttons), so reserve a generous base height to keep
      // sibling fan-out cards from overlapping. Expanded cards reserve extra room for the inline
      // child pipeline so the expansion pushes neighbours apart instead of overlapping them.
      const subtaskExpanded = nt === 'subtask' && expandedKeys.has(node.id);
      const baseHeight = nt === 'subtask' ? 244 : (NODE_TYPE_H[nt ?? ''] ?? NODE_H);
      nodeSizeHints[node.id] = {
        width:  NODE_TYPE_W[nt ?? ''] ?? NODE_W,
        height: baseHeight + (subtaskExpanded ? EXPANDED_PIPELINE_RESERVE : 0),
      };

      const planned = node.kind === 'planned';

      if (nt === 'subtask') {
        // Subtask node — look up topology status by mapped id.
        const topoNode = resolveSubtaskTopoNode(node.id, topology);
        // Defensive: read display fields from flat props OR nested data map.
        const agentField  = node.agent  ?? (node.data?.['agent']  as string | undefined) ?? topoNode?.assignedAgent;
        const modelField  = node.model  ?? (node.data?.['model']  as string | undefined) ?? topoNode?.selectedModelId;
        const phaseField  = node.phase  ?? (node.data?.['phase']  as string | undefined);
        const childRunId  = readChildRunId(node) ?? topoNode?.childRunId;
        // node.id is "plan:subtask-{id}"; the subtask.* timing map is keyed by the raw "{id}".
        const subtaskKey  = node.id.replace(/^plan:/, '').replace(/^subtask-/, '');
        const timing      = subtaskTiming[subtaskKey];
        return {
          id:   node.id,
          type: 'subtask',
          data: {
            graphNodeId:   node.id,
            label:         node.label,
            topoStatus:    topoNode?.status ?? 'pending',
            topoNode,
            childGraphRef: node.child_graph_ref,
            childRunId,
            agent:         agentField,
            agentRole:     agentField ? roleByAgent[agentField] : undefined,
            model:         modelField,
            phase:         phaseField,
            projectId:     projectId ?? '',
            startedAt:     timing?.startedAt,
            completedAt:   timing?.completedAt,
            executionPodName: topoNode?.executionPodName ?? null,
            dir:           'GRID',
          } as SubtaskNodeData,
          position: { x: 0, y: 0 },
        };
      }

      // Coordinator or collective-assembly node — use generic WorkflowNode. def.key MUST be the
      // node ROLE (not node.id), so WorkflowNode's role-based logic fires: the review gate becomes
      // action-required ("Awaiting your review") and the coordinator keeps its "View session" button.
      const roleKey = node.role;
      const coordTopoNode = topology.nodes['coordinator'];

      // Collective-assembly stage status. Two sources combine: the phase projection
      // (assemblyNodeStatus) covers RAI + the human Review gate, but merge/scribe have no distinct
      // orchestration phase, so their started/completed state is taken from the stage's own
      // timing events. Phase status wins when present (it preserves the review "failed"/decline
      // semantics); timing fills in the merge/scribe window so every stage can go live.
      const isAssemblyRole = roleKey === 'rai' || roleKey === 'review' || roleKey === 'merge' || roleKey === 'scribe';
      const at = isAssemblyRole ? assemblyTiming[roleKey] : undefined;
      const terminalStage = node.terminal_stage ?? readStr(node.data ?? {}, ['terminal_stage', 'terminalStage']);
      const terminalOrParkedPhase = orch.phase === 'failed'
        || orch.phase === 'blocked'
        || orch.phase === 'declined'
        || orch.phase === 'needs_resolution';
      const timingStatus: StepStatus | undefined =
        at?.completedAt !== undefined ? 'completed'
        : at?.startedAt !== undefined ? 'started'
        : undefined;
      const phaseStatus = isAssemblyRole && !(planned && terminalOrParkedPhase && !terminalStage)
        ? assemblyNodeStatus(roleKey, orch.phase, terminalStage)
        : undefined;
      // Timing wins once a stage has actually finished: after the user approves the review (or
      // merge/scribe begin), the orchestration phase can linger on `in_review`, which would
      // otherwise keep the Human Review gate showing "Awaiting your review". A real decline still
      // surfaces via phaseStatus === 'failed', which keeps precedence.
      const assemblyStatus = isAssemblyRole
        ? (phaseStatus === 'failed' ? 'failed'
           : timingStatus === 'completed' ? 'completed'
           : (phaseStatus ?? timingStatus))
        : undefined;

      let nodePlanned = planned;
      let stepStatus: StepStatus;
      if (node.id === 'coordinator') {
        stepStatus = topoStatusToStepStatus(coordNodeStatusOverride ?? coordTopoNode?.status ?? 'unknown');
      } else if (node.id === 'outcome-plan') {
        stepStatus = specConfirmed
          ? 'completed'
          : (latestOutcomePlanEvent || latestOutcomePlanDraftingEvent || coordStatusField === 'drafting')
            ? 'started'
            : 'pending';
      } else if (node.id === 'work-plan') {
        stepStatus = workPlanSeen ? 'completed' : 'pending';
      } else if (assemblyStatus !== undefined) {
        stepStatus = assemblyStatus;
        nodePlanned = false; // the stage has been reached; it is live, not planned
      } else {
        stepStatus = 'pending';
      }

      const st: ExecutorState = nodePlanned
        ? { status: 'pending' }
        : { status: stepStatus };

      // Feed the stage's wall-clock timing so the generic WorkflowNode renders a live count-up
      // timer (RAI / Review / Merge / Scribe), matching the subtask cards.
      if (at?.startedAt !== undefined) {
        st.startedAt = at.startedAt;
        st.completedAt = at.completedAt;
      }

      const def: ExecutorDef = {
        key:             roleKey,
        label:           node.label,
        roleDescription: roleDescForRole(node.role),
        Icon:            iconForRole(node.role),
      };

      return {
        id:   node.id,
        type: 'workflow',
        data: {
          def,
          state:     st,
          isPlanned: nodePlanned,
          nodeType:  nt,
          runId:     runId      ?? '',
          executionId: runId    ?? '',
          projectId:   projectId ?? '',
          // Assembly/workflow stages (RAI / Human Review / Merge / Scribe) have their own
          // persisted sub-run streams — carry the child run id so selecting the node in the
          // session tree scopes Activity to that sub-run instead of an empty stream.
          childRunId:    readChildRunId(node),
          childGraphRef: node.child_graph_ref,
          dir:         'GRID',
        } as WorkflowNodeData,
        position: { x: 0, y: 0 },
      };
    });

    const laidOutNodes = layoutDagColumns(
      raw,
      fwdEdges,
      {
        rankdir: 'LR',
        rankSep: 96,
        nodeSep: COORDINATOR_GRAPH_NODE_SEP,
      },
      nodeSizeHints,
    );
    return {
      rfNodes:      laidOutNodes,
      displayEdges: routeGridEdges(allEdges, laidOutNodes),
    };
  }, [planningDescriptor, topology, projectId, runId, coordNodeStatusOverride, orch.phase, subtaskTiming, assemblyTiming, roleByAgent, expandedKeys, latestOutcomePlanDraftingEvent, latestOutcomePlanEvent, specConfirmed, workPlanSeen, coordStatusField]);

  const hasSubtaskNodes = useMemo(
    () => (planningDescriptor?.nodes ?? []).some((n) => n.node_type === 'subtask'),
    [planningDescriptor],
  );
  const outcomePlanDraftingActive = !specConfirmed
    && !latestOutcomePlanEvent
    && !workPlanSeen
    && (orch.phase === 'drafting_outcome' || Boolean(latestOutcomePlanDraftingEvent));
  const inSpecAuthoring = !specConfirmed && !hasSubtaskNodes && (orch.phase === 'unknown' || outcomePlanDraftingActive);

  // While the Coordinator is still drafting the Outcome plan (inSpecAuthoring), the assembly
  // stages (RAI / Human Review / Merge / Scribe) are not yet committed work — no spec confirmed,
  // no subtasks, no orchestration phase. Presenting them as planned pipeline nodes implies a
  // downstream plan that does not exist. Filter them (and edges referencing them) out of the
  // rendered graph until drafting ends, leaving only the live Coordinator node. The descriptor
  // itself is left untouched; this is purely a display-time projection.
  const assemblyNodeIds = useMemo(() => {
    const ids = new Set<string>();
    for (const n of planningDescriptor?.nodes ?? []) {
      const role = (n.role ?? '').toLowerCase();
      if (role === 'rai' || role === 'review' || role === 'merge' || role === 'scribe') ids.add(n.id);
    }
    return ids;
  }, [planningDescriptor]);

  const { displayNodes, displayEdges2 } = useMemo<{ displayNodes: Node[]; displayEdges2: Edge[] }>(() => {
    if (!inSpecAuthoring) return { displayNodes: rfNodes, displayEdges2: displayEdges };
    const filteredNodes = rfNodes.filter((n) => !assemblyNodeIds.has(n.id));
    // Defensive fallback: never render an empty graph box. If filtering would drop every node
    // (e.g. a descriptor with assembly stages but no coordinator node), keep the full graph.
    if (filteredNodes.length === 0) return { displayNodes: rfNodes, displayEdges2: displayEdges };
    const keptIds = new Set(filteredNodes.map((n) => n.id));
    const filteredEdges = displayEdges.filter((e) => keptIds.has(e.source) && keptIds.has(e.target));
    return { displayNodes: filteredNodes, displayEdges2: filteredEdges };
  }, [inSpecAuthoring, rfNodes, displayEdges, assemblyNodeIds]);

  const [outcomePlanClarifying, setOutcomePlanClarifying] = useState(false);

  useEffect(() => {
    if (latestOutcomePlanEvent) queueMicrotask(() => setOutcomePlanClarifying(false));
  }, [latestOutcomePlanEvent]);

  const { sessionTree, sessionNodeIds, defaultSessionNodeId } = useMemo<{
    sessionTree: RunSessionTree[];
    sessionNodeIds: Set<string>;
    defaultSessionNodeId: string | null;
  }>(() => {
    const candidates = displayNodes.filter((node) => {
      // Include every planned subtask — not only dispatched ones (with a childRunId) — so the
      // session tree mirrors the full work plan. Planned/pending subtasks show their status but
      // have no streamed conversation until they are dispatched.
      if (node.type === 'subtask') {
        return true;
      }
      const wfData = node.data as WorkflowNodeData | undefined;
      return wfData?.def?.key === 'coordinator'
        || wfData?.def?.key === 'outcome_plan'
        || wfData?.def?.key === 'work_plan'
        || wfData?.def?.key === 'rai'
        || wfData?.def?.key === 'review'
        || wfData?.def?.key === 'merge'
        || wfData?.def?.key === 'scribe';
    });
    if (candidates.length === 0) {
      return {
        sessionTree: [],
        sessionNodeIds: new Set<string>(),
        defaultSessionNodeId: null,
      };
    }

    // Reading-order rank derived from the graph's rank axis. The run graph lays out
    // left-to-right (LR), so successive ranks increase in X. This rank only informs the
    // sibling reading order below; the tree row indent comes from real nesting depth
    // (see flattenRunTree), not from this value.
    const xValues = [...new Set(candidates.map((node) => Math.round(node.position.x ?? 0)))].sort((a, b) => a - b);
    const depthByRank = new Map<number, number>(xValues.map((x, index) => [x, index]));

    const sessionMeta = new Map<string, {
      nodeId: string;
      label: string;
      agentName?: string;
      agentRole?: string;
      status: string;
      childRunId?: string;
      startedAt?: number;
      completedAt?: number;
      depth: number;
      x: number;
      y: number;
      isCoordinator: boolean;
    }>();

    for (const node of candidates) {
      const x = Math.round(node.position.x ?? 0);
      const y = Math.round(node.position.y ?? 0);
      const depth = depthByRank.get(x) ?? 0;
      if (node.type === 'subtask') {
        const data = node.data as SubtaskNodeData;
        sessionMeta.set(node.id, {
          nodeId: node.id,
          label: data.label,
          agentName: data.agent,
          agentRole: data.agentRole,
          status: String(data.topoStatus ?? 'pending'),
          childRunId: data.childRunId,
          startedAt: data.startedAt,
          completedAt: data.completedAt,
          depth,
          x,
          y,
          isCoordinator: false,
        });
      } else {
        const data = node.data as WorkflowNodeData;
        const status =
          data.def.key === 'outcome_plan'
            ? (outcomePlanClarifying && !specConfirmed ? 'needs_clarification' : specConfirmed ? 'confirmed' : latestOutcomePlanEvent ? 'awaiting_confirmation' : outcomePlanDraftingActive ? 'drafting_outcome' : 'pending')
            : data.def.key === 'work_plan'
              ? (workPlanSeen ? 'completed' : 'pending')
              : data.state.status === 'started' ? 'running'
                : data.state.status === 'completed' ? 'completed'
                  : data.state.status === 'failed' ? 'failed'
                    : data.state.status === 'revise' ? 'rai_flagged'
                      : 'pending';
        // Only the real coordinator/root node defaults its agent name to "Coordinator".
        // Child workflow/assembly stages leave agentName undefined unless they carry a
        // genuine assignment, so the render-level fallback labels them correctly instead
        // of mislabeling every pipeline stage as the Coordinator.
        const isCoordinatorNode = node.id === 'coordinator' || data.def.key === 'coordinator';
        sessionMeta.set(node.id, {
          nodeId: node.id,
          label: data.def.label,
          agentName: data.agentName ?? (isCoordinatorNode ? 'Coordinator' : undefined),
          agentRole: data.agentRoleTitle ?? data.def.roleDescription,
          status,
          // Assembly/workflow stages carry their own sub-run id so selecting RAI / Human Review /
          // Scribe streams the real sub-run instead of falling through to an empty scope.
          childRunId: isCoordinatorNode ? undefined : data.childRunId,
          depth,
          x,
          y,
          isCoordinator: isCoordinatorNode,
        });
      }
    }

    const rootMeta = [...sessionMeta.values()].find((meta) => meta.isCoordinator) ?? [...sessionMeta.values()][0];
    if (!rootMeta) {
      return {
        sessionTree: [],
        sessionNodeIds: new Set<string>(),
        defaultSessionNodeId: null,
      };
    }

    // The coordinator dispatches every subtask directly, so the session tree is flat:
    // one Coordinator root with all subtasks as siblings. Data dependencies between
    // subtasks are shown in the graph, not as session-tree nesting.
    const childIdsByParent = new Map<string, string[]>();
    for (const meta of sessionMeta.values()) {
      if (meta.nodeId === rootMeta.nodeId) continue;
      const list = childIdsByParent.get(rootMeta.nodeId) ?? [];
      list.push(meta.nodeId);
      childIdsByParent.set(rootMeta.nodeId, list);
    }

    const buildTree = (nodeId: string): RunSessionTree => {
      const meta = sessionMeta.get(nodeId)!;
      const children = [...(childIdsByParent.get(nodeId) ?? [])]
        .sort((a, b) => {
          const childA = sessionMeta.get(a)!;
          const childB = sessionMeta.get(b)!;
          return (childA.depth - childB.depth) || (childA.y - childB.y) || (childA.x - childB.x);
        })
        .map((childId) => buildTree(childId));
      return {
        nodeId: meta.nodeId,
        label: meta.label,
        agentName: meta.agentName,
        agentRole: meta.agentRole,
        status: meta.status,
        childRunId: meta.childRunId,
        startedAt: meta.startedAt,
        completedAt: meta.completedAt,
        children,
        depth: meta.depth,
      };
    };

    return {
      sessionTree: [buildTree(rootMeta.nodeId)],
      sessionNodeIds: new Set(sessionMeta.keys()),
      defaultSessionNodeId: rootMeta.nodeId,
    };
  }, [displayNodes, latestOutcomePlanEvent, outcomePlanClarifying, outcomePlanDraftingActive, specConfirmed, workPlanSeen]);

  const flatSessionTree = useMemo(() => flattenRunTree(sessionTree), [sessionTree]);
  const taskRows = flatSessionTree.filter((node) => node.nodeId !== defaultSessionNodeId);
  const taskStatusSummary = taskRows.reduce(
    (acc, node) => {
      const status = node.status;
      if (FAILED_TASK_STATUSES.has(status)) acc.failed += 1;
      else if (BLOCKED_TASK_STATUSES.has(status)) acc.blocked += 1;
      else if (WAITING_TASK_STATUSES.has(status)) acc.waiting += 1;
      else if (PENDING_TASK_STATUSES.has(status)) acc.pending += 1;
      return acc;
    },
    { pending: 0, waiting: 0, blocked: 0, failed: 0 },
  );
  const hasRunningSessionItem = flatSessionTree.some((node) => node.startedAt !== undefined && node.completedAt === undefined);
  const elapsedNow = useTickingNow(hasRunningSessionItem);
  const earliestStart = flatSessionTree.reduce<number | undefined>(
    (min, node) => (node.startedAt == null ? min : min == null ? node.startedAt : Math.min(min, node.startedAt)),
    undefined,
  );
  const elapsedLabel = earliestStart ? fmtTotal(elapsedNow - earliestStart) : '0s';
  const runStatusText = viewState.label;
  const aiCreditsLabel = `${formatAic(tokenBreakdown?.totalNanoAiu ?? null)} AI credits`;
  const taskCountsLabel = `${taskRows.length} tasks · ${taskStatusSummary.pending} pending · ${taskStatusSummary.waiting} waiting`;

  // ---------------------------------------------------------------------------
  // Steering chat side panel (#163) — a slide-in chat replaces the old inline steer bar.
  // ---------------------------------------------------------------------------

  const [planPanelOpen, setPlanPanelOpen] = useState(false);
  const [artifactsPanelOpen, setArtifactsPanelOpen] = useState(false);
  // Run-wide (coordinator-level) collective-diff summary for the Changes chip above the composer.
  const [runChangesSummary, setRunChangesSummary] = useState<{ files: number; added: number; removed: number } | null>(null);
  const [topologyPanelOpen, setTopologyPanelOpen] = useState(false);
  const [topologyView, setTopologyView] = useState<'topology' | 'progress'>('topology');
  const [sessionPanelOpen, setSessionPanelOpen] = useState(true);
  const [panelNodeId, setPanelNodeId] = useState<string | null>(null);
  const [composerFocusSignal, setComposerFocusSignal] = useState(0);
  const [runDetailsOpen, setRunDetailsOpen] = useState(false);
  const lastSelectedOutcomePlanSeqRef = useRef<number | null>(null);

  const openPanelForNode = useCallback((nodeId: string) => {
    setPanelNodeId(nodeId);
    setSessionPanelOpen(true);
  }, []);

  const focusOutcomePlanComposer = useCallback(() => {
    setOutcomePlanClarifying(true);
    setPanelNodeId('outcome-plan');
    setSessionPanelOpen(true);
    setComposerFocusSignal((value) => value + 1);
  }, []);

  useEffect(() => {
    if (!latestOutcomePlanEvent || isChildRun) return;
    if (lastSelectedOutcomePlanSeqRef.current === latestOutcomePlanEvent.sequence) return;
    lastSelectedOutcomePlanSeqRef.current = latestOutcomePlanEvent.sequence;
    queueMicrotask(() => {
      setPanelNodeId('outcome-plan');
      setSessionPanelOpen(true);
    });
  }, [isChildRun, latestOutcomePlanEvent]);

  const activePanelNodeId = panelNodeId && sessionNodeIds.has(panelNodeId) ? panelNodeId : defaultSessionNodeId;
  const selectedSessionItem = flatSessionTree.find((node) => node.nodeId === activePanelNodeId) ?? flatSessionTree[0] ?? null;
  const executingSessionItem = useMemo(() => {
    const nonRoot = flatSessionTree.filter((node) => node.nodeId !== defaultSessionNodeId);
    if (viewState.terminal) {
      if (viewState.bucket === 'failed' || viewState.bucket === 'blocked') {
        return nonRoot.find((node) => FAILED_TASK_STATUSES.has(node.status))
          ?? nonRoot.find((node) => BLOCKED_TASK_STATUSES.has(node.status) || WAITING_TASK_STATUSES.has(node.status))
          ?? nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
          ?? selectedSessionItem
          ?? flatSessionTree[0]
          ?? null;
      }
      return nonRoot.find((node) => semanticStateColorForStatus(node.status) === 'success')
        ?? nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
        ?? selectedSessionItem
        ?? flatSessionTree[0]
        ?? null;
    }
    return nonRoot.find((node) => EXECUTING_TASK_STATUSES.has(node.status))
      ?? nonRoot.find((node) => WAITING_TASK_STATUSES.has(node.status) || BLOCKED_TASK_STATUSES.has(node.status))
      ?? selectedSessionItem
      ?? flatSessionTree[0]
      ?? null;
  }, [defaultSessionNodeId, flatSessionTree, selectedSessionItem, viewState.bucket, viewState.terminal]);
  const executingTaskStatus = executingSessionItem
    ? runTreeStatusLabel(executingSessionItem.status, executingSessionItem.nodeId === 'outcome-plan' ? outcomePlanConfirmedBy : undefined)
    : orchPhaseLabel(orch.phase);
  const executingStateColor = executingSessionItem
    ? semanticStateColorForStatus(executingSessionItem.status)
    : semanticStateColorForBucket(viewState.bucket);
  const runStatusColor = semanticStateColorForBucket(viewState.bucket);
  const executionWorkflowName = selectedWorkflow?.name ?? 'pending';
  const executionWhy = selectedWorkflow?.rationale
    ?? viewState.reason
    ?? (selectedWorkflow
      ? selectedWorkflow.auto
        ? 'Automatically selected by the coordinator'
        : 'Selected for this orchestration'
      : goal
        ? `Goal: ${goal}`
        : orch.phase !== 'unknown'
          ? `Phase: ${orchPhaseLabel(orch.phase)}`
          : `Source: ${viewState.sourceLabel}`);
  const executionTaskLabel = executingSessionItem
    ? `${executingSessionItem.label} (${executingTaskStatus})`
    : orchPhaseLabel(orch.phase);
  const executionKickerLabel = viewState.terminal
    ? runStatusColor === 'danger' ? 'Failed' : 'Finished'
    : viewState.bucket === 'waiting' ? 'Waiting'
      : viewState.bucket === 'pending' ? 'Queued'
        : viewState.bucket === 'blocked' ? 'Blocked'
          : 'Executing';
  const executionTaskPrefix = viewState.terminal && runStatusColor === 'danger' ? 'Last attempted' : 'Task';
  const executionDisplayStateColor = viewState.terminal ? runStatusColor : executingStateColor;
  const executionReasonPrefix = runStatusColor === 'danger' ? 'Failure context' : 'Why';
  const executionContextReason = runStatusColor === 'danger'
    ? (viewState.reason ?? executionWhy)
    : executionWhy;
  const selectedGraphNodeId = selectedSessionItem?.nodeId ?? defaultSessionNodeId;
  const linkedDisplayNodes = useMemo(
    () => displayNodes.map((node) => ({
      ...node,
      selected: node.id === selectedGraphNodeId,
    })),
    [displayNodes, selectedGraphNodeId],
  );

  // Merge "Browse files": route to the project Workspace with the coordinator integration branch
  // selected, so refresh/back preserve the browsed ref and the user lands in the WORK section.
  const browseAssemblyFiles = useCallback(() => {
    if (!projectId || !runId) return;
    const query = new URLSearchParams({
      run: runId,
      ref: `agentweaver/integration/${runId}`,
    });
    navigate(`/projects/${projectId}/workspace?${query.toString()}`);
  }, [navigate, projectId, runId]);

  // Collective-assembly "View execution": RAI/Scribe have their own persisted sub-run streams, so
  // focus that session in the slide-up. For the review node, open the artifacts/review panel where
  // the Approve / Request changes / Decline actions live.
  const viewAssemblyExecution = useCallback((id: string) => {
    if (id.endsWith('-rai') || id.endsWith('-scribe')) openPanelForNode(id);
    else setArtifactsPanelOpen(true);
  }, [openPanelForNode]);

  // Option toggles — optimistic update, revert on error. Both cascade to children server-side.
  const toggleAutopilot = useCallback((next: boolean) => {
    if (!runId || autopilotBusy || !viewState.canToggleAutomation) return;
    setAutomationError(null);
    setAutopilot(next);
    setAutopilotBusy(true);
    apiClient.setAutopilot(runId, next)
      .then((res) => setAutopilot(Boolean(res.autopilot)))
      .catch((err) => {
        setAutopilot(!next);
        setAutomationError(`Autopilot update failed: ${formatApiErrorMessage(err, 'Could not update autopilot.')}`);
      })
      .finally(() => setAutopilotBusy(false));
  }, [runId, autopilotBusy, viewState.canToggleAutomation]);

  const toggleAutoApprove = useCallback((next: boolean) => {
    if (!runId || autoApproveBusy || !viewState.canToggleAutomation) return;
    setAutomationError(null);
    setAutoApprove(next);
    setAutoApproveBusy(true);
    apiClient.setAutoApprove(runId, next)
      .then((res) => setAutoApprove(Boolean(res.auto_approve_tools)))
      .catch((err) => {
        setAutoApprove(!next);
        setAutomationError(`Auto-approve update failed: ${formatApiErrorMessage(err, 'Could not update auto-approve.')}`);
      })
      .finally(() => setAutoApproveBusy(false));
  }, [runId, autoApproveBusy, viewState.canToggleAutomation]);

  const handleRetry = useCallback(async () => {
    if (!runId || !projectId || retrying) return;
    setRetrying(true);
    setRetryError(null);
    setStopError(null);
    try {
      const res = await apiClient.retryRun(runId);
      navigate(`/projects/${projectId}/orchestrations/${res.run_id}`);
    } catch (err) {
      setRetryError(formatApiErrorMessage(err, 'Could not retry this run.'));
      setRetrying(false);
    }
  }, [runId, projectId, retrying, navigate]);

  const handleStopRun = useCallback(async () => {
    if (!runId || stopping) return;
    setStopping(true);
    setStopError(null);
    try {
      await apiClient.steerCoordinator(runId, { kind: 'stop' });
      reconnectStream();
    } catch (err) {
      setStopError(formatApiErrorMessage(err, 'Could not stop this run.'));
    } finally {
      setStopping(false);
    }
  }, [reconnectStream, runId, stopping]);

  const startPreview = () => {
    const port = parseInt(previewTargetPort, 10);
    if (isNaN(port) || port <= 0 || port > 65535) {
      setPreviewError('Enter a valid port number (1–65535).');
      return;
    }
    if (!runId) return;
    setPreviewBusy(true);
    setPreviewError(undefined);
    apiClient.startPortForward(runId, port)
      .then((session) => setPreviewSession(session))
      .catch((err) => setPreviewError(formatApiErrorMessage(err, 'Could not start the sandbox preview.')))
      .finally(() => setPreviewBusy(false));
  };

  const stopPreview = () => {
    if (!runId || !previewSession) return;
    setPreviewBusy(true);
    apiClient.stopPortForward(runId, previewSession.session_id)
      .then(() => setPreviewSession(undefined))
      .catch((err) => setPreviewError(formatApiErrorMessage(err, 'Could not stop the sandbox preview.')))
      .finally(() => setPreviewBusy(false));
  };

  const isKubernetesSandbox = sandboxBackend === 'kubernetes-sandbox-claim';
  const previewUrl = previewSession?.preview_url ?? previewSession?.previewUrl ?? null;
  const keepaliveUrl = previewSession?.keepalive_url ?? previewSession?.keepaliveUrl ?? null;

  useEffect(() => {
    if (!keepaliveUrl) return;
    const id = setInterval(() => {
      apiClient.pingKeepalive(keepaliveUrl)
        .catch((err) => setPreviewError(`Preview keepalive failed: ${formatApiErrorMessage(err, 'The preview connection may expire.')}`));
    }, 60_000);
    return () => clearInterval(id);
  }, [keepaliveUrl]);

  const shortId         = runId && runId.length > 8 ? runId.slice(0, 8) : (runId ?? '');
  const isConnecting    = streamStatus === 'connecting';
  const isStreaming     = streamStatus === 'streaming';
  const hasGraph        = rfNodes.length > 0;
  const isRetryable     = viewState.canRetry;
  const retriedFromShort = retriedFrom ? retriedFrom.slice(0, 8) : null;
  // Auto-size the graph band to its content so it grows as subtask pipelines expand, instead of a
  // fixed height that clips tall fan-outs (horizontal LR layout still varies in height per rank).
  const graphHeight = useMemo(() => {
    if (rfNodes.length === 0) return 200;
    let minY = Infinity;
    let maxY = -Infinity;
    for (const n of rfNodes) {
      const nt = (n.data as { nodeType?: string } | undefined)?.nodeType;
      // Mirror the layout size hints: subtask cards reserve a taller base, plus the inline-pipeline
      // reserve when expanded, so the band grows to exactly contain the (possibly expanded) cards.
      const base = nt === 'subtask' ? 244 : (NODE_TYPE_H[nt ?? ''] ?? NODE_H);
      const h = base + (nt === 'subtask' && expandedKeys.has(n.id) ? EXPANDED_PIPELINE_RESERVE : 0);
      minY = Math.min(minY, n.position.y);
      maxY = Math.max(maxY, n.position.y + h);
    }
    // Loopback arcs (e.g. "RAI flags" above, "Request changes" below) route ~ARC_GAP(40)px plus a
    // label outside the node box on each side. Reserve headroom so fitView leaves room for them
    // instead of clipping the arcs/labels at the band edges.
    const hasLoopback = displayEdges.some((e) => e.type === 'loopback');
    const loopHeadroom = hasLoopback ? 132 : 0;
    return Math.max(180, maxY - minY + 56 + loopHeadroom);
  }, [rfNodes, expandedKeys, displayEdges]);

  const graphViewport = useMemo(() => {
    if (displayNodes.length === 0) {
      return {
        width: '100%',
        height: graphHeight,
        defaultViewport: { x: 0, y: 0, zoom: 1 },
      };
    }
    const paddingX = 64;
    const paddingTop = displayEdges2.some((e) => e.type === 'loopback') ? 132 : 64;
    const paddingBottom = 64;
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const node of displayNodes) {
      const size = graphNodeSize(node);
      minX = Math.min(minX, node.position.x);
      minY = Math.min(minY, node.position.y);
      maxX = Math.max(maxX, node.position.x + size.width);
      maxY = Math.max(maxY, node.position.y + size.height);
    }
    const width = Math.max(dagContainerSize.width || 640, maxX - minX + paddingX * 2);
    const height = Math.max(graphHeight, maxY - minY + paddingTop + paddingBottom);
    return {
      width,
      height,
      defaultViewport: {
        x: paddingX - minX,
        y: paddingTop - minY,
        zoom: 1,
      },
    };
  }, [displayNodes, displayEdges2, graphHeight, dagContainerSize.width]);

  const effectiveGraphZoom = zoom;
  // The toggle/stop endpoints 409 on a non-active run, so only offer them while the run is live.
  const coordActive     = viewState.canStop;

  // A run can be terminally finished at the RUN level (Failed/Declined/Merged) while its WorkPlan
  // status still reads `in_review` — e.g. a run interrupted by a pre-durability build. In that state
  // the in-memory assembly-review gate is NOT armed, so presenting an actionable review bar would
  // 409. Treat the review as actionable only when the run itself is not terminal.
  const runTerminal = viewState.terminal;
  const reviewActionable = orch.phase === 'in_review' && !runTerminal;

  // Map the coordinator orchestration phase onto the standard artifact-browser run status so the
  // reused Changes/Files rail shows the review bar (Approve / Request changes / Decline) exactly when
  // the ONE collective human-review gate is open.
  const coordRunStatus = useMemo(() => {
    if (runTerminal) {
      switch (runLevelStatus) {
        case 'completed':
        case 'merged':       return 'merged';
        case 'declined':     return 'declined';
        case 'failed':
        case 'blocked':
        case 'merge_failed': return 'merge_failed';
        default:             return runLevelStatus ?? 'merge_failed';
      }
    }
    switch (orch.phase) {
      case 'in_review':  return reviewActionable ? 'awaiting_review' : (runLevelStatus ?? 'merge_failed');
      case 'complete':   return 'merged';
      case 'declined':   return 'declined';
      case 'failed':
      case 'blocked':    return 'merge_failed';
      default:           return 'in_progress';
    }
  }, [orch.phase, reviewActionable, runLevelStatus, runTerminal]);

  // Adapter that points the standard artifact browser at the coordinator's collective assembly:
  // files/diff come from the integration branch (the coordinator owns no worktree), and the three
  // review actions are delivered to the collective assembly gate instead of the per-run endpoints.
  const coordAdapter = useMemo<ArtifactBrowserAdapter>(() => ({
    getFiles: (rid, filter) => apiClient.getAssemblyFiles(rid, filter),
    getFileDiff: (rid, path) => apiClient.getAssemblyFileDiff(rid, path),
    getWorkspace: (rid) => apiClient.getAssemblyWorkspace(rid),
    getContent: (rid, path) => apiClient.getAssemblyFileContent(rid, path),
    approve: (rid) => apiClient.reviewAssembly(rid, 'approve'),
    approveLabel: 'Approve & merge',
    approveAriaLabel: 'Approve human review and continue to merge',
    approveAcceptedStatus: 'review_accepted',
    requestChanges: (rid, comment) => apiClient.reviewAssembly(rid, 'request_changes', comment),
    decline: (rid) => apiClient.reviewAssembly(rid, 'decline'),
  }), []);

  // Run-wide changes summary: the coordinator's collective integration diff (assembly files).
  // getAssemblyFiles returns [] before assembly runs, so this stays null until real changes exist.
  // Refetch as the run advances/settles (coordRunStatus) so the Changes chip diff stays live.
  useEffect(() => {
    if (isChildRun || !runId) {
      setRunChangesSummary(null);
      return;
    }
    // Clear stale chips from the previous run immediately so a new runId never briefly shows
    // the prior run's counts while the new getAssemblyFiles is in flight.
    setRunChangesSummary(null);
    let cancelled = false;
    apiClient.getAssemblyFiles(runId)
      .then((entries) => {
        if (cancelled) return;
        if (!entries || entries.length === 0) {
          setRunChangesSummary(null);
          return;
        }
        const added = entries.reduce((sum, e) => sum + (e.added_lines ?? 0), 0);
        const removed = entries.reduce((sum, e) => sum + (e.removed_lines ?? 0), 0);
        setRunChangesSummary({ files: entries.length, added, removed });
      })
      .catch(() => { if (!cancelled) setRunChangesSummary(null); });
    return () => { cancelled = true; };
  }, [isChildRun, runId, coordRunStatus]);

  // Plan chip count = number of planned subtasks in the work plan (real, from the graph descriptor).
  const planItemCount = useMemo(
    () => displayNodes.filter((n) => n.type === 'subtask').length,
    [displayNodes],
  );

  // Run-wide summary chips pinned just above the composer (coordinator scope only). Each chip is
  // shown only when its data exists, and opens the matching run-level SlidePanel overlay. Per-subtask
  // changes remain reachable via the Activity | Changes segmented control (not these chips).
  const runSummaryChips = useMemo<ReactNode>(() => {
    if (isChildRun) return null;
    const chips: ReactNode[] = [];
    if (runChangesSummary) {
      // One "Changes" chip — the run's collective integration diff. Files count + the +A −R delta
      // both derive from the same assembly diff, so we don't split into a duplicate "Artifacts" chip
      // that opens the same panel with a misleading label.
      chips.push(
        <button
          key="changes"
          type="button"
          className={styles.runChip}
          onClick={() => setArtifactsPanelOpen(true)}
          data-testid="run-summary-chip-changes"
          title="Review the collective integration diff for this run"
        >
          <span className={styles.runChipLabel}>Changes</span>
          <span className={styles.runChipCount}>
            {`${runChangesSummary.files.toLocaleString()} ${runChangesSummary.files === 1 ? 'file' : 'files'}`}
          </span>
          <span className={styles.runChipAdded}>+{runChangesSummary.added.toLocaleString()}</span>
          <span className={styles.runChipRemoved}>&minus;{runChangesSummary.removed.toLocaleString()}</span>
        </button>,
      );
    }
    if (planItemCount > 0) {
      chips.push(
        <button
          key="plan"
          type="button"
          className={styles.runChip}
          onClick={() => setPlanPanelOpen(true)}
          data-testid="run-summary-chip-plan"
          title={`${planItemCount} planned ${planItemCount === 1 ? 'task' : 'tasks'} — open the plan`}
          aria-label={`Open the plan: ${planItemCount} planned ${planItemCount === 1 ? 'task' : 'tasks'}`}
        >
          <span className={styles.runChipLabel}>Plan</span>
          <span className={styles.runChipCount}>
            {`${planItemCount} ${planItemCount === 1 ? 'task' : 'tasks'}`}
          </span>
        </button>,
      );
    }
    return chips.length > 0 ? <>{chips}</> : null;
  }, [isChildRun, runChangesSummary, planItemCount, styles]);

  const primaryAction = reviewActionable
    ? {
        label: 'Review changes',
        icon: <DocumentRegular />,
        disabled: false,
        onClick: () => setArtifactsPanelOpen(true),
        testId: 'coordinator-review-changes',
      }
    : latestOutcomePlanEvent && !specConfirmed
      ? {
          label: 'Review outcome plan',
          icon: <DocumentRegular />,
          disabled: false,
          onClick: () => setPlanPanelOpen(true),
          testId: 'coordinator-review-outcome-plan',
        }
      : null;

  const handleAssemblyApproval = useCallback(async (decision: 'approve' | 'decline') => {
    if (!runId) return;
    setAutomationError(null);
    try {
      await apiClient.reviewAssembly(runId, decision);
      reconnectStream();
    } catch (err) {
      setAutomationError(`Assembly review failed: ${formatApiErrorMessage(err, 'Could not update assembly review.')}`);
    }
  }, [reconnectStream, runId]);

  // Assembly review artifacts surfaced inside the approval gate (open the matching center tab).
  const assemblyArtifacts = useMemo<AgentArtifact[]>(() => {
    if (isChildRun) return [];
    return [
      {
        id: 'outcome-plan',
        title: 'Outcome plan',
        type: latestOutcomePlanEvent || specConfirmed ? 'Review artifact' : 'Pending artifact',
        icon: <DocumentRegular />,
        onOpen: () => setPlanPanelOpen(true),
      },
      {
        id: 'assembly-artifacts',
        title: 'Assembly artifacts',
        type: reviewActionable ? 'Review gate' : 'Files',
        icon: <FolderRegular />,
        onOpen: () => setArtifactsPanelOpen(true),
      },
    ];
  }, [isChildRun, latestOutcomePlanEvent, reviewActionable, specConfirmed]);

  // Nested agentic progress tree: coordinator/agents and their tasks with live status.
  const coordinatorProgressSteps = useMemo<AgentStep[]>(() => (
    flatSessionTree.length > 0
      ? flatSessionTree.slice(0, 12).map((item) => {
          const color = semanticStateColorForStatus(item.status);
          const statusLabel = runTreeStatusLabel(item.status, item.nodeId === 'outcome-plan' ? outcomePlanConfirmedBy : undefined);
          const owner = item.agentName
            ? `${item.agentName}${item.agentRole ? ` (${item.agentRole})` : ''}`
            : item.agentRole
              ? `Coordinator (${item.agentRole})`
              : 'Coordinator';
          const step: AgentStep = {
            id: item.nodeId,
            title: item.label,
            body: `${statusLabel} \u00b7 ${owner}`,
            status: semanticStateColorToAgentStatus(color),
            defaultOpen: item.nodeId === selectedSessionItem?.nodeId || color === 'running' || color === 'input',
            needsInput: color === 'input',
            riskText: color === 'input' ? 'This step is waiting on an operator decision or a blocked dependency.' : undefined,
            artifacts: item.childRunId
              ? [{
                  id: `${item.nodeId}-session`,
                  title: item.childRunId,
                  type: 'Child run',
                  icon: <OpenRegular />,
                  onOpen: () => openPanelForNode(item.nodeId),
                }]
              : undefined,
          };
          return step;
        })
      : [{
          id: 'waiting-for-plan',
          title: 'Waiting for coordinator plan',
          body: graphEmptyCopy(isConnecting, noWorkPlan, graphError, viewState).body,
          status: isConnecting || isStreaming ? 'running' : 'pending',
          defaultOpen: true,
        }]
  ), [
    flatSessionTree,
    graphError,
    isConnecting,
    isStreaming,
    noWorkPlan,
    openPanelForNode,
    outcomePlanConfirmedBy,
    selectedSessionItem?.nodeId,
    viewState,
  ]);

  const approvalSteps = useMemo<AgentStep[]>(() => reviewActionable
    ? [{
        id: 'assembly-review',
        title: 'Human review',
        statusBadge: 'Run-level',
        body: 'Review the assembled result and the integration diff, then decide whether to merge and finalize this run.',
        status: 'warning',
        needsInput: true,
        riskText: 'Approve to merge and finalize (Merge \u2192 Scribe), or decline to send it back.',
        disclaimer: 'You can request changes from the Artifacts tab.',
        approveLabel: 'Approve & merge',
        denyLabel: 'Decline',
        artifacts: assemblyArtifacts,
        defaultOpen: true,
      }]
    : [], [reviewActionable, assemblyArtifacts]);

  // ---------------------------------------------------------------------------
  // Messages — the intent-grouped Timeline now lives inside AgentSessionPanel,
  // which owns the scope-aware event stream (coordinator root vs. selected child),
  // the composer/steering, approvals and Changes/Files. The page no longer builds
  // its own timeline model.
  // ---------------------------------------------------------------------------

  const graphEmptyState = graphEmptyCopy(isConnecting, noWorkPlan, graphError, viewState);
  const topologySelectionCopy = selectedSessionItem
    ? `Selected: ${selectedSessionItem.label}`
    : orch.phase !== 'unknown'
      ? orchPhaseLabel(orch.phase)
      : 'No task selected';
  const topologyInspectorContent = (
    <div className={styles.topologyInspector} data-testid="topology-inspector">
      <div className={styles.topologyInspectorSummary}>
        <Text className={styles.hint}>{topologySelectionCopy}</Text>
        <Text className={styles.hint}>Select a node to focus its run messages, changes, and files.</Text>
      </div>
      <TabList
        selectedValue={topologyView}
        onTabSelect={(_, data) => setTopologyView(data.value === 'progress' ? 'progress' : 'topology')}
        aria-label="Topology inspector views"
        size="small"
      >
        <Tab value="topology" icon={<FlowchartRegular />}>Topology</Tab>
        <Tab value="progress" icon={<BotRegular />}>Progress</Tab>
      </TabList>
      {topologyView === 'topology' ? hasGraph ? (
        <ExecutionModalContext.Provider value={viewAssemblyExecution}>
        <BrowseFilesContext.Provider value={browseAssemblyFiles}>
        <ActiveEdgeContext.Provider value={activeLoopbackId}>
        <CoordinatorSessionContext.Provider value={() => openPanelForNode('coordinator')}>
        <CoordExpandContext.Provider value={expandValue}>
        <CoordPanelContext.Provider value={openPanelForNode}>
          <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} onFit={resetZoom} maxZoom={maxZoom} />
          <div
            className={`${styles.dagContainer} ${styles.topologyDag}`}
            ref={setDagViewportRef}
            style={{ overflow: 'auto' }}
            role="region"
            data-testid="topology-scroll-container"
            data-graph-scroll="owned"
            data-pan-enabled="true"
            data-scroll-mode="auto"
            tabIndex={0}
            aria-label="Scrollable topology graph. Drag or scroll to inspect the execution flow."
          >
            <div data-testid="topology-graph-canvas" style={{ zoom: effectiveGraphZoom, width: graphViewport.width, height: graphViewport.height }}>
              <ReactFlow
                key={`${displayNodes.length}:${displayEdges2.length}:${graphHeight}:${dagContainerSize.width}:${dagContainerSize.height}:${[...expandedKeys].sort().join(',')}`}
                nodes={linkedDisplayNodes}
                edges={displayEdges2}
                nodeTypes={coordinatorNodeTypes}
                edgeTypes={workflowEdgeTypes}
                defaultViewport={graphViewport.defaultViewport}
                minZoom={1}
                maxZoom={1}
                nodesDraggable={false}
                nodesConnectable={false}
                nodesFocusable={false}
                edgesFocusable={false}
                panOnScroll
                preventScrolling={false}
                zoomOnScroll={false}
                zoomOnPinch={false}
                zoomOnDoubleClick={false}
                panOnDrag
                style={{ width: graphViewport.width, height: graphViewport.height }}
                onNodeClick={(_, node) => openPanelForNode(node.id)}
                proOptions={{ hideAttribution: true }}
              >
                <MiniMap
                  nodeStrokeWidth={0}
                  nodeBorderRadius={3}
                  zoomable
                  pannable
                  bgColor="var(--colorNeutralBackground2)"
                  maskColor="rgba(0, 0, 0, 0.06)"
                  maskStrokeColor="var(--colorNeutralStroke1)"
                  maskStrokeWidth={2}
                  style={{
                    bottom: 8,
                    right: 8,
                    width: 104,
                    height: 72,
                    border: '1px solid var(--colorNeutralStroke2)',
                    borderRadius: '6px',
                    boxShadow: 'var(--shadow4)',
                  }}
                  nodeColor={(n) => {
                    const s = (n.data as SubtaskNodeData | undefined)?.topoStatus as string | undefined;
                    if (s === 'completed' || s === 'assemble_ready') return '#107c41';
                    if (s === 'running' || s === 'dispatching' || s === 'awaiting_assembly' || s === 'assembling') return '#8c837c';
                    if (s === 'waiting') return '#d47c00';
                    if (s === 'failed' || s === 'declined') return '#c50f1f';
                    return '#b8afa8';
                  }}
                />
              </ReactFlow>
            </div>
          </div>
          {inSpecAuthoring && <Text className={styles.hint}>The execution pipeline appears once you confirm the Outcome plan.</Text>}
        </CoordPanelContext.Provider>
        </CoordExpandContext.Provider>
        </CoordinatorSessionContext.Provider>
        </ActiveEdgeContext.Provider>
        </BrowseFilesContext.Provider>
        </ExecutionModalContext.Provider>
      ) : (
        <EmptyState title={graphEmptyState.title} description={graphEmptyState.body} />
      ) : (
        <AgentStepList steps={coordinatorProgressSteps} aria-label="Coordinator progress" />
      )}
    </div>
  );
  const stateIconClass = (color: SemanticStateColor) => {
    switch (color) {
      case 'running': return styles.runTreeStatusRunning;
      case 'success': return styles.runTreeStatusSuccess;
      case 'danger': return styles.runTreeStatusDanger;
      case 'input': return styles.runTreeStatusInput;
      default: return styles.runTreeStatusQueued;
    }
  };
  const stateTextClass = (color: SemanticStateColor) => {
    switch (color) {
      case 'running': return styles.stateTextRunning;
      case 'success': return styles.stateTextSuccess;
      case 'danger': return styles.stateTextDanger;
      case 'input': return styles.stateTextInput;
      default: return styles.stateTextQueued;
    }
  };
  const treeBranchIds = useMemo(() => {
    const ids: string[] = [];
    const walk = (nodes: RunSessionTree[]) => nodes.forEach((n) => {
      if (n.children.length > 0) { ids.push(n.nodeId); walk(n.children); }
    });
    walk(sessionTree);
    return ids;
  }, [sessionTree]);
  const renderTreeItems = (nodes: RunSessionTree[]): ReactNode => nodes.map((item) => {
    const color = semanticStateColorForStatus(item.status);
    const statusLabel = runTreeStatusLabel(item.status, item.nodeId === 'outcome-plan' ? outcomePlanConfirmedBy : undefined);
    const selected = item.nodeId === activePanelNodeId;
    // Task-first layout: PRIMARY = task title (root/coordinator = "Coordinator").
    // SECONDARY = "{statusLabel} · {agentName} ({role})". Agent identity lives only
    // in the secondary line — no role pill, no "Unassigned agent" bold fallback.
    const isRootNode = item.nodeId === defaultSessionNodeId;
    const primaryText = isRootNode ? 'Coordinator' : item.label;
    const identityName = isRootNode ? 'Coordinator' : item.agentName;
    const identityRole = isRootNode ? (item.agentRole ?? 'Coordinator') : item.agentRole;
    const identityText = identityName
      ? (identityRole ? `${identityName} (${identityRole})` : identityName)
      : (identityRole ?? '');
    const avatarName = identityName ?? item.agentRole ?? item.label;
    const layout = (
      <TreeItemLayout
        iconBefore={(
          <span
            className={mergeClasses(styles.runTreeStatusIcon, stateIconClass(color))}
            data-testid="run-tree-status-icon"
            data-state-color={color}
            style={{ backgroundColor: 'transparent', borderTopStyle: 'none' }}
            aria-hidden="true"
          >
            {runTreeStatusIcon(item.status)}
          </span>
        )}
      >
        <span className={styles.treeNode}>
          <AgentAvatar name={avatarName} size={22} circle />
          <span className={styles.treeText}>
            <span className={styles.treePrimary} title={primaryText}>{primaryText}</span>
            <span className={styles.treeMetaRow}>
              <span
                className={mergeClasses(styles.treeStatusText, stateTextClass(color))}
                data-state-color={color}
              >
                <span className={styles.treeStatusDot} aria-hidden="true" />
                {statusLabel}
              </span>
              {identityText ? (
                <span className={styles.treeIdentity} title={identityText}>{`\u00b7 ${identityText}`}</span>
              ) : null}
            </span>
          </span>
        </span>
      </TreeItemLayout>
    );
    return item.children.length > 0 ? (
      <TreeItem
        key={item.nodeId}
        itemType="branch"
        value={item.nodeId}
        aria-label={`Select ${item.label}: ${statusLabel}`}
        className={selected ? styles.runTreeItemSelected : undefined}
        onClick={() => openPanelForNode(item.nodeId)}
      >
        {layout}
        <Tree>{renderTreeItems(item.children)}</Tree>
      </TreeItem>
    ) : (
      <TreeItem
        key={item.nodeId}
        itemType="leaf"
        value={item.nodeId}
        aria-label={`Select ${item.label}: ${statusLabel}`}
        className={selected ? styles.runTreeItemSelected : undefined}
        onClick={() => openPanelForNode(item.nodeId)}
      >
        {layout}
      </TreeItem>
    );
  });
  const retryHint = isRetryable ? 'Retry resumes failed work' : 'Retry after failure';
  const stopHint = viewState.canStop ? 'Stop cancels run' : 'Stop while running';
  const retryAriaLabel = isRetryable ? 'Retry failed run' : `Retry failed unavailable: ${retryHint}`;
  const stopAriaLabel = viewState.canStop ? 'Stop run' : `Stop run unavailable: ${stopHint}`;

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  if (runLoadError && !restDescriptor && events.length === 0) {
    return (
      <div className={styles.root}>
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
          <span aria-hidden="true">/</span>
          <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
          <span aria-hidden="true">/</span>
          <span>Orchestration {shortId}</span>
        </nav>
        <section className={styles.pageError} aria-live="polite">
          <Display>{viewState.label}</Display>
          <Text className={styles.stateReason}>{runLoadError.message}</Text>
          {runLoadError.detail && <Text className={styles.stateReason}>{runLoadError.detail}</Text>}
          <Text className={styles.phaseSource}>{viewState.sourceLabel}</Text>
          <div className={styles.pageErrorActions}>
            <Button appearance="primary" onClick={() => window.location.reload()}>Refresh</Button>
            <Button appearance="secondary" onClick={() => navigate(`/projects/${projectId}`)}>Back to project</Button>
          </div>
        </section>
      </div>
    );
  }

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
        <span aria-hidden="true">/</span>
        <span>Orchestration {shortId}</span>
      </nav>

      {(retryError || stopError || automationError || workPlanError || (runLoadError && (restDescriptor || events.length > 0)) || streamError || droppedEventCount > 0 || streamStatus === 'error') && (
        <div className={styles.statusBannerStack} aria-live="polite">
          {runLoadError && (restDescriptor || events.length > 0) && (
            <MessageBar intent="warning">
              <MessageBarBody>Run refresh failed: {runLoadError.message}{runLoadError.detail ? ` ${runLoadError.detail}` : ''}</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Reconnect stream</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {workPlanError && (
            <MessageBar intent="warning">
              <MessageBarBody>Work plan refresh failed: {workPlanError.message}{workPlanError.detail ? ` ${workPlanError.detail}` : ''}</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Refresh</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {(streamError || streamStatus === 'error' || droppedEventCount > 0) && (
            <MessageBar intent="warning" data-testid="coordinator-stream-health">
              <MessageBarBody>
                {streamError ? `Live stream issue: ${streamError}` : streamStatus === 'error' ? 'Live stream disconnected.' : 'Live stream dropped older events.'}
                {droppedEventCount > 0 ? ` ${droppedEventCount} event${droppedEventCount === 1 ? '' : 's'} were dropped from the in-memory buffer; refresh for a complete replay.` : ' Refresh or reconnect if the graph looks stale.'}
              </MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" size="small" onClick={reconnectStream}>Reconnect</Button>
              </MessageBarActions>
            </MessageBar>
          )}
          {retryError && (
            <MessageBar intent="error">
              <MessageBarBody>Retry failed: {retryError}</MessageBarBody>
            </MessageBar>
          )}
          {stopError && (
            <MessageBar intent="error">
              <MessageBarBody>Stop failed: {stopError}</MessageBarBody>
            </MessageBar>
          )}
          {automationError && (
            <MessageBar intent="error">
              <MessageBarBody>{automationError}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      )}

      <div className={styles.console} data-testid="run-operator-console">
        <div className={styles.runHeader} data-testid="run-header">
          <div className={styles.identityArea} data-testid="run-summary">
            <div className={styles.topTitleRow}>
              <div className={styles.identityLead}>
                <h1
                  className={styles.titleText}
                  style={{ whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden' }}
                  title={`Orchestration run ${runId}`}
                  data-testid="run-title"
                >
                  Orchestration
                </h1>
                {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Live" />}
                <span
                  className={mergeClasses(styles.statusChip, styles.statusChipStrong, stateTextClass(runStatusColor))}
                  data-testid="run-status-chip"
                  data-state-color={runStatusColor}
                >
                  {runStatusText}
                </span>
              </div>
              <div className={styles.statsStrip} aria-label="Run progress" data-testid="run-progress-chips">
                <span className={styles.statusChip}>{taskCountsLabel}</span>
                {taskStatusSummary.blocked > 0 && (
                  <span className={mergeClasses(styles.statusChip, styles.stateTextInput)} data-state-color="input">
                    {taskStatusSummary.blocked} blocked
                  </span>
                )}
                {taskStatusSummary.failed > 0 && (
                  <span className={mergeClasses(styles.statusChip, styles.stateTextDanger)} data-state-color="danger">
                    {taskStatusSummary.failed} failed
                  </span>
                )}
                <span className={styles.statusChip}>{elapsedLabel} elapsed</span>
                <Popover positioning="below-start">
                  <PopoverTrigger disableButtonEnhancement>
                    <Button appearance="secondary" size="small" className={styles.statusChip}>{aiCreditsLabel}</Button>
                  </PopoverTrigger>
                  <PopoverSurface className={styles.creditsSurface}>
                    <AgentTokenBreakdown data={tokenBreakdown} roleByAgent={roleByAgent} />
                  </PopoverSurface>
                </Popover>
              </div>
              <div className={styles.compactChromeActions}>
                {primaryAction && (
                  <Button
                    appearance="primary"
                    size="small"
                    icon={primaryAction.icon}
                    disabled={primaryAction.disabled}
                    onClick={primaryAction.onClick}
                    data-testid="compact-primary-run-action"
                  >
                    {primaryAction.label}
                  </Button>
                )}
                <Button
                  appearance={isRetryable ? 'secondary' : 'subtle'}
                  size="small"
                  icon={<ArrowRepeatAllRegular />}
                  disabled={!isRetryable || retrying}
                  onClick={() => void handleRetry()}
                  data-testid="coordinator-retry-button"
                  aria-label={retryAriaLabel}
                  title={retryHint}
                />
                <Button
                  appearance={viewState.canStop ? 'secondary' : 'subtle'}
                  size="small"
                  icon={stopping ? <Spinner size="extra-tiny" /> : <DismissRegular />}
                  disabled={!viewState.canStop || stopping}
                  onClick={() => void handleStopRun()}
                  data-testid="coordinator-stop-button"
                  aria-label={stopAriaLabel}
                  title={stopHint}
                />
                <Button
                  appearance="transparent"
                  size="small"
                  icon={<FlowchartRegular />}
                  onClick={() => setTopologyPanelOpen(true)}
                  data-testid="open-topology-panel"
                  aria-label="Topology"
                  title="Topology"
                />
                {isKubernetesSandbox && (
                  <Button
                    appearance="transparent"
                    size="small"
                    icon={<OpenRegular />}
                    onClick={() => { setPreviewDialogOpen(true); setPreviewError(undefined); }}
                    aria-label="Preview Sandbox"
                    title="Preview Sandbox"
                  />
                )}
                <Button
                  appearance="subtle"
                  size="small"
                  aria-expanded={runDetailsOpen}
                  onClick={() => setRunDetailsOpen((value) => !value)}
                  data-testid="run-chrome-toggle"
                >
                  Details
                </Button>
              </div>
            </div>
            <div className={styles.executionContext} data-testid="coordinator-execution-indicator" aria-label={`${executionKickerLabel} workflow ${executionWorkflowName}. ${executionTaskPrefix} ${executionTaskLabel}. ${executionReasonPrefix}: ${executionContextReason}`}>
              <span className={mergeClasses(styles.executionKicker, stateTextClass(executionDisplayStateColor))} data-state-color={executionDisplayStateColor}>{executionKickerLabel}</span>
              <span className={styles.executionSeparator} aria-hidden="true">·</span>
              <span className={styles.executionValue} title={executionWorkflowName}>
                <FlowchartRegular aria-hidden="true" />
                <span>Workflow: {executionWorkflowName}</span>
              </span>
              <span className={styles.executionSeparator} aria-hidden="true">·</span>
              <span className={`${styles.executionValue} ${stateTextClass(executionDisplayStateColor)}`} title={executionTaskLabel} data-state-color={executionDisplayStateColor}>
                {executionTaskPrefix}: {executionTaskLabel}
              </span>
              <span className={styles.executionSeparator} aria-hidden="true">·</span>
              <span className={styles.executionReason} title={executionContextReason}>{executionReasonPrefix}: {executionContextReason}</span>
            </div>
            {runDetailsOpen && <div className={styles.metaRail} aria-label="Run metadata" data-testid="run-metadata">
              <span className={styles.metaItem} title={runId}>
                <span className={styles.metaItemStrong}>Run</span>
                <span className={styles.metaValue}>{shortId}</span>
              </span>
              {selectedWorkflow && (
                <>
                  <span className={styles.metaSeparator} aria-hidden="true">·</span>
                  <Tooltip
                    relationship="description"
                    content={
                      selectedWorkflow.rationale
                        ? `${selectedWorkflow.auto ? 'Auto-selected' : 'Selected'}: ${selectedWorkflow.rationale}`
                        : selectedWorkflow.auto
                          ? 'Automatically selected by the coordinator'
                          : 'Selected for this orchestration'
                    }
                  >
                    <span className={styles.metaItem} data-testid="coordinator-selected-workflow" title={selectedWorkflow.name}>
                      <FlowchartRegular aria-hidden="true" />
                      <span className={styles.metaItemStrong}>{selectedWorkflow.auto ? 'Auto workflow' : 'Workflow'}</span>
                      <span className={styles.metaValue}>{selectedWorkflow.name}</span>
                    </span>
                  </Tooltip>
                </>
              )}
              {!selectedWorkflow && goal && (
                <>
                  <span className={styles.metaSeparator} aria-hidden="true">·</span>
                  <span className={styles.metaItem} title={goal}>
                    <span className={styles.metaItemStrong}>Goal</span>
                    <span className={styles.metaValue}>{goal}</span>
                  </span>
                </>
              )}
              {retriedFromShort && (
                <>
                  <span className={styles.metaSeparator} aria-hidden="true">·</span>
                  <span className={styles.metaItem}>
                    <span className={styles.metaItemStrong}>Retried from</span>
                    <Link to={`/projects/${projectId}/orchestrations/${retriedFrom}`} className={styles.breadcrumbLink}>
                      {retriedFromShort}
                    </Link>
                  </span>
                </>
              )}
              <span className={styles.metaSeparator} aria-hidden="true">·</span>
              <span className={styles.metaItem} title={viewState.sourceLabel} data-testid="run-status-source">
                <span className={styles.metaItemStrong}>Status source:</span>
                <span className={styles.metaValue}>{viewState.sourceLabel}</span>
              </span>
              <span className={styles.metaSeparator} aria-hidden="true">·</span>
              <span className={styles.metaItem}>{formatPhaseUpdated(orch.updatedAt)}</span>
            </div>}
            {runDetailsOpen && (
              <details className={styles.statusDetails} data-testid="run-status-details">
                <summary className={styles.statusDetailsSummary}>Status details</summary>
                <div className={styles.statusDetailsBody}>
                  {viewState.bucket === 'unknown' && (
                    <Text className={styles.phaseSource}>
                      Waiting for a durable coordinator phase instead of assuming the run is running.
                    </Text>
                  )}
                  {viewState.reason && <Text className={styles.stateReason}>{viewState.reason}</Text>}
                </div>
              </details>
            )}
          </div>
        </div>

        <div className={styles.bodyGrid}>
          <aside className={styles.treeRail} aria-label="Run tree">
            <div className={styles.treeRailHeader}>
              <TitleText>Run tree</TitleText>
              <Text className={styles.hint}>{flatSessionTree.length} nodes</Text>
            </div>
            {flatSessionTree.length === 0 ? (
              <div className={styles.treeEmpty}>
                <EmptyState
                  title="Coordinator is still shaping the run"
                  description="The task tree appears after the outcome plan or saved work plan arrives. Keep the stream open; if it stays empty, message the coordinator from Chat or retry a failed run."
                />
              </div>
            ) : (
              <div className={styles.treeScroll}>
                <Tree aria-label="Run tree" size="small" openItems={treeBranchIds}>
                  {renderTreeItems(sessionTree)}
                </Tree>
              </div>
            )}
          </aside>

          <section className={styles.centerZone} aria-label="Selected task">
            <div className={styles.centerHeader}>
              <div className={styles.centerTabRow}>
                {hasGraph && (
                  <div
                    role="button"
                    tabIndex={0}
                    className={styles.minimapButton}
                    aria-label="Open full topology graph"
                    onClick={() => setTopologyPanelOpen(true)}
                    onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); setTopologyPanelOpen(true); } }}
                    data-testid="open-topology-minimap"
                  >
                    <div className={styles.minimapCanvas} aria-hidden="true">
                      <ExecutionModalContext.Provider value={viewAssemblyExecution}>
                      <BrowseFilesContext.Provider value={browseAssemblyFiles}>
                      <ActiveEdgeContext.Provider value={activeLoopbackId}>
                      <CoordinatorSessionContext.Provider value={() => openPanelForNode('coordinator')}>
                      <CoordExpandContext.Provider value={expandValue}>
                      <CoordPanelContext.Provider value={openPanelForNode}>
                        <ReactFlow
                          nodes={linkedDisplayNodes}
                          edges={displayEdges2}
                          nodeTypes={coordinatorNodeTypes}
                          edgeTypes={workflowEdgeTypes}
                          fitView
                          fitViewOptions={{ padding: 0.12 }}
                          nodesDraggable={false}
                          nodesConnectable={false}
                          nodesFocusable={false}
                          edgesFocusable={false}
                          elementsSelectable={false}
                          panOnDrag={false}
                          panOnScroll={false}
                          zoomOnScroll={false}
                          zoomOnPinch={false}
                          zoomOnDoubleClick={false}
                          preventScrolling={false}
                          proOptions={{ hideAttribution: true }}
                          style={{ width: '100%', height: '100%' }}
                        >
                          <MiniMap
                            nodeStrokeWidth={0}
                            nodeBorderRadius={2}
                            pannable={false}
                            zoomable={false}
                            bgColor="var(--colorNeutralBackground2)"
                            maskColor="transparent"
                            nodeColor={(n) => {
                              const s = (n.data as SubtaskNodeData | undefined)?.topoStatus as string | undefined;
                              if (s === 'completed' || s === 'assemble_ready') return '#107c41';
                              if (s === 'running' || s === 'dispatching' || s === 'awaiting_assembly' || s === 'assembling') return '#8c837c';
                              if (s === 'waiting') return '#d47c00';
                              if (s === 'failed' || s === 'declined') return '#c50f1f';
                              return '#b8afa8';
                            }}
                          />
                        </ReactFlow>
                      </CoordPanelContext.Provider>
                      </CoordExpandContext.Provider>
                      </CoordinatorSessionContext.Provider>
                      </ActiveEdgeContext.Provider>
                      </BrowseFilesContext.Provider>
                      </ExecutionModalContext.Provider>
                    </div>
                  </div>
                )}
              </div>
            </div>

            {reviewActionable && approvalSteps.length > 0 && (
              <div className={styles.approvalGateWrap}>
                <AgentStepList
                  steps={approvalSteps}
                  onApprove={() => void handleAssemblyApproval('approve')}
                  onDeny={() => void handleAssemblyApproval('decline')}
                  aria-label="Approvals and gates"
                />
              </div>
            )}

            <div className={styles.centerTabBody}>
              {/* Messages — the single conversation surface. AgentSessionPanel renders the
                  intent-driven Timeline (ChainOfThought steps), the composer/steering with
                  Autopilot/Auto-approve toggles, in-thread approvals and per-scope Changes. */}
              <div className={styles.readoutBody}>
                <AgentSessionPanel
                  variant="docked"
                  open={sessionPanelOpen && Boolean(activePanelNodeId)}
                  selectedNodeId={activePanelNodeId}
                  coordinatorRunId={runId ?? ''}
                  projectId={projectId ?? ''}
                  tree={sessionTree}
                  onClose={() => setSessionPanelOpen(false)}
                  onSelectNode={openPanelForNode}
                  onCoordinatorFollowUp={reconnectStream}
                  coordinatorActive={coordActive}
                  composerFocusSignal={composerFocusSignal}
                  onOutcomePlanClarify={() => setOutcomePlanClarifying(true)}
                  artifactAdapter={coordAdapter}
                  runChips={runSummaryChips}
                  automation={{
                    autopilot,
                    autoApprove,
                    autopilotBusy,
                    autoApproveBusy,
                    canToggle: viewState.canToggleAutomation,
                    onToggleAutopilot: () => toggleAutopilot(!autopilot),
                    onToggleAutoApprove: () => toggleAutoApprove(!autoApprove),
                  }}
                />
              </div>
            </div>
          </section>
        </div>
      </div>

      <SlidePanel
        open={topologyPanelOpen}
        onClose={() => setTopologyPanelOpen(false)}
        title="Topology"
        width="min(1040px, 96vw)"
        bodyClassName={styles.topologyPanelBody}
      >
        {topologyInspectorContent}
      </SlidePanel>

      {!isChildRun && (
        <SlidePanel
          open={planPanelOpen}
          onClose={() => setPlanPanelOpen(false)}
          title="Outcome plan"
          width="min(880px, 96vw)"
        >
          <OutcomePlanPanel
            runId={runId}
            projectId={projectId ?? undefined}
            events={events}
            streamStatus={streamStatus}
            runStatus={runLevelStatus}
            onCollapse={() => setPlanPanelOpen(false)}
            onReconnect={reconnectStream}
            onClarifyPlan={() => { setPlanPanelOpen(false); focusOutcomePlanComposer(); }}
          />
        </SlidePanel>
      )}

      {!isChildRun && runId && (
        <SlidePanel
          open={artifactsPanelOpen}
          onClose={() => setArtifactsPanelOpen(false)}
          title="Artifacts"
          width="min(960px, 96vw)"
        >
          <CoordinatorArtifactsPanel runId={runId} runStatus={coordRunStatus} adapter={coordAdapter} />
        </SlidePanel>
      )}

      {/* Sandbox preview port-forward dialog */}
      <Dialog open={previewDialogOpen} onOpenChange={(_, d) => { if (!d.open) setPreviewDialogOpen(false); }}>
        <DialogSurface style={{ maxWidth: previewUrl ? '900px' : '480px' }}>
          <DialogBody>
            <DialogTitle
              action={
                <Button
                  appearance="subtle"
                  aria-label="Close"
                  icon={<DismissRegular />}
                  onClick={() => setPreviewDialogOpen(false)}
                />
              }
            >
              Sandbox Preview
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, paddingTop: tokens.spacingVerticalM }}>
              {!previewSession ? (
                <>
                  <Text>
                    Preview traffic is proxied through the Agentweaver API server.
                    Enter the port your app is listening on inside the sandbox.
                  </Text>
                  <Field label="Target port (inside sandbox)" validationMessage={previewError} validationState={previewError ? 'error' : 'none'}>
                    <Input
                      type="number"
                      value={previewTargetPort}
                      onChange={(_, d) => setPreviewTargetPort(d.value)}
                      disabled={previewBusy}
                    />
                  </Field>
                </>
              ) : (
                <>
                  <Text>
                    Preview active for port {previewSession.target_port} on pod <code>{previewSession.pod_name}</code>.
                    {previewUrl ? ' The proxied preview is shown below.' : ' The API server did not return a proxied preview URL.'}
                  </Text>
                  {previewUrl && (
                    <iframe
                      title="Sandbox preview"
                      src={previewUrl}
                      referrerPolicy="no-referrer"
                      style={{ width: '100%', minHeight: '360px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 6 }}
                    />
                  )}
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    Session ID: {previewSession.session_id}
                  </Text>
                </>
              )}
            </DialogContent>
            <DialogActions>
              {!previewSession ? (
                <>
                  {previewUrl && (
                    <Button appearance="primary" icon={<OpenRegular />} onClick={() => window.open(previewUrl, '_blank', 'noopener,noreferrer')}>
                      Open preview
                    </Button>
                  )}
                  <Button
                    appearance="primary"
                    onClick={startPreview}
                    disabled={previewBusy}
                    icon={previewBusy ? <Spinner size="extra-tiny" /> : undefined}
                  >
                    Start
                  </Button>
                  <Button appearance="secondary" onClick={() => setPreviewDialogOpen(false)}>Cancel</Button>
                </>
              ) : (
                <>
                  <Button
                    appearance="secondary"
                    onClick={stopPreview}
                    disabled={previewBusy}
                  >
                    Stop
                  </Button>
                  <Button appearance="secondary" onClick={() => setPreviewDialogOpen(false)}>Close</Button>
                </>
              )}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
