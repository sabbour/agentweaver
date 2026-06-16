import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Spinner,
  Text,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
  ArrowSyncRegular,
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  MergeRegular,
  NotebookRegular,
  PersonRegular,
  PersonClockRegular,
  ShieldRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  MarkerType,
  Position,
  Handle,
  useEdges,
  useNodes,
  type Node,
  type Edge,
  type NodeProps,
  type EdgeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H } from '../utils/dagLayout';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type StepStatus = 'pending' | 'started' | 'completed' | 'skipped' | 'failed';
type ExecutorKey = 'agent' | 'rai' | 'review' | 'merge' | 'scribe';

interface ExecutorState {
  status: StepStatus;
  agentName?: string;
  intent?: string;      // latest agent.intent text — replaces static "Working on task..."
  startedAt?: number;   // ms epoch — from timestamp_utc in workflow.step event
  completedAt?: number; // ms epoch — from timestamp_utc of first terminal event
}

interface ExecutorDef {
  key: ExecutorKey;
  label: string;
  roleDescription: string;
  Icon: FluentIcon;
}

const EXECUTORS: ExecutorDef[] = [
  { key: 'agent',  label: 'Agent',  roleDescription: 'AI Assistant',    Icon: BotRegular      },
  { key: 'rai',    label: 'Rai',    roleDescription: 'RAI Reviewer',     Icon: ShieldRegular   },
  { key: 'review', label: 'Review', roleDescription: 'Human Review',     Icon: PersonRegular   },
  { key: 'merge',  label: 'Merge',  roleDescription: 'Merge Coordinator',Icon: MergeRegular    },
  { key: 'scribe', label: 'Scribe', roleDescription: 'Session Logger',   Icon: NotebookRegular },
];

// ---------------------------------------------------------------------------
// Node data shape passed into React Flow custom nodes
// ---------------------------------------------------------------------------

interface WorkflowNodeData extends Record<string, unknown> {
  def: ExecutorDef;
  state: ExecutorState;
  agentName?: string;
  agentRoleTitle?: string;   // actual team role title for the agent executor
  runId: string;
  executionId: string;
  projectId: string;
  reviewedBy?: string;
}

// ---------------------------------------------------------------------------
// Styles — page-level
// ---------------------------------------------------------------------------

const usePageStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
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
  dagContainer: {
    height: '520px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '& .react-flow__renderer': {
      borderRadius: '8px',
    },
  },
});

// ---------------------------------------------------------------------------
// Styles — custom workflow node
// ---------------------------------------------------------------------------

const useNodeStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '14px',
    width: `${NODE_W}px`,
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
    cursor: 'default',
  },
  cardActive: {
    borderLeft: `3px solid ${tokens.colorBrandForeground1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  cardActionRequired: {
    border: `2px solid ${tokens.colorPaletteMarigoldBorderActive}`,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
  },
  cardHeader: {
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
  },
  statusBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '2px 7px',
    borderRadius: '999px',
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  badgePending:   { backgroundColor: tokens.colorNeutralBackground4,              color: tokens.colorNeutralForeground3  },
  badgeStarted:   { backgroundColor: tokens.colorBrandBackground2,                color: tokens.colorBrandForeground1    },
  badgeAwaiting:  { backgroundColor: tokens.colorPaletteMarigoldBackground2,      color: tokens.colorPaletteMarigoldForeground1 },
  badgeCompleted: { backgroundColor: tokens.colorPaletteGreenBackground2,         color: tokens.colorPaletteGreenForeground1 },
  badgeSkipped:   { backgroundColor: tokens.colorNeutralBackground3,              color: tokens.colorNeutralForeground4  },
  badgeFailed:    { backgroundColor: tokens.colorPaletteRedBackground2,           color: tokens.colorPaletteRedForeground1 },
  cardMain: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  cardIcon: {
    display: 'flex',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  cardTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },
  cardTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  cardRole: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    marginTop: '1px',
  },
  cardSubText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginTop: '2px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardTimer: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
    marginTop: '1px',
  },
  cardActions: {
    marginTop: tokens.spacingVerticalXS,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  reviewerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    marginTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Status badge + description helpers
// ---------------------------------------------------------------------------

function statusLabel(s: StepStatus) {
  if (s === 'pending')   return 'Pending';
  if (s === 'started')   return 'In Progress';
  if (s === 'completed') return 'Complete';
  if (s === 'skipped')   return 'Skipped';
  if (s === 'failed')    return 'Failed';
  return s;
}

function StatusBadge({ status, isAwaiting }: { status: StepStatus; isAwaiting?: boolean }) {
  const s = useNodeStyles();
  if (isAwaiting) {
    return (
      <span className={`${s.statusBadge} ${s.badgeAwaiting}`}>
        <PersonClockRegular fontSize={10} aria-hidden="true" />
        Awaiting
      </span>
    );
  }
  const badgeClass = {
    pending:   s.badgePending,
    started:   s.badgeStarted,
    completed: s.badgeCompleted,
    skipped:   s.badgeSkipped,
    failed:    s.badgeFailed,
  }[status];
  const Icon = {
    pending:   CircleRegular,
    started:   ArrowSyncRegular,
    completed: CheckmarkCircleRegular,
    skipped:   SubtractCircleRegular,
    failed:    DismissCircleRegular,
  }[status];
  return (
    <span className={`${s.statusBadge} ${badgeClass}`}>
      <Icon fontSize={10} aria-hidden="true" />
      {statusLabel(status)}
    </span>
  );
}

function statusDescription(key: ExecutorKey, status: StepStatus): string | null {
  if (status === 'pending') return null;
  if (key === 'agent') {
    if (status === 'started')   return 'Working on task...';
    if (status === 'completed') return 'Finished';
    if (status === 'failed')    return 'Failed';
  }
  if (key === 'rai') {
    if (status === 'started')   return 'Reviewing safety...';
    if (status === 'completed') return 'Passed';
    if (status === 'failed')    return 'Flagged';
  }
  if (key === 'review') {
    if (status === 'started')   return 'Awaiting your review';
    if (status === 'completed') return 'Reviewed';
    if (status === 'skipped')   return 'Skipped';
  }
  if (key === 'merge') {
    if (status === 'started')   return 'Merging...';
    if (status === 'completed') return 'Merged';
    if (status === 'failed')    return 'Merge failed';
    if (status === 'skipped')   return 'Skipped';
  }
  if (key === 'scribe') {
    if (status === 'started')   return 'Logging session...';
    if (status === 'completed') return 'Done';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Elapsed timer helpers
// ---------------------------------------------------------------------------

function formatDuration(ms: number): string {
  const secs = Math.floor(ms / 1000);
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  const s = secs % 60;
  if (mins < 60) return `${mins}m ${s}s`;
  const hrs = Math.floor(mins / 60);
  const m = mins % 60;
  return `${hrs}h ${m}m`;
}

function ElapsedTimer({ startedAt, completedAt }: { startedAt?: number; completedAt?: number }) {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!startedAt || completedAt) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [startedAt, completedAt]);

  if (!startedAt) return null;
  const elapsed = Math.max(0, (completedAt ?? now) - startedAt);
  return <>{formatDuration(elapsed)}</>;
}

// ---------------------------------------------------------------------------
// Custom React Flow node
// Only left (target) and right (source) handles — no hardcoded top/bottom.
// Loop-back arcs are drawn by the LoopbackEdge custom edge component.
// Interactive elements carry the "nopan nodrag" classes so React Flow does
// not swallow click events on buttons / links inside the card.
// ---------------------------------------------------------------------------

function WorkflowNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const { def, state, agentName, agentRoleTitle, runId, executionId, projectId, reviewedBy } = data as WorkflowNodeData;
  const { key, label, Icon } = def;
  const { status, startedAt, completedAt, intent } = state;

  const isActive         = status === 'started' && key !== 'review';
  const isHumanWaiting   = key === 'review' && status === 'started';
  const cardClass = [
    s.card,
    isActive        ? s.cardActive         : '',
    isHumanWaiting  ? s.cardActionRequired : '',
  ].filter(Boolean).join(' ');

  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  // For the agent card while running, prefer the live intent over the static description.
  const rawSubText = statusDescription(key as ExecutorKey, status);
  const subText    = (key === 'agent' && status === 'started' && intent) ? intent : rawSubText;
  // For the agent card use the actual team role title; otherwise the executor's static description.
  const roleText   = key === 'agent' ? (agentRoleTitle ?? def.roleDescription) : def.roleDescription;

  return (
    <div className={cardClass} role="article" aria-label={`${label}: ${statusLabel(status)}`}>
      {/* Standard LR handles — no Top/Bottom, positions are generic */}
      <Handle type="target" position={Position.Left}  style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />

      {/* Status badge */}
      <div className={s.cardHeader}>
        <StatusBadge status={status} isAwaiting={isHumanWaiting} />
      </div>

      {/* Icon + title */}
      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          <Icon fontSize={22} />
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{label}</span>
          <span className={s.cardRole}>{roleText}</span>
          {agentName && <span className={s.cardSubText}>{agentName as string}</span>}
          {subText && <span className={s.cardSubText}>{subText}</span>}
          {startedAt !== undefined && (
            <span className={s.cardTimer}>
              <ElapsedTimer startedAt={startedAt} completedAt={completedAt} />
            </span>
          )}
        </div>
      </div>

      {/* Action buttons — nopan/nodrag prevents React Flow from swallowing clicks */}
      {key === 'agent' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/projects/${projectId}/runs/${runId}/execution/${executionId}`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View execution</Button>
          </Link>
        </div>
      )}
      {key === 'rai' && (status === 'started' || status === 'completed' || status === 'failed') && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/projects/${projectId}/runs/${runId}/execution/${executionId}-rai`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View execution</Button>
          </Link>
        </div>
      )}
      {key === 'scribe' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          {(status === 'started' || status === 'completed' || status === 'failed') && (
            <Link to={`/projects/${projectId}/runs/${runId}/execution/${executionId}-scribe`} style={{ textDecoration: 'none' }}>
              <Button appearance="outline" size="small">View execution</Button>
            </Link>
          )}
          <Link to={`/projects/${projectId}/memories`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View memories</Button>
          </Link>
        </div>
      )}
      {key === 'review' && status === 'started' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/projects/${projectId}/runs/${runId}/execution/${executionId}`} style={{ textDecoration: 'none' }}>
            <Button appearance="primary" size="medium">Review now</Button>
          </Link>
        </div>
      )}
      {key === 'review' && status === 'completed' && reviewedBy && (
        <div className={`${s.reviewerRow} nopan nodrag`}>
          <img
            src={`https://github.com/${reviewedBy}.png?size=28`}
            style={{ width: 28, height: 28, borderRadius: '50%', border: `2px solid ${tokens.colorBrandForeground1}` }}
            alt={reviewedBy as string}
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground2 }}>{reviewedBy as string}</Text>
        </div>
      )}
    </div>
  );
}

