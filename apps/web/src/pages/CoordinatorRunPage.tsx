import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  InfoLabel,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowRepeatAllRegular,
  ArrowRoutingRegular,
  BotRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  DismissRegular,
  EditRegular,
  SendRegular,
  StopRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  Handle,
  Position,
  useReactFlow,
  useNodesInitialized,
  type Node,
  type Edge,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream, type RunStreamEvent } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { GraphDescriptor, SteerKind, RunStatus, WorkPlanResponse, CoordinatorChildResponse, TokenUsageSummary } from '../api/types';
import { layoutDag, NODE_W, NODE_H, NODE_TYPE_W, NODE_TYPE_H } from '../utils/dagLayout';
import type { NodeSizeHint } from '../utils/dagLayout';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';
import { AgentAvatar } from '../components/AgentAvatar';
import { CostChip } from '../components/CostChip';
import { AgentRail } from '../components/AgentRail';
import { SteerPanel } from '../components/SteerPanel';
import { AutomationToggle } from '../components/AutomationToggle';
import { AUTOMATION_HELP } from '../components/automationHelp';
import { SteeringLegend } from '../components/SteeringLegend';
import { STEERING_HELP } from '../components/steeringHelp';
import { deriveAgentQueues } from '../api/agentQueues';
import { QuestionAnswerCard } from '../components/QuestionAnswerCard';
import { LifecycleEventCard } from '../components/LifecycleEventCard';
import { Timeline } from '../components/Timeline';
import { useTimelineItems } from '../timeline/useTimelineItems';
import { stripSerializedWorkPlanMessages } from '../timeline/coordinatorPlanFilter';
import { RunLayout } from '../components/RunLayout';
import { RunWatcher } from '../components/RunWatcher';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import {
  workflowNodeTypes,
  forwardEdge,
  loopbackEdge,
  workflowEdgeTypes,
  coordinatorLoopbackLabel,
  roleDescForRole,
  iconForRole,
  useNodeStyles,
  StatusBadge,
  ElapsedTimer,
  CoordinatorSessionContext,
  ExecutionModalContext,
  BrowseFilesContext,
  ActiveEdgeContext,
  type ExecutorDef,
  type ExecutorState,
  type StepStatus,
  type WorkflowNodeData,
} from '../components/WorkflowGraphPanel';
import {
  buildTopologyState,
  initialTopologyState,
  seedTopologyFromWorkPlan,
  type CoordinatorTopologyState,
  type TopologyNodeState,
} from '../state/topologyReducer';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';

// ---------------------------------------------------------------------------
// Steering context — page-level; lets the steer bar trigger the dialog
// ---------------------------------------------------------------------------

interface SteerRequest {
  kind: SteerKind;
}

const CoordSteerContext = createContext<((req: SteerRequest) => void) | undefined>(undefined);

// Subtask pipeline expansion is controlled at the page level so the graph container height can grow
// to fit expanded child pipelines (instead of clipping them inside the fixed-height canvas).
interface CoordExpandValue { expanded: Set<string>; toggle: (key: string) => void; }
const CoordExpandContext = createContext<CoordExpandValue | undefined>(undefined);

// "View run" on a subtask opens the child run in a modal (reusing the standard RunWatcher) rather
// than navigating away from the orchestration.
const CoordViewRunContext = createContext<((runId: string) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Topology status helpers
// ---------------------------------------------------------------------------

function topoStatusToStepStatus(status: string): StepStatus {
  switch (status) {
    case 'dispatched':     return 'started';
    case 'running':        return 'started';
    case 'assemble_ready': return 'completed';
    case 'rai_flagged':    return 'revise';
    case 'completed':      return 'completed';
    case 'failed':         return 'failed';
    default:               return 'pending';
  }
}

function topoStatusToLabel(status: string): string {
  switch (status) {
    case 'dispatched':     return 'Dispatched';
    case 'running':        return 'Running';
    case 'assemble_ready': return 'Awaiting assembly';
    case 'rai_flagged':    return 'RAI flagged';
    case 'completed':      return 'Completed';
    case 'failed':         return 'Failed';
    default:               return 'Pending';
  }
}

/** Map a coordinator graph node id (e.g. 'plan:subtask-1') to its topology node. */
function resolveSubtaskTopoNode(
  graphNodeId: string,
  topology: CoordinatorTopologyState,
): TopologyNodeState | undefined {
  if (topology.nodes[graphNodeId]) return topology.nodes[graphNodeId];
  // Strip 'plan:' prefix: 'plan:subtask-1' → 'subtask-1'
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
  | 'dispatching'
  | 'awaiting_assembly'
  | 'assembling'
  | 'in_review'
  | 'complete'
  | 'failed'
  | 'blocked'
  | 'declined'
  | 'unknown';

interface OrchState {
  phase: OrchPhase;
  reason?: string;
  diff?: string;
  conflictFiles?: string[];
  conflictBranch?: string;
}

// Maps a terminal assembly reason code (integration_conflict, integration_build_error, merge_failed,
// assembly_declined, …) to a human-readable explanation for the blocked/failed card. Falls back to
// the raw reason when unknown so nothing is hidden.
function friendlyAssemblyReason(reason: string | undefined): string {
  // The reason can arrive as the bare event code ("ineligible_subtasks") or as the
  // run-result string ("assembly_blocked: ineligible_subtasks"). Normalize the
  // prefix so both map to the same case.
  const normalized = reason?.replace(/^assembly_blocked:\s*/i, '');
  switch (normalized) {
    case 'ineligible_subtasks':
      return "Assembly can't start because one or more subtasks didn't finish successfully. Every subtask must reach a ready state before the coordinator can assemble the combined result — there is no partial assembly. The blocking subtasks are listed below; reroute to the coordinator to re-run them, or stop the run.";
    case 'integration_conflict':
      return 'Two subtasks changed the same lines, so their branches could not be combined automatically. Resolve by steering the coordinator to re-run the affected subtask(s) against the latest changes, or stop and merge manually.';
    case 'integration_build_error':
      return 'The combined integration branch could not be built (a git/worktree error occurred while assembling the subtask branches).';
    case 'merge_failed':
      return 'Merging the assembled output into your branch failed (a conflict appeared at final merge time).';
    default:
      return reason ? `The collective assembly stopped: ${reason}.` : 'The collective assembly could not complete.';
  }
}

// A subtask that blocked the all-or-nothing assembly gate, surfaced by the
// coordinator.assembly_blocked event payload (LOCKED CONTRACT).
interface IneligibleSubtask {
  id: number;
  title: string;
  status: string;
  agent: string;
}

// Find the latest coordinator.assembly_blocked event and read its
// ineligibleSubtasks list. Returns undefined for older runs that omit it so the
// blocked panel can fall back to the single-message form.
function readIneligibleSubtasks(events: RunStreamEvent[]): IneligibleSubtask[] | undefined {
  let raw: unknown;
  for (const evt of events) {
    if (evt.type === 'coordinator.assembly_blocked') {
      const v = evt.payload['ineligibleSubtasks'] ?? evt.payload['ineligible_subtasks'];
      if (Array.isArray(v)) raw = v;
    }
  }
  if (!Array.isArray(raw)) return undefined;
  return raw.map((item) => {
    const o = (item ?? {}) as Record<string, unknown>;
    return {
      id: typeof o.id === 'number' ? o.id : Number(o.id) || 0,
      title: o.title != null ? String(o.title) : '',
      status: o.status != null ? String(o.status) : '',
      agent: o.agent != null ? String(o.agent) : '',
    };
  });
}

// Bare ineligible subtask ids (older runs may carry only ids, no detail rows).
function readIneligibleSubtaskIds(events: RunStreamEvent[]): number[] | undefined {
  let raw: unknown;
  for (const evt of events) {
    if (evt.type === 'coordinator.assembly_blocked') {
      const v = evt.payload['ineligibleSubtaskIds'] ?? evt.payload['ineligible_subtask_ids'];
      if (Array.isArray(v)) raw = v;
    }
  }
  if (!Array.isArray(raw)) return undefined;
  const ids = raw.map((x) => (typeof x === 'number' ? x : Number(x))).filter((n) => !Number.isNaN(n));
  return ids.length > 0 ? ids : undefined;
}

function humanizeSubtaskStatus(status: string): string {
  if (!status) return '';
  const spaced = status.replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

// Status → badge intent + label for a blocking subtask.
function subtaskStatusBadge(status: string): {
  intent: 'warning' | 'danger' | 'informative' | 'subtle';
  label: string;
} {
  switch (status) {
    case 'rai_flagged':
      return { intent: 'warning', label: 'RAI-flagged' };
    case 'failed':
      return { intent: 'danger', label: 'Failed' };
    case 'running':
      return { intent: 'informative', label: 'Still running' };
    case 'dispatched':
      return { intent: 'informative', label: 'Dispatched' };
    case 'pending':
      return { intent: 'informative', label: 'Pending' };
    default:
      return { intent: 'subtle', label: humanizeSubtaskStatus(status) || status };
  }
}

// One-line per-status hint shown under each blocking subtask row.
function subtaskStatusHint(status: string): string {
  switch (status) {
    case 'rai_flagged':
      return "RAI flagged this subtask's output. Reroute to the coordinator to re-run it against the feedback, or stop.";
    case 'failed':
      return 'This subtask failed. Reroute to the coordinator to retry it, or stop.';
    case 'running':
    case 'dispatched':
    case 'pending':
      return "This subtask hasn't finished yet.";
    default:
      return '';
  }
}

// coordinator.assembly_* event type → phase. These event types may not be emitted
// yet; absence simply means we fall through to the status field / work-plan status.
const ASSEMBLY_EVENT_PHASE: Record<string, OrchPhase> = {
  'coordinator.assembly_started': 'assembling',
  'coordinator.assembly_review_requested': 'in_review',
  'coordinator.assembly_changes_requested': 'dispatching', // re-dispatch resets the phase
  'coordinator.assembly_completed': 'complete',
  'coordinator.assembly_failed': 'failed',
  'coordinator.assembly_blocked': 'blocked',
  'coordinator.assembly_declined': 'declined',
};

function normalizePhase(raw: string | undefined | null): OrchPhase {
  if (!raw) return 'unknown';
  const k = raw.toLowerCase().replace(/[^a-z]/g, '');
  if (k.includes('awaitingassembly')) return 'awaiting_assembly';
  if (k.includes('assembling')) return 'assembling';
  if (k.includes('inreview')) return 'in_review';
  if (k.includes('complete')) return 'complete';
  if (k.includes('fail')) return 'failed';
  if (k.includes('block')) return 'blocked';
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

// Priority: live assembly_* events (last wins) > coordinator_status field > work-plan status.
function deriveOrchState(
  events: RunStreamEvent[],
  statusField: string | undefined,
  reasonField: string | undefined,
  workPlanStatus: string | undefined,
): OrchState {
  let winner: { phase: OrchPhase; payload: Record<string, unknown> } | undefined;
  for (const evt of events) {
    const phase = ASSEMBLY_EVENT_PHASE[evt.type as string];
    if (phase) winner = { phase, payload: evt.payload };
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
    };
  }
  const fieldPhase = normalizePhase(statusField);
  if (fieldPhase !== 'unknown') return { phase: fieldPhase, reason: reasonField ?? undefined };
  const wpPhase = normalizePhase(workPlanStatus);
  if (wpPhase !== 'unknown') return { phase: wpPhase };
  return { phase: 'unknown' };
}

// Coordinator graph node status (so it never shows a stale "Pending").
function orchPhaseToTopoStatus(phase: OrchPhase): string | undefined {
  switch (phase) {
    case 'complete': return 'completed';
    case 'failed':
    case 'blocked':
    case 'declined': return 'failed';
    case 'unknown': return undefined;
    default: return 'running';
  }
}

// Collective-assembly stage node status, derived from the orchestration phase. Assembly is
// automated EXCEPT the Human Review gate, which waits on the user: during `in_review` the review
// node becomes 'started' so WorkflowNode renders it action-required ("Awaiting your review").
// Returns undefined for stages not yet reached so the backend planned/live kind is preserved.
// role ∈ {rai, review, merge, scribe}.
function assemblyNodeStatus(role: string, phase: OrchPhase): StepStatus | undefined {
  switch (phase) {
    case 'assembling':
      return role === 'rai' ? 'started' : undefined;
    case 'in_review':
      if (role === 'rai')    return 'completed';
      if (role === 'review') return 'started';
      return undefined;
    case 'complete':
      return 'completed';
    case 'declined':
      if (role === 'review') return 'failed';
      if (role === 'rai')    return 'completed';
      return undefined;
    default:
      return undefined;
  }
}

function orchPhaseLabel(phase: OrchPhase): string {
  switch (phase) {
    case 'dispatching':       return 'Dispatching';
    case 'awaiting_assembly': return 'Awaiting assembly';
    case 'assembling':        return 'Assembling';
    case 'in_review':         return 'In review';
    case 'complete':          return 'Complete';
    case 'failed':            return 'Failed';
    case 'blocked':           return 'Blocked';
    case 'declined':          return 'Declined';
    default:                  return 'Running';
  }
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
function AggregateElapsed({ states }: { states: Record<string, ExecutorState> }) {
  const hasRunning = Object.values(states).some((st) => st.startedAt !== undefined && st.completedAt === undefined);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!hasRunning) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [hasRunning]);
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
  /** Layout direction for handle placement. 'LR' (default) = left/right; 'TB' = top/bottom. */
  dir?: 'LR' | 'TB';
}

// Fallback child pipeline defs (used when a child run's graph descriptor is not yet available).
const INLINE_CHILD_FALLBACK: ExecutorDef[] = [
  { key: 'agent',          label: 'Agent',          roleDescription: 'AI Assistant',                Icon: iconForRole('agent')    },
  { key: 'rai',            label: 'Rai',             roleDescription: 'RAI Reviewer',                Icon: iconForRole('rai')      },
  { key: 'assemble-ready', label: 'Assemble-ready',  roleDescription: 'Awaiting collective assembly', Icon: iconForRole('assembly') },
];

// Vertical space (px) a subtask node reserves below its body when its child pipeline is expanded,
// so dagre spaces sibling subtasks apart instead of letting the expansion overlap neighbours.
const EXPANDED_PIPELINE_RESERVE = 188;

// Dagre's nodesep is the vertical gap between sibling nodes in LR layout. Subtask cards can be
// taller than the generic hints because their titles/metadata wrap, so keep a generous separation
// for fan-out columns.
const COORDINATOR_GRAPH_NODE_SEP = 96;

// Refits the graph to the viewport AFTER React Flow has measured the node DOM. The bare `fitView`
// prop only fits once at mount using estimated sizes, so on the initial pre-spec load the wide
// linear chain (Coordinator → RAI → Review → Merge → Scribe) was fitted before measurement and the
// last node (Scribe) ended up clipped off the right edge. Re-fitting once nodes are initialized —
// and whenever the layout token changes (node/edge count, expansion, height) — keeps the whole
// pipeline in view without leaving stale vertical whitespace.
function GraphAutoFit({ token }: { token: string }) {
  const { fitView } = useReactFlow();
  const initialized = useNodesInitialized();
  useEffect(() => {
    if (!initialized) return;
    const id = requestAnimationFrame(() => {
      void fitView({ padding: 0.12, maxZoom: 1.1, duration: 150 });
    });
    return () => cancelAnimationFrame(id);
  }, [initialized, token, fitView]);
  return null;
}

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

function SubtaskNode({ id, data }: NodeProps) {
  const s = useNodeStyles();
  const d = data as SubtaskNodeData;
  const expandCtx = useContext(CoordExpandContext);
  const viewRun = useContext(CoordViewRunContext);
  const expanded = expandCtx?.expanded.has(id) ?? false;
  const [childDescriptor, setChildDescriptor] = useState<GraphDescriptor | null>(null);
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  // Fetch the child run's graph descriptor only when expanded.
  useEffect(() => {
    if (!expanded || !d.childRunId) return;
    let cancelled = false;
    apiClient.getRunGraph(d.childRunId as string)
      .then((desc) => { if (!cancelled) setChildDescriptor(desc); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [expanded, d.childRunId]);

  // Subscribe to the child run's live SSE events only while expanded; tear down on collapse.
  const childStreamRunId = expanded && d.childRunId ? (d.childRunId as string) : '';
  const { events: childEvents } = useRunStream(childStreamRunId);

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
        map['assemble-ready'] = { status: 'completed', completedAt: !isNaN(tsMs) ? tsMs : Date.now() };
      }
    }
    return map;
  }, [childEvents]);

  // Build the ordered list of child pipeline nodes: from the descriptor when available,
  // or from the hardcoded fallback while the fetch is in-flight or unavailable.
  const childNodes = useMemo<Array<{ def: ExecutorDef; state: ExecutorState }>>(() => {
    const defs = childDescriptor
      ? childDescriptor.nodes.map((n) => ({
          key:             n.id,
          label:           n.label,
          roleDescription: roleDescForRole(n.role),
          Icon:            iconForRole(n.role),
        }))
      : INLINE_CHILD_FALLBACK;
    return defs.map((def) => ({
      def,
      state: childStepStates[def.key] ?? { status: 'pending' },
    }));
  }, [childDescriptor, childStepStates]);

  const stepStatus = topoStatusToStepStatus(d.topoStatus as string);
  const statusLabel = topoStatusToLabel(d.topoStatus as string);

  return (
    <div
      className={`${s.card} ${s.cardSubtask}${stepStatus === 'started' ? ` ${s.cardActive}` : ''}`}
      data-node-type="subtask"
      role="article"
      aria-label={`${d.label as string}: ${d.topoStatus as string}`}
    >
      <Handle type="target" position={d.dir === 'TB' ? Position.Top : Position.Left} style={handleStyle} />
      <Handle type="source" position={d.dir === 'TB' ? Position.Bottom : Position.Right} style={handleStyle} />

      <div className={s.cardHeader}>
        <CostChip totalNanoAiu={d.totalNanoAiu as number | null | undefined} totalTokens={d.totalTokens as number | null | undefined} />
        <StatusBadge status={stepStatus} label={statusLabel} />
      </div>

      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          {d.agent
            ? <AgentAvatar name={d.agent as string} size={28} circle />
            : <BotRegular fontSize={22} />}
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{d.label as string}</span>
          <span className={s.cardRole}>{(d.agentRole as string | undefined) ?? 'Subtask Agent'}</span>
          {d.agent && <span className={s.cardSubText}>{d.agent as string}</span>}
          {d.model && <span className={s.cardModel}>{d.model as string}</span>}
          {d.phase && <span className={s.cardSubText}>{d.phase as string}</span>}
        </div>
      </div>

      {d.childGraphRef && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button
            appearance="outline"
            size="small"
            aria-label={`${expanded ? 'Collapse' : 'Expand'} pipeline for ${d.label as string}`}
            onClick={() => expandCtx?.toggle(id)}
          >
            {expanded ? 'Collapse pipeline' : 'Expand pipeline'}
          </Button>
          {d.childRunId && (
            <Button
              appearance="outline"
              size="small"
              onClick={() => viewRun?.(d.childRunId as string)}
            >
              View run
            </Button>
          )}
        </div>
      )}

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
          {childNodes.map((node, i) => (
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
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  runIdLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  goal: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  // Graph band — full-width horizontal pipeline above the two columns.
  graphBand: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  agentRailBand: {
    padding: `${tokens.spacingVerticalS} 0`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  // Two-column layout: outcome spec on the LEFT (collapsible), coordinator session on the RIGHT.
  twoCol: {
    display: 'grid',
    gridTemplateColumns: 'minmax(300px, 360px) minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 1024px)': {
      gridTemplateColumns: '1fr',
    },
  },
  twoColCollapsed: {
    display: 'grid',
    gridTemplateColumns: '44px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
  },
  twoColSessionCollapsed: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 44px',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 1024px)': {
      gridTemplateColumns: '1fr',
    },
  },
  leftCol: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    maxHeight: 'calc(100vh - 180px)',
    overflowY: 'auto',
    paddingRight: tokens.spacingHorizontalS,
    '@media (max-width: 1024px)': {
      maxHeight: 'none',
    },
  },
  outcomeHeaderRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  sessionHeaderRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  outcomeRail: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalXS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    minHeight: '160px',
  },
  railLabel: {
    writingMode: 'vertical-rl',
    transform: 'rotate(180deg)',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    letterSpacing: '0.04em',
    userSelect: 'none',
  },
  centerCol: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  sectionTitleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  conflictFiles: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  conflictList: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalL,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  blockedSubtasks: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  blockedSubtaskRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  blockedSubtaskHead: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  blockedSubtaskTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  blockedSubtaskAgent: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  dagContainer: {
    minHeight: '200px',
    width: '100%',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '& .react-flow__renderer': { borderRadius: '8px' },
  },
  steerBar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  steerInput: {
    flex: 1,
    minWidth: '220px',
  },
  viewRunSurface: {
    maxWidth: '92vw',
    width: '1200px',
    padding: tokens.spacingVerticalM,
  },
  viewRunBody: {
    display: 'flex',
    flexDirection: 'column',
    height: '82vh',
    gap: tokens.spacingVerticalS,
  },
  viewRunHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  steerLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  steerScopeNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  steerNote: {
    marginTop: tokens.spacingVerticalXS,
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: tokens.spacingVerticalM,
  },
  actionRequired: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalS,
  },
  toggleGroup: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    rowGap: tokens.spacingVerticalXS,
  },
  sessionToolbar: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalXS} 0`,
  },
  actionSource: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  reviewActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  diffBox: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    maxHeight: '220px',
    overflowY: 'auto',
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusSmall,
    padding: tokens.spacingVerticalS,
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

function steerKindLabel(kind: SteerKind): string {
  if (kind === 'stop') return 'Stop';
  if (kind === 'send') return 'Send';
  if (kind === 'redirect') return 'Redirect';
  if (kind === 'amend') return 'Amend';
  return 'Steer';
}

// Maps a successful inline steer response status to a compact confirmation line.
function steerStatusMessage(status: string): string {
  if (status === 'applied') return 'Applied — re-running the affected work with your guidance.';
  if (status === 'queued') return 'Queued — applies at the next step.';
  return 'Steering message sent.';
}

// Verbatim explanation for the steer info affordance (visible InfoLabel, not hover-only).
const STEER_INFO =
  `Sends a course-correction to the coordinator. It applies at the next step of the affected subtasks, or resumes the run if it's parked (blocked or failed). Targets all active subtasks. Send: ${STEERING_HELP.send} Redirect: ${STEERING_HELP.redirect} Amend: ${STEERING_HELP.amend}`;

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();
  const navigate = useNavigate();

  const { events, status: streamStatus, reconnect: reconnectStream } = useRunStream(runId ?? '');

  // Ctrl+Scroll zoom for the orchestration graph, mirroring WorkflowRunPage.
  const { zoom, zoomIn, zoomOut, viewportRef, maxZoom } = useCtrlScrollZoom({ maxZoom: 2 });

  // REST seed: coordinator GraphDescriptor (GET /api/runs/{id}/graph, coordinator variant).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);

  // Topology seed from work plan + children (for subtask status projection).
  const [topoSeed, setTopoSeed] = useState(initialTopologyState);

  // Agent name → role title, fetched from the project team roster, so a subtask card can show the
  // assigned agent's ROLE (e.g. "Repo Auditor") and not just their cast name (e.g. "Deckard").
  const [roleByAgent, setRoleByAgent] = useState<Record<string, string>>({});

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    // Fetch graph descriptor for REST seed (so finished coordinator runs still render).
    apiClient.getRunGraph(runId)
      .then((desc) => { if (!cancelled) setRestDescriptor(desc); })
      .catch(() => {});

    // Fetch work plan + children for topology status seed + AgentRail.
    void (async () => {
      const [workPlan, children] = await Promise.all([
        apiClient.getWorkPlan(runId).catch(() => null),
        apiClient.getCoordinatorChildren(runId).catch(() => null),
      ]);
      if (cancelled) return;
      if (workPlan) {
        setTopoSeed(seedTopologyFromWorkPlan(workPlan, children));
        setWorkPlanData(workPlan);
        setChildrenData(children ?? []);
      }
    })();

    return () => { cancelled = true; };
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


  // ---------------------------------------------------------------------------
  // Orchestration lifecycle poll (issues 3 & 4). Reads the coordinator_status field
  // (added by the backend concurrently — optional) plus the work-plan status, both
  // tolerated as absent. Polls until the orchestration reaches a terminal phase.
  // ---------------------------------------------------------------------------
  const [coordStatusField, setCoordStatusField] = useState<string | undefined>(undefined);
  const [coordStatusReason, setCoordStatusReason] = useState<string | undefined>(undefined);
  const [workPlanStatus, setWorkPlanStatus] = useState<string | undefined>(undefined);
  // Actual run-level RunStatus (distinct from the WorkPlan/orchestration phase). A run can be
  // terminally Failed/Declined at the run level while its WorkPlan.Status is still `in_review`
  // (e.g. a run interrupted by an old build before the durability fix): the in-memory assembly
  // gate is NOT armed, so showing an actionable review bar would 409. We use this to suppress the
  // review affordance for a terminal run and show its failure reason instead.
  const [runLevelStatus, setRunLevelStatus] = useState<RunStatus | undefined>(undefined);
  const [retriedFrom, setRetriedFrom] = useState<string | null>(null);
  // Per-run work-plan + children snapshot — used to drive the AgentRail.
  const [workPlanData, setWorkPlanData] = useState<WorkPlanResponse | null>(null);
  const [childrenData, setChildrenData] = useState<CoordinatorChildResponse[]>([]);
  const [coordUsage, setCoordUsage] = useState<TokenUsageSummary | null>(null);
  const [childUsageByRun, setChildUsageByRun] = useState<Record<string, TokenUsageSummary>>({});

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    apiClient.getRunUsage(runId)
      .then((usage) => { if (!cancelled) setCoordUsage(usage); })
      .catch(() => { if (!cancelled) setCoordUsage(null); });
    return () => { cancelled = true; };
  }, [runId]);

  useEffect(() => {
    let cancelled = false;
    const childIds = childrenData.map((c) => c.childRunId).filter(Boolean);
    if (childIds.length === 0) { setChildUsageByRun({}); return; }
    void Promise.all(childIds.map(async (id) => {
      try {
        const usage = await apiClient.getRunUsage(id);
        return [id, usage] as const;
      } catch {
        return null;
      }
    })).then((entries) => {
      if (cancelled) return;
      setChildUsageByRun(Object.fromEntries(entries.filter((x): x is readonly [string, TokenUsageSummary] => x !== null)));
    });
    return () => { cancelled = true; };
  }, [childrenData]);
  // True once the work-plan endpoint has confirmed a 404 (run has no plan yet / is stuck).
  // Used to render a graceful empty state and to back off the lifecycle poll so the page
  // doesn't hammer the 404 endpoint on a tight loop.
  const [noWorkPlan, setNoWorkPlan] = useState(false);
  // Retry state for the header button.
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  // Per-run option toggles (autopilot + auto-approve-tools). Seeded once from the run detail,
  // then driven by user toggles (optimistic). Both cascade to the coordinator's children.
  const [autopilot, setAutopilot] = useState(false);
  const [autoApprove, setAutoApprove] = useState(false);
  const [autopilotBusy, setAutopilotBusy] = useState(false);
  const [autoApproveBusy, setAutoApproveBusy] = useState(false);
  const seededToggles = useRef(false);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const TERMINAL = new Set<OrchPhase>(['complete', 'failed', 'blocked', 'declined']);

    const tick = async () => {
      const detail = await apiClient.getRun(runId).catch(() => null);
      // Fetch the work-plan separately so we can distinguish "no plan yet" (404) from a
      // transient failure. A 404 must NOT be retried on the tight 4s cadence — that spams
      // the endpoint for early/stuck runs that have no plan. We flag it and back off.
      let wp: WorkPlanResponse | null = null;
      let wpMissing = false;
      try {
        wp = await apiClient.getWorkPlan(runId);
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) wpMissing = true;
        wp = null;
      }
      if (cancelled) return;
      setNoWorkPlan(wpMissing);
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
      const phase = normalizePhase(statusField) !== 'unknown'
        ? normalizePhase(statusField)
        : normalizePhase(wpStatus);
      if (!TERMINAL.has(phase)) {
        // Back off substantially while the work plan is absent (404) so we stop hammering
        // the endpoint on a tight loop; resume the normal cadence once a plan exists.
        const delay = wpMissing ? 30000 : 4000;
        timer = setTimeout(() => { void tick(); }, delay);
      }
    };

    void tick();
    return () => { cancelled = true; if (timer) clearTimeout(timer); };
  }, [runId]);

  // Goal is carried by the coordinator.started event.
  const goal = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'coordinator.started' && typeof evt.payload['goal'] === 'string') {
        return evt.payload['goal'] as string;
      }
    }
    return undefined;
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

  // Derived orchestration lifecycle (issues 3 & 4).
  const orch = useMemo<OrchState>(
    () => deriveOrchState(events, coordStatusField, coordStatusReason, workPlanStatus),
    [events, coordStatusField, coordStatusReason, workPlanStatus],
  );

  // Coordinator graph node status override so it never shows a stale "Pending".
  const coordNodeStatusOverride = orchPhaseToTopoStatus(orch.phase);

  // Blocking subtasks behind an all-or-nothing assembly gate (ineligible_subtasks).
  const ineligibleSubtasks = useMemo(() => readIneligibleSubtasks(events), [events]);
  const ineligibleSubtaskIds = useMemo(() => readIneligibleSubtaskIds(events), [events]);

  // Session run — reuse the standard rich run Timeline over the coordinator's OWN event stream,
  // so the coordinator session reads like every other agent's "view run" (turn groups, tool cards,
  // agent messages) instead of a bespoke milestone list.
  const { items: coordItemsRaw, runOutcome: coordRunOutcome } = useTimelineItems(events, runId ?? '');
  // Suppress the decompose agent's serialized work-plan JSON message: the structured work plan is
  // already surfaced by the "Decomposed into N subtasks" lifecycle chip + the work-plan panel/graph,
  // so the raw JSON array must not be dumped verbatim into the session timeline.
  const coordItems = useMemo(() => stripSerializedWorkPlanMessages(coordItemsRaw), [coordItemsRaw]);
  const liveRun = streamStatus === 'connecting' || streamStatus === 'streaming';

  // Outcome column collapse — fold the spec to a thin left rail to give the session room.
  const [outcomeCollapsed, setOutcomeCollapsed] = useState(false);

  // Auto-collapse the outcome spec once it is confirmed (dispatch is unblocked from that point),
  // freeing horizontal space for the session. Only auto-fires once so the user can re-expand.
  const specConfirmed = useMemo(
    () => events.some((e) => e.type === 'coordinator.outcome_spec.confirmed'),
    [events],
  );
  const autoCollapsedRef = useRef(false);
  useEffect(() => {
    if (specConfirmed && !autoCollapsedRef.current) {
      autoCollapsedRef.current = true;
      setOutcomeCollapsed(true);
    }
  }, [specConfirmed]);

  // Session column collapse — symmetric to the outcome rail, but folds to a thin RIGHT rail. While
  // the outcome spec is still being authored (no work plan yet) the session has nothing useful to
  // show, so it defaults collapsed to hand the outcome panel the full width; it auto-expands once the
  // orchestration moves past spec authoring (spec confirmed, subtasks dispatched, or any orch phase).
  // A manual toggle takes over from the automatic default.
  const hasSubtaskNodes = useMemo(
    () => (effectiveDescriptor?.nodes ?? []).some((n) => n.node_type === 'subtask'),
    [effectiveDescriptor],
  );
  const inSpecAuthoring = !specConfirmed && !hasSubtaskNodes && orch.phase === 'unknown';
  const [sessionCollapseOverride, setSessionCollapseOverride] = useState<boolean | null>(null);
  const sessionCollapsed = sessionCollapseOverride ?? inSpecAuthoring;

  // Bubbled child questions + tool-approval requests re-projected onto the coordinator stream
  // (issue: make it easy to answer/approve from the all-up view). Each item records the source
  // childRunId + subtaskId so the answer/grant is routed to the CHILD that asked, not the
  // coordinator run. Questions collapse once an agent.question_answered for the same requestId is
  // re-projected (or optimistically, inside QuestionAnswerCard). Defensive payload key reads.
  const childRequests = useMemo<Array<
    | { type: 'question'; requestId: string; childRunId: string; subtaskId?: string; question: string; answer?: string; timedOut?: boolean; seq: number }
    | { type: 'approval'; requestId: string; childRunId: string; subtaskId?: string; toolName: string; url?: string; message?: string; seq: number }
  >>(() => {
    const questions = new Map<string, { childRunId: string; subtaskId?: string; question: string; seq: number }>();
    const approvals = new Map<string, { childRunId: string; subtaskId?: string; toolName: string; url?: string; message?: string; seq: number }>();
    const answered = new Map<string, { answer: string; timedOut: boolean }>();
    for (const evt of events) {
      const p = evt.payload;
      if (evt.type === 'coordinator.child_question') {
        const requestId = readStr(p, ['requestId', 'request_id']);
        const childRunId = readStr(p, ['childRunId', 'child_run_id']);
        if (!requestId || !childRunId) continue;
        questions.set(requestId, {
          childRunId,
          subtaskId: readStr(p, ['subtaskId', 'subtask_id']),
          question: readStr(p, ['question']) ?? '',
          seq: evt.sequence,
        });
      } else if (evt.type === 'coordinator.child_approval_required') {
        const requestId = readStr(p, ['requestId', 'request_id']);
        const childRunId = readStr(p, ['childRunId', 'child_run_id']);
        if (!requestId || !childRunId) continue;
        approvals.set(requestId, {
          childRunId,
          subtaskId: readStr(p, ['subtaskId', 'subtask_id']),
          toolName: readStr(p, ['toolName', 'tool_name']) ?? 'unknown',
          url: readStr(p, ['url']),
          message: readStr(p, ['message']),
          seq: evt.sequence,
        });
      } else if (evt.type === 'agent.question_answered') {
        const requestId = readStr(p, ['requestId', 'request_id']);
        if (!requestId) continue;
        answered.set(requestId, {
          answer: readStr(p, ['answer']) ?? '',
          timedOut: Boolean(p['timedOut'] ?? p['timed_out'] ?? false),
        });
      }
    }
    const out: Array<
      | { type: 'question'; requestId: string; childRunId: string; subtaskId?: string; question: string; answer?: string; timedOut?: boolean; seq: number }
      | { type: 'approval'; requestId: string; childRunId: string; subtaskId?: string; toolName: string; url?: string; message?: string; seq: number }
    > = [];
    for (const [requestId, q] of questions) {
      const ans = answered.get(requestId);
      out.push({ type: 'question', requestId, ...q, answer: ans?.answer, timedOut: ans?.timedOut });
    }
    for (const [requestId, a] of approvals) {
      out.push({ type: 'approval', requestId, ...a });
    }
    return out.sort((x, y) => x.seq - y.seq);
  }, [events]);

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
    const STARTED = new Set(['subtask.dispatched', 'subtask.running']);
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

  // Which coordinator loopback arc (if any) is currently "lit" blue: the review→coordinator
  // "Request changes" arc while a human-review request-changes wave is re-dispatching, or the
  // rai→coordinator "RAI flags" arc while an RAI flag is looping back. Mirrors the per-run page's
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
        t === 'coordinator.assembly_blocked'
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
    if (!effectiveDescriptor) return { rfNodes: [], displayEdges: [] };

    const fwdEdges: Edge[] = [];
    const allEdges: Edge[] = [];
    // Role lookup so loopback labels are derived from the SOURCE node's role rather than its
    // exact id (robust across descriptor id schemes). Tank adds two coordinator-level loopbacks:
    // rai→coordinator and review→coordinator (loopback:true, no label field on GraphEdge). Render
    // them as labelled back-edges matching the per-run loopback styling. Falls back gracefully when
    // a descriptor has zero loopbacks (older runs) — the loop simply produces no loopback edges.
    const roleById: Record<string, string> = {};
    for (const n of effectiveDescriptor.nodes) roleById[n.id] = (n.role ?? '').toLowerCase();
    for (const edge of effectiveDescriptor.edges) {
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
    const raw: Node[] = effectiveDescriptor.nodes.map((node) => {
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
        const childRunId  = node.child_run_id ?? (node.data?.['child_run_id'] as string | undefined) ?? topoNode?.childRunId;
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
            totalNanoAiu:  childRunId ? childUsageByRun[childRunId]?.total_nano_aiu : undefined,
            totalTokens:   childRunId ? childUsageByRun[childRunId]?.total_tokens : undefined,
            dir:           'LR',
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
      const timingStatus: StepStatus | undefined =
        at?.completedAt !== undefined ? 'completed'
        : at?.startedAt !== undefined ? 'started'
        : undefined;
      const phaseStatus = isAssemblyRole ? assemblyNodeStatus(roleKey, orch.phase) : undefined;
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
        stepStatus = topoStatusToStepStatus(coordNodeStatusOverride ?? coordTopoNode?.status ?? 'running');
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
          dir:         'LR',
          totalNanoAiu: node.id === 'coordinator' ? coordUsage?.total_nano_aiu : undefined,
          totalTokens:  node.id === 'coordinator' ? coordUsage?.total_tokens : undefined,
        } as WorkflowNodeData,
        position: { x: 0, y: 0 },
      };
    });

    return {
      rfNodes:      layoutDag(raw, fwdEdges, { rankdir: 'LR', rankSep: 64, nodeSep: COORDINATOR_GRAPH_NODE_SEP }, nodeSizeHints),
      displayEdges: allEdges,
    };
  }, [effectiveDescriptor, topology, projectId, runId, coordNodeStatusOverride, orch.phase, subtaskTiming, assemblyTiming, roleByAgent, expandedKeys, childUsageByRun, coordUsage]);

  // While the Coordinator is still drafting the outcome spec (inSpecAuthoring), the assembly
  // stages (RAI / Human Review / Merge / Scribe) are not yet committed work — no spec confirmed,
  // no subtasks, no orchestration phase. Presenting them as planned pipeline nodes implies a
  // downstream plan that does not exist. Filter them (and edges referencing them) out of the
  // rendered graph until drafting ends, leaving only the live Coordinator node. The descriptor
  // itself is left untouched; this is purely a display-time projection.
  const assemblyNodeIds = useMemo(() => {
    const ids = new Set<string>();
    for (const n of effectiveDescriptor?.nodes ?? []) {
      const role = (n.role ?? '').toLowerCase();
      if (role === 'rai' || role === 'review' || role === 'merge' || role === 'scribe') ids.add(n.id);
    }
    return ids;
  }, [effectiveDescriptor]);

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

  // ---------------------------------------------------------------------------
  // Steering dialog
  // ---------------------------------------------------------------------------

  const [steerReq, setSteerReq] = useState<SteerRequest | null>(null);
  const [instruction, setInstruction] = useState('');
  const [busy, setBusy] = useState(false);
  const [steerError, setSteerError] = useState<string | null>(null);

  const openSteer = useCallback((req: SteerRequest) => {
    setSteerReq(req);
    setInstruction('');
    setSteerError(null);
  }, []);

  const closeSteer = useCallback(() => {
    setSteerReq(null);
    setInstruction('');
    setSteerError(null);
    setBusy(false);
  }, []);

  const submitSteer = useCallback(async () => {
    if (!steerReq || !runId) return;
    setBusy(true);
    setSteerError(null);
    try {
      await apiClient.steerCoordinator(runId, {
        kind: steerReq.kind,
        instruction: steerReq.kind === 'stop' ? undefined : instruction.trim() || undefined,
      });
      closeSteer();
    } catch (err) {
      setSteerError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
      setBusy(false);
    }
  }, [steerReq, instruction, runId, closeSteer]);

  // ---------------------------------------------------------------------------
  // Session panel anchor — the coordinator node's "View session" scrolls here.
  const sessionRef = useRef<HTMLDivElement>(null);
  const scrollToSession = useCallback(() => {
    // If the session column is collapsed, expand it first so the ref is in the DOM.
    setSessionCollapseOverride(false);
    // Defer scroll by one animation frame so React re-renders the ref'd element before scrolling.
    requestAnimationFrame(() => {
      sessionRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  }, []);

  // Review/Changes panel anchor — the Human Review gate's "Review now" scrolls here.
  const reviewRef = useRef<HTMLDivElement>(null);
  const scrollToReview = useCallback(() => {
    reviewRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, []);

  // Merge "Browse files": route to the project Workspace with the coordinator integration branch
  // selected, so refresh/back preserve the browsed ref and the user lands in the WORK section.
  const [filesFocusSignal] = useState(0);
  const browseAssemblyFiles = useCallback(() => {
    if (!projectId || !runId) return;
    const query = new URLSearchParams({
      run: runId,
      ref: `agentweaver/integration/${runId}`,
    });
    navigate(`/projects/${projectId}/workspace?${query.toString()}`);
  }, [navigate, projectId, runId]);

  // "View run" modal — renders the selected child run (or a collective-assembly sub-run stream
  // such as `${runId}-rai` / `${runId}-scribe`) via the standard RunWatcher in a dialog.
  const [viewRunId, setViewRunId] = useState<string | null>(null);
  const openChildRun = useCallback((id: string) => setViewRunId(id), []);

  // Collective-assembly "View execution": the RAI and Scribe stages run a real agent turn on their
  // own persisted sub-run stream (`${runId}-rai` / `${runId}-scribe`), so open that stream in the
  // RunWatcher dialog to surface the actual work (tool calls, inbox review, memory writes) — same
  // pattern the per-run page uses. Merge "Browse files" and Review "Review now" own no separate run,
  // so they jump to the reused Changes/Files review panel.
  const viewAssemblyExecution = useCallback((id: string) => {
    if (id.endsWith('-rai') || id.endsWith('-scribe')) setViewRunId(id);
    else scrollToReview();
  }, [scrollToReview]);

  // Inline coordinator steering from the graph toolbar — sends redirect/amend with the typed text
  // directly (falling back to the confirmation dialog when no text is provided).
  const [steerText, setSteerText] = useState('');
  const [steerBusy, setSteerBusy] = useState(false);
  // Transient inline confirmation/error after an inline send (the bar otherwise "feels pending").
  const [steerNote, setSteerNote] = useState<{ intent: 'success' | 'error'; text: string } | null>(null);
  const quickSteer = useCallback(async (kind: SteerKind) => {
    if (!runId) return;
    const text = steerText.trim();
    if ((kind === 'send' || kind === 'redirect' || kind === 'amend') && !text) {
      openSteer({ kind });
      return;
    }
    setSteerBusy(true);
    setSteerNote(null);
    try {
      const res = await apiClient.steerCoordinator(runId, {
        kind,
        instruction: kind === 'stop' ? undefined : text || undefined,
      });
      if (kind !== 'stop') {
        setSteerText('');
        setSteerNote({ intent: 'success', text: steerStatusMessage(res.status) });
      }
    } catch (err) {
      // Surface failures inline AND via the dialog path so the user can retry with full context.
      setSteerNote({
        intent: 'error',
        text:
          err instanceof ApiError
            ? `Steer failed (${err.status}): ${err.body}`
            : err instanceof Error ? err.message : String(err),
      });
      openSteer({ kind });
    } finally {
      setSteerBusy(false);
    }
  }, [runId, steerText, openSteer]);

  // Auto-clear the inline steer confirmation after a few seconds so it stays non-blocking.
  useEffect(() => {
    if (!steerNote) return;
    const t = setTimeout(() => setSteerNote(null), 6000);
    return () => clearTimeout(t);
  }, [steerNote]);

  // Option toggles — optimistic update, revert on error. Both cascade to children server-side.
  const toggleAutopilot = useCallback((next: boolean) => {
    if (!runId || autopilotBusy) return;
    setAutopilot(next);
    setAutopilotBusy(true);
    apiClient.setAutopilot(runId, next)
      .then((res) => setAutopilot(Boolean(res.autopilot)))
      .catch(() => setAutopilot(!next))
      .finally(() => setAutopilotBusy(false));
  }, [runId, autopilotBusy]);

  const toggleAutoApprove = useCallback((next: boolean) => {
    if (!runId || autoApproveBusy) return;
    setAutoApprove(next);
    setAutoApproveBusy(true);
    apiClient.setAutoApprove(runId, next)
      .then((res) => setAutoApprove(Boolean(res.auto_approve_tools)))
      .catch(() => setAutoApprove(!next))
      .finally(() => setAutoApproveBusy(false));
  }, [runId, autoApproveBusy]);

  const handleRetry = useCallback(async () => {
    if (!runId || !projectId || retrying) return;
    setRetrying(true);
    setRetryError(null);
    try {
      const res = await apiClient.retryRun(runId);
      navigate(`/projects/${projectId}/orchestrations/${res.run_id}`);
    } catch (err) {
      setRetryError(
        err instanceof Error ? err.message : String(err),
      );
      setRetrying(false);
    }
  }, [runId, projectId, retrying, navigate]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId         = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting    = streamStatus === 'connecting';
  const isStreaming     = streamStatus === 'streaming';
  const hasGraph        = rfNodes.length > 0;
  const isRetryable     = runLevelStatus === 'failed' || runLevelStatus === 'merge_failed';
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
  const needsInstruction = steerReq?.kind === 'redirect' || steerReq?.kind === 'amend';
  // The toggle endpoints 409 on a non-active run, so only offer them while the orchestration is live.
  const coordActive     = !['complete', 'failed', 'blocked', 'declined'].includes(orch.phase);

  // A run can be terminally finished at the RUN level (Failed/Declined/Merged) while its WorkPlan
  // status still reads `in_review` — e.g. a run interrupted by a pre-durability build. In that state
  // the in-memory assembly-review gate is NOT armed, so presenting an actionable review bar would
  // 409. Treat the review as actionable only when the run itself is not terminal.
  const runTerminal = runLevelStatus !== undefined
    && ['failed', 'declined', 'merge_failed', 'merged', 'completed'].includes(runLevelStatus);
  const reviewActionable = orch.phase === 'in_review' && !runTerminal;

  // Map the coordinator orchestration phase onto the standard artifact-browser run status so the
  // reused Changes/Files rail shows the review bar (Approve / Request changes / Decline) exactly when
  // the ONE collective human-review gate is open.
  const coordRunStatus = useMemo(() => {
    switch (orch.phase) {
      case 'in_review':  return reviewActionable ? 'awaiting_review' : (runLevelStatus ?? 'merge_failed');
      case 'complete':   return 'merged';
      case 'declined':   return 'declined';
      case 'failed':
      case 'blocked':    return 'merge_failed';
      default:           return 'in_progress';
    }
  }, [orch.phase, reviewActionable, runLevelStatus]);

  // Per-run agent load items for the AgentRail — derived from the work-plan + children snapshot.
  const agentItems = useMemo(
    () => (workPlanData && runId ? deriveAgentQueues(workPlanData, childrenData, runId) : []),
    [workPlanData, childrenData, runId],
  );

  // Adapter that points the standard artifact browser at the coordinator's collective assembly:
  // files/diff come from the integration branch (the coordinator owns no worktree), and the three
  // review actions are delivered to the collective assembly gate instead of the per-run endpoints.
  const coordAdapter = useMemo<ArtifactBrowserAdapter>(() => ({
    getFiles: (rid, filter) => apiClient.getAssemblyFiles(rid, filter),
    getFileDiff: (rid, path) => apiClient.getAssemblyFileDiff(rid, path),
    getWorkspace: (rid) => apiClient.getAssemblyWorkspace(rid),
    getContent: (rid, path) => apiClient.getAssemblyFileContent(rid, path),
    approve: (rid) => apiClient.reviewAssembly(rid, 'approve'),
    requestChanges: (rid, comment) => apiClient.reviewAssembly(rid, 'request_changes', comment),
    decline: (rid) => apiClient.reviewAssembly(rid, 'decline'),
  }), []);

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span aria-hidden="true">/</span>
        <span>Orchestration {shortId}</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Orchestration</Title2>
        {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Connecting" />}
        {isRetryable && (
          <Button
            appearance="primary"
            size="small"
            icon={<ArrowRepeatAllRegular />}
            disabled={retrying}
            onClick={() => void handleRetry()}
            data-testid="coordinator-retry-button"
          >
            Retry
          </Button>
        )}
        {retriedFromShort && (
          <Text className={styles.runIdLabel}>
            Retried from{' '}
            <Link
              to={`/projects/${projectId}/orchestrations/${retriedFrom}`}
              className={styles.breadcrumbLink}
            >
              {retriedFromShort}
            </Link>
          </Text>
        )}
      </div>
      {retryError && (
        <MessageBar intent="error">
          <MessageBarBody>Retry failed: {retryError}</MessageBarBody>
        </MessageBar>
      )}

      {goal && <Text className={styles.goal}>Goal: {goal}</Text>}

      {/* Coordinator graph — full-width horizontal band on top (fits the pipeline far better
          than a narrow side column). */}
      <div className={styles.graphBand}>
        <div className={styles.sectionTitleRow}>
          <Title3>Coordinator Graph</Title3>
          {orch.phase !== 'unknown' && (
            <span className={styles.steerLabel}>{orchPhaseLabel(orch.phase)}</span>
          )}
          {isStreaming && <Spinner size="extra-tiny" aria-label="Live" />}
        </div>
        <Text className={styles.hint}>
          Live view of the coordinator and its subtasks. Expand a subtask to see its pipeline, or use
          the steering controls to send a course-correction to the coordinator or stop the orchestration.
        </Text>

        <CoordSteerContext.Provider value={openSteer}>
          {coordActive && (
          <div className={styles.steerBar}>
            <InfoLabel
              className={styles.steerLabel}
              info={STEER_INFO}
              infoButton={{ 'aria-label': 'About steering the coordinator' }}
            >
              Steer coordinator:
            </InfoLabel>
            <Input
              size="small"
              className={styles.steerInput}
              value={steerText}
              onChange={(_, v) => setSteerText(v.value)}
              placeholder="Message the coordinator with a course-correction…"
              disabled={steerBusy || !coordActive}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && steerText.trim()) { e.preventDefault(); void quickSteer('send'); }
              }}
            />
            <Button appearance="primary" size="small" icon={<SendRegular />}
              disabled={steerBusy || !coordActive || !steerText.trim()}
              onClick={() => void quickSteer('send')}>
              Send
            </Button>
            <Button appearance="subtle" size="small" icon={<ArrowRoutingRegular />}
              disabled={steerBusy || !coordActive}
              onClick={() => void quickSteer('redirect')}>
              Redirect
            </Button>
            <Button appearance="subtle" size="small" icon={<EditRegular />}
              disabled={steerBusy || !coordActive}
              onClick={() => void quickSteer('amend')}>
              Amend
            </Button>
            <Button appearance="subtle" size="small" icon={<StopRegular />}
              disabled={steerBusy || !coordActive}
              onClick={() => openSteer({ kind: 'stop' })}>
              Stop
            </Button>
            {steerBusy && <Spinner size="extra-tiny" aria-label="Steering" />}
            <span className={styles.steerScopeNote}>Applies to all active subtasks.</span>
          </div>
          )}
          {coordActive && <SteeringLegend />}
          {steerNote && (
            <MessageBar intent={steerNote.intent} className={styles.steerNote}>
              <MessageBarBody>{steerNote.text}</MessageBarBody>
            </MessageBar>
          )}

          {hasGraph ? (
            <ExecutionModalContext.Provider value={viewAssemblyExecution}>
            <BrowseFilesContext.Provider value={browseAssemblyFiles}>
            <ActiveEdgeContext.Provider value={activeLoopbackId}>
            <CoordinatorSessionContext.Provider value={scrollToSession}>
            <CoordExpandContext.Provider value={expandValue}>
            <CoordViewRunContext.Provider value={openChildRun}>
              <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} maxZoom={maxZoom} />
              <div className={styles.dagContainer} style={{ height: graphHeight }} ref={viewportRef}>
                <div style={{ zoom, width: '100%', height: '100%' }}>
                <ReactFlow
                  key={`${displayNodes.length}:${displayEdges2.length}`}
                  nodes={displayNodes}
                  edges={displayEdges2}
                  nodeTypes={coordinatorNodeTypes}
                  edgeTypes={workflowEdgeTypes}
                  fitView
                  fitViewOptions={{ padding: 0.12, maxZoom: 1.1 }}
                  minZoom={0.4}
                  nodesDraggable={false}
                  nodesConnectable={false}
                  nodesFocusable={false}
                  edgesFocusable={false}
                  panOnScroll={false}
                  zoomOnScroll={false}
                  zoomOnPinch={false}
                  zoomOnDoubleClick={false}
                  panOnDrag
                  proOptions={{ hideAttribution: true }}
                >
                  <GraphAutoFit
                    token={`${displayNodes.length}:${displayEdges2.length}:${graphHeight}:${[...expandedKeys].sort().join(',')}`}
                  />
                </ReactFlow>
                </div>
              </div>
              {inSpecAuthoring && (
                <Text className={styles.hint}>
                  The execution pipeline appears once you confirm the outcome spec.
                </Text>
              )}
            </CoordViewRunContext.Provider>
            </CoordExpandContext.Provider>
            </CoordinatorSessionContext.Provider>
            </ActiveEdgeContext.Provider>
            </BrowseFilesContext.Provider>
            </ExecutionModalContext.Provider>
          ) : (
            <Text className={styles.hint}>
              {isConnecting
                ? 'Connecting to coordinator stream...'
                : noWorkPlan
                  ? 'No work plan available yet.'
                  : 'Waiting for coordinator graph...'}
            </Text>
          )}
        </CoordSteerContext.Provider>
      </div>

      {/* Agent rail — compact per-agent load summary derived from the work plan.
          Phase 2 TODO: wire onSelectAgent to filter/highlight the topology and work plan. */}
      {workPlanData && (
        <div className={styles.agentRailBand}>
          <AgentRail agents={agentItems} title="Agents" />
        </div>
      )}

      {/* Two-column layout: [Outcome (collapsible, auto-collapses on confirm)] [Coordinator session
          (collapsible to a thin right rail; starts collapsed while the spec is authored)]. */}
      <div className={
        outcomeCollapsed
          ? styles.twoColCollapsed
          : sessionCollapsed
            ? styles.twoColSessionCollapsed
            : styles.twoCol
      }>
        {/* COL 1 — outcome spec, collapsible to a thin left rail. */}
        {outcomeCollapsed ? (
          <div className={styles.outcomeRail}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronRightRegular />}
              aria-label="Expand outcome spec"
              onClick={() => setOutcomeCollapsed(false)}
            />
            <span className={styles.railLabel}>Outcome spec</span>
          </div>
        ) : (
          <div className={styles.leftCol}>
            <OutcomeSpecPanel runId={runId} projectId={projectId ?? undefined} events={events} streamStatus={streamStatus} onCollapse={() => setOutcomeCollapsed(true)} onReconnect={reconnectStream} />
          </div>
        )}

        {/* COL 2 — coordinator session: automation/actions controls, the rich run view, and steering.
            Collapsible to a thin right rail (mirrors the outcome rail) to give the outcome panel the
            full width while the spec is still being authored. */}
        {sessionCollapsed ? (
          <div className={styles.outcomeRail}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronLeftRegular />}
              aria-label="Expand coordinator session"
              onClick={() => setSessionCollapseOverride(false)}
            />
            <span className={styles.railLabel}>Coordinator session</span>
          </div>
        ) : (
        <div ref={sessionRef} className={styles.centerCol}>
          <div className={styles.sessionHeaderRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronRightRegular />}
              aria-label="Collapse coordinator session"
              onClick={() => setSessionCollapseOverride(true)}
            />
          </div>
          {/* Assembly review affordance — de-confuses the stuck state (issues 3 & 4). */}
          {(orch.phase === 'awaiting_assembly' || orch.phase === 'assembling') && (
            <div className={styles.panel}>
              <div className={styles.sectionTitleRow}>
                <Spinner size="tiny" aria-label="Assembling" />
                <Title3>Assembling collective output…</Title3>
              </div>
              <Text className={styles.hint}>
                The subtasks are complete; the coordinator is integrating their outputs for collective review.
              </Text>
            </div>
          )}

          {reviewActionable && (
            <MessageBar intent="warning">
              <MessageBarBody>
                Your review is pending. Review the assembled changes in the Changes panel below, then
                Approve, request a Change, or Decline.
              </MessageBarBody>
            </MessageBar>
          )}

          {orch.phase === 'in_review' && runTerminal && (
            <MessageBar intent="error">
              <MessageBarBody>
                This orchestration ended ({runLevelStatus}) before the collective review could be
                completed, so the review gate is no longer open. {orch.reason ?? ''} Start a new
                coordinator run to retry.
              </MessageBarBody>
            </MessageBar>
          )}

          {(orch.phase === 'failed' || orch.phase === 'blocked' || orch.phase === 'declined') && (
            <div className={styles.panel} data-testid="assembly-blocked-panel">
              <Title3>Assembly {orchPhaseLabel(orch.phase).toLowerCase()}</Title3>
              <MessageBar intent="error">
                <MessageBarBody>{friendlyAssemblyReason(orch.reason)}</MessageBarBody>
              </MessageBar>
              {orch.conflictFiles && orch.conflictFiles.length > 0 && (
                <div className={styles.conflictFiles}>
                  <Text className={styles.hint}>Conflicting file{orch.conflictFiles.length > 1 ? 's' : ''}:</Text>
                  <ul className={styles.conflictList}>
                    {orch.conflictFiles.map((f) => (
                      <li key={f}><code>{f}</code></li>
                    ))}
                  </ul>
                </div>
              )}
              {ineligibleSubtasks && ineligibleSubtasks.length > 0 ? (
                <div className={styles.blockedSubtasks}>
                  <Text className={styles.hint}>Blocking subtask{ineligibleSubtasks.length > 1 ? 's' : ''}:</Text>
                  {ineligibleSubtasks.map((st) => {
                    const badge = subtaskStatusBadge(st.status);
                    const hint = subtaskStatusHint(st.status);
                    return (
                      <div key={st.id} className={styles.blockedSubtaskRow}>
                        <div className={styles.blockedSubtaskHead}>
                          <Text className={styles.blockedSubtaskTitle}>{st.title || `#${st.id}`}</Text>
                          <Badge appearance="tint" color={badge.intent} size="small">{badge.label}</Badge>
                          {st.agent && <Text className={styles.blockedSubtaskAgent}>{st.agent}</Text>}
                        </div>
                        {hint && <Text className={styles.hint}>{hint}</Text>}
                      </div>
                    );
                  })}
                </div>
              ) : (
                ineligibleSubtaskIds && ineligibleSubtaskIds.length > 0 && (
                  <div className={styles.conflictFiles}>
                    <Text className={styles.hint}>Blocking subtask{ineligibleSubtaskIds.length > 1 ? 's' : ''}:</Text>
                    <ul className={styles.conflictList}>
                      {ineligibleSubtaskIds.map((id) => (
                        <li key={id}>#{id}</li>
                      ))}
                    </ul>
                  </div>
                )
              )}
              <Text className={styles.hint}>
                Use the controls below to redirect the coordinator with an instruction, or stop the run.
              </Text>
              <SteerPanel runId={runId} blockReason={orch.reason} />
            </div>
          )}

          {orch.phase === 'complete' && (
            <MessageBar intent="success">
              <MessageBarBody>Orchestration complete.</MessageBarBody>
            </MessageBar>
          )}

          {/* Session controls — compact automation toolbar + bubbled child actions. */}
          <div className={styles.sessionToolbar}>
            {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Live" />}
            {/* Automation toggles — autopilot + auto-approve-tools. Both cascade to children.
                Each carries a visible InfoLabel (i) explaining what it does. */}
            <div className={styles.toggleGroup}>
              <AutomationToggle
                label="Autopilot"
                info={AUTOMATION_HELP.autopilotOrchestration}
                checked={autopilot}
                disabled={autopilotBusy || !coordActive}
                onChange={(checked) => toggleAutopilot(checked)}
              />
              <AutomationToggle
                label="Auto-approve tools"
                info={AUTOMATION_HELP.autoApproveOrchestration}
                checked={autoApprove}
                disabled={autoApproveBusy || !coordActive}
                onChange={(checked) => toggleAutoApprove(checked)}
              />
            </div>
          </div>

          {/* Action required — bubbled child questions + tool-approval requests. Answers/grants
              target the CHILD that asked (childRunId), not the coordinator run. Each item
              collapses once resolved. */}
          {childRequests.length > 0 && (
              <div className={styles.actionRequired} aria-label="Child actions awaiting a response">
                {childRequests.map((item) => {
                  const label = item.subtaskId ? `Subtask ${item.subtaskId}` : `Child ${item.childRunId.slice(0, 8)}`;
                  if (item.type === 'question') {
                    return (
                      <QuestionAnswerCard
                        key={`q-${item.requestId}`}
                        runId={item.childRunId}
                        requestId={item.requestId}
                        question={item.question}
                        answer={item.answer}
                        timedOut={item.timedOut}
                        sourceLabel={label}
                      />
                    );
                  }
                  // Reuse the existing HITL tool-approval card via a synthetic event, targeted at
                  // the childRunId so allow/deny POST against the child's tool-approval endpoints.
                  return (
                    <div key={`a-${item.requestId}`}>
                      <Text className={styles.actionSource}>{label} · approval required</Text>
                      <LifecycleEventCard
                        event={{
                          sequence: item.seq,
                          type: 'tool.approval_required',
                          payload: {
                            requestId: item.requestId,
                            toolName: item.toolName,
                            url: item.url,
                            intention: item.message,
                          },
                        }}
                        runId={item.childRunId}
                      />
                    </div>
                  );
                })}
              </div>
            )}

          {/* Rich run view — the standard Changes/Files rail + review bar reused for the coordinator.
              The coordinator owns no worktree, so the adapter points the artifact browser at the
              collective integration-branch diff and routes Approve/Change/Decline to the assembly
              gate. The center is the coordinator's own run timeline, so the session reads like every
              other agent's "view run". The ref is the scroll target for the gate's "Review now". */}
          <div ref={reviewRef}>
          <RunLayout
            runId={runId ?? ''}
            runStatus={coordRunStatus}
            artifactAdapter={coordAdapter}
            focusFilesSignal={filesFocusSignal}
            centerContent={
              <Timeline
                items={coordItems}
                streamStatus={streamStatus}
                isLiveRun={coordActive && liveRun}
                runId={runId}
                runOutcome={coordRunOutcome}
              />
            }
            style={{ height: '70vh', minHeight: '520px' }}
          />
          </div>
        </div>
        )}
      </div>

      {/* View-run modal — the standard run view (Changes/Files + timeline) for a child subtask,
          opened in a dialog so the user never leaves the orchestration. */}
      <Dialog open={!!viewRunId} onOpenChange={(_, d) => { if (!d.open) setViewRunId(null); }}>
        <DialogSurface className={styles.viewRunSurface}>
          <DialogBody className={styles.viewRunBody}>
            <div className={styles.viewRunHeader}>
              <Title3>
                {viewRunId?.endsWith('-rai')
                  ? 'RAI review (collective assembly)'
                  : viewRunId?.endsWith('-scribe')
                    ? 'Scribe documentation (collective assembly)'
                    : `Child run ${viewRunId ? viewRunId.slice(0, 8) : ''}`}
              </Title3>
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label="Close run"
                onClick={() => setViewRunId(null)}
              />
            </div>
            {viewRunId && <RunWatcher runId={viewRunId} style={{ flex: 1, minHeight: 0 }} />}
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* Steering dialog */}
      <Dialog open={!!steerReq} onOpenChange={(_, d) => { if (!d.open) closeSteer(); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {steerReq ? steerKindLabel(steerReq.kind) : ''} — Coordinator
            </DialogTitle>
            <DialogContent>
              <div className={styles.dialogFields}>
                {steerReq?.kind === 'stop' ? (
                  <Text>Stop this orchestration? No further work will be dispatched.</Text>
                ) : (
                  <>
                    <Text>Describe the course-correction to send to the coordinator.</Text>
                    <Field label="Instruction" required>
                      <Textarea
                        value={instruction}
                        onChange={(_, v) => setInstruction(v.value)}
                        placeholder="e.g. Target the v2 API instead, and add integration tests."
                        rows={4}
                      />
                    </Field>
                  </>
                )}
                {steerError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{steerError}</MessageBarBody>
                  </MessageBar>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={busy} onClick={closeSteer}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={busy || (!!needsInstruction && !instruction.trim())}
                onClick={() => void submitSteer()}
              >
                {busy ? 'Sending' : steerReq?.kind === 'stop' ? 'Stop' : 'Send'}
              </Button>
              {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
