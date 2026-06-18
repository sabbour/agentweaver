import { createContext, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
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
  ArrowRoutingRegular,
  BotRegular,
  CheckmarkCircleRegular,
  EditRegular,
  SendRegular,
  StopRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  Handle,
  Position,
  type Node,
  type Edge,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream, type RunStreamEvent } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { GraphDescriptor, SteerKind, AssemblyReviewDecision } from '../api/types';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H, NODE_TYPE_W, NODE_TYPE_H } from '../utils/dagLayout';
import type { NodeSizeHint } from '../utils/dagLayout';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';
import { AgentAvatar } from '../components/AgentAvatar';
import {
  workflowNodeTypes,
  forwardEdge,
  loopbackEdge,
  coordinatorLoopbackLabel,
  roleDescForRole,
  iconForRole,
  useNodeStyles,
  StatusBadge,
  ElapsedTimer,
  statusDescription,
  CoordinatorSessionContext,
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

// ---------------------------------------------------------------------------
// Steering context — page-level; lets the steer bar trigger the dialog
// ---------------------------------------------------------------------------

interface SteerRequest {
  kind: SteerKind;
}

const CoordSteerContext = createContext<((req: SteerRequest) => void) | undefined>(undefined);

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
}

// coordinator.assembly_* event type → phase. These event types may not be emitted
// yet; absence simply means we fall through to the status field / work-plan status.
const ASSEMBLY_EVENT_PHASE: Record<string, OrchPhase> = {
  'coordinator.assembly_assembling': 'assembling',
  'coordinator.assembly_review_requested': 'in_review',
  'coordinator.assembly_complete': 'complete',
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
    return {
      phase: winner.phase,
      reason: readStr(winner.payload, ['reason', 'message', 'error', 'detail']),
      diff: readStr(winner.payload, ['diff', 'summary', 'integrationDiff', 'integration_diff', 'treeHash', 'tree_hash']),
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

interface Milestone {
  key: string;
  label: string;
  ts?: number;
}

function readTs(p: Record<string, unknown>): number | undefined {
  const t = p['timestamp_utc'] ?? p['timestamp'] ?? p['ts'] ?? p['at'];
  if (t == null) return undefined;
  const ms = new Date(String(t)).getTime();
  return isNaN(ms) ? undefined : ms;
}

// Build a chronological list of orchestration milestones from the coordinator's own
// event stream. Tolerant of missing payload fields and unknown event variants.
function buildTimeline(events: RunStreamEvent[]): Milestone[] {
  const out: Milestone[] = [];
  let seq = 0;
  const push = (label: string, ts?: number) => out.push({ key: `${seq++}`, label, ts });
  for (const evt of events) {
    const p = evt.payload;
    const ts = readTs(p);
    const sid = p['subtaskId'] != null ? String(p['subtaskId']) : undefined;
    switch (evt.type) {
      case 'coordinator.started': {
        const goal = typeof p['goal'] === 'string' ? p['goal'] as string : undefined;
        push(goal ? `Coordinator started — ${goal}` : 'Coordinator started', ts);
        break;
      }
      case 'coordinator.outcome_spec.confirmed':
        push('Outcome spec confirmed', ts);
        break;
      case 'coordinator.work_plan': {
        const n = Array.isArray(p['subtasks']) ? (p['subtasks'] as unknown[]).length : undefined;
        push(n != null ? `Work plan ready — ${n} subtask${n === 1 ? '' : 's'}` : 'Work plan ready', ts);
        break;
      }
      case 'subtask.dispatched':       push(`Subtask ${sid ?? ''} dispatched`.trim(), ts); break;
      case 'subtask.running':          push(`Subtask ${sid ?? ''} running`.trim(), ts); break;
      case 'subtask.assemble_ready':   push(`Subtask ${sid ?? ''} ready for assembly`.trim(), ts); break;
      case 'subtask.rai_flagged':      push(`Subtask ${sid ?? ''} flagged by RAI`.trim(), ts); break;
      case 'subtask.completed':        push(`Subtask ${sid ?? ''} completed`.trim(), ts); break;
      case 'subtask.failed':           push(`Subtask ${sid ?? ''} failed`.trim(), ts); break;
      case 'coordinator.children_complete': push('All subtasks complete', ts); break;
      case 'coordinator.assembly_assembling':      push('Assembling collective output', ts); break;
      case 'coordinator.assembly_review_requested': push('Collective review requested', ts); break;
      case 'coordinator.assembly_complete':        push('Assembly complete', ts); break;
      case 'coordinator.assembly_failed':          push(`Assembly failed${readStr(p, ['reason', 'message']) ? `: ${readStr(p, ['reason', 'message'])}` : ''}`, ts); break;
      case 'coordinator.assembly_blocked':         push(`Assembly blocked${readStr(p, ['reason', 'message']) ? `: ${readStr(p, ['reason', 'message'])}` : ''}`, ts); break;
      case 'coordinator.assembly_declined':        push(`Assembly declined${readStr(p, ['reason', 'message']) ? `: ${readStr(p, ['reason', 'message'])}` : ''}`, ts); break;
      case 'coordinator.steering': {
        const kind = readStr(p, ['kind']) ?? 'steer';
        const status = readStr(p, ['status']) ?? 'requested';
        push(`Steering ${kind} ${status}`, ts);
        break;
      }
      default: break;
    }
  }
  return out;
}

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
  model: string | undefined;
  phase: string | undefined;
  projectId: string;
}

// Fallback child pipeline defs (used when a child run's graph descriptor is not yet available).
const INLINE_CHILD_FALLBACK: ExecutorDef[] = [
  { key: 'agent',          label: 'Agent',          roleDescription: 'AI Assistant',                Icon: iconForRole('agent')    },
  { key: 'rai',            label: 'Rai',             roleDescription: 'RAI Reviewer',                Icon: iconForRole('rai')      },
  { key: 'assemble-ready', label: 'Assemble-ready',  roleDescription: 'Awaiting collective assembly', Icon: iconForRole('assembly') },
];

// A compact pipeline node card rendered inline inside a SubtaskNode expansion panel.
// Does not use React Flow Handles (rendered outside a ReactFlow canvas).
function ChildNodeMiniCard({ def, state }: { def: ExecutorDef; state: ExecutorState }) {
  const s = useNodeStyles();
  const { key, label, Icon } = def;
  const { status, startedAt, completedAt, message } = state;
  const subText = message ?? statusDescription(key, status);
  return (
    <div
      className={`${s.card} ${s.cardDefault} ${status === 'started' ? s.cardActive : ''}`}
      role="article"
      aria-label={`${label}: ${status}`}
      data-testid={`child-node-${key}`}
    >
      <div className={s.cardHeader}>
        <StatusBadge status={status} />
      </div>
      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          <Icon fontSize={20} />
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{label}</span>
          <span className={s.cardRole}>{def.roleDescription}</span>
          {subText && <span className={s.cardSubText}>{subText}</span>}
          {startedAt !== undefined && (
            <span className={s.cardTimer}>
              <ElapsedTimer startedAt={startedAt} completedAt={completedAt} />
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

function SubtaskNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const d = data as SubtaskNodeData;
  const [expanded, setExpanded] = useState(false);
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
  const { events: childEvents } = useRunStream(childStreamRunId, API_KEY, API_URL);

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
      className={`${s.card} ${s.cardSubtask}`}
      data-node-type="subtask"
      role="article"
      aria-label={`${d.label as string}: ${d.topoStatus as string}`}
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />

      <div className={s.cardHeader}>
        <StatusBadge status={stepStatus} label={statusLabel} />
        {expanded && Object.keys(childStepStates).length > 0 && (
          <span className={s.cardTimer}>
            <AggregateElapsed states={childStepStates} />
          </span>
        )}
      </div>

      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          {d.agent
            ? <AgentAvatar name={d.agent as string} size={28} circle />
            : <BotRegular fontSize={22} />}
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{d.label as string}</span>
          <span className={s.cardRole}>Subtask Agent</span>
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
            onClick={() => setExpanded((prev) => !prev)}
          >
            {expanded ? 'Collapse pipeline' : 'Expand pipeline'}
          </Button>
          {d.childRunId && (
            <Link
              to={`/projects/${d.projectId as string}/runs/${d.childRunId as string}/workflow`}
              style={{ textDecoration: 'none' }}
            >
              <Button appearance="outline" size="small">View run</Button>
            </Link>
          )}
        </div>
      )}

      {/* Inline child pipeline — live node cards with status badges, elapsed timers, and messages. */}
      {expanded && (
        <div
          className="nopan nodrag"
          style={{
            marginTop: 10,
            display: 'flex',
            flexDirection: 'row',
            alignItems: 'flex-start',
            flexWrap: 'wrap',
            gap: 0,
          }}
        >
          {childNodes.map((node, i) => (
            <span key={node.def.key} style={{ display: 'inline-flex', alignItems: 'flex-start' }}>
              <ChildNodeMiniCard def={node.def} state={node.state} />
              {i < childNodes.length - 1 && (
                <span
                  aria-hidden="true"
                  style={{
                    alignSelf: 'center',
                    color: 'var(--colorNeutralForeground3)',
                    padding: '0 4px',
                    fontSize: 'var(--fontSizeBase300)',
                    userSelect: 'none',
                  }}
                >
                  →
                </span>
              )}
            </span>
          ))}
        </div>
      )}
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
    maxWidth: '1400px',
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
  // Two-column layout: outcome spec on the LEFT (scrollable), topology on the RIGHT.
  twoCol: {
    display: 'grid',
    gridTemplateColumns: 'minmax(320px, 420px) minmax(0, 1fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 980px)': {
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
    '@media (max-width: 980px)': {
      maxHeight: 'none',
    },
  },
  rightCol: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
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
  dagContainer: {
    height: '520px',
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
  steerLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
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
  sessionScroll: {
    maxHeight: '300px',
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  timelineRow: {
    display: 'flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalS,
    fontSize: tokens.fontSizeBase200,
  },
  timelineTime: {
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
    minWidth: '64px',
    flexShrink: 0,
  },
  timelineLabel: {
    color: tokens.colorNeutralForeground1,
  },
  chatRow: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: tokens.spacingHorizontalS,
  },
  chatGrow: {
    flex: 1,
    minWidth: 0,
  },
  steeringFeed: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  feedItem: {
    fontSize: tokens.fontSizeBase200,
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
  if (kind === 'redirect') return 'Redirect';
  return 'Amend';
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // REST seed: coordinator GraphDescriptor (GET /api/runs/{id}/graph, coordinator variant).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);

  // Topology seed from work plan + children (for subtask status projection).
  const [topoSeed, setTopoSeed] = useState(initialTopologyState);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    // Fetch graph descriptor for REST seed (so finished coordinator runs still render).
    apiClient.getRunGraph(runId)
      .then((desc) => { if (!cancelled) setRestDescriptor(desc); })
      .catch(() => {});

    // Fetch work plan + children for topology status seed.
    void (async () => {
      const [workPlan, children] = await Promise.all([
        apiClient.getWorkPlan(runId).catch(() => null),
        apiClient.getCoordinatorChildren(runId).catch(() => null),
      ]);
      if (cancelled) return;
      if (workPlan) setTopoSeed(seedTopologyFromWorkPlan(workPlan, children));
    })();

    return () => { cancelled = true; };
  }, [runId]);

  // ---------------------------------------------------------------------------
  // Orchestration lifecycle poll (issues 3 & 4). Reads the coordinator_status field
  // (added by the backend concurrently — optional) plus the work-plan status, both
  // tolerated as absent. Polls until the orchestration reaches a terminal phase.
  // ---------------------------------------------------------------------------
  const [coordStatusField, setCoordStatusField] = useState<string | undefined>(undefined);
  const [coordStatusReason, setCoordStatusReason] = useState<string | undefined>(undefined);
  const [workPlanStatus, setWorkPlanStatus] = useState<string | undefined>(undefined);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const TERMINAL = new Set<OrchPhase>(['complete', 'failed', 'blocked', 'declined']);

    const tick = async () => {
      const [detail, wp] = await Promise.all([
        apiClient.getRun(runId).catch(() => null),
        apiClient.getWorkPlan(runId).catch(() => null),
      ]);
      if (cancelled) return;
      const statusField = detail?.coordinator_status ?? undefined;
      const reasonField = detail?.coordinator_status_reason ?? undefined;
      const wpStatus = wp?.status ?? undefined;
      setCoordStatusField(statusField);
      setCoordStatusReason(reasonField);
      setWorkPlanStatus(wpStatus);
      const phase = normalizePhase(statusField) !== 'unknown'
        ? normalizePhase(statusField)
        : normalizePhase(wpStatus);
      if (!TERMINAL.has(phase)) {
        timer = setTimeout(() => { void tick(); }, 4000);
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

  // Session timeline (issue 6) — chronological orchestration milestones.
  const timeline = useMemo<Milestone[]>(() => buildTimeline(events), [events]);
  const timelineStart = timeline.find((m) => m.ts !== undefined)?.ts;

  // Steering feedback (issue 6) — directive state from coordinator.steering events.
  const steeringFeed = useMemo(() => {
    const out: Array<{ id: string; kind: string; status: string; instruction?: string }> = [];
    for (const evt of events) {
      if (evt.type !== 'coordinator.steering') continue;
      const p = evt.payload;
      const id = readStr(p, ['directiveId']) ?? String(out.length);
      const rec = {
        id,
        kind: readStr(p, ['kind']) ?? 'steer',
        status: readStr(p, ['status']) ?? 'requested',
        instruction: readStr(p, ['instruction']),
      };
      const existing = out.find((d) => d.id === id);
      if (existing) Object.assign(existing, rec);
      else out.push(rec);
    }
    return out;
  }, [events]);

  // Topology state for subtask status projection.
  const topology = useMemo(
    () => buildTopologyState(events, topoSeed),
    [events, topoSeed],
  );

  // Build React Flow nodes + forward edges from the coordinator descriptor.
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
      nodeSizeHints[node.id] = {
        width:  NODE_TYPE_W[nt ?? ''] ?? NODE_W,
        height: NODE_TYPE_H[nt ?? ''] ?? NODE_H,
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
            model:         modelField,
            phase:         phaseField,
            projectId:     projectId ?? '',
          } as SubtaskNodeData,
          position: { x: 0, y: 0 },
        };
      }

      // Coordinator or planned assembly node — use generic WorkflowNode.
      const coordTopoNode = topology.nodes['coordinator'];
      const coordStatus: StepStatus = node.id === 'coordinator'
        ? topoStatusToStepStatus(coordNodeStatusOverride ?? coordTopoNode?.status ?? 'running')
        : 'pending';

      const st: ExecutorState = planned
        ? { status: 'pending' }
        : { status: coordStatus };

      const def: ExecutorDef = {
        key:             node.id,
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
          isPlanned: planned,
          nodeType:  nt,
          runId:     runId      ?? '',
          executionId: '',
          projectId:   projectId ?? '',
        } as WorkflowNodeData,
        position: { x: 0, y: 0 },
      };
    });

    return {
      rfNodes:      layoutDag(raw, fwdEdges, { rankdir: 'LR', rankSep: 60, nodeSep: 30 }, nodeSizeHints),
      displayEdges: allEdges,
    };
  }, [effectiveDescriptor, topology, projectId, runId, coordNodeStatusOverride]);

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
  // Steering chat box (issue 6) — persistent free-form steering on the page.
  // ---------------------------------------------------------------------------
  const [chatText, setChatText] = useState('');
  const [chatBusy, setChatBusy] = useState(false);
  const [chatError, setChatError] = useState<string | null>(null);

  const sendChatSteer = useCallback(async (kind: SteerKind) => {
    if (!runId) return;
    if (kind !== 'stop' && !chatText.trim()) return;
    setChatBusy(true);
    setChatError(null);
    try {
      await apiClient.steerCoordinator(runId, {
        kind,
        instruction: kind === 'stop' ? undefined : chatText.trim() || undefined,
      });
      if (kind !== 'stop') setChatText('');
    } catch (err) {
      setChatError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setChatBusy(false);
    }
  }, [runId, chatText]);

  // ---------------------------------------------------------------------------
  // Assembly review (issues 3 & 4) — collective human review over the integration output.
  // ---------------------------------------------------------------------------
  const [reviewComment, setReviewComment] = useState('');
  const [reviewBusy, setReviewBusy] = useState(false);
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [reviewDone, setReviewDone] = useState<AssemblyReviewDecision | null>(null);

  const submitReview = useCallback(async (decision: AssemblyReviewDecision) => {
    if (!runId) return;
    if (decision !== 'approve' && !reviewComment.trim()) {
      setReviewError('A comment is required to request changes or decline.');
      return;
    }
    setReviewBusy(true);
    setReviewError(null);
    try {
      await apiClient.reviewAssembly(runId, {
        decision,
        comment: reviewComment.trim() || undefined,
      });
      setReviewDone(decision);
      setReviewComment('');
    } catch (err) {
      setReviewError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setReviewBusy(false);
    }
  }, [runId, reviewComment]);

  // Session panel anchor — the coordinator node's "View session" scrolls here.
  const sessionRef = useRef<HTMLDivElement>(null);
  const scrollToSession = useCallback(() => {
    sessionRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, []);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId         = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting    = streamStatus === 'connecting';
  const isStreaming     = streamStatus === 'streaming';
  const hasGraph        = rfNodes.length > 0;
  const needsInstruction = steerReq?.kind === 'redirect' || steerReq?.kind === 'amend';

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
        <span className={styles.runIdLabel}>{shortId}</span>
        {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Connecting" />}
      </div>

      {goal && <Text className={styles.goal}>Goal: {goal}</Text>}

      {/* Two-column layout: outcome spec on the LEFT (scrollable), topology on the RIGHT. */}
      <div className={styles.twoCol}>
        {/* LEFT — outcome spec in its own scroll container. */}
        <div className={styles.leftCol}>
          <Title3>Outcome spec</Title3>
          <OutcomeSpecPanel runId={runId} events={events} streamStatus={streamStatus} />
        </div>

        {/* RIGHT — execution topology + assembly review + session/steering. */}
        <div className={styles.rightCol}>
          {/* Unified coordinator graph — coordinator + subtasks + planned assembly. */}
          <div className={styles.section}>
            <div className={styles.sectionTitleRow}>
              <Title3>Coordinator Graph</Title3>
              {orch.phase !== 'unknown' && (
                <span className={styles.steerLabel}>{orchPhaseLabel(orch.phase)}</span>
              )}
              {isStreaming && <Spinner size="extra-tiny" aria-label="Live" />}
            </div>
            <Text className={styles.hint}>
              Live view of the coordinator and its subtasks. Expand a subtask to see its pipeline, or use
              the steering controls to stop, redirect, or amend the orchestration.
            </Text>

            {/* Steering bar — always visible when coordinator run is mounted. */}
            <CoordSteerContext.Provider value={openSteer}>
              <div className={styles.steerBar}>
                <span className={styles.steerLabel}>Steer coordinator:</span>
                <Button appearance="subtle" size="small" icon={<StopRegular />}
                  onClick={() => openSteer({ kind: 'stop' })}>
                  Stop
                </Button>
                <Button appearance="subtle" size="small" icon={<ArrowRoutingRegular />}
                  onClick={() => openSteer({ kind: 'redirect' })}>
                  Redirect
                </Button>
                <Button appearance="subtle" size="small" icon={<EditRegular />}
                  onClick={() => openSteer({ kind: 'amend' })}>
                  Amend
                </Button>
              </div>

              {/* ReactFlow canvas */}
              {hasGraph ? (
                <CoordinatorSessionContext.Provider value={scrollToSession}>
                  <div className={styles.dagContainer}>
                    <ReactFlow
                      nodes={rfNodes}
                      edges={displayEdges}
                      nodeTypes={coordinatorNodeTypes}
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
                    />
                  </div>
                </CoordinatorSessionContext.Provider>
              ) : (
                <Text className={styles.hint}>
                  {isConnecting ? 'Connecting to coordinator stream...' : 'Waiting for coordinator graph...'}
                </Text>
              )}
            </CoordSteerContext.Provider>
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

          {orch.phase === 'in_review' && reviewDone === null && (
            <div className={styles.panel}>
              <Title3>Assembly review</Title3>
              <Text className={styles.hint}>
                Review the assembled integration output, then approve, request changes, or decline.
              </Text>
              {orch.diff && <div className={styles.diffBox}>{orch.diff}</div>}
              <Field label="Comment (required to request changes or decline)">
                <Textarea
                  value={reviewComment}
                  onChange={(_, v) => setReviewComment(v.value)}
                  placeholder="Optional for approve; required otherwise."
                  rows={3}
                />
              </Field>
              {reviewError && (
                <MessageBar intent="error"><MessageBarBody>{reviewError}</MessageBarBody></MessageBar>
              )}
              <div className={styles.reviewActions}>
                <Button appearance="primary" icon={<CheckmarkCircleRegular />} disabled={reviewBusy}
                  onClick={() => void submitReview('approve')}>
                  Approve
                </Button>
                <Button appearance="secondary" icon={<EditRegular />} disabled={reviewBusy}
                  onClick={() => void submitReview('request_changes')}>
                  Request changes
                </Button>
                <Button appearance="secondary" icon={<StopRegular />} disabled={reviewBusy}
                  onClick={() => void submitReview('decline')}>
                  Decline
                </Button>
                {reviewBusy && <Spinner size="extra-tiny" aria-hidden="true" />}
              </div>
            </div>
          )}

          {reviewDone && (
            <MessageBar intent={reviewDone === 'approve' ? 'success' : 'info'}>
              <MessageBarBody>Review submitted: {reviewDone.replace('_', ' ')}.</MessageBarBody>
            </MessageBar>
          )}

          {(orch.phase === 'failed' || orch.phase === 'blocked' || orch.phase === 'declined') && (
            <div className={styles.panel}>
              <Title3>Assembly {orchPhaseLabel(orch.phase).toLowerCase()}</Title3>
              <MessageBar intent="error">
                <MessageBarBody>{orch.reason ?? 'The collective assembly could not complete.'}</MessageBarBody>
              </MessageBar>
              <Text className={styles.hint}>
                The subtasks are parked. Use the steering chat below to redirect or amend the orchestration, or stop it.
              </Text>
            </div>
          )}

          {orch.phase === 'complete' && (
            <MessageBar intent="success">
              <MessageBarBody>Orchestration complete.</MessageBarBody>
            </MessageBar>
          )}

          {/* All-up coordinator session + steering chat box (issue 6). */}
          <div ref={sessionRef} className={styles.panel}>
            <div className={styles.sectionTitleRow}>
              <Title3>Coordinator session</Title3>
              {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Live" />}
            </div>
            <Text className={styles.hint}>
              All-up orchestration timeline from the coordinator&apos;s own event stream.
            </Text>
            {timeline.length === 0 ? (
              <Text className={styles.hint}>No milestones yet.</Text>
            ) : (
              <div className={styles.sessionScroll}>
                {timeline.map((m) => (
                  <div key={m.key} className={styles.timelineRow}>
                    <span className={styles.timelineTime}>
                      {m.ts !== undefined && timelineStart !== undefined
                        ? `+${fmtTotal(Math.max(0, m.ts - timelineStart))}`
                        : '—'}
                    </span>
                    <span className={styles.timelineLabel}>{m.label}</span>
                  </div>
                ))}
              </div>
            )}

            {/* Steering chat box — submits free-form steering without opening a dialog. */}
            <Title3>Steer the coordinator</Title3>
            <div className={styles.chatRow}>
              <div className={styles.chatGrow}>
                <Textarea
                  value={chatText}
                  onChange={(_, v) => setChatText(v.value)}
                  placeholder="Message the coordinator to amend or redirect…"
                  rows={2}
                  disabled={chatBusy}
                />
              </div>
              <Button appearance="primary" icon={<SendRegular />}
                disabled={chatBusy || !chatText.trim()} onClick={() => void sendChatSteer('amend')}>
                Send
              </Button>
            </div>
            <div className={styles.steerBar}>
              <Button appearance="subtle" size="small" icon={<ArrowRoutingRegular />}
                disabled={chatBusy || !chatText.trim()} onClick={() => void sendChatSteer('redirect')}>
                Redirect
              </Button>
              <Button appearance="subtle" size="small" icon={<StopRegular />}
                disabled={chatBusy} onClick={() => void sendChatSteer('stop')}>
                Stop
              </Button>
              {chatBusy && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
            {chatError && (
              <MessageBar intent="error"><MessageBarBody>{chatError}</MessageBarBody></MessageBar>
            )}
            {steeringFeed.length > 0 && (
              <div className={styles.steeringFeed}>
                {steeringFeed.map((d) => (
                  <span key={d.id} className={styles.feedItem}>
                    {steerKindLabel(d.kind as SteerKind)} — {d.status}
                    {d.instruction ? `: ${d.instruction}` : ''}
                  </span>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>

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
                    <Text>
                      {steerReq?.kind === 'redirect'
                        ? 'Describe the new direction for the orchestration.'
                        : 'Describe the amendment to apply to the orchestration.'}
                    </Text>
                    <Field label="Instruction" required>
                      <Textarea
                        value={instruction}
                        onChange={(_, v) => setInstruction(v.value)}
                        placeholder={steerReq?.kind === 'redirect'
                          ? 'e.g. Target the v2 API instead.'
                          : 'e.g. Also add integration tests.'}
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

