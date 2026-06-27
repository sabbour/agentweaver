/**
 * WorkflowGraphPanel — shared generic workflow graph renderer.
 *
 * Provides the reusable WorkflowNode card, LoopbackEdge, edge helpers, styles, and
 * contexts consumed by both WorkflowRunPage (agent pipeline) and CoordinatorRunPage
 * (unified coordinator + subtask + planned-assembly view).
 *
 * Reuse rule: import from here; do NOT copy these definitions into page files.
 */
import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
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
  FolderRegular,
  MergeRegular,
  NotebookRegular,
  PersonClockRegular,
  PersonRegular,
  ShieldRegular,
  ShieldKeyholeRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import {
  Handle,
  MarkerType,
  Panel,
  Position,
  ReactFlow,
  useEdges,
  useNodes,
  type Edge,
  type EdgeProps,
  type Node,
  type NodeProps,
} from '@xyflow/react';
import { apiClient } from '../api/apiClient';
import { AgentAvatar } from './AgentAvatar';
import { PodIndicator } from './PodIndicator';
import { useRuntimeInfo } from '../hooks/useRuntimeInfo';
import type { GraphNodeType, WorkflowGraphDto } from '../api/types';
import { NODE_W, NODE_H, NODE_TYPE_W, NODE_TYPE_H, layoutDag } from '../utils/dagLayout';

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export type StepStatus = 'pending' | 'started' | 'completed' | 'skipped' | 'failed' | 'revise';

export interface ExecutorDef {
  key: string;
  label: string;
  roleDescription: string;
  Icon: FluentIcon;
}

export interface ExecutorState {
  status: StepStatus;
  agentName?: string;
  intent?: string;
  reviewer?: string;
  startedAt?: number;
  completedAt?: number;
  /** Short human-readable status line from the backend workflow.step payload.message field. */
  message?: string;
  /** Per-step execution pod name from workflow.step SSE (spec-018). Null today; non-null per-agent after distributed phases. */
  executionPodName?: string | null;
}

/** Data passed into every React Flow WorkflowNode.  Optional fields are ignored when
 *  absent, so the same component type works in both workflow-run and coordinator views. */
export interface WorkflowNodeData extends Record<string, unknown> {
  def: ExecutorDef;
  state: ExecutorState;
  /** node_type drives card width and shape. */
  nodeType?: GraphNodeType;
  isPlanned?: boolean;
  agentName?: string;
  agentRoleTitle?: string;
  modelId?: string;
  runId?: string;
  executionId?: string;
  projectId?: string;
  reviewedBy?: string;
  runOutcome?: { achieved: boolean; reason: string };
  runDegraded?: { toolName: string; reason: string };
  /** Per-node execution pod name (spec-018). Null today (global fallback used); non-null per-agent after distributed phases. */
  executionPodName?: string | null;
  /** Layout direction for handle placement. 'LR' (default) = left/right; 'TB' = top/bottom. */
  dir?: 'LR' | 'TB';
  /** When true and the node is running, an orange tool-approval badge is shown. */
  hasPendingApproval?: boolean;
}

// ---------------------------------------------------------------------------
// Contexts — provided at page level, consumed by node/edge components
// ---------------------------------------------------------------------------

/** WorkflowRunPage: open the execution detail modal for a given executionId. */
export const ExecutionModalContext = createContext<((executionId: string) => void) | undefined>(undefined);

/** WorkflowRunPage: id of the active loopback edge (highlighted in blue). */
export const ActiveEdgeContext = createContext<string | undefined>(undefined);

/** CoordinatorRunPage: open/scroll to the all-up orchestration session panel. */
export const CoordinatorSessionContext = createContext<(() => void) | undefined>(undefined);

/**
 * CoordinatorRunPage: the Merge stage's "Browse files" opens the assembled filesystem in the
 * project Workspace instead of the per-run execution modal.
 */
export const BrowseFilesContext = createContext<((executionId: string) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Role → icon / description helpers (exported so pages can build node data)
// ---------------------------------------------------------------------------

export function roleDescForRole(role: string): string {
  const map: Record<string, string> = {
    agent:       'AI Assistant',
    rai:         'RAI Reviewer',
    review:      'Human Review',
    merge:       'Merge Coordinator',
    scribe:      'Session Logger',
    coordinator: 'Coordinator',
    subtask:     'Subtask Agent',
    assembly:    'Awaiting collective assembly',
  };
  return map[role] ?? role;
}

export function iconForRole(role: string): FluentIcon {
  const map: Record<string, FluentIcon> = {
    agent:       BotRegular,
    rai:         ShieldRegular,
    review:      PersonRegular,
    merge:       MergeRegular,
    scribe:      NotebookRegular,
    coordinator: BotRegular,
    subtask:     BotRegular,
    assembly:    CheckmarkCircleRegular,
  };
  return map[role] ?? CircleRegular;
}

// ---------------------------------------------------------------------------
// Styles — shared card styles
// ---------------------------------------------------------------------------

export const useNodeStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '14px',
    boxSizing: 'border-box',
    position: 'relative',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
    cursor: 'default',
  },
  // node_type=agent: primary / largest
  cardAgent: {
    width: `${NODE_TYPE_W.agent}px`,
  },
  // node_type=gate: decision shape (dashed border, slightly narrower)
  cardGate: {
    width: `${NODE_TYPE_W.gate}px`,
    borderRadius: '4px',
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
  },
  // node_type=action: smaller secondary (e.g. Merge, Scribe)
  cardAction: {
    width: `${NODE_TYPE_W.action}px`,
  },
  // node_type=terminal: small endpoint
  cardTerminal: {
    width: `${NODE_TYPE_W.terminal}px`,
    borderRadius: '12px',
  },
  // node_type=subtask: medium-large expandable node
  cardSubtask: {
    width: `${NODE_TYPE_W.subtask}px`,
  },
  // default / legacy width when node_type is absent
  cardDefault: {
    width: `${NODE_W}px`,
  },
  cardActive: {
    borderLeft: `3px solid ${tokens.colorBrandForeground1}`,
    backgroundColor: tokens.colorBrandBackground2,
    animationName: {
      '0%':   { boxShadow: `0 0 0 0 ${tokens.colorBrandStroke1}` },
      '70%':  { boxShadow: `0 0 0 5px transparent` },
      '100%': { boxShadow: `0 0 0 0 transparent` },
    },
    animationDuration: '1.8s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  cardActionRequired: {
    border: `2px solid ${tokens.colorPaletteMarigoldBorderActive}`,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    animationName: {
      '0%':   { boxShadow: `0 0 0 0 ${tokens.colorPaletteMarigoldBorderActive}` },
      '70%':  { boxShadow: `0 0 0 5px transparent` },
      '100%': { boxShadow: `0 0 0 0 transparent` },
    },
    animationDuration: '2s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  // Continuous rotation for the in-progress (started) badge's sync icon so a running stage reads as
  // actively working at a glance. Honours reduced-motion.
  spinIcon: {
    animationName: {
      from: { transform: 'rotate(0deg)' },
      to:   { transform: 'rotate(360deg)' },
    },
    animationDuration: '1.4s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'linear',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  cardPlanned: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    opacity: 0.6,
  },
  cardHeader: {
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
  },
  // Orange tool-approval badge overlay in the top-right corner of a running node.
  approvalBadge: {
    position: 'absolute',
    top: '-6px',
    right: '-6px',
    width: '20px',
    height: '20px',
    borderRadius: '999px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorStatusWarningBackground3,
    color: tokens.colorNeutralForegroundInverted,
    boxShadow: tokens.shadow4,
    zIndex: 1,
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
    flex: 1,
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
  cardFooter: {
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
    marginTop: '2px',
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
// StatusBadge
// ---------------------------------------------------------------------------

function statusLabel(s: StepStatus): string {
  if (s === 'pending')   return 'Pending';
  if (s === 'started')   return 'In Progress';
  if (s === 'completed') return 'Complete';
  if (s === 'skipped')   return 'Skipped';
  if (s === 'failed')    return 'Failed';
  if (s === 'revise')    return 'Revise';
  return s;
}

export function StatusBadge({
  status,
  isAwaiting,
  isPlanned,
  label: labelOverride,
}: {
  status: StepStatus;
  isAwaiting?: boolean;
  isPlanned?: boolean;
  label?: string;
}) {
  const s = useNodeStyles();
  if (isPlanned) {
    return <span className={`${s.statusBadge} ${s.badgePending}`}>Planned</span>;
  }
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
  const BadgeIcon = {
    pending:   CircleRegular,
    started:   ArrowSyncRegular,
    completed: CheckmarkCircleRegular,
    skipped:   SubtractCircleRegular,
    failed:    DismissCircleRegular,
    revise:    AlertRegular,
  }[status];
  return (
    <span className={`${s.statusBadge} ${badgeClass}`}>
      <BadgeIcon fontSize={10} aria-hidden="true" className={status === 'started' ? s.spinIcon : undefined} />
      {labelOverride ?? statusLabel(status)}
    </span>
  );
}

// ---------------------------------------------------------------------------
// ElapsedTimer
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

export function ElapsedTimer({ startedAt, completedAt }: { startedAt?: number; completedAt?: number }) {
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
// statusDescription helper (exported for use in page files)
// ---------------------------------------------------------------------------

export function statusDescription(key: string, status: StepStatus): string | null {
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
  if (key === 'assemble-ready') {
    if (status === 'started')   return 'Awaiting assembly...';
    if (status === 'completed') return 'Ready for assembly';
    if (status === 'failed')    return 'Failed';
  }
  return null;
}

// ---------------------------------------------------------------------------
// WorkflowNode — generic card component.
// node_type drives width/shape class; role drives icon and colour.
// ---------------------------------------------------------------------------

export function WorkflowNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const {
    def,
    state,
    nodeType,
    isPlanned,
    agentName,
    agentRoleTitle,
    modelId,
    executionId,
    projectId,
    reviewedBy,
    runOutcome,
    runDegraded,
    hasPendingApproval,
    executionPodName: nodeExecutionPodName,
  } = data as WorkflowNodeData;
  const { podName: globalPodName } = useRuntimeInfo();
  // Per-node pod name takes priority; fall back to the global API pod when on k8s.
  const resolvedPodName = (nodeExecutionPodName as string | null | undefined) ?? globalPodName;
  const { key, label, Icon } = def;
  const { status, startedAt, completedAt, intent, message } = state;

  const openModal = useContext(ExecutionModalContext);
  const openSession = useContext(CoordinatorSessionContext);
  const browseFiles = useContext(BrowseFilesContext);

  const effectiveStatus: StepStatus =
    key === 'agent' && status === 'completed' && (runOutcome?.achieved === false || runDegraded !== undefined)
      ? 'revise'
      : status;

  const degradedReason =
    key === 'agent' && runDegraded !== undefined && runOutcome?.achieved !== false
      ? `Blocked: ${runDegraded.reason}`
      : undefined;

  const isActive       = effectiveStatus === 'started' && key !== 'review';
  const isHumanWaiting = key === 'review' && effectiveStatus === 'started';

  // Pick node-type-specific width class (planned nodes keep default width)
  const widthClass = isPlanned
    ? s.cardDefault
    : nodeType === 'agent'    ? s.cardAgent
    : nodeType === 'gate'     ? s.cardGate
    : nodeType === 'action'   ? s.cardAction
    : nodeType === 'terminal' ? s.cardTerminal
    : nodeType === 'subtask'  ? s.cardSubtask
    :                           s.cardDefault;

  const cardClass = [
    s.card,
    widthClass,
    isActive        ? s.cardActive         : '',
    isHumanWaiting  ? s.cardActionRequired : '',
    isPlanned       ? s.cardPlanned        : '',
  ].filter(Boolean).join(' ');

  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  const targetPos = (data as WorkflowNodeData).dir === 'TB' ? Position.Top : Position.Left;
  const sourcePos = (data as WorkflowNodeData).dir === 'TB' ? Position.Bottom : Position.Right;
  const rawSubText = statusDescription(key, effectiveStatus);
  // message (from workflow.step payload) takes priority over the hardcoded statusDescription fallback.
  const subText    = degradedReason ?? ((key === 'agent' && effectiveStatus === 'started' && intent) ? intent : (message ?? rawSubText));
  const roleText   = key === 'agent' ? (agentRoleTitle ?? def.roleDescription) : def.roleDescription;

  return (
    <>
      <PodIndicator podName={resolvedPodName} />
      <div
        className={cardClass}
        role="article"
        aria-label={`${label}: ${statusLabel(effectiveStatus)}`}
        data-node-type={nodeType ?? 'default'}
      >
      <Handle type="target" position={targetPos} style={handleStyle} />
      <Handle type="source" position={sourcePos} style={handleStyle} />

      {hasPendingApproval && status === 'started' && (
        <div
          className={s.approvalBadge}
          role="img"
          aria-label="Tool approval required"
          title="Tool approval required"
        >
          <ShieldKeyholeRegular fontSize={12} aria-hidden="true" />
        </div>
      )}

      <div className={s.cardHeader}>
        <StatusBadge
          status={effectiveStatus}
          isAwaiting={isHumanWaiting}
          isPlanned={!!isPlanned}
          label={key === 'agent' && effectiveStatus === 'revise' ? 'Incomplete' : undefined}
        />
      </div>

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
        </div>
      </div>

      {key === 'coordinator' && !isPlanned && openSession && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" onClick={() => openSession()}>
            View session
          </Button>
        </div>
      )}
      {key === 'agent' && !isPlanned && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" onClick={() => openModal?.(executionId as string)}>
            View execution
          </Button>
        </div>
      )}
      {key === 'rai' && !isPlanned && (status === 'started' || status === 'completed' || status === 'failed' || status === 'revise') && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-rai`)}>
            View execution
          </Button>
        </div>
      )}
      {key === 'scribe' && !isPlanned && (
        <div className={`${s.cardActions} nopan nodrag`}>
          {(status === 'started' || status === 'completed' || status === 'failed') && startedAt !== undefined && (
            <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-scribe`)}>
              View execution
            </Button>
          )}
          <Link to={`/projects/${projectId as string}/memories`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View memories</Button>
          </Link>
        </div>
      )}
      {key === 'merge' && !isPlanned && status === 'completed' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="outline" size="small" icon={<FolderRegular />} onClick={() => (browseFiles ?? openModal)?.(executionId as string)}>
            Browse files
          </Button>
        </div>
      )}
      {key === 'review' && !isPlanned && status === 'started' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button appearance="primary" size="small" onClick={() => openModal?.(executionId as string)}>
            Review now
          </Button>
        </div>
      )}
      {key === 'review' && !isPlanned && (status === 'completed' || status === 'revise') && reviewedBy && (
        <div className={`${s.reviewerRow} nopan nodrag`}>
          <img
            src={`https://github.com/${reviewedBy as string}.png?size=28`}
            style={{ width: 28, height: 28, borderRadius: '50%', border: `2px solid ${tokens.colorBrandForeground1}` }}
            alt={reviewedBy as string}
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground2 }}>{reviewedBy as string}</Text>
        </div>
      )}

      {startedAt !== undefined && (
        <div className={s.cardFooter}>
          <span className={s.cardTimer}>
            <ElapsedTimer startedAt={startedAt} completedAt={completedAt} />
          </span>
        </div>
      )}
    </div>
    </>
  );
}

/** ReactFlow node types map for workflow nodes. Spread or use directly. */
export const workflowNodeTypes = { workflow: WorkflowNode };

// ---------------------------------------------------------------------------
// Loopback edge — orthogonal path with clearance heuristics
// ---------------------------------------------------------------------------

const LOOPBACK_STROKE        = 'var(--colorNeutralStroke1)';
const LOOPBACK_STROKE_ACTIVE = 'var(--colorBrandForeground1)';
const LOOPBACK_TEXT_COLOR    = 'var(--colorNeutralForeground2)';
const ARC_GAP         = 40;
const STAGGER         = 36;
const CORNER_R        = 10;
const CARD_H_FALLBACK = NODE_H * 1.4;

function loopbackPath(sx: number, sy: number, tx: number, ty: number, apexY: number, above: boolean): string {
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
  }
  return [
    `M ${sx},${sy}`,
    `L ${sx},${apexY - r}`,
    `Q ${sx},${apexY} ${sx - r},${apexY}`,
    `L ${tx + r},${apexY}`,
    `Q ${tx},${apexY} ${tx},${apexY - r}`,
    `L ${tx},${ty}`,
  ].join(' ');
}

export function LoopbackEdge({ id, sourceX, sourceY, targetX, targetY, label, data }: EdgeProps) {
  const allEdges = useEdges();
  const allNodes = useNodes();
  const activeEdgeId = useContext(ActiveEdgeContext);

  const myEdge   = allEdges.find(e => e.id === id);
  const sourceId = myEdge?.source ?? '';
  const targetId = myEdge?.target ?? '';

  const sourceNode = allNodes.find(n => n.id === sourceId);
  const targetNode = allNodes.find(n => n.id === targetId);

  // Use measured width per node so differently-sized cards are routed correctly.
  const srcW    = sourceNode?.measured?.width  ?? NODE_W;
  const tgtW    = targetNode?.measured?.width  ?? NODE_W;
  const srcHalf = (sourceNode?.measured?.height ?? NODE_H) / 2;
  const tgtHalf = (targetNode?.measured?.height ?? NODE_H) / 2;

  const siblings = allEdges
    .filter(e => e.type === 'loopback' && e.target === targetId)
    .sort((a, b) => {
      const ax = allNodes.find(n => n.id === a.source)?.position.x ?? 0;
      const bx = allNodes.find(n => n.id === b.source)?.position.x ?? 0;
      return ax - bx;
    });

  const myIndex   = siblings.findIndex(e => e.id === id);
  const autoAbove = myIndex % 2 === 0;
  const above     = data?.above !== undefined ? Boolean(data.above) : autoAbove;

  const sx = sourceX - srcW / 2;
  const tx = targetX + tgtW / 2;
  const sy = above ? sourceY - srcHalf : sourceY + srcHalf;
  const ty = above ? targetY - tgtHalf : targetY + tgtHalf;

  const minX = Math.min(sx, tx);
  const maxX = Math.max(sx, tx);
  const spannedNodes = allNodes.filter(n => {
    const nl = n.position.x;
    const nr = nl + (n.measured?.width ?? NODE_W);
    return nr > minX && nl < maxX;
  });

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

  const sameSideBefore = siblings.slice(0, myIndex).filter((e, i) => {
    const sAbove = e.data?.above !== undefined ? Boolean(e.data.above) : i % 2 === 0;
    return sAbove === above;
  }).length;
  apexY += (above ? -1 : 1) * sameSideBefore * STAGGER;

  const d        = loopbackPath(sx, sy, tx, ty, apexY, above);
  const midX     = (sx + tx) / 2;
  const labelY   = above ? apexY - 6 : apexY + 14;
  const markerId = `lb-arrow-${id}`;
  const isActive = id === activeEdgeId;
  const stroke   = isActive ? LOOPBACK_STROKE_ACTIVE : LOOPBACK_STROKE;

  return (
    <>
      <defs>
        <marker id={markerId} markerWidth="8" markerHeight="6" refX="6" refY="3" orient="auto">
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={stroke} />
        </marker>
      </defs>
      <path
        d={d}
        fill="none"
        stroke={stroke}
        strokeWidth={isActive ? 2 : 1.5}
        strokeDasharray={isActive ? undefined : '5 3'}
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

/** ReactFlow edge types map including the loopback edge. */
export const workflowEdgeTypes = { loopback: LoopbackEdge };

// ---------------------------------------------------------------------------
// Edge builder helpers (exported so pages can build edge arrays)
// ---------------------------------------------------------------------------

const STROKE_MUTED = 'var(--colorNeutralStroke2)';

export function forwardEdge(id: string, source: string, target: string, animated = false): Edge {
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

export function loopbackEdge(id: string, source: string, target: string, label: string): Edge {
  return { id, source, target, type: 'loopback', label };
}

// Derive a human-readable label for a coordinator-level loopback back-edge from the SOURCE
// node's role (falling back to its id) so it is robust across descriptor id schemes. Tank adds
// two coordinator loopbacks: rai→coordinator (re-dispatch on RAI flags) and review→coordinator
// (request changes). GraphEdge carries no label field, so the renderer computes it here.
export function coordinatorLoopbackLabel(sourceRole: string | undefined, sourceId: string | undefined): string {
  const role = (sourceRole ?? '').toLowerCase();
  if (role.includes('rai'))    return 'RAI flags';
  if (role.includes('review')) return 'Request changes';
  const id = (sourceId ?? '').toLowerCase();
  if (id.includes('rai'))    return 'RAI flags';
  if (id.includes('review')) return 'Request changes';
  return 'Rework';
}

// ---------------------------------------------------------------------------
// WorkflowDefinitionInlinePanel — read-only static graph for WorkflowsPage
// ---------------------------------------------------------------------------

const useInlinePanelStyles = makeStyles({
  container: {
    height: '320px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    '& .react-flow__renderer': { borderRadius: '8px' },
  },
  loadingWrap: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '320px',
  },
});

const FIT_VIEW_OPTS = { padding: 0.2, maxZoom: 1.1 };

/**
 * Fetches the static graph descriptor for a workflow definition and renders a
 * read-only ReactFlow graph. Intended for inline expansion in WorkflowsPage.
 */
export function WorkflowDefinitionInlinePanel({
  projectId,
  workflowId,
}: {
  projectId: string;
  workflowId: string;
}) {
  const s = useInlinePanelStyles();
  const [graph, setGraph]     = useState<WorkflowGraphDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    apiClient.getWorkflowGraph(projectId, workflowId)
      .then((g) => { if (!cancelled) { setGraph(g); setLoading(false); } })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : String(err));
          setLoading(false);
        }
      });
    return () => { cancelled = true; };
  }, [projectId, workflowId]);

  const rfEdges = useMemo<Edge[]>(() => {
    if (!graph) return [];
    return graph.edges.map((e) =>
      e.loopback
        ? loopbackEdge(`${e.from}->${e.to}`, e.from, e.to, e.label ?? '')
        : forwardEdge(`${e.from}->${e.to}`, e.from, e.to),
    );
  }, [graph]);

  const rfNodes = useMemo<Node[]>(() => {
    if (!graph) return [];
    const forwardOnly = rfEdges.filter((e) => e.type !== 'loopback');
    const hints: Record<string, { width: number; height: number }> = {};
    const raw: Node[] = graph.nodes.map((n) => {
      const nt = n.node_type;
      hints[n.id] = {
        width:  NODE_TYPE_W[nt ?? ''] ?? NODE_W,
        height: NODE_TYPE_H[nt ?? ''] ?? NODE_H,
      };
      return {
        id: n.id,
        type: 'workflow',
        position: { x: 0, y: 0 },
        data: {
          def: {
            key:             n.role,
            label:           n.label,
            roleDescription: roleDescForRole(n.role),
            Icon:            iconForRole(n.role),
          },
          state:     { status: 'pending' },
          nodeType:  nt,
          isPlanned: true,
        } as WorkflowNodeData,
      };
    });
    return layoutDag(raw, forwardOnly, { rankdir: 'LR', rankSep: 60, nodeSep: 20 }, hints);
  }, [graph, rfEdges]);

  if (loading) {
    return (
      <div className={s.loadingWrap}>
        <Spinner size="small" label="Loading graph" />
      </div>
    );
  }

  if (error) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error}</MessageBarBody>
      </MessageBar>
    );
  }

  if (!graph || graph.nodes.length === 0) return null;

  return (
    <ExecutionModalContext.Provider value={undefined}>
      <ActiveEdgeContext.Provider value={undefined}>
        <div className={s.container}>
          <ReactFlow
            nodes={rfNodes}
            edges={rfEdges}
            nodeTypes={workflowNodeTypes}
            edgeTypes={workflowEdgeTypes}
            fitView
            fitViewOptions={FIT_VIEW_OPTS}
            nodesDraggable={false}
            nodesConnectable={false}
            nodesFocusable={false}
            edgesFocusable={false}
            panOnScroll={false}
            zoomOnScroll
            zoomActivationKeyCode={['Meta', 'Control']}
            zoomOnPinch
            zoomOnDoubleClick={false}
            panOnDrag
            proOptions={{ hideAttribution: true }}
          >
            <Panel position="bottom-right">
              <span style={{ fontSize: '11px', color: 'var(--colorNeutralForeground3)', padding: '2px 6px', background: 'var(--colorNeutralBackground1)', borderRadius: '4px', border: '1px solid var(--colorNeutralStroke2)' }}>
                Read-only
              </span>
            </Panel>
          </ReactFlow>
        </div>
      </ActiveEdgeContext.Provider>
    </ExecutionModalContext.Provider>
  );
}