const nodeTypes = { workflow: WorkflowNode };

// ---------------------------------------------------------------------------
// Custom loopback edge — topology-agnostic, heuristic orthogonal routing.
//
// Shape: orthogonal path with rounded corners (quadratic bezier at each turn)
// connecting the TOP or BOTTOM CENTER of each card:
//   above: source top → up → corner → left rail → corner → down to target top
//   below: source bottom → down → corner → left rail → corner → up to target bottom
//
// Coordinate derivation (all from guaranteed React Flow props):
//   sx  = sourceX - NODE_W/2   (center-X: right-handle - half-width)
//   tx  = targetX + NODE_W/2   (center-X: left-handle + half-width)
//   sy  = sourceY ± srcHalf    (top or bottom from center-Y ± half measured-height)
//   ty  = targetY ± tgtHalf
//
// Heuristics:
// 1. SIDE — Sort siblings by source X, even=above, odd=below.
// 2. CLEARANCE — apexY clears all intermediate node edges + ARC_GAP.
// 3. STAGGER — Each same-side sibling adds STAGGER px so rails don't overlap.
// ---------------------------------------------------------------------------

const LOOPBACK_STROKE      = 'var(--colorNeutralStroke1)';
const LOOPBACK_TEXT_COLOR  = 'var(--colorNeutralForeground2)';
const ARC_GAP    = 40; // clearance above/below the tallest card in the arc span
const STAGGER    = 36; // extra rail separation per same-side sibling
const CORNER_R   = 10; // radius for corner rounding
// Fallback card height when React Flow hasn't measured yet (should be >= actual max card height)
const CARD_H_FALLBACK = NODE_H * 1.4;

function loopbackPath(sx: number, sy: number, tx: number, ty: number, apexY: number, above: boolean): string {
  // Clamp radius so corners never exceed half the shorter dimension
  const r = Math.min(CORNER_R, Math.abs(sx - tx) / 4, Math.abs(apexY - sy) / 2, Math.abs(apexY - ty) / 2);
  if (above) {
    return [
      `M ${sx},${sy}`,
      `L ${sx},${apexY + r}`,
      `Q ${sx},${apexY} ${sx - r},${apexY}`,
      `L ${tx + r},${apexY}`,
      `Q ${tx},${apexY} ${tx},${apexY + r}`,
      `L ${tx},${ty}`,
    ].join(' ');
  } else {
    return [
      `M ${sx},${sy}`,
      `L ${sx},${apexY - r}`,
      `Q ${sx},${apexY} ${sx - r},${apexY}`,
      `L ${tx + r},${apexY}`,
      `Q ${tx},${apexY} ${tx},${apexY - r}`,
      `L ${tx},${ty}`,
    ].join(' ');
  }
}

function LoopbackEdge({ id, sourceX, sourceY, targetX, targetY, label, data }: EdgeProps) {
  const allEdges = useEdges();
  const allNodes = useNodes();

  const myEdge   = allEdges.find(e => e.id === id);
  const sourceId = myEdge?.source ?? '';
  const targetId = myEdge?.target ?? '';

  const sourceNode = allNodes.find(n => n.id === sourceId);
  const targetNode = allNodes.find(n => n.id === targetId);

  const srcHalf = (sourceNode?.measured?.height ?? NODE_H) / 2;
  const tgtHalf = (targetNode?.measured?.height ?? NODE_H) / 2;

  // --- Heuristic 1: side ---
  const siblings = allEdges
    .filter(e => e.type === 'loopback' && e.target === targetId)
    .sort((a, b) => {
      const ax = allNodes.find(n => n.id === a.source)?.position.x ?? 0;
      const bx = allNodes.find(n => n.id === b.source)?.position.x ?? 0;
      return ax - bx; // nearest source to target first
    });

  const myIndex   = siblings.findIndex(e => e.id === id);
  const autoAbove = myIndex % 2 === 0;
  const above     = data?.above !== undefined ? Boolean(data.above) : autoAbove;

  // --- Derive center-X from handle positions, top/bottom-Y from measured height ---
  const sx = sourceX - NODE_W / 2;  // right-handle → center-X
  const tx = targetX + NODE_W / 2;  // left-handle  → center-X
  const sy = above ? sourceY - srcHalf : sourceY + srcHalf;
  const ty = above ? targetY - tgtHalf : targetY + tgtHalf;

  // --- Heuristic 2: clearance — ALL nodes whose X range overlaps the arc span ---
  // Include source and target: if source is the tallest card, its bottom IS the constraint.
  const minX = Math.min(sx, tx);
  const maxX = Math.max(sx, tx);
  const spannedNodes = allNodes.filter(n => {
    const nl = n.position.x;
    const nr = nl + NODE_W;
    return nr > minX && nl < maxX;
  });

  // Fallback when spannedNodes is empty (source/target adjacent with zero gap)
  const fallbackTop    = sourceY - srcHalf;
  const fallbackBottom = sourceY + srcHalf;

  let apexY: number;
  if (above) {
    const minTop = spannedNodes.length > 0
      ? Math.min(...spannedNodes.map(n => n.position.y))
      : fallbackTop;
    apexY = minTop - ARC_GAP;
  } else {
    const maxBottom = spannedNodes.length > 0
      ? Math.max(...spannedNodes.map(n => n.position.y + (n.measured?.height ?? CARD_H_FALLBACK)))
      : fallbackBottom;
    apexY = maxBottom + ARC_GAP;
  }

  // --- Heuristic 3: stagger same-side siblings ---
  const sameSideBefore = siblings.slice(0, myIndex).filter((e, i) => {
    const sAbove = e.data?.above !== undefined ? Boolean(e.data.above) : i % 2 === 0;
    return sAbove === above;
  }).length;
  apexY += (above ? -1 : 1) * sameSideBefore * STAGGER;

  const d       = loopbackPath(sx, sy, tx, ty, apexY, above);
  const midX    = (sx + tx) / 2;
  const labelY  = above ? apexY - 6 : apexY + 14;
  const markerId = `lb-arrow-${id}`;

  return (
    <>
      <defs>
        <marker
          id={markerId}
          markerWidth="8"
          markerHeight="6"
          refX="6"
          refY="3"
          orient="auto"
        >
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={LOOPBACK_STROKE} />
        </marker>
      </defs>
      <path
        d={d}
        fill="none"
        stroke={LOOPBACK_STROKE}
        strokeWidth={1.5}
        strokeDasharray="5 3"
        markerEnd={`url(#${markerId})`}
      />
      {label != null && (
        <text
          x={midX}
          y={labelY}
          textAnchor="middle"
          fontSize={12}
          fill={LOOPBACK_TEXT_COLOR}
          fontWeight={600}
          style={{ userSelect: 'none', pointerEvents: 'none' }}
        >
          {label as string}
        </text>
      )}
    </>
  );
}

const edgeTypes = { loopback: LoopbackEdge };

// ---------------------------------------------------------------------------
// Edge helpers
// ---------------------------------------------------------------------------

const STROKE_MUTED = 'var(--colorNeutralStroke2)';

function forwardEdge(id: string, source: string, target: string, animated = false): Edge {
  return {
    id,
    source,
    target,
    type: 'default',
    animated,
    style: { stroke: STROKE_MUTED, strokeWidth: 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: STROKE_MUTED, width: 12, height: 12 },
  };
}

function loopbackEdge(id: string, source: string, target: string, label: string): Edge {
  return {
    id,
    source,
    target,
    type: 'loopback',
    label,
    // No data.above/offset — heuristics in LoopbackEdge compute everything automatically.
  };
}

// Forward edges only — fed to dagre. Loop-backs are excluded so dagre doesn't
// invert cycles and corrupt LR rank assignment.
const FORWARD_EDGES: Edge[] = [
  forwardEdge('agent-rai',    'agent',  'rai'),
  forwardEdge('rai-review',   'rai',    'review'),
  forwardEdge('review-merge', 'review', 'merge'),
  forwardEdge('merge-scribe', 'merge',  'scribe'),
];

// Loop-back edges — sides and heights computed automatically by LoopbackEdge.
const LOOPBACK_EDGES: Edge[] = [
  loopbackEdge('rai-agent-revise',    'rai',    'agent', 'Revise'),
  loopbackEdge('review-agent-change', 'review', 'agent', 'Request changes'),
];

const ALL_EDGES: Edge[] = [...FORWARD_EDGES, ...LOOPBACK_EDGES];

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function WorkflowRunPage() {
  const styles = usePageStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const [agentName,      setAgentName]      = useState<string | undefined>(undefined);
  const [agentRoleTitle, setAgentRoleTitle] = useState<string | undefined>(undefined);
  const [runStatus,      setRunStatus]      = useState<string | undefined>(undefined);
  const [reviewedBy,     setReviewedBy]     = useState<string | undefined>(undefined);
  const [executionId,    setExecutionId]    = useState<string | undefined>(undefined);
  const [loading,        setLoading]        = useState(true);

  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;

    Promise.all([
      apiClient.getProjectRuns(projectId),
      apiClient.getTeam(projectId),
    ]).then(([runs, team]) => {
        if (cancelled) return;
        const run = runs.find((r) => r.workflow_run_id === runId);
        const name = run?.agent_name ?? undefined;
        setAgentName(name);
        setRunStatus(run?.status       ?? undefined);
        setReviewedBy(run?.reviewed_by ?? undefined);
        setExecutionId(run?.execution_id ?? undefined);

        // Look up the team member by cast name to get their role title
        if (name) {
          const member = team.members.find(
            m => m.name.toLowerCase() === name.toLowerCase()
          );
          if (member) setAgentRoleTitle(member.role_title);
        }
      })
      .catch(() => { /* non-fatal */ })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, runId]);

  const { events, status: streamStatus } = useRunStream(executionId ?? '', API_KEY, API_URL);

  // Derive executor states from SSE workflow.step events plus semantic review/merge events.
  const executorStates = useMemo<Record<string, ExecutorState>>(() => {
    const map: Record<string, ExecutorState> = {};
    for (const evt of events) {
      if (evt.type === 'workflow.step') {
        const step      = String(evt.payload['step'] ?? '');
        const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
        const evtAgent  = evt.payload['agent_name'] != null ? String(evt.payload['agent_name']) : undefined;
        const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
        const prev = map[step];
        const newState: ExecutorState = { status: evtStatus, agentName: evtAgent ?? prev?.agentName };
        if (evtStatus === 'started') {
          newState.startedAt = !isNaN(tsMs) ? tsMs : undefined;
        } else {
          newState.startedAt = prev?.startedAt;
          if (!isNaN(tsMs)) newState.completedAt = tsMs;
        }
        map[step] = newState;
      } else if (evt.type === 'review.changes_requested') {
        // Belt-and-suspenders: backend now emits workflow.step for review, but handle the
        // semantic event too so older in-flight runs update correctly.
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'completed' };
        }
      } else if (evt.type === 'review.approved') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'completed' };
        }
      } else if (evt.type === 'review.declined') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'completed' };
        }
        if (!map['merge']) {
          map['merge'] = { status: 'skipped' };
        }
      } else if (evt.type === 'merge.completed') {
        if (!map['merge'] || map['merge'].status === 'started') {
          map['merge'] = { ...map['merge'], status: 'completed' };
        }
      } else if (evt.type === 'merge.failed') {
        if (!map['merge'] || map['merge'].status === 'started') {
          map['merge'] = { ...map['merge'], status: 'failed' };
        }
      } else if (evt.type === 'agent.intent') {
        // Track the latest intent message so the agent card shows real progress text.
        const intentText = evt.payload['intent'] != null ? String(evt.payload['intent']) : undefined;
        if (intentText) {
          const prev = map['agent'];
          map['agent'] = { ...prev, status: prev?.status ?? 'started', intent: intentText };
        }
      }
    }

    // Optimistic: if merge completed and stream is done, show Scribe as completed.
    // Scribe events go to a sub-stream ({runId}-scribe) that this page doesn't subscribe to,
    // so we infer completion from the merge outcome.
    const streamDone = streamStatus === 'done' || streamStatus === 'error';
    if (streamDone && map['merge']?.status === 'completed' && !map['scribe']) {
      map['scribe'] = { status: 'completed' };
    }

    // Fallback: stream done but no step events — infer from terminal run status.
    const hasStepEvents = Object.keys(map).length > 0;
    if (!hasStepEvents && streamDone && runStatus) {
      const isTerminal = ['merged', 'declined', 'merge_failed', 'failed', 'completed'].includes(runStatus);
      if (isTerminal) {
        const agentState: StepStatus = runStatus === 'failed' ? 'failed' : 'completed';
        map['agent'] = { status: agentState, agentName };
        if (runStatus !== 'failed') {
          map['rai']    = { status: 'completed' };
          map['scribe'] = { status: 'completed' };
        }
        if (runStatus === 'merged') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'completed' };
        } else if (runStatus === 'declined') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'skipped' };
        } else if (runStatus === 'merge_failed') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'failed' };
        } else if (runStatus === 'completed') {
          map['review'] = { status: 'skipped' };
          map['merge']  = { status: 'skipped' };
        }
      }
    }

    return map;
  }, [events, streamStatus, runStatus, agentName]);

  // Build React Flow nodes; run dagre on forward edges only for LR layout.
  const rfNodes: Node[] = useMemo(() => {
    const raw: Node[] = EXECUTORS.map((def) => ({
      id: def.key,
      type: 'workflow',
      data: {
        def,
        state:           executorStates[def.key] ?? { status: 'pending' },
        agentName:       def.key === 'agent' ? (executorStates['agent']?.agentName ?? agentName) : undefined,
        agentRoleTitle:  def.key === 'agent' ? agentRoleTitle : undefined,
        runId:           runId      ?? '',
        executionId:     executionId ?? '',
        projectId:       projectId  ?? '',
        reviewedBy:      def.key === 'review' ? reviewedBy : undefined,
      } as WorkflowNodeData,
      position: { x: 0, y: 0 },
    }));
    return layoutDag(raw, FORWARD_EDGES, { rankdir: 'LR', rankSep: 60, nodeSep: 30 });
  }, [executorStates, agentName, agentRoleTitle, reviewedBy, executionId, runId, projectId]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId      = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting = streamStatus === 'connecting';

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span aria-hidden="true">/</span>
        <span>Run {shortId}</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Run</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {(loading || isConnecting) && <Spinner size="extra-tiny" aria-label="Loading" />}
      </div>

      {/* React Flow diagram
          - fitView only fits to nodes (loop-back SVG arcs don't affect bounds)
          - elementsSelectable omitted (default true) so pointer events flow to buttons
          - nodesDraggable=false / nodesConnectable=false for read-only mode
      */}
      <div className={styles.dagContainer}>
        <ReactFlow
          nodes={rfNodes}
          edges={ALL_EDGES}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          fitView
          fitViewOptions={{ padding: 0.15, maxZoom: 1.1 }}
          minZoom={0.5}
          nodesDraggable={false}
          nodesConnectable={false}
          nodesFocusable={false}
          edgesFocusable={false}
          panOnScroll={false}
          zoomOnScroll={false}
          zoomOnPinch={false}
          zoomOnDoubleClick={false}
          panOnDrag={false}
          proOptions={{ hideAttribution: true }}
        />
      </div>
    </div>
  );
}