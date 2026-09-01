import { apiClient } from '../api/apiClient';
import {
  Button,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatModelLabel } from '../utils/agentIdentity';
import {
  buildSteppedConnectorRoute,
  COMPACT_CARD_H,
  FIXED_CARD_H,
  COMPACT_NODE_W,
  FIXED_NODE_W,
  layoutDagStaircase,
  NODE_TYPE_W,
  NODE_W,
  routeGridEdges,
  roundedOrthogonalPath,
  workflowNodeSizeHint,
} from '../utils/dagLayout';
import { AiCredits } from './AiCredits';
import { PodIndicator } from './PodIndicator';
import {
  AlertRegular,
  ArrowSyncRegular,
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  FolderRegular,
  MergeRegular,
  MoreHorizontalRegular,
  NotebookRegular,
  PersonClockRegular,
  PersonRegular,
  ShieldKeyholeRegular,
  ShieldRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
import { cloneElement, createContext, Fragment, useContext, useEffect, useMemo, useState } from 'react';
import type { FocusEvent as ReactFocusEvent, ReactElement, ReactNode } from 'react';
import { Link } from 'react-router-dom';
import {
  EdgeLabelRenderer,
  Handle,
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
import type { GraphNodeType, WorkflowGraphDto } from '../api/types';
/**
 * WorkflowGraphPanel — shared generic workflow graph renderer.
 *
 * Provides the reusable WorkflowNode card, LoopbackEdge, edge helpers, styles, and
 * contexts consumed by operator graph surfaces, including CoordinatorRunPage
 * (unified coordinator + subtask + planned-assembly view).
 *
 * Reuse rule: import from here; do NOT copy these definitions into page files.
 */
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
  /** Child sub-run id for assembly/workflow stages that own a persisted sub-run stream. */
  childRunId?: string;
  /** Graph ref (e.g. "run:{id}") for the stage's sub-run, when present. */
  childGraphRef?: string;
  reviewedBy?: string;
  runOutcome?: { achieved: boolean; reason: string };
  runDegraded?: { toolName: string; reason: string };
  /** Per-node execution pod name (spec-018). Null today (global fallback used); non-null per-agent after distributed phases. */
  executionPodName?: string | null;
  /** Layout direction for handle placement. 'LR' = left/right; 'TB' = top/bottom; 'GRID' exposes all sides for routed grid edges. */
  dir?: 'LR' | 'TB' | 'GRID';
  /**
   * When true, the node's connection handles are rendered visible and interactive so the
   * user can drag-to-connect nodes in the editable canvas (VisualWorkflowEditor). Defaults
   * to false: every read-only render surface (CoordinatorRunPage, WorkflowGraphPanel,
   * LandingWorkflowDemo) keeps handles as invisible, non-interactive edge anchors.
   */
  connectable?: boolean;
  /** Stable harness selector for the editable node face. */
  interactionTestId?: string;
  /** Prefix used to expose stable source/target handle selectors in the editable canvas. */
  handleTestIdPrefix?: string;
  /** Marks the workflow entry point in the editable workflow canvas. */
  isStart?: boolean;
  /** Editor-only inline badge for setup/validation state that should be visible on the node face. */
  editorBadge?: {
    label: string;
    title?: string;
  };
  /** Editing actions supplied only by the visual workflow editor. */
  editorActions?: {
    addNext: () => void;
    rename: () => void;
    remove: () => void;
    select: () => void;
  };
  /** When true and the node is running, an orange tool-approval badge is shown. */
  hasPendingApproval?: boolean;
  /** Active preview URL associated with a build/test gate. */
  previewUrl?: string | null;
  totalNanoAiu?: number | null;
  totalTokens?: number | null;
}

// ---------------------------------------------------------------------------
// Contexts — provided at page level, consumed by node/edge components
// ---------------------------------------------------------------------------

/** Open the execution detail modal for a given executionId. */
export const ExecutionModalContext = createContext<((executionId: string) => void) | undefined>(undefined);

/** Id of the active loopback edge (highlighted in blue). */
export const ActiveEdgeContext = createContext<string | undefined>(undefined);

/** CoordinatorRunPage: open/scroll to the all-up orchestration session panel. */
export const CoordinatorSessionContext = createContext<((opts?: { closeTopology?: boolean }) => void) | undefined>(undefined);

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
    outcome_plan: 'Planning gate',
    work_plan:    'Work planning',
    build_test:   'Build/test gate',
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
    outcome_plan: NotebookRegular,
    work_plan:    NotebookRegular,
    build_test:   CheckmarkCircleRegular,
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
  // Colored top-accent strip keyed to status. A thin 2px line flush with the card's rounded
  // top corners — retires the heavier accent bar.
  accentBar: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    height: '2px',
    borderTopLeftRadius: tokens.borderRadiusMedium,
    borderTopRightRadius: tokens.borderRadiusMedium,
    pointerEvents: 'none',
  },
  accentPending:   { backgroundColor: tokens.colorNeutralStroke2 },
  accentStarted:   { backgroundColor: tokens.colorPaletteMarigoldBorderActive },
  accentAwaiting:  { backgroundColor: tokens.colorPaletteMarigoldBorderActive },
  accentCompleted: { backgroundColor: tokens.colorPaletteGreenForeground1 },
  accentSkipped:   { backgroundColor: tokens.colorPaletteLightTealForeground2 },
  accentFailed:    { backgroundColor: tokens.colorPaletteRedForeground1 },
  accentRevise:    { backgroundColor: tokens.colorStatusWarningForeground1 },
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
    borderRadius: tokens.borderRadiusXLarge,
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
    borderTopColor: tokens.colorPaletteMarigoldBorderActive,
    borderRightColor: tokens.colorPaletteMarigoldBorderActive,
    borderBottomColor: tokens.colorPaletteMarigoldBorderActive,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    animationName: {
      '0%':   { boxShadow: `0 0 0 0 ${tokens.colorPaletteMarigoldBorderActive}` },
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
  cardSelected: {
    outline: `2px solid ${tokens.colorNeutralForeground2}`,
    outlineOffset: '2px',
    boxShadow: tokens.shadow4,
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
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  // Orange tool-approval badge overlay in the top-right corner of a running node.
  approvalBadge: {
    position: 'absolute',
    top: '-6px',
    right: '-6px',
    width: '20px',
    height: '20px',
    borderRadius: tokens.borderRadiusCircular,
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
    borderRadius: tokens.borderRadiusCircular,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  badgePending:   { backgroundColor: tokens.colorNeutralBackground4,              color: tokens.colorNeutralForeground3  },
  badgeStarted:   { backgroundColor: tokens.colorPaletteMarigoldBackground2,       color: tokens.colorPaletteMarigoldForeground2 },
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

  // -------------------------------------------------------------------------
  // Compact "pill" node — a fixed, legible DAG node. The wrapper stacks the card
  // and a model-name caption that renders just below the card border (outside the
  // card box), mirroring Copilot Studio. The card face shows an avatar, a title
  // (+ AI credits), and a muted "Name (Role)" line. Status is conveyed by the 2px
  // top accent + aria-label; richer detail lives in the hover popover.
  // -------------------------------------------------------------------------
  pillWrap: {
    boxSizing: 'border-box',
    width: `${COMPACT_NODE_W}px`,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  // Narrower wrapper for the compact gate/system/coordinator nodes (icon + title + optional model
  // caption). Subtask nodes keep the full COMPACT_NODE_W via pillWrap.
  pillWrapShort: {
    width: `${FIXED_NODE_W}px`,
  },
  pill: {
    boxSizing: 'border-box',
    width: '100%',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    position: 'relative',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    transitionProperty: 'box-shadow, transform, border-color',
    transitionDuration: tokens.durationFaster,
    transitionTimingFunction: tokens.curveEasyEase,
    ':hover': {
      boxShadow: tokens.shadow8,
      transform: 'translateY(-1px)',
      border: `1px solid ${tokens.colorNeutralStroke1}`,
    },
    '@media (prefers-reduced-motion: reduce)': {
      transitionProperty: 'none',
      ':hover': { transform: 'none' },
    },
    ':focus-visible': {
      outline: '2px solid #8c837c',
      outlineOffset: '2px',
    },
  },
  // Tall pill — subtask (agent) nodes carrying avatar + 2-line title + Name(Role) + credits.
  pillTall: {
    minHeight: `${COMPACT_CARD_H}px`,
  },
  // Short pill — fixed stage/gate/system nodes (icon + one-line title + optional sub-label). The
  // minHeight is only a FLOOR, so the Human Review gate still grows to fit on-face action buttons.
  pillShort: {
    minHeight: `${FIXED_CARD_H}px`,
  },
  // On-face action row for the Human Review gate while it awaits a decision. It re-enables pointer
  // events (the node face is the click target for select/zoom) and stops propagation so pressing the
  // button acts on the review, not the node selection.
  pillFaceActions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXS,
    pointerEvents: 'auto',
    '& button': { minWidth: 0 },
  },
  pillSelected: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    boxShadow: `0 0 0 1.5px ${tokens.colorNeutralStroke1}, ${tokens.shadow4}`,
  },
  pillPlanned: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    opacity: 0.7,
  },
  pillIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  pillBody: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    flex: 1,
    gap: '1px',
  },
  pillTitleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minWidth: 0,
  },
  startBadge: {
    flexShrink: 0,
    padding: '1px 6px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    color: tokens.colorPaletteMarigoldForeground2,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
  },
  editorBadge: {
    alignSelf: 'flex-start',
    display: 'inline-flex',
    alignItems: 'center',
    padding: '1px 6px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorStatusWarningBackground2,
    color: tokens.colorStatusWarningForeground1,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
  },
  pillTitle: {
    flex: 1,
    minWidth: 0,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground1,
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
    whiteSpace: 'normal',
    wordBreak: 'break-word',
  },
  pillCredits: {
    flexShrink: 0,
    display: 'inline-flex',
    alignItems: 'center',
  },
  pillNameRole: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  pillSub: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  pillModelCaption: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase100,
    color: tokens.colorNeutralForeground3,
    paddingLeft: tokens.spacingHorizontalXS,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // Detail popover surface (metadata that used to crowd the card face).
  detailSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: '240px',
    maxWidth: '320px',
    // The hover popover is informational and can overlap the node/arrow region; never let its
    // surface swallow a click meant for the node (click = select + cinematic zoom). Interactive
    // affordances (the action buttons/links) opt back in via detailActions below.
    pointerEvents: 'none',
  },
  detailHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  detailHeaderText: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  detailTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  detailRole: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  detailRows: {
    display: 'grid',
    gridTemplateColumns: 'auto 1fr',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalXXS,
    alignItems: 'baseline',
  },
  detailLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  detailValue: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    minWidth: 0,
  },
  detailValueMono: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
  },
  detailActions: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    // Re-enable pointer events for the actionable controls (the surface disables them).
    pointerEvents: 'auto',
    '& button': { width: '100%' },
    '& a': { width: '100%' },
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

/** Pick the colored top-accent class for a node given its status flags. */
export function accentClass(
  s: ReturnType<typeof useNodeStyles>,
  status: StepStatus,
  opts?: { isPlanned?: boolean; isAwaiting?: boolean },
): string {
  if (opts?.isPlanned)  return s.accentPending;
  if (opts?.isAwaiting) return s.accentAwaiting;
  return {
    pending:   s.accentPending,
    started:   s.accentStarted,
    completed: s.accentCompleted,
    skipped:   s.accentSkipped,
    failed:    s.accentFailed,
    revise:    s.accentRevise,
  }[status];
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
    if (status === 'started')   return 'Preparing assembly...';
    if (status === 'completed') return 'Ready for assembly';
    if (status === 'failed')    return 'Failed';
  }
  return null;
}

// ---------------------------------------------------------------------------
// NodeDetailPopover — supplementary metadata for a compact pill node.
// Opens on hover/focus (click still selects the node via the graph's onNodeClick),
// showing the full agent/model/phase/status/duration/credits/pod detail that no
// longer crowds the small node face.
// ---------------------------------------------------------------------------

export interface NodeDetailRow {
  label: string;
  value: ReactNode;
  mono?: boolean;
}

export function NodeDetailPopover({
  title,
  roleText,
  Icon,
  avatar,
  rows,
  actions,
  children,
}: {
  title: string;
  roleText?: string;
  Icon: FluentIcon;
  avatar?: ReactNode;
  rows: NodeDetailRow[];
  actions?: ReactNode;
  children: ReactElement;
}) {
  const s = useNodeStyles();
  const [open, setOpen] = useState(false);
  const visibleRows = rows.filter((r) => r.value !== undefined && r.value !== null && r.value !== '');

  // Open on FOCUS as well as hover so keyboard users reach the metadata (the visible status chip was
  // removed from the face). onFocus/onBlur bubble from the focusable pill inside `children`; closing
  // only when focus actually leaves the pill keeps click/Enter = select working. Cloning the child
  // (instead of adding a wrapper) preserves the trigger's box geometry so hover still works.
  const triggerChild = cloneElement(children, {
    onFocus: (event: ReactFocusEvent<HTMLElement>) => {
      (children.props as { onFocus?: (e: ReactFocusEvent<HTMLElement>) => void }).onFocus?.(event);
      setOpen(true);
    },
    onBlur: (event: ReactFocusEvent<HTMLElement>) => {
      (children.props as { onBlur?: (e: ReactFocusEvent<HTMLElement>) => void }).onBlur?.(event);
      const related = event.relatedTarget;
      if (!(related instanceof globalThis.Node) || !event.currentTarget.contains(related)) setOpen(false);
    },
  } as Partial<typeof children.props>);

  return (
    <Popover
      open={open}
      onOpenChange={(_, d) => setOpen(d.open)}
      openOnHover
      mouseLeaveDelay={200}
      withArrow
      positioning="above"
      trapFocus={false}
    >
      <PopoverTrigger disableButtonEnhancement>{triggerChild}</PopoverTrigger>
      <PopoverSurface className={mergeClasses(s.detailSurface, 'nopan', 'nodrag')}>
        <div className={s.detailHeader}>
          <span className={s.pillIcon} aria-hidden="true">{avatar ?? <Icon fontSize={22} />}</span>
          <div className={s.detailHeaderText}>
            <span className={s.detailTitle}>{title}</span>
            {roleText && <span className={s.detailRole}>{roleText}</span>}
          </div>
        </div>
        {visibleRows.length > 0 && (
          <div className={s.detailRows}>
            {visibleRows.map((r, i) => (
              <Fragment key={`${r.label}-${i}`}>
                <span className={s.detailLabel}>{r.label}</span>
                <span className={mergeClasses(s.detailValue, r.mono ? s.detailValueMono : undefined)}>{r.value}</span>
              </Fragment>
            ))}
          </div>
        )}
        {actions && <div className={mergeClasses(s.detailActions, 'nopan', 'nodrag')}>{actions}</div>}
      </PopoverSurface>
    </Popover>
  );
}

// ---------------------------------------------------------------------------
// WorkflowNode — compact pill node.
// node_type drives the data-node-type attribute; role drives icon and colour.
// The face shows an avatar/icon, a one-line title, an optional live status line,
// and a single status pill. All richer detail lives in the hover popover.
// ---------------------------------------------------------------------------

export function WorkflowNode({ data, selected }: NodeProps) {
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
    previewUrl,
    totalNanoAiu,
    totalTokens,
    executionPodName: nodeExecutionPodName,
    connectable,
    interactionTestId,
    handleTestIdPrefix,
    isStart,
    editorBadge,
    editorActions,
  } = data as WorkflowNodeData;
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
  // The Human Review gate shows its primary action on the FACE while awaiting a decision, so it
  // grows to fit; every other fixed stage/gate/system node stays compact.
  const showFaceReviewAction = key === 'review' && !isPlanned && isHumanWaiting && Boolean(openModal);

  // WorkflowNodes are ALWAYS the compact card (icon + title only on the face). Only the actual
  // subtask (agent) nodes get the tall/rich face — that lives in SubtaskNode, not here. The Human
  // Review gate stays compact but GROWS to fit its on-face approve/decline action while awaiting.
  const pillClass = mergeClasses(
    s.pill,
    s.pillShort,
    isActive        ? s.cardActive         : undefined,
    isHumanWaiting  ? s.cardActionRequired : undefined,
    isPlanned       ? s.pillPlanned        : undefined,
    selected        ? s.pillSelected       : undefined,
  );

  // Read-only surfaces render handles as invisible, non-interactive edge anchors. In the
  // editable canvas (connectable) they must be visible AND hittable, otherwise React Flow
  // has no target to start a drag-to-connect gesture from (`pointerEvents: 'none'` swallows
  // the pointerdown before a connection can begin).
  const handleStyle: React.CSSProperties = connectable
    ? {
        opacity: 1,
        pointerEvents: 'all',
        width: 10,
        height: 10,
        background: tokens.colorBrandBackground,
        border: `1.5px solid ${tokens.colorNeutralBackground1}`,
        zIndex: 1,
      }
    : { opacity: 0, pointerEvents: 'none' };
  const dir = (data as WorkflowNodeData).dir;
  const targetPos = dir === 'TB' ? Position.Top : Position.Left;
  const sourcePos = dir === 'TB' ? Position.Bottom : Position.Right;
  const rawSubText = statusDescription(key, effectiveStatus);
  // message (from workflow.step payload) takes priority over the hardcoded statusDescription fallback.
  const subText    = degradedReason ?? ((key === 'agent' && effectiveStatus === 'started' && intent) ? intent : (message ?? rawSubText));
  const roleText   = agentRoleTitle ?? def.roleDescription;
  const coordinatorClickable = key === 'coordinator' && !isPlanned && Boolean(openSession);
  const statusText = isPlanned ? 'Planned' : isHumanWaiting ? 'Awaiting' : statusLabel(effectiveStatus);
  const handleTestId = (suffix: string) => handleTestIdPrefix ? `${handleTestIdPrefix}-${suffix}` : undefined;

  // Compact face: always the role ICON (never an avatar). The agent name lives in the hover popover.
  const avatar = <Icon fontSize={20} />;

  // Model caption rendered BELOW the compact card for ANY node that HAS a model (data-driven, not
  // role-gated) — so Coordinator, RAI, Scribe, and any gate carrying a model show it beneath the
  // pill, while nodes with no model show nothing. Everything else (agent, credits, role, phase,
  // duration, pod) is popover-only.
  const modelCaption = modelId ? formatModelLabel(modelId as string) : undefined;

  const rows: NodeDetailRow[] = [
    { label: 'Status', value: statusText },
    { label: 'Role', value: roleText },
    ...(agentName ? [{ label: 'Agent', value: agentName as string }] : []),
    ...(modelId ? [{ label: 'Model', value: formatModelLabel(modelId as string), mono: true }] : []),
    ...(startedAt !== undefined ? [{ label: 'Duration', value: <ElapsedTimer startedAt={startedAt} completedAt={completedAt} /> }] : []),
    ...(nodeExecutionPodName ? [{ label: 'Pod', value: nodeExecutionPodName as string, mono: true }] : []),
    ...((totalNanoAiu != null || totalTokens != null)
      ? [{ label: 'Credits', value: <AiCredits totalNanoAiu={totalNanoAiu as number | null | undefined} totalTokens={totalTokens as number | null | undefined} /> }]
      : []),
  ];

  const actions = (
    <>
      {key === 'coordinator' && !isPlanned && openSession && (
        <Button
          appearance="outline"
          size="small"
          onClick={(event) => {
            // Stop the click from also reaching the pill's own onClick / the graph's onNodeClick —
            // "View session" closes the topology overlay, unlike a plain click on the node face.
            event.stopPropagation();
            openSession({ closeTopology: true });
          }}
        >
          View session
        </Button>
      )}
      {key === 'agent' && !isPlanned && (
        <Button appearance="outline" size="small" onClick={() => openModal?.(executionId as string)}>View execution</Button>
      )}
      {key === 'rai' && !isPlanned && (status === 'started' || status === 'completed' || status === 'failed' || status === 'revise') && (
        <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-rai`)}>View execution</Button>
      )}
      {key === 'build_test' && !isPlanned && previewUrl && (
        <Button appearance="primary" size="small" onClick={() => window.open(previewUrl as string, '_blank', 'noopener,noreferrer')}>Open preview</Button>
      )}
      {key === 'scribe' && !isPlanned && (
        <>
          {(status === 'started' || status === 'completed' || status === 'failed') && startedAt !== undefined && (
            <Button appearance="outline" size="small" onClick={() => openModal?.(`${executionId as string}-scribe`)}>View execution</Button>
          )}
          <Link to={`/projects/${projectId as string}/memories`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View memories</Button>
          </Link>
        </>
      )}
      {key === 'merge' && !isPlanned && status === 'completed' && (
        <Button appearance="outline" size="small" icon={<FolderRegular />} onClick={() => (browseFiles ?? openModal)?.(executionId as string)}>Browse files</Button>
      )}
      {/* Review-now while awaiting renders on the node FACE (see showFaceReviewAction), not here. */}
      {key === 'review' && !isPlanned && (status === 'completed' || status === 'revise') && reviewedBy && (
        <div className={s.reviewerRow}>
          <img
            src={`https://github.com/${reviewedBy as string}.png?size=28`}
            style={{ width: 24, height: 24, borderRadius: '50%', border: `2px solid ${tokens.colorNeutralStroke1}` }}
            alt={reviewedBy as string}
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground2 }}>{reviewedBy as string}</Text>
        </div>
      )}
    </>
  );

  const face = (
    <div className={mergeClasses(s.pillWrap, s.pillWrapShort)}>
      <div
        className={pillClass}
        role="article"
        aria-label={`${label}: ${statusLabel(effectiveStatus)}`}
        aria-current={selected ? 'true' : undefined}
        data-node-type={nodeType ?? 'default'}
        data-testid={interactionTestId ?? (coordinatorClickable ? 'coordinator-card' : undefined)}
        tabIndex={0}
        onClick={coordinatorClickable ? () => openSession?.() : undefined}
        onKeyDown={coordinatorClickable ? (e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            openSession?.();
          }
        } : editorActions ? (e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            editorActions.select();
          }
        } : undefined}
      >
        {dir === 'GRID' ? (
          <>
            <Handle id="target-left" type="target" position={Position.Left} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('target-left')} />
            <Handle id="target-right" type="target" position={Position.Right} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('target-right')} />
            <Handle id="target-top" type="target" position={Position.Top} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('target-top')} />
            <Handle id="target-bottom" type="target" position={Position.Bottom} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('target-bottom')} />
            <Handle id="source-left" type="source" position={Position.Left} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('source-left')} />
            <Handle id="source-right" type="source" position={Position.Right} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('source-right')} />
            <Handle id="source-top" type="source" position={Position.Top} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('source-top')} />
            <Handle id="source-bottom" type="source" position={Position.Bottom} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('source-bottom')} />
          </>
        ) : (
          <>
            <Handle type="target" position={targetPos} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('target')} />
            <Handle type="source" position={sourcePos} style={handleStyle} isConnectable={!!connectable} data-testid={handleTestId('source')} />
          </>
        )}

        <span
          className={`${s.accentBar} ${accentClass(s, effectiveStatus, { isPlanned: !!isPlanned, isAwaiting: isHumanWaiting })}`}
          aria-hidden="true"
        />

        {hasPendingApproval && status === 'started' && (
          <div className={s.approvalBadge} role="img" aria-label="Tool approval required" title="Tool approval required">
            <ShieldKeyholeRegular fontSize={12} aria-hidden="true" />
          </div>
        )}

        <span className={s.pillIcon} aria-hidden="true">{avatar}</span>
        <div className={s.pillBody}>
          <div className={s.pillTitleRow}>
            <span className={s.pillTitle}>{label}</span>
            {isStart && <span className={s.startBadge}>Start</span>}
          </div>
          {editorBadge && (
            <span className={s.editorBadge} title={editorBadge.title}>
              {editorBadge.label}
            </span>
          )}
          {subText && <span className={s.pillSub}>{subText}</span>}
          {showFaceReviewAction && (
            <div
              className={mergeClasses(s.pillFaceActions, 'nopan', 'nodrag')}
              onClick={(e) => e.stopPropagation()}
              onKeyDown={(e) => e.stopPropagation()}
              role="presentation"
            >
              <Button appearance="primary" size="small" onClick={() => openModal?.(executionId as string)}>Review now</Button>
            </div>
          )}
        </div>
        {editorActions && (
          <div className={mergeClasses(s.pillFaceActions, 'nopan', 'nodrag')}>
            <Button
              appearance="outline"
              size="small"
              onClick={(event) => {
                event.stopPropagation();
                editorActions.addNext();
              }}
            >
              Add next step
            </Button>
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button appearance="subtle" size="small" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${label}`} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem onClick={editorActions.rename}>Rename</MenuItem>
                  <MenuItem onClick={editorActions.remove}>Delete</MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        )}
      </div>
      {modelCaption && <span className={s.pillModelCaption} title={modelCaption}>{modelCaption}</span>}
    </div>
  );

  return (
    <>
      <PodIndicator podName={nodeExecutionPodName as string | null | undefined} />
      <NodeDetailPopover title={label} roleText={roleText} Icon={Icon} avatar={avatar} rows={rows} actions={actions}>
        {face}
      </NodeDetailPopover>
    </>
  );
}


/** ReactFlow node types map for workflow nodes. Spread or use directly. */
export const workflowNodeTypes = { workflow: WorkflowNode };

// ---------------------------------------------------------------------------
// Routed edges — quiet orthogonal paths with rounded corners
// ---------------------------------------------------------------------------

const LOOPBACK_STROKE        = 'var(--colorNeutralStroke1)';
const LOOPBACK_STROKE_ACTIVE = 'var(--colorNeutralForeground1)';
const LOOPBACK_TEXT_COLOR    = 'var(--colorNeutralForeground2)';
const RETURN_RAIL_GAP        = 36;
const RETURN_RAIL_STAGGER    = 26;

function markerId(prefix: string, id: string): string {
  return `${prefix}-${String(id).replace(/[^a-zA-Z0-9_-]/g, '-')}`;
}

export function LoopbackEdge({ id, sourceX, sourceY, targetX, targetY, label, data }: EdgeProps) {
  const allEdges = useEdges();
  const allNodes = useNodes();
  const activeEdgeId = useContext(ActiveEdgeContext);

  const myEdge   = allEdges.find(e => e.id === id);
  const sourceId = myEdge?.source ?? '';
  const targetId = myEdge?.target ?? '';
  const loopbackData = data as { returnSide?: 'left' | 'right' } | undefined;

  const sourceNode = allNodes.find(n => n.id === sourceId);
  const targetNode = allNodes.find(n => n.id === targetId);

  const siblings = allEdges
    .filter(e => e.type === 'loopback')
    .sort((a, b) => {
      const ax = allNodes.find(n => n.id === a.source)?.position.x ?? 0;
      const bx = allNodes.find(n => n.id === b.source)?.position.x ?? 0;
      return ax - bx;
    });

  const myIndex   = siblings.findIndex(e => e.id === id);
  const sourceRight = (sourceNode?.position.x ?? sourceX) + (sourceNode?.measured?.width ?? NODE_W);
  const targetRight = (targetNode?.position.x ?? targetX) + (targetNode?.measured?.width ?? NODE_W);
  const returningLeft = loopbackData?.returnSide === 'right'
    ? true
    : loopbackData?.returnSide === 'left'
      ? false
      : targetRight <= sourceRight;
  const nodeBounds = allNodes.reduce(
    (bounds, node) => {
      const width = node.measured?.width ?? NODE_W;
      return {
        minX: Math.min(bounds.minX, node.position.x),
        maxX: Math.max(bounds.maxX, node.position.x + width),
      };
    },
    { minX: Math.min(sourceX, targetX), maxX: Math.max(sourceX, targetX) },
  );
  const sameSideBefore = siblings.slice(0, Math.max(0, myIndex)).filter((edge) => {
    const s = allNodes.find(n => n.id === edge.source);
    const t = allNodes.find(n => n.id === edge.target);
    if (!s || !t) return returningLeft;
    const sRight = s.position.x + (s.measured?.width ?? NODE_W);
    const tRight = t.position.x + (t.measured?.width ?? NODE_W);
    return (tRight <= sRight) === returningLeft;
  }).length;
  const railX = returningLeft
    ? nodeBounds.maxX + RETURN_RAIL_GAP + sameSideBefore * RETURN_RAIL_STAGGER
    : nodeBounds.minX - RETURN_RAIL_GAP - sameSideBefore * RETURN_RAIL_STAGGER;
  const route = roundedOrthogonalPath([
    { x: sourceX, y: sourceY },
    { x: railX, y: sourceY },
    { x: railX, y: targetY },
    { x: targetX, y: targetY },
  ], 10);
  const labelX = railX;
  const labelY = (sourceY + targetY) / 2;
  const markerIdValue = markerId('lb-arrow', id);
  const isActive = id === activeEdgeId;
  const stroke   = isActive ? LOOPBACK_STROKE_ACTIVE : LOOPBACK_STROKE;

  return (
    <>
      <defs>
        <marker id={markerIdValue} markerWidth="8" markerHeight="6" refX="6" refY="3" orient="auto">
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={stroke} />
        </marker>
      </defs>
      <path
        d={route}
        fill="none"
        stroke={stroke}
        strokeWidth={isActive ? 2 : 1.5}
        strokeDasharray={isActive ? undefined : '5 3'}
        strokeLinecap="round"
        strokeLinejoin="round"
        markerEnd={`url(#${markerIdValue})`}
      />
      {label != null && (
        <text
          x={labelX}
          y={labelY - 6}
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
export const workflowEdgeTypes = { loopback: LoopbackEdge, spine: SpineEdge };

// ---------------------------------------------------------------------------
// SpineEdge — forward dependency edge routed as one quiet stepped connector.
// ---------------------------------------------------------------------------

const SPINE_STROKE = 'var(--colorNeutralStroke1)';

function SpineEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  label,
  data,
}: EdgeProps) {
  const spineData = data as { flowDirection?: 'horizontal' | 'vertical' } | undefined;
  const route = buildSteppedConnectorRoute({
    sourceX,
    sourceY,
    targetX,
    targetY,
    orientation: spineData?.flowDirection,
  });
  const markerIdValue = markerId('spine-arrow', id);

  return (
    <>
      <defs>
        <marker id={markerIdValue} markerWidth="7" markerHeight="6" refX="6" refY="3" orient="auto">
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={SPINE_STROKE} />
        </marker>
      </defs>
      <path
        id={id}
        data-testid="workflow-spine-edge"
        d={route.path}
        fill="none"
        stroke={SPINE_STROKE}
        strokeWidth={1.4}
        strokeLinecap="round"
        strokeLinejoin="round"
        markerEnd={`url(#${markerIdValue})`}
      />
      {label != null && label !== '' && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan"
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${route.labelX}px, ${route.labelY}px)`,
              background: 'var(--colorNeutralBackground1)',
              border: '1px solid var(--colorNeutralStroke2)',
              borderRadius: '4px',
              padding: '1px 6px',
              fontSize: '11px',
              color: 'var(--colorNeutralForeground2)',
              pointerEvents: 'none',
              whiteSpace: 'nowrap',
            }}
          >
            {label}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}

// ---------------------------------------------------------------------------
// Edge builder helpers (exported so pages can build edge arrays)
// ---------------------------------------------------------------------------

const STROKE_MUTED = 'var(--colorNeutralStroke2)';

export function forwardEdge(id: string, source: string, target: string, animated = false): Edge {
  return {
    id,
    source,
    target,
    type: 'spine',
    animated,
    style: { stroke: STROKE_MUTED, strokeWidth: 1.5 },
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
 * Builds the read-only workflow-definition graph with the same staircase and
 * grid-edge-routing pipeline used by the coordinator topology.
 */
// eslint-disable-next-line react-refresh/only-export-components -- pure graph transform is unit-tested independently.
export function buildWorkflowDefinitionGraph(graph: WorkflowGraphDto): { rfNodes: Node[]; rfEdges: Edge[] } {
  const allEdges = graph.edges.map((edge) =>
    edge.loopback
      ? loopbackEdge(`${edge.from}->${edge.to}`, edge.from, edge.to, edge.label ?? '')
      : forwardEdge(`${edge.from}->${edge.to}`, edge.from, edge.to),
  );
  const forwardEdges = allEdges.filter((edge) => edge.type !== 'loopback');
  const hints: Record<string, { width: number; height: number }> = {};
  const raw: Node[] = graph.nodes.map((node) => {
    const nodeType = node.node_type;
    hints[node.id] = workflowNodeSizeHint(nodeType);
    return {
      id: node.id,
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {
        def: {
          key:             node.role,
          label:           node.label,
          roleDescription: roleDescForRole(node.role),
          Icon:            iconForRole(node.role),
        },
        state:     { status: 'pending' },
        nodeType,
        isPlanned: true,
        // Grid routing chooses among all four sides, so expose the matching
        // source and target handles used by CoordinatorRunPage.
        dir: 'GRID',
      } as WorkflowNodeData,
    };
  });
  const rfNodes = layoutDagStaircase(
    raw,
    forwardEdges,
    { rankdir: 'LR', rankSep: 40, nodeSep: 20, targetAspect: 1.35, minStepRanks: 3 },
    hints,
  );

  return { rfNodes, rfEdges: routeGridEdges(allEdges, rfNodes) };
}

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
    const loadGraph = async () => {
      setLoading(true);
      setError(null);
      try {
        const g = await apiClient.getWorkflowGraph(projectId, workflowId);
        if (!cancelled) {
          setGraph(g);
        }
      } catch (err: unknown) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : String(err));
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };
    void loadGraph();
    return () => { cancelled = true; };
  }, [projectId, workflowId]);

  const { rfNodes, rfEdges } = useMemo(
    () => graph ? buildWorkflowDefinitionGraph(graph) : { rfNodes: [], rfEdges: [] },
    [graph],
  );

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
              <Text size={200} style={{ color: 'var(--colorNeutralForeground3)' }}>Read-only</Text>
            </Panel>
          </ReactFlow>
        </div>
      </ActiveEdgeContext.Provider>
    </ExecutionModalContext.Provider>
  );
}
