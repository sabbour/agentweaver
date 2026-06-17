import { useCallback, useContext, useEffect, useMemo, useState, createContext } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Spinner,
  Text,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
  AlertRegular,
  ArrowSyncRegular,
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  DismissRegular,
  FolderRegular,
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
import type { TeamDto } from '../api/types';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H } from '../utils/dagLayout';
import { RunWatcher } from '../components/RunWatcher';
import { AgentAvatar } from '../components/AgentAvatar';

// Context lets WorkflowNode (defined outside WorkflowRunPage) open the execution modal
// without needing to pass callbacks through React Flow node data.
const ExecutionModalContext = createContext<((executionId: string) => void) | undefined>(undefined);

// Context to highlight the loopback arc that caused the current active retrigger.
const ActiveEdgeContext = createContext<string | undefined>(undefined);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type StepStatus = 'pending' | 'started' | 'completed' | 'skipped' | 'failed' | 'revise';
type ExecutorKey = 'agent' | 'rai' | 'review' | 'merge' | 'scribe';

interface ExecutorState {
  status: StepStatus;
  agentName?: string;
  intent?: string;      // latest agent.intent text — replaces static "Working on task..."
  reviewer?: string;    // GitHub username of the human reviewer (review step only)
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
  modelId?: string;          // model used for the agent executor
  runId: string;
  executionId: string;
  projectId: string;
  reviewedBy?: string;
  runOutcome?: { achieved: boolean; reason: string };
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
  badgeAwaiting:  { backgroundColor: tokens.colorPaletteMarigoldBorderActive,     color: tokens.colorNeutralForegroundInverted },
  badgeCompleted: { backgroundColor: tokens.colorPaletteGreenBackground2,         color: tokens.colorPaletteGreenForeground1 },
  badgeSkipped:   { backgroundColor: tokens.colorPaletteLightTealBackground2,     color: tokens.colorPaletteLightTealForeground2 },
  badgeFailed:    { backgroundColor: tokens.colorPaletteRedBackground2,           color: tokens.colorPaletteRedForeground1 },
  badgeRevise:    { backgroundColor: tokens.colorStatusWarningBackground2,        color: tokens.colorStatusWarningForeground1 },
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
  cardModel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    fontFamily: tokens.fontFamilyMonospace,
    marginTop: '2px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardActions: {
    marginTop: tokens.spacingVerticalXS,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    '& button': { width: '100%' },
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
  if (s === 'revise')    return 'Revise';
  return s;
}

function StatusBadge({ status, isAwaiting, label: labelOverride }: { status: StepStatus; isAwaiting?: boolean; label?: string }) {
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
    revise:    s.badgeRevise,
  }[status];
  const Icon = {
    pending:   CircleRegular,
    started:   ArrowSyncRegular,
    completed: CheckmarkCircleRegular,
    skipped:   SubtractCircleRegular,
    failed:    DismissCircleRegular,
    revise:    AlertRegular,
  }[status];
  return (
    <span className={`${s.statusBadge} ${badgeClass}`}>
      <Icon fontSize={10} aria-hidden="true" />
      {labelOverride ?? statusLabel(status)}
    </span>
  );
}

function statusDescription(key: ExecutorKey, status: StepStatus): string | null {
  if (status === 'pending') return null;
  if (key === 'agent') {
    if (status === 'started')   return 'Working on task...';
    if (status === 'completed') return 'Finished';
    if (status === 'revise')    return 'Task not achieved';
    if (status === 'failed')    return 'Failed';
  }
  if (key === 'rai') {
    if (status === 'started')   return 'Reviewing safety...';
    if (status === 'completed') return 'Passed';
    if (status === 'revise')    return 'Revision requested';
    if (status === 'failed')    return 'Flagged';
  }
  if (key === 'review') {
    if (status === 'started')   return 'Awaiting your review';
    if (status === 'completed') return 'Reviewed';
    if (status === 'revise')    return 'Revision requested';
    if (status === 'failed')    return 'Declined';
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
    if (status === 'skipped')   return 'Skipped';
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
  const { def, state, agentName, agentRoleTitle, modelId, executionId, projectId, reviewedBy, runOutcome } = data as WorkflowNodeData;
  const { key, label, Icon } = def;
  const { status, startedAt, completedAt, intent } = state;

  const openModal = useContext(ExecutionModalContext);

  // When the agent completed but `report_outcome` flagged achieved=false, show amber warning.
  const effectiveStatus: StepStatus =
    key === 'agent' && status === 'completed' && runOutcome?.achieved === false
      ? 'revise'
      : status;

  const isActive         = effectiveStatus === 'started' && key !== 'review';
  const isHumanWaiting   = key === 'review' && effectiveStatus === 'started';
  const cardClass = [
    s.card,
    isActive        ? s.cardActive         : '',
    isHumanWaiting  ? s.cardActionRequired : '',
  ].filter(Boolean).join(' ');

  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  // For the agent card while running, prefer the live intent over the static description.
  const rawSubText = statusDescription(key as ExecutorKey, effectiveStatus);
  const subText    = (key === 'agent' && effectiveStatus === 'started' && intent) ? intent : rawSubText;
  // For the agent card use the actual team role title; otherwise the executor's static description.
  const roleText   = key === 'agent' ? (agentRoleTitle ?? def.roleDescription) : def.roleDescription;

  return (
    <div className={cardClass} role="article" aria-label={`${label}: ${statusLabel(effectiveStatus)}`}>
      {/* Standard LR handles — no Top/Bottom, positions are generic */}
      <Handle type="target" position={Position.Left}  style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />

      {/* Status badge */}
      <div className={s.cardHeader}>
        <StatusBadge
          status={effectiveStatus}
          isAwaiting={isHumanWaiting}
          label={key === 'agent' && effectiveStatus === 'revise' ? 'Incomplete' : undefined}
        />
      </div>

      {/* Icon + title */}
      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          {key === 'agent' && agentName
            ? <AgentAvatar name={agentName as string} size={28} circle />
            : <Icon fontSize={22} />}
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{label}</span>
          <span className={s.cardRole}>{roleText}</span>
          {agentName && <span className={s.cardSubText}>{agentName as string}</span>}
          {modelId && key === 'agent' && <span className={s.cardModel}>{modelId as string}</span>}
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
          <Button appearance="outline" size="small" onClick={() => openModal?.(executionId as string)}>
            View execution
          </Button>
        </div>
      )}
      {key === 'rai' && (status === 'started' || status === 'completed' || status === 'failed' || status === 'revise') && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-rai`)}>
            View execution
          </Button>
        </div>
      )}
      {key === 'scribe' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          {(status === 'started' || status === 'completed' || status === 'failed') && startedAt !== undefined && (
            <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-scribe`)}>
              View execution
            </Button>
          )}
          <Link to={`/projects/${projectId}/memories`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View memories</Button>
          </Link>
        </div>
      )}
      {key === 'merge' && status === 'completed' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" icon={<FolderRegular />} onClick={() => openModal?.(executionId as string)}>
            Browse files
          </Button>
        </div>
      )}
      {key === 'review' && status === 'started' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="primary" size="small" onClick={() => openModal?.(executionId as string)}>
            Review now
          </Button>
        </div>
      )}
      {key === 'review' && (status === 'completed' || status === 'revise') && reviewedBy && (
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

const LOOPBACK_STROKE        = 'var(--colorNeutralStroke1)';
const LOOPBACK_STROKE_ACTIVE = 'var(--colorBrandForeground1)';
const LOOPBACK_TEXT_COLOR    = 'var(--colorNeutralForeground2)';
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
  const activeEdgeId = useContext(ActiveEdgeContext);

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
  const isActive = id === activeEdgeId;
  const stroke   = isActive ? LOOPBACK_STROKE_ACTIVE : LOOPBACK_STROKE;

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
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={stroke} />
        </marker>
      </defs>
      <path
        d={d}
        fill="none"
        stroke={stroke}
        strokeWidth={isActive ? 2 : 1.5}
        strokeDasharray={isActive ? undefined : "5 3"}
        markerEnd={`url(#${markerId})`}
      />
      {label != null && (
        <text
          x={midX}
          y={labelY}
          textAnchor="middle"
          fontSize={12}
          fill={isActive ? LOOPBACK_STROKE_ACTIVE : LOOPBACK_TEXT_COLOR}
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
  const [modelId,        setModelId]        = useState<string | undefined>(undefined);
  const [runStatus,      setRunStatus]      = useState<string | undefined>(undefined);
  const [reviewedBy,     setReviewedBy]     = useState<string | undefined>(undefined);
  const [executionId,    setExecutionId]    = useState<string | undefined>(undefined);
  const [loading,        setLoading]        = useState(true);
  const [modalExecId,    setModalExecId]    = useState<string | undefined>(undefined);
  const [team,           setTeam]           = useState<TeamDto | undefined>(undefined);

  const openExecutionModal = useCallback((id: string) => setModalExecId(id), []);

  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;

    Promise.all([
      apiClient.getProjectRuns(projectId),
      apiClient.getTeam(projectId),
    ]).then(([runs, teamData]) => {
        if (cancelled) return;
        const run = runs.find((r) => r.workflow_run_id === runId);
        const name = run?.agent_name ?? undefined;
        setAgentName(name);
        setRunStatus(run?.status       ?? undefined);
        setReviewedBy(run?.reviewed_by ?? undefined);
        setExecutionId(run?.execution_id ?? undefined);
        setModelId(run?.model_id ?? undefined);
        setTeam(teamData);

        // Look up the team member by cast name to get their role title
        if (name) {
          const member = teamData.members.find(
            m => m.name.toLowerCase() === name.toLowerCase()
          );
          if (member) setAgentRoleTitle(member.role_title);
        }
      })
      .catch(() => { /* non-fatal */ })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, runId]);

  const { events, status: streamStatus, reconnect } = useRunStream(executionId ?? '', API_KEY, API_URL);

  // Extract the run outcome (report_outcome { achieved, reason }) from the event stream.
  const runOutcome = useMemo<{ achieved: boolean; reason: string } | undefined>(() => {
    for (let i = events.length - 1; i >= 0; i--) {
      if (events[i].type === 'run.outcome') {
        const p = events[i].payload;
        return { achieved: p['achieved'] as boolean, reason: String(p['reason'] ?? '') };
      }
    }
    return undefined;
  }, [events]);

  // Derive executor states from SSE workflow.step events plus semantic review/merge events.
  const executorStates = useMemo<Record<string, ExecutorState>>(() => {
    const map: Record<string, ExecutorState> = {};
    for (const evt of events) {
      if (evt.type === 'workflow.step') {
        const step      = String(evt.payload['step'] ?? '');
        const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
        const evtAgent  = evt.payload['agent_name'] != null ? String(evt.payload['agent_name']) : undefined;
        const evtReviewer = evt.payload['reviewer'] != null ? String(evt.payload['reviewer']) : undefined;
        const tsStr = evt.payload['timestamp_utc'] != null ? String(evt.payload['timestamp_utc']) : undefined;
        const tsMs = tsStr ? new Date(tsStr).getTime() : NaN;
        const prev = map[step];
        const newState: ExecutorState = { status: evtStatus, agentName: evtAgent ?? prev?.agentName, reviewer: evtReviewer ?? prev?.reviewer };
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
          map['review'] = { ...map['review'], status: 'revise', completedAt: Date.now() };
        }
      } else if (evt.type === 'review.approved') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'completed', completedAt: Date.now() };
        }
      } else if (evt.type === 'review.declined') {
        if (!map['review'] || map['review'].status === 'started') {
          map['review'] = { ...map['review'], status: 'failed', completedAt: Date.now() };
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

    // Optimistic: if stream is done and merge completed but no scribe events arrived,
    // treat as skipped (scribe was skipped due to missing project/agent context, not completed).
    const streamDone = streamStatus === 'done' || streamStatus === 'error';
    if (streamDone && map['merge']?.status === 'completed' && !map['scribe']) {
      map['scribe'] = { status: 'skipped' };
    }

    // Optimistic: if stream is done and agent completed but no review/merge events arrived,
    // the run had no changes — both steps were skipped by the workflow.
    if (streamDone && map['agent']?.status === 'completed' && !map['review'] && !map['merge']) {
      map['review'] = { status: 'skipped' };
      map['merge']  = { status: 'skipped' };
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

  // Determine which loopback arc is "active" (lit in blue) based on executor states.
  // rai→agent: active when Rai requested a revision and agent is now running again.
  // review→agent: active when review completed via request-changes and agent is running.
  const activeLoopbackId = useMemo<string | undefined>(() => {
    const raiStatus    = executorStates['rai']?.status;
    const agentStatus  = executorStates['agent']?.status;
    const mergeStatus  = executorStates['merge']?.status;
    if (raiStatus === 'revise' && agentStatus === 'started') return 'rai-agent-revise';
    // review→agent: review requested changes but no merge started → request-changes loop
    if (agentStatus === 'started' && !mergeStatus && executorStates['review']?.status === 'revise') {
      return 'review-agent-change';
    }
    return undefined;
  }, [executorStates]);

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
        modelId:         def.key === 'agent' ? modelId : undefined,
        runId:           runId      ?? '',
        executionId:     executionId ?? '',
        projectId:       projectId  ?? '',
        reviewedBy:      def.key === 'review' ? (executorStates['review']?.reviewer ?? reviewedBy) : undefined,
        runOutcome:      def.key === 'agent' ? runOutcome : undefined,
      } as WorkflowNodeData,
      position: { x: 0, y: 0 },
    }));
    return layoutDag(raw, FORWARD_EDGES, { rankdir: 'LR', rankSep: 60, nodeSep: 30 });
  }, [executorStates, agentName, agentRoleTitle, modelId, reviewedBy, executionId, runId, projectId, runOutcome]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId      = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting = streamStatus === 'connecting';
  const projectName  = team?.project_name ?? projectId;

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{projectName}</Link>
        <span aria-hidden="true">/</span>
        <span>Run {shortId}</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Run</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {(loading || isConnecting) && <Spinner size="extra-tiny" aria-label="Loading" />}
      </div>

      {/* React Flow diagram wrapped in contexts so WorkflowNode can open the modal and arc highlighting works */}
      <ExecutionModalContext.Provider value={openExecutionModal}>
      <ActiveEdgeContext.Provider value={activeLoopbackId}>
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
      </ActiveEdgeContext.Provider>
      </ExecutionModalContext.Provider>

      {/* Execution detail modal */}
      <Dialog open={!!modalExecId} onOpenChange={(_, d) => { if (!d.open) setModalExecId(undefined); }}>
        <DialogSurface style={{ maxWidth: '90vw', width: '900px', maxHeight: '90vh' }}>
          <DialogBody style={{ height: '80vh', display: 'flex', flexDirection: 'column' }}>
            <DialogTitle
              action={
                <Button
                  appearance="subtle"
                  aria-label="Close"
                  icon={<DismissRegular />}
                  onClick={() => setModalExecId(undefined)}
                />
              }
            >
              Execution {modalExecId ? modalExecId.slice(0, 8) : ''}
            </DialogTitle>
            <DialogContent style={{ flex: 1, overflow: 'hidden', padding: 0, display: 'flex', flexDirection: 'column' }}>
              {modalExecId && <RunWatcher key={modalExecId} runId={modalExecId} style={{ height: '100%' }} onReviewAction={reconnect} />}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setModalExecId(undefined)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}