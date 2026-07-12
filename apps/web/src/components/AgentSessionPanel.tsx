import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ChevronDownRegular,
  CircleRegular,
  ClockRegular,
  DismissCircleFilled,
  DismissRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import { useRunStream } from '../api/sse';
import type { EventType, RunStreamEvent } from '../api/sse';
import type { RaiVerdictEventPayload, RaiVerdictToken } from '../api/types';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import { mergeRunEvents as sharedMergeRunEvents, SEED_STATUSES } from '../timeline/mergeRunEvents';
import { isSerializedWorkPlan } from '../timeline/coordinatorPlanFilter';
import { deriveHumanTitle } from '../timeline/reducer';
import { buildRunTimeline } from '../timeline/runTimelineSteps';
import type { RunTimelineModel, RunTimelineStep } from '../timeline/runTimelineSteps';
import { AgentAvatar } from './AgentAvatar';
import { AiCredits } from './AiCredits';
import { AutomationToggle } from './AutomationToggle';
import { AUTOMATION_HELP } from './automationHelp';
import { FileViewerModal } from './FileViewerModal';
import { OutcomePlanPanel } from './OutcomePlanPanel';
import { RunTimeline } from './RunTimeline';
import { ApprovalGate } from './ui/agentic';
import { Composer } from './ui/copilot';
const PANEL_TOP = '48px';
const useStyles = makeStyles({
  backdrop: {
    position: 'fixed',
    top: PANEL_TOP,
    right: 0,
    bottom: 0,
    left: 'var(--app-nav-width, 180px)',
    backgroundColor: 'rgba(0, 0, 0, 0.12)',
    opacity: 0,
    pointerEvents: 'none',
    transitionProperty: 'opacity',
    transitionDuration: '220ms',
    transitionTimingFunction: 'ease-out',
    zIndex: 1098,
  },
  backdropOpen: {
    opacity: 1,
    pointerEvents: 'auto',
  },
  panel: {
    position: 'fixed',
    top: PANEL_TOP,
    left: 'var(--app-nav-width, 180px)',
    right: 0,
    bottom: 0,
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow64,
    transform: 'translateY(100%)',
    transitionProperty: 'transform',
    transitionDuration: '220ms',
    transitionTimingFunction: 'ease-out',
    pointerEvents: 'none',
    zIndex: 1099,
  },
  panelOpen: {
    transform: 'translateY(0)',
    pointerEvents: 'auto',
  },
  dragHandleWrap: {
    display: 'flex',
    justifyContent: 'center',
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXXS,
  },
  dragHandle: {
    width: '64px',
    height: '4px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralStroke3,
  },
  shell: {
    flex: 1,
    minHeight: 0,
    display: 'grid',
    gridTemplateColumns: '260px minmax(0, 1fr)',
    gridTemplateRows: 'minmax(0, 1fr)',
  },
  sidebar: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  sidebarHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  sidebarTitle: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
  },
  treeScroll: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    padding: tokens.spacingHorizontalXS,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  treeItem: {
    width: '100%',
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground1,
    textAlign: 'left',
    cursor: 'pointer',
    minHeight: '40px',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  treeItemSelected: {
    backgroundColor: tokens.colorNeutralBackground1Hover,
    fontWeight: tokens.fontWeightSemibold,
  },
  guides: {
    display: 'flex',
    flexDirection: 'row',
    alignSelf: 'stretch',
    flexShrink: 0,
  },
  guideCol: {
    position: 'relative',
    width: '16px',
    alignSelf: 'stretch',
    flexShrink: 0,
  },
  guideVertical: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    left: '50%',
    width: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  elbowTop: {
    position: 'absolute',
    top: 0,
    height: '50%',
    left: '50%',
    width: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  elbowBottom: {
    position: 'absolute',
    top: '50%',
    bottom: 0,
    left: '50%',
    width: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  elbowHorizontal: {
    position: 'absolute',
    top: '50%',
    left: '50%',
    right: 0,
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  statusGlyph: {
    flexShrink: 0,
    fontSize: '16px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '18px',
    height: '18px',
  },
  statusGlyphSuccess: { color: tokens.colorPaletteGreenForeground1 },
  statusGlyphDanger: { color: tokens.colorPaletteRedForeground1 },
  statusGlyphWarning: { color: tokens.colorPaletteMarigoldForeground1 },
  statusGlyphRunning: { color: tokens.colorNeutralForeground2 },
  statusGlyphPending: { color: tokens.colorNeutralForeground4 },
  treeLabelCol: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    flex: 1,
    gap: '1px',
  },
  treeLinePrimary: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightRegular,
    lineHeight: tokens.lineHeightBase300,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  treeLinePrimarySelected: {
    fontWeight: tokens.fontWeightSemibold,
  },
  treeLineSecondary: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  treeMeta: {
    flexShrink: 0,
    marginLeft: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  treeMetaDanger: {
    color: tokens.colorPaletteRedForeground1,
  },
  main: {
    minWidth: 0,
    minHeight: 0,
    display: 'flex',
    flexDirection: 'column',
  },
  mainHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  mainHeaderInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
  },
  badgeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  identityRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  identityText: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  agentName: {
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  agentRole: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  metaText: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  tabList: {
    paddingLeft: tokens.spacingHorizontalM,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  composerUtilityRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalL,
    paddingTop: tokens.spacingVerticalXS,
  },
  content: {
    flex: 1,
    minHeight: 0,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
  },
  tabBody: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
  },
  narrativeToolbar: {
    position: 'sticky',
    top: 0,
    zIndex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalS,
    padding: `0 0 ${tokens.spacingVerticalXS}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  emptyState: {
    padding: tokens.spacingVerticalXL,
    color: tokens.colorNeutralForeground3,
  },
  jumpToLatestBar: {
    position: 'sticky',
    bottom: 0,
    zIndex: 1,
    display: 'flex',
    justifyContent: 'flex-end',
    paddingTop: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  statusChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: tokens.fontSizeBase200,
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'nowrap',
  },
  chatFeed: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  timelineApprovals: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    marginTop: tokens.spacingVerticalM,
  },
  conversationTurn: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  messageRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  messageCard: {
    display: 'grid',
    gridTemplateColumns: '24px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} 0`,
    backgroundColor: 'transparent',
  },
  messageMeta: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  authorBlock: {
    display: 'flex',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
    minWidth: 0,
  },
  authorName: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
  },
  messageRole: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  messageBubble: {
    maxWidth: '76ch',
    borderRadius: tokens.borderRadiusLarge,
    padding: 0,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    lineHeight: tokens.lineHeightBase300,
  },
  markdownBody: {
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
    '& p': {
      marginTop: 0,
      marginBottom: tokens.spacingVerticalS,
    },
    '& p:last-child': {
      marginBottom: 0,
    },
    '& h1, & h2, & h3, & h4, & h5, & h6': {
      marginTop: tokens.spacingVerticalM,
      marginBottom: tokens.spacingVerticalS,
      lineHeight: tokens.lineHeightBase400,
      fontWeight: tokens.fontWeightSemibold,
    },
    '& h1:first-child, & h2:first-child, & h3:first-child, & h4:first-child, & h5:first-child, & h6:first-child': {
      marginTop: 0,
    },
    '& ul, & ol': {
      marginTop: 0,
      marginBottom: tokens.spacingVerticalS,
      paddingLeft: tokens.spacingHorizontalXL,
    },
    '& li': {
      marginBottom: tokens.spacingVerticalXXS,
    },
    '& blockquote': {
      margin: `${tokens.spacingVerticalS} 0`,
      padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
      border: `1px solid ${tokens.colorNeutralStroke2}`,
      borderRadius: tokens.borderRadiusMedium,
      backgroundColor: tokens.colorNeutralBackground2,
      color: tokens.colorNeutralForeground2,
    },
    '& a': {
      color: tokens.colorBrandForegroundLink,
      textDecorationLine: 'none',
      ':hover': {
        textDecorationLine: 'underline',
      },
    },
    '& code': {
      fontFamily: tokens.fontFamilyMonospace,
      fontSize: tokens.fontSizeBase200,
      backgroundColor: tokens.colorNeutralBackground2,
      borderRadius: tokens.borderRadiusSmall,
      padding: '1px 4px',
    },
    '& pre': {
      margin: `${tokens.spacingVerticalS} 0`,
      padding: tokens.spacingVerticalS,
      borderRadius: tokens.borderRadiusMedium,
      backgroundColor: tokens.colorNeutralBackground2,
      overflowX: 'auto',
      maxWidth: '100%',
    },
    '& pre code': {
      display: 'block',
      padding: 0,
      backgroundColor: 'transparent',
      whiteSpace: 'pre',
      overflowWrap: 'normal',
    },
    '& table': {
      borderCollapse: 'collapse',
      display: 'block',
      overflowX: 'auto',
      maxWidth: '100%',
      marginBottom: tokens.spacingVerticalS,
    },
    '& th, & td': {
      border: `1px solid ${tokens.colorNeutralStroke2}`,
      padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    },
  },
  bubbleSystem: {
    backgroundColor: tokens.colorNeutralBackground2,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  bubbleUser: {
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  bubbleAgent: {
    backgroundColor: 'transparent',
  },
  activityEventRow: {
    display: 'grid',
    gridTemplateColumns: '1px minmax(0, 1fr) auto',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXXS} 0 ${tokens.spacingVerticalXXS} 32px`,
    color: tokens.colorNeutralForeground3,
  },
  activityRail: {
    width: '1px',
    minHeight: '18px',
    alignSelf: 'stretch',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralStroke2,
  },
  activityGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: `${tokens.spacingVerticalXXS} 0`,
  },
  activityEventText: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
    overflowWrap: 'anywhere',
  },
  toolsBox: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginLeft: '32px',
    padding: `${tokens.spacingVerticalXS} 0`,
  },
  toolsButton: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    backgroundColor: 'transparent',
    border: 'none',
    padding: 0,
    cursor: 'pointer',
    textAlign: 'left',
    color: tokens.colorNeutralForeground3,
  },
  activitySummaryButton: {
    display: 'inline-flex',
    alignItems: 'center',
    alignSelf: 'flex-start',
    gap: tokens.spacingHorizontalXS,
    marginLeft: '32px',
    minHeight: '24px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    padding: `2px ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground3,
    cursor: 'pointer',
    fontSize: tokens.fontSizeBase200,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2Hover,
      color: tokens.colorNeutralForeground2,
    },
  },
  toolsList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    paddingTop: tokens.spacingVerticalXS,
  },
  toolRow: {
    display: 'grid',
    gridTemplateColumns: '16px minmax(0, 1fr) 16px',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  toolKind: {
    display: 'inline-flex',
    color: tokens.colorNeutralForeground3,
  },
  toolLabel: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontFamily: tokens.fontFamilyMonospace,
  },
  toolRowMuted: {
    color: tokens.colorNeutralForeground4,
  },
  toolCheck: {
    color: tokens.colorPaletteGreenForeground1,
  },
  fileRows: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginLeft: '32px',
  },
  fileRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, max-content) minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minHeight: '24px',
    padding: `${tokens.spacingVerticalXXS} 0`,
    borderRadius: 0,
    backgroundColor: 'transparent',
  },
  fileName: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fileMeta: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  fileCardInfo: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, max-content) minmax(0, 1fr)',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalXS,
    minWidth: 0,
    flex: 1,
  },
  disclosure: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    backgroundColor: 'transparent',
    border: 'none',
    padding: 0,
    cursor: 'pointer',
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightSemibold,
  },
  stickyComposer: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL} ${tokens.spacingVerticalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  composerInput: {
    flex: 1,
  },
  composerError: {
    padding: `0 ${tokens.spacingHorizontalL} ${tokens.spacingVerticalS}`,
  },
  composerStatus: {
    padding: `0 ${tokens.spacingHorizontalL} ${tokens.spacingVerticalS}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  composerStatusSuccess: {
    color: tokens.colorPaletteGreenForeground1,
  },
  approvalGateWrap: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginTop: tokens.spacingVerticalXS,
  },
  approvalGateHeading: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  approvalResolved: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
  },
  loadingWrap: {
    padding: tokens.spacingVerticalXL,
  },
  dockedPanel: {
    position: 'static',
    inset: 'auto',
    height: '100%',
    minHeight: 0,
    boxShadow: 'none',
    borderTop: 0,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    transform: 'none',
    pointerEvents: 'auto',
    zIndex: 'auto',
  },
  shellNoSidebar: {
    gridTemplateColumns: 'minmax(0, 1fr)',
  },
  composerStack: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  runChipsBar: {
    flexShrink: 0,
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  composerContext: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL} 0`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  stickyNeedInput: {
    margin: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalL} 0`,
  },
});

export interface RunSessionTree {
  nodeId: string;
  label: string;
  agentName?: string;
  agentRole?: string;
  status: string;
  childRunId?: string;
  startedAt?: number;
  completedAt?: number;
  children: RunSessionTree[];
  depth: number;
}

export interface AgentSessionPanelProps {
  open: boolean;
  onClose: () => void;
  tree: RunSessionTree[];
  selectedNodeId: string | null;
  onSelectNode: (nodeId: string) => void;
  coordinatorRunId: string;
  projectId: string;
  onCoordinatorFollowUp?: () => void;
  coordinatorActive?: boolean;
  /** Automation state + handlers, surfaced in the composer utility row (coordinator scope only). */
  automation?: {
    autopilot: boolean;
    autoApprove: boolean;
    autopilotBusy?: boolean;
    autoApproveBusy?: boolean;
    canToggle: boolean;
    onToggleAutopilot: () => void;
    onToggleAutoApprove: () => void;
  };
  variant?: 'modal' | 'docked';
  composerFocusSignal?: number;
  onOutcomePlanClarify?: () => void;
  /** Points the shared artifact browser at the coordinator's collective assembly (integration
   *  branch) when a coordinator-aggregate node is selected. Per-subtask runs use the standard
   *  per-run endpoints (no adapter). */
  artifactAdapter?: ArtifactBrowserAdapter;
  /** Run-wide summary chips (Changes / Plan / …) pinned just above the composer for the
   *  coordinator scope. Hidden for non-coordinator (child) scopes, whose per-scope changes
   *  live in the Activity | Changes segmented control instead. */
  runChips?: ReactNode;
  /** Run-level AI-credits indicator, rendered immediately left of the composer send button as the
   *  shared hoverable AiCredits control (Session credits + USD estimate). */
  credits?: {
    totalNanoAiu?: number | null;
    detail?: ReactNode;
  };
  /** True once the run has decomposed into a work plan / dispatched subtasks (or is terminal), so the
   *  inline Outcome-plan view hides the pre-dispatch "Break into tasks" authoring action. */
  outcomePlanDispatched?: boolean;
}

interface ConversationRow {
  key: string;
  role: 'system' | 'user' | 'agent' | 'activity';
  content: string;
  timestamp?: number;
  authorOverride?: { displayName: string; avatarName: string; roleLabel: string; collapsedLabel?: string };
}

interface ConversationTool {
  callId: string;
  toolName: string;
  title: string;
  settled: boolean;
  errored: boolean;
  args: Record<string, unknown>;
}

interface ConversationTurn {
  key: string;
  rows: ConversationRow[];
  toolCalls: ConversationTool[];
  approvals: Array<{ event: RunStreamEvent; isResolved: boolean; resolvedScope: string | null }>;
  filePaths: string[];
  // True only for the turn whose agent.turn.start has no matching agent.turn.end —
  // i.e. the single event-level active turn. Drives the streaming affordance.
  open?: boolean;
}

interface FlatTreeNode extends RunSessionTree {
  guides: boolean[];
  isLast: boolean;
  level: number;
  isCoordinator: boolean;
}

function mergeRunEvents(seed: RunStreamEvent[], live: RunStreamEvent[]): RunStreamEvent[] {
  // The session panel presents a sequence-sorted transcript — see the shared helper.
  return sharedMergeRunEvents(seed, live, { sort: true });
}

function readString(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (value != null && String(value).trim() !== '') return String(value);
  }
  return undefined;
}

interface OutcomeSpecMessage {
  desiredOutcome?: string;
  scope?: string;
}

function parseOutcomeSpecMessage(content: string): OutcomeSpecMessage | null {
  const trimmed = content.trim();
  if (!trimmed.startsWith('{') || !/"desired_outcome"|"desiredOutcome"/.test(trimmed)) return null;
  try {
    const parsed = JSON.parse(trimmed) as Record<string, unknown>;
    const desiredOutcome = readString(parsed, ['desiredOutcome', 'desired_outcome']);
    const scope = readString(parsed, ['scope']);
    if (!desiredOutcome && !scope) return null;
    return { desiredOutcome, scope };
  } catch {
    return null;
  }
}

function formatOutcomeSpecMessage(spec: OutcomeSpecMessage): string {
  return [
    '### Outcome plan',
    spec.desiredOutcome ? `**Desired outcome:**\n\n${spec.desiredOutcome}` : null,
    spec.scope ? `**Scope:**\n\n${spec.scope}` : null,
  ].filter(Boolean).join('\n\n');
}

function normalizeRaiRationale(value: string | undefined): string | undefined {
  const trimmed = value?.trim();
  if (!trimmed || trimmed === '-' || trimmed === '—' || trimmed === '---') return undefined;
  return trimmed;
}

function readTimestamp(evt: RunStreamEvent): number | undefined {
  const raw = readString(evt.payload, ['timestamp_utc', 'timestampUtc', 'timestamp']);
  if (!raw) return undefined;
  const ms = new Date(raw).getTime();
  return Number.isNaN(ms) ? undefined : ms;
}

function formatDurationMs(ms: number): string {
  const secs = Math.max(0, Math.floor(ms / 1000));
  if (secs < 60) return `${secs}s`;
  const mins = Math.floor(secs / 60);
  const s = secs % 60;
  if (mins < 60) return `${mins}m ${s}s`;
  const hrs = Math.floor(mins / 60);
  const m = mins % 60;
  return `${hrs}h ${m}m`;
}

function formatStartedMeta(startedAt?: string | null, status?: string | null): string {
  const statusKey = (status ?? '').toLowerCase();
  if (!startedAt) {
    if (statusKey === 'pending' || statusKey === 'queued' || statusKey === 'planned') return 'Not started yet';
    if (statusKey === 'running' || statusKey === 'in_progress' || statusKey === 'dispatching') return 'Start time unavailable';
    return 'Timing unavailable';
  }
  const startedMs = new Date(startedAt).getTime();
  if (Number.isNaN(startedMs)) return 'Timing unavailable';
  const elapsed = Math.max(0, Date.now() - startedMs);
  return `Started ${formatDurationMs(elapsed)} ago`;
}

interface ParticipantIdentity {
  displayName: string;
  avatarName: string;
  role?: string;
}

// True for tools that PRODUCE or MODIFY a file — only these deserve a full preview card in the
// session pane. Reads/views are shown compactly as tool-call rows instead, avoiding the noisy
// double-render of a single read as both a row and a large "Workspace file" card.
function isFileWriteTool(toolName: string): boolean {
  const n = toolName.toLowerCase();
  return n.includes('write') || n.includes('create') || n.includes('edit') || n.includes('patch') || n.includes('apply');
}

// A FluentUI glyph that makes the operation type obvious at a glance.

function cleanText(value: string | undefined): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function nonGenericName(value: string | undefined): string | undefined {
  const name = cleanText(value);
  if (!name) return undefined;
  const normalized = name.toLowerCase();
  if (normalized === 'agent' || normalized === 'ai assistant' || normalized === 'assistant') return undefined;
  return name;
}

function formatNameRole(name: string, role: string | undefined): string {
  if (!role) return name;
  if (name.trim().toLowerCase() === role.trim().toLowerCase()) return name;
  return `${name} (${role})`;
}

function participantIdentityForNode(item: RunSessionTree | null): ParticipantIdentity {
  const role = cleanText(item?.agentRole);
  const avatarName = nonGenericName(item?.agentName)
    ?? nonGenericName(item?.label)
    ?? 'Assistant';
  return {
    avatarName,
    role,
    displayName: formatNameRole(avatarName, role),
  };
}


function statusLabel(status: string): string {
  switch (status) {
    case 'drafting_outcome': return 'Drafting outcome plan';
    case 'revising': return 'Changes requested — revising';
    case 'planning': return 'Planning';
    case 'dispatched': return 'Dispatching';
    case 'running':
    case 'in_progress': return 'Running';
    case 'assemble_ready': return 'Ready for assembly';
    case 'awaiting_assembly': return 'Preparing assembly';
    case 'awaiting_review': return 'Awaiting review';
    case 'awaiting_confirmation': return 'Awaiting confirmation';
    case 'needs_clarification': return 'Needs clarification';
    case 'confirmed': return 'Confirmed';
    case 'rai_flagged': return 'RAI flagged';
    case 'completed':
    case 'merged': return 'Completed';
    case 'failed':
    case 'merge_failed': return 'Failed';
    case 'declined': return 'Declined';
    case 'planned': return 'Planned';
    default: return status ? status.replace(/_/g, ' ') : 'Pending';
  }
}

type StatusKind = 'success' | 'danger' | 'awaiting' | 'running' | 'pending';

function statusKind(status: string): StatusKind {
  switch (status) {
    case 'completed':
    case 'merged':
    case 'assemble_ready':
    case 'confirmed':
      return 'success';
    case 'failed':
    case 'merge_failed':
    case 'declined':
      return 'danger';
    case 'awaiting_assembly':
    case 'drafting_outcome':
    case 'planning':
      return 'running';
    case 'rai_flagged':
    case 'waiting':
    case 'awaiting_confirmation':
    case 'awaiting_review':
    case 'needs_clarification':
    case 'revising':
      return 'awaiting';
    case 'running':
    case 'dispatched':
    case 'dispatching':
    case 'in_progress':
      return 'running';
    default:
      return 'pending';
  }
}

function StatusGlyph({ status, className }: { status: string; className?: string }) {
  const kind = statusKind(status);
  if (kind === 'success') return <CheckmarkCircleFilled className={className} />;
  if (kind === 'danger') return <DismissCircleFilled className={className} />;
  if (kind === 'awaiting') return <ClockRegular className={className} />;
  if (kind === 'running') return <Spinner size="extra-tiny" className={className} />;
  return <CircleRegular className={className} />;
}

const TERMINAL_EMPTY_STATUSES = new Set(['completed', 'merged', 'confirmed', 'assemble_ready', 'failed', 'merge_failed', 'declined']);

interface RaiVerdict {
  verdict?: RaiVerdictToken;
  rationale?: string;
}

type RaiVerdictPresentation = {
  intent: 'success' | 'warning' | 'error' | 'info';
  label: string;
  emoji: string;
};

const RAI_VERDICT_PRESENTATION: Record<RaiVerdictToken, RaiVerdictPresentation> = {
  red:    { intent: 'error',   label: 'Red',    emoji: '🔴' },
  revise: { intent: 'warning', label: 'Revise', emoji: '🟡' },
  yellow: { intent: 'warning', label: 'Yellow', emoji: '🟡' },
  green:  { intent: 'success', label: 'Green',  emoji: '🟢' },
};

const UNKNOWN_RAI_VERDICT_PRESENTATION: RaiVerdictPresentation = {
  intent: 'info',
  label: 'Unknown',
  emoji: '⚪',
};

function parseRaiVerdictToken(value: string | undefined): RaiVerdictToken | undefined {
  if (value === 'green' || value === 'yellow' || value === 'red' || value === 'revise') return value;
  return undefined;
}

function isAssemblyAggregateNode(item: RunSessionTree | FlatTreeNode | null | undefined): boolean {
  if (!item) return false;
  const key = `${item.nodeId} ${item.label}`.toLowerCase();
  return key.includes('assembly-rai')
    || key.includes('assembly-review')
    || key.includes('assembly-merge')
    || key.includes('assembly-scribe')
    || /\brai\b/.test(key)
    || key.includes('human review')
    || /\bmerge\b/.test(key)
    || /\bscribe\b/.test(key);
}

function isRaiNode(item: RunSessionTree | FlatTreeNode | null | undefined): boolean {
  if (!item) return false;
  const key = `${item.nodeId} ${item.label}`.toLowerCase();
  return key.includes('assembly-rai') || /\brai\b/.test(key);
}

function latestRaiVerdict(events: RunStreamEvent[]): RaiVerdict | null {
  for (let i = events.length - 1; i >= 0; i -= 1) {
    const evt = events[i];
    if (evt.type !== 'rai.verdict') continue;
    const payload = evt.payload as Partial<RaiVerdictEventPayload>;
    const rawTrafficLight = readString(evt.payload, ['trafficLight', 'traffic_light']);
    const rawVerdict = typeof payload.verdict === 'string' ? payload.verdict : rawTrafficLight;
    return {
      verdict: parseRaiVerdictToken(rawVerdict),
      rationale: normalizeRaiRationale(typeof payload.rationale === 'string'
        ? payload.rationale
        : readString(evt.payload, ['message', 'summary'])),
    };
  }
  return null;
}

function RaiVerdictCard({ verdict }: { verdict: RaiVerdict }) {
  const presentation = verdict.verdict
    ? RAI_VERDICT_PRESENTATION[verdict.verdict]
    : UNKNOWN_RAI_VERDICT_PRESENTATION;
  return (
    <MessageBar intent={presentation.intent} data-testid="rai-verdict-card" data-intent={presentation.intent}>
      <MessageBarBody>
        RAI verdict: {presentation.emoji} {presentation.label}
        {verdict.rationale ? ` — ${verdict.rationale}` : ''}
      </MessageBarBody>
    </MessageBar>
  );
}

function EmptySessionStatusFallback({ item }: { item: RunSessionTree }) {
  const styles = useStyles();
  const label = statusLabel(item.status);
  const duration = formatNodeDuration(item.startedAt, item.completedAt);
  const isTerminal = TERMINAL_EMPTY_STATUSES.has(item.status);

  if (!isTerminal) {
    return <Text className={styles.emptyState}>No streamed messages yet for this session.</Text>;
  }

  return (
    <MessageBar intent={statusKind(item.status) === 'danger' ? 'error' : 'success'}>
      <MessageBarBody>
        {item.label} {label.toLowerCase()}
        {duration ? ` in ${duration}` : ''}. No chat messages were emitted for this completed platform gate.
      </MessageBarBody>
    </MessageBar>
  );
}

function formatNodeDuration(startedAt?: number, completedAt?: number): string | null {
  if (!startedAt) return null;
  const end = completedAt ?? Date.now();
  const ms = end - startedAt;
  if (!Number.isFinite(ms) || ms < 0) return null;
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return seconds ? `${minutes}m ${seconds}s` : `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return mins ? `${hours}h ${mins}m` : `${hours}h`;
}

function buildTurns(events: RunStreamEvent[]): ConversationTurn[] {
  const turns: ConversationTurn[] = [];
  const pendingTools = new Map<string, ConversationTool>();
  const resolvedApprovals = new Map<string, string>();
  let current: ConversationTurn | null = null;
  let syntheticIndex = 0;

  for (const evt of events) {
    if (evt.type !== 'tool.approval_resolved' && evt.type !== 'coordinator.child_approval_resolved') continue;
    const requestId = readString(evt.payload, ['requestId', 'request_id']);
    if (!requestId) continue;
    if (Boolean(evt.payload['expired'])) resolvedApprovals.set(requestId, 'expired');
    else if (Boolean(evt.payload['approved'])) resolvedApprovals.set(requestId, readString(evt.payload, ['scope']) ?? 'approved');
    else resolvedApprovals.set(requestId, 'deny');
  }

  const ensureTurn = () => {
    if (current) return current;
    syntheticIndex += 1;
    current = { key: `synthetic-${syntheticIndex}`, rows: [], toolCalls: [], approvals: [], filePaths: [] };
    turns.push(current);
    return current;
  };
  const appendAgentText = (evt: RunStreamEvent, content: string) => {
    const turn = ensureTurn();
    const existing = [...turn.rows].reverse().find((row) => row.role === 'agent');
    if (existing) {
      existing.content += content;
      return;
    }
    turn.rows.push({
      key: `agent-${evt.sequence}`,
      role: 'agent',
      content,
      timestamp: readTimestamp(evt),
    });
  };

  const addFilePath = (turn: ConversationTurn, toolName: string, args: Record<string, unknown>) => {
    // Only files the agent wrote/edited earn a preview card; reads stay as compact tool rows.
    if (!isFileWriteTool(toolName)) return;
    const value = args['path'] ?? args['file'];
    if (typeof value !== 'string') return;
    if (!turn.filePaths.includes(value)) turn.filePaths.push(value);
  };

  for (const evt of events) {
    if (evt.type === 'agent.turn.start') {
      current = {
        key: String(evt.payload['turnId'] ?? `turn-${evt.sequence}`),
        rows: [],
        toolCalls: [],
        approvals: [],
        filePaths: [],
      };
      turns.push(current);
      continue;
    }
    if (evt.type === 'agent.turn.end') {
      current = null;
      continue;
    }
    if (evt.type === 'agent.system_prompt') {
      const content = readString(evt.payload, ['content', 'prompt', 'systemPrompt']);
      if (!content) continue;
      ensureTurn().rows.push({
        key: `system-${evt.sequence}`,
        role: 'system',
        content,
        timestamp: readTimestamp(evt),
      });
      continue;
    }
    if (evt.type === 'agent.task') {
      const content = readString(evt.payload, ['task', 'content', 'instruction']);
      if (!content) continue;
      ensureTurn().rows.push({
        key: `user-${evt.sequence}`,
        role: 'user',
        content,
        timestamp: readTimestamp(evt),
      });
      continue;
    }
    if (evt.type === 'agent.message' || evt.type === 'agent.message.delta') {
      const content = readString(evt.payload, ['content', 'delta', 'text']);
      if (!content) continue;
      if (isSerializedWorkPlan(content)) continue;
      appendAgentText(evt, content);
      continue;
    }
    if (evt.type === 'agent.intent') {
      const content = readString(evt.payload, ['intent', 'message', 'summary']);
      if (!content) continue;
      ensureTurn().rows.push({
        key: `intent-${evt.sequence}`,
        role: 'activity',
        content,
        timestamp: readTimestamp(evt),
      });
      continue;
    }
    if (evt.type === 'tool.call') {
      const args = (evt.payload['arguments'] as Record<string, unknown>) ?? {};
      const toolName = String(evt.payload['toolName'] ?? 'tool');
      const call: ConversationTool = {
        callId: String(evt.payload['callId'] ?? evt.sequence),
        toolName,
        title: deriveHumanTitle(toolName, args),
        settled: false,
        errored: false,
        args,
      };
      const turn = ensureTurn();
      turn.toolCalls.push(call);
      addFilePath(turn, toolName, args);
      pendingTools.set(call.callId, call);
      continue;
    }
    if (evt.type === 'tool.approval_required' || evt.type === 'shell.approval_required') {
      const requestId = readString(evt.payload, ['requestId', 'request_id']) ?? '';
      const resolvedScope = requestId ? (resolvedApprovals.get(requestId) ?? null) : null;
      ensureTurn().approvals.push({
        event: evt,
        isResolved: resolvedScope !== null,
        resolvedScope,
      });
      continue;
    }
    if (evt.type === 'tool.result' || evt.type === 'tool.error') {
      const callId = String(evt.payload['callId'] ?? '');
      const tool = pendingTools.get(callId);
      if (tool) {
        tool.settled = true;
        if (evt.type === 'tool.error') tool.errored = true;
      }
    }
  }
  // The turn still open at loop end (an agent.turn.start with no matching
  // agent.turn.end) is the single event-level active turn — mark it so only
  // its agent row streams. Cleared implicitly on agent.turn.end (current=null).
  if (current) current.open = true;
  for (const turn of turns) {
    for (const row of turn.rows) {
      if (row.role !== 'agent') continue;
      const outcomeSpec = parseOutcomeSpecMessage(row.content);
      if (!outcomeSpec) continue;
      row.content = formatOutcomeSpecMessage(outcomeSpec);
      row.authorOverride = {
        displayName: 'Coordinator (Outcome plan)',
        avatarName: 'Coordinator',
        roleLabel: 'outcome plan',
      };
    }
  }
  return turns.filter((turn) => turn.rows.length > 0 || turn.toolCalls.length > 0 || turn.approvals.length > 0);
}

interface SubtaskNarrativeInfo {
  id: string;
  title?: string;
  agent?: string;
  role?: string;
}

function readArray(payload: Record<string, unknown>, keys: string[]): unknown[] | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (Array.isArray(value)) return value;
  }
  return undefined;
}

function readGateKind(payload: Record<string, unknown>): string | undefined {
  const gateKind = payload['gateKind'] ?? payload['gate_kind'];
  return gateKind == null ? undefined : String(gateKind).toLowerCase();
}

function gateLabelForKind(gateKind: string | undefined): string {
  switch (gateKind) {
    case 'build-test': return 'Build & Test';
    case 'rubberduck': return 'Rubberduck';
    case 'human-review':
    case undefined: return 'Human Review';
    default: return gateKind.replace(/[-_]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  }
}

function buildSubtaskInfo(events: RunStreamEvent[]): Map<string, SubtaskNarrativeInfo> {
  const subtasks = new Map<string, SubtaskNarrativeInfo>();
  const upsert = (id: string | undefined, patch: Partial<SubtaskNarrativeInfo>) => {
    if (!id) return;
    const current = subtasks.get(id) ?? { id };
    const next: SubtaskNarrativeInfo = { ...current };
    if (patch.title) next.title = patch.title;
    if (patch.agent) next.agent = patch.agent;
    if (patch.role) next.role = patch.role;
    subtasks.set(id, next);
  };

  for (const evt of events) {
    if (evt.type === 'coordinator.work_plan') {
      for (const raw of readArray(evt.payload, ['subtasks', 'tasks']) ?? []) {
        const item = (raw ?? {}) as Record<string, unknown>;
        const id = readString(item, ['id', 'subtaskId', 'subtask_id']);
        upsert(id, {
          title: readString(item, ['title', 'task', 'name']),
          agent: readString(item, ['assignedAgent', 'assigned_agent', 'agent']),
          role: readString(item, ['role', 'roleTitle', 'role_title']),
        });
      }
    }
    if (evt.type.startsWith('subtask.') || evt.type.startsWith('coordinator.child_')) {
      const id = readString(evt.payload, ['subtaskId', 'subtask_id']);
      upsert(id, {
        title: readString(evt.payload, ['title', 'task', 'name']),
        agent: readString(evt.payload, ['assignedAgent', 'assigned_agent', 'agentName', 'agent_name', 'agent']),
        role: readString(evt.payload, ['role', 'roleTitle', 'role_title', 'agentRole', 'agent_role']),
      });
    }
  }
  return subtasks;
}

function subtaskDescription(payload: Record<string, unknown>, subtasks: Map<string, SubtaskNarrativeInfo>): string {
  const id = readString(payload, ['subtaskId', 'subtask_id']);
  const info = id ? subtasks.get(id) : undefined;
  const title = readString(payload, ['title', 'task', 'name']) ?? info?.title ?? (id ? `Subtask ${id}` : 'Subtask');
  const agent = readString(payload, ['assignedAgent', 'assigned_agent', 'agentName', 'agent_name', 'agent']) ?? info?.agent;
  const role = readString(payload, ['role', 'roleTitle', 'role_title', 'agentRole', 'agent_role']) ?? info?.role;
  const actor = agent ? ` — ${formatNameRole(agent, role)}` : '';
  return `${title}${actor}`;
}

function coordinatorActivityLine(evt: RunStreamEvent, subtasks: Map<string, SubtaskNarrativeInfo>, gateLabelBySequence: Map<number, string>): string | null {
  const p = evt.payload;
  switch (evt.type) {
    case 'coordinator.started': {
      const goal = readString(p, ['goal', 'message']);
      return goal ? `Coordinator started: ${goal}` : 'Coordinator started.';
    }
    case 'coordinator.recovered': {
      const status = readString(p, ['status']);
      return status ? `Coordinator recovered from ${status}.` : 'Coordinator recovered and resumed work.';
    }
    case 'coordinator.outcome_spec': {
      const outcome = readString(p, ['desiredOutcome', 'desired_outcome']);
      return outcome ? `Outcome plan drafted: ${outcome}` : 'Outcome plan drafted for review.';
    }
    case 'coordinator.workflow_selected': {
      const name = readString(p, ['selectedName', 'selected_name', 'selectedId', 'selected_id']);
      const rationale = readString(p, ['rationale']);
      return `Selected workflow: ${name ?? 'workflow'}${rationale ? ` — ${rationale}` : ''}`;
    }
    case 'coordinator.work_plan': {
      const subtasksCount = readArray(p, ['subtasks', 'tasks'])?.length;
      return subtasksCount != null ? `Coordinator created a work plan with ${subtasksCount} subtasks.` : 'Coordinator created a work plan.';
    }
    case 'coordinator.steering': {
      const instruction = readString(p, ['instruction', 'message', 'kind']);
      return instruction ? `Coordinator steering applied: ${instruction}` : 'Coordinator steering applied.';
    }
    case 'coordinator.child_stall_detected':
      return `Child stalled; redispatching ${subtaskDescription(p, subtasks)}.`;
    case 'coordinator.children_complete': {
      const total = readString(p, ['total']);
      const ready = readString(p, ['assembleReady', 'assemble_ready']);
      const failed = readString(p, ['failed']);
      return `Child subtasks complete${total ? `: ${total} total` : ''}${ready ? `, ${ready} ready for assembly` : ''}${failed ? `, ${failed} failed` : ''}.`;
    }
    case 'subtask.dispatched':
      return `Dispatched subtask: ${subtaskDescription(p, subtasks)}.`;
    case 'subtask.pending_capacity': {
      const reason = readString(p, ['reason', 'capacityReason', 'capacity_reason']);
      return `Subtask waiting for capacity: ${subtaskDescription(p, subtasks)}${reason ? ` — ${reason}` : ''}.`;
    }
    case 'subtask.running':
      return `Subtask running: ${subtaskDescription(p, subtasks)}.`;
    case 'subtask.assemble_ready':
      return `Subtask ready for assembly: ${subtaskDescription(p, subtasks)}.`;
    case 'subtask.rai_flagged':
      return `Subtask flagged by RAI: ${subtaskDescription(p, subtasks)}.`;
    case 'subtask.completed':
      return `Subtask completed: ${subtaskDescription(p, subtasks)}.`;
    case 'subtask.failed': {
      const reason = readString(p, ['reason', 'error', 'message']);
      return `Subtask failed: ${subtaskDescription(p, subtasks)}${reason ? ` — ${reason}` : ''}.`;
    }
    case 'coordinator.assembly_started':
      return `Collective assembly started${readString(p, ['integrationBranch', 'integration_branch']) ? ` on ${readString(p, ['integrationBranch', 'integration_branch'])}` : ''}.`;
    case 'coordinator.integration_conflict_auto_resolved':
      return `Collective assembly: auto-resolved merge conflict${readArray(p, ['conflictingFiles', 'conflicting_files'])?.length ? ` in ${readArray(p, ['conflictingFiles', 'conflicting_files'])!.join(', ')}` : ''}.`;
    case 'coordinator.assembly_rai_started':
      return 'Collective assembly: RAI check started.';
    case 'coordinator.assembly_rai_completed':
      return Boolean(p['raiSafetyFlagged'] ?? p['rai_safety_flagged'])
        ? 'Collective assembly: RAI check completed with safety flags.'
        : 'Collective assembly: RAI check completed.';
    case 'coordinator.assembly_review_requested':
      return 'Human review requested for collective assembly.';
    case 'coordinator.assembly_review_approved': {
      const reviewer = readString(p, ['reviewer']);
      return `Human review approved${reviewer ? ` by ${reviewer}` : ''}.`;
    }
    case 'coordinator.assembly_review_preserved':
      return 'Human review preserved after coordinator failure.';
    case 'coordinator.assembly_changes_requested': {
      const rawIds = readArray(p, ['redispatchedSubtaskIds', 'redispatchSubtaskIds']) ?? [];
      const gate = gateLabelBySequence.get(evt.sequence) ?? 'Assembly gate';
      const feedback = readString(p, ['feedback']);
      return `🔁 ${gate} requested changes → revising ${rawIds.length} subtask${rawIds.length === 1 ? '' : 's'}${feedback ? ` — Feedback: ${feedback}` : ''}.`;
    }
    case 'coordinator.assembly_merge_started':
      return 'Collective assembly: merge started.';
    case 'coordinator.assembly_merge_completed': {
      const commit = readString(p, ['commitHash', 'commit_hash']);
      return `Collective assembly: merge completed${commit ? ` (${commit.slice(0, 8)})` : ''}.`;
    }
    case 'coordinator.assembly_merge_failed': {
      const reason = readString(p, ['reason', 'error']);
      return `Collective assembly: merge failed${reason ? ` — ${reason}` : ''}.`;
    }
    case 'coordinator.assembly_scribe_started':
      return 'Collective assembly: scribe started.';
    case 'coordinator.assembly_scribe_completed':
      return 'Collective assembly: scribe completed.';
    case 'coordinator.assembly_completed': {
      const commit = readString(p, ['commitHash', 'commit_hash']);
      return `Collective assembly completed${commit ? ` (${commit.slice(0, 8)})` : ''}.`;
    }
    case 'coordinator.assembly_blocked': {
      const reason = readString(p, ['reason']);
      return `Collective assembly blocked${reason ? ` — ${reason}` : ''}.`;
    }
    case 'coordinator.assembly_declined': {
      const reason = readString(p, ['reason']);
      return `Collective assembly declined${reason ? ` — ${reason}` : ''}.`;
    }
    case 'coordinator.assembly_failed': {
      const reason = readString(p, ['reason', 'error']);
      return `Collective assembly failed${reason ? ` — ${reason}` : ''}.`;
    }
    case 'coordinator.child_question': {
      const question = readString(p, ['question']) ?? 'Question pending.';
      return `Child question from ${subtaskDescription(p, subtasks)}: ${question}`;
    }
    case 'coordinator.child_approval_required': {
      const tool = readString(p, ['toolName', 'tool_name']) ?? 'tool';
      const message = readString(p, ['message', 'url']);
      return `Tool approval required from ${subtaskDescription(p, subtasks)}: ${tool}${message ? ` — ${message}` : ''}`;
    }
    case 'tool.approval_required': {
      const tool = readString(p, ['toolName', 'tool_name']) ?? 'tool';
      const message = readString(p, ['message', 'url', 'intention']);
      return `Tool approval required: ${tool}${message ? ` — ${message}` : ''}`;
    }
    case 'shell.approval_required': {
      const command = readString(p, ['command']) ?? 'command';
      const intention = readString(p, ['intention', 'message']);
      return `Command approval required: ${command}${intention ? ` — ${intention}` : ''}`;
    }
    case 'coordinator.child_approval_resolved': {
      const outcome = Boolean(p['expired']) ? 'expired' : Boolean(p['approved']) ? 'approved' : 'denied';
      return `Child tool approval ${outcome} for ${subtaskDescription(p, subtasks)}.`;
    }
    case 'coordinator.autopilot_answered': {
      const answer = readString(p, ['answer']);
      return `Autopilot answered child question for ${subtaskDescription(p, subtasks)}${answer ? `: ${answer}` : '.'}`;
    }
    default:
      return null;
  }
}


function turnsToTimelineModel(turns: ConversationTurn[], eventCount: number): RunTimelineModel {
  const steps: RunTimelineStep[] = turns.map((turn, index) => {
    const messages = turn.rows
      .filter((row) => row.role !== 'system' && row.role !== 'user')
      .map((row, rowIndex) => ({
        messageId: row.key || `${turn.key}-row-${rowIndex}`,
        text: row.content,
        streaming: Boolean(turn.open && row.role === 'agent'),
      }));
    return {
      id: turn.key || `turn-${index}`,
      intent: index === 0 ? 'Activity' : `Activity ${index + 1}`,
      status: turn.open ? 'running' : 'complete',
      active: Boolean(turn.open),
      synthetic: true,
      tools: [],
      messages,
      children: messages.map((message) => ({ kind: 'message' as const, message })),
      sequence: index + 1,
    };
  });
  return {
    steps,
    eventCount,
    running: steps.some((step) => step.active),
  };
}

function buildCoordinatorTurns(events: RunStreamEvent[]): ConversationTurn[] {
  const subtasks = buildSubtaskInfo(events);
  const turns: ConversationTurn[] = [];
  const resolvedApprovals = new Map<string, string>();
  const gateLabelBySequence = new Map<number, string>();
  let latestGateLabel = gateLabelForKind(undefined);
  let firstSystem: ConversationRow | null = null;
  let firstTask: ConversationRow | null = null;
  let activityTurn: ConversationTurn | null = null;

  for (const evt of events) {
    if (evt.type === 'coordinator.assembly_review_requested') {
      latestGateLabel = gateLabelForKind(readGateKind(evt.payload));
    } else if (evt.type === 'coordinator.assembly_changes_requested') {
      gateLabelBySequence.set(evt.sequence, latestGateLabel);
    }
    if (evt.type === 'tool.approval_resolved' || evt.type === 'coordinator.child_approval_resolved') {
      const requestId = readString(evt.payload, ['requestId', 'request_id']);
      if (!requestId) continue;
      if (Boolean(evt.payload['expired'])) resolvedApprovals.set(requestId, 'expired');
      else if (Boolean(evt.payload['approved'])) resolvedApprovals.set(requestId, readString(evt.payload, ['scope']) ?? 'approved');
      else resolvedApprovals.set(requestId, 'deny');
    }
    if (!firstSystem && evt.type === 'agent.system_prompt') {
      const content = readString(evt.payload, ['content', 'prompt', 'systemPrompt']);
      if (content) firstSystem = { key: `system-${evt.sequence}`, role: 'system', content, timestamp: readTimestamp(evt) };
    }
    if (!firstTask && evt.type === 'agent.task') {
      const content = readString(evt.payload, ['task', 'content', 'instruction']);
      if (content) firstTask = { key: `task-${evt.sequence}`, role: 'user', content, timestamp: readTimestamp(evt) };
    }
  }

  if (firstSystem || firstTask) {
    turns.push({
      key: 'coordinator-prompt-details',
      rows: [firstSystem, firstTask].filter((row): row is ConversationRow => row !== null),
      toolCalls: [],
      approvals: [],
      filePaths: [],
    });
  }

  for (const evt of events) {
    const line = coordinatorActivityLine(evt, subtasks, gateLabelBySequence);
    if (!line) continue;
    const requestId = readString(evt.payload, ['requestId', 'request_id']) ?? '';
    const resolvedScope = requestId ? (resolvedApprovals.get(requestId) ?? null) : null;
    const isApprovalRequest = evt.type === 'coordinator.child_approval_required'
      || evt.type === 'tool.approval_required'
      || evt.type === 'shell.approval_required';
    const approvals = isApprovalRequest
      ? [{ event: evt, isResolved: resolvedScope !== null, resolvedScope }]
      : [];
    if (!activityTurn) {
      activityTurn = {
        key: `coordinator-activity-${evt.sequence}`,
        rows: [],
        toolCalls: [],
        approvals: [],
        filePaths: [],
      };
      turns.push(activityTurn);
    }
    activityTurn.rows.push({ key: `activity-${evt.sequence}`, role: 'activity', content: line, timestamp: readTimestamp(evt) });
    activityTurn.approvals.push(...approvals);
  }

  return turns.length > 0 ? turns : buildTurns(events);
}

function flattenTree(
  nodes: RunSessionTree[],
  coordinatorNodeId: string | null,
  ancestorsHasNext: boolean[] = [],
): FlatTreeNode[] {
  return nodes.flatMap((node, index) => {
    const isLast = index === nodes.length - 1;
    const current: FlatTreeNode = {
      ...node,
      guides: ancestorsHasNext,
      isLast,
      level: ancestorsHasNext.length,
      isCoordinator: node.nodeId === coordinatorNodeId,
    };
    return [current, ...flattenTree(node.children, coordinatorNodeId, [...ancestorsHasNext, !isLast])];
  });
}

export function AgentSessionPanel({
  open,
  onClose,
  tree,
  selectedNodeId,
  onSelectNode,
  coordinatorRunId,
  projectId,
  onCoordinatorFollowUp,
  coordinatorActive = false,
  automation,
  variant = 'modal',
  composerFocusSignal = 0,
  onOutcomePlanClarify,
  artifactAdapter,
  runChips,
  credits,
  outcomePlanDispatched = false,
}: AgentSessionPanelProps) {
  const styles = useStyles();
  const composerRef = useRef<HTMLDivElement>(null);
  const focusComposer = useCallback(() => {
    composerRef.current?.querySelector('textarea')?.focus();
  }, []);
  const messagesScrollRef = useRef<HTMLDivElement>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const docked = variant === 'docked';
  const [isVisible, setIsVisible] = useState(open);
  const [seedEvents, setSeedEvents] = useState<RunStreamEvent[]>([]);
  const [runDetailLoading, setRunDetailLoading] = useState(false);
  const [runDetailError, setRunDetailError] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<{ started_at?: string | null; ended_at?: string | null; status?: string | null } | null>(null);
  const [followUp, setFollowUp] = useState('');
  const [followUpBusy, setFollowUpBusy] = useState(false);
  const [followUpError, setFollowUpError] = useState<string | null>(null);
  const [followUpNotice, setFollowUpNotice] = useState<string | null>(null);

  const coordinatorNodeId = tree[0]?.nodeId ?? null;
  const flatTree = useMemo(
    () => flattenTree(tree, coordinatorNodeId),
    [tree, coordinatorNodeId],
  );
  const selectedItem = useMemo(
    () => flatTree.find((item) => item.nodeId === selectedNodeId) ?? flatTree[0] ?? null,
    [flatTree, selectedNodeId],
  );
  const selectedIsAssemblyAggregate = isAssemblyAggregateNode(selectedItem);
  const selectedRunId = selectedItem
    ? (selectedItem.isCoordinator || selectedItem.nodeId === 'outcome-plan' || selectedItem.nodeId === 'work-plan' || selectedIsAssemblyAggregate ? coordinatorRunId : (selectedItem.childRunId ?? ''))
    : '';

  // Coordinator-aggregate nodes (coordinator itself, work-plan, outcome-plan) own no worktree —
  // their artifacts live on the integration branch, so route through the assembly adapter. Per
  // subtask runs use the standard per-run endpoints (undefined adapter).
  const isCoordinatorAggregate = !!selectedItem
    && (selectedItem.isCoordinator
      || selectedItem.nodeId === 'work-plan'
      || selectedItem.nodeId === 'outcome-plan'
      || selectedIsAssemblyAggregate);
  const effectiveAdapter = useMemo(
    () => (isCoordinatorAggregate ? artifactAdapter : undefined),
    [isCoordinatorAggregate, artifactAdapter],
  );
  const canBrowseSelectedRun = selectedRunId.trim().length > 0;
  const selectedRunUnavailableReason = selectedItem && !isCoordinatorAggregate && !selectedItem.childRunId
    ? 'This planned task has not been dispatched yet. Changes and files become available after the coordinator starts the child run.'
    : null;

  // Reuse the shared artifact browser hook so the Changes tab renders the dense changed-files list
  // and the Files tab renders the full workspace FOLDER TREE (getRunWorkspace / assembly workspace),
  // not just the changed files. This is the same hook WorkspacePage drives.
  const artifactState = useArtifactBrowser(
    open && canBrowseSelectedRun ? selectedRunId : '',
    runDetail?.status ?? '',
    undefined,
    undefined,
    undefined,
    undefined,
    effectiveAdapter,
  );
  const {
    selectedPath,
    diff: selectedDiff,
    diffLoading: selectedDiffLoading,
    diffError: selectedDiffError,
    selectedPathIsChanged,
    clearSelection,
  } = artifactState;

  const { events: liveEvents } = useRunStream(open && canBrowseSelectedRun ? selectedRunId : '');
  const events = useMemo(() => mergeRunEvents(seedEvents, liveEvents), [seedEvents, liveEvents]);
  const selectedRaiVerdict = useMemo(
    () => (isRaiNode(selectedItem) ? latestRaiVerdict(events) : null),
    [events, selectedItem],
  );
  const turns = useMemo(
    () => (selectedItem?.isCoordinator || selectedItem?.nodeId === 'work-plan' || selectedIsAssemblyAggregate)
      ? buildCoordinatorTurns(events)
      : buildTurns(events),
    [events, selectedItem?.isCoordinator, selectedItem?.nodeId, selectedIsAssemblyAggregate],
  );
  // The Messages surface renders the intent-driven Timeline (ChainOfThought steps) from
  // the same scope-aware event stream. `turns` is still used for approvals, file
  // references and the needs-input counters. The timeline model, approvals and the
  // empty-state fallback all derive from the SAME `turns` for assembly aggregate /
  // coordinator scopes so they can never disagree (e.g. render assembly activity while
  // also showing "No streamed messages yet").
  const timelineModel = useMemo(
    () => {
      if (selectedIsAssemblyAggregate) {
        return turnsToTimelineModel(turns, events.length);
      }
      if (selectedItem?.isCoordinator || selectedItem?.nodeId === 'work-plan') {
        const model = buildRunTimeline(events, { stripSerializedWorkPlan: true });
        return model.steps.length > 0 ? model : turnsToTimelineModel(turns, events.length);
      }
      return buildRunTimeline(events, {
        stripSerializedWorkPlan: false,
      });
    },
    [events, selectedItem?.isCoordinator, selectedItem?.nodeId, selectedIsAssemblyAggregate, turns],
  );
  const timelineApprovals = useMemo(
    () => turns.flatMap((turn) => turn.approvals),
    [turns],
  );
  const selectedIdentity = useMemo(() => participantIdentityForNode(selectedItem), [selectedItem]);

  useEffect(() => {
    if (docked) {
      setIsVisible(true);
      return undefined;
    }
    if (open) {
      setIsVisible(true);
      return undefined;
    }
    const timeoutId = window.setTimeout(() => setIsVisible(false), 220);
    return () => window.clearTimeout(timeoutId);
  }, [docked, open]);

  useEffect(() => {
    if (!open) return undefined;
    const onKeyDown = (evt: KeyboardEvent) => {
      if (evt.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open, onClose]);

  useEffect(() => {
    setSeedEvents([]);
    setFollowUpError(null);
    setFollowUpNotice(null);
  }, [selectedRunId]);

  // Keep the shared artifact hook pointed at the changed-files view. The Changes segment
  // lists the selected scope's created/changed files; the run-wide workspace tree lives in
  // the page-level Artifacts overlay, not here.
  useEffect(() => {
    artifactState.setActiveTab('changes');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedRunId]);

  useEffect(() => {
    if (composerFocusSignal > 0) {
      if (selectedItem?.nodeId === 'outcome-plan') {
        setFollowUp((value) => value.trim() ? value : 'Clarify the outcome plan: ');
      }
      focusComposer();
    }
  }, [composerFocusSignal, selectedItem?.nodeId, focusComposer]);

  const focusOutcomePlanClarification = useCallback(() => {
    setFollowUp((value) => value.trim() ? value : 'Clarify the outcome plan: ');
    onOutcomePlanClarify?.();
    window.setTimeout(() => focusComposer(), 0);
  }, [onOutcomePlanClarify, focusComposer]);

  const jumpToLatestMessage = useCallback(() => {
    messagesEndRef.current?.scrollIntoView({ block: 'end', behavior: 'smooth' });
    messagesScrollRef.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    if (!open || !canBrowseSelectedRun) {
      setRunDetail(null);
      setRunDetailError(null);
      setRunDetailLoading(false);
      return;
    }
    let cancelled = false;
    setRunDetailLoading(true);
    setRunDetailError(null);
    apiClient.getRun(selectedRunId)
      .then((detail) => {
        if (cancelled) return;
        setRunDetail(detail);
        if (!SEED_STATUSES.has(detail.status)) {
          setSeedEvents([]);
          return;
        }
        return apiClient.getRunEvents(selectedRunId)
          .then((persisted) => {
            if (cancelled) return;
            setSeedEvents(persisted.map((event) => ({
              sequence: event.sequence,
              type: event.type as EventType,
              payload: event.payload,
            })));
          })
          .catch(() => {});
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setRunDetail(null);
        setRunDetailError(formatApiErrorMessage(err, 'Could not load run metadata.'));
      })
      .finally(() => {
        if (!cancelled) setRunDetailLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [open, selectedRunId, canBrowseSelectedRun]);

  // File list, diffs and the workspace tree are now fetched by the shared useArtifactBrowser hook
  // (see artifactState above), so the previous bespoke getRunFiles/getRunFileContent/getRunFileDiff
  // effects and their state have been removed. Opening a file selects it in the shared hook, which
  // drives the FileViewerModal below.
  const handleSendFollowUp = useCallback(async () => {
    const instruction = followUp.trim();
    if (!instruction || followUpBusy) return;
    setFollowUpBusy(true);
    setFollowUpError(null);
    setFollowUpNotice(null);
    try {
      await apiClient.steerCoordinator(coordinatorRunId, {
        kind: 'send',
        instruction,
        ...(selectedItem && !selectedItem.isCoordinator && selectedItem.childRunId
          ? { target_child_run_id: selectedItem.childRunId }
          : {}),
      });
      setFollowUp('');
      setFollowUpNotice('Message sent to coordinator.');
      onCoordinatorFollowUp?.();
    } catch (err: unknown) {
      setFollowUpError(formatApiErrorMessage(err, 'Could not send the coordinator message.'));
    } finally {
      setFollowUpBusy(false);
    }
  }, [coordinatorRunId, followUp, followUpBusy, onCoordinatorFollowUp, selectedItem]);

  if (!selectedItem || !isVisible) return null;

  const pendingApprovalCount = turns.reduce(
    (sum, turn) => sum + turn.approvals.filter((approval) => !approval.isResolved).length,
    0,
  );
  const pendingQuestionCount = events.filter((evt) =>
    evt.type === 'agent.question_asked' || evt.type === 'coordinator.child_question'
  ).length;
  // A turn "streams" while its run is still live — the coordinator scope tracks coordinatorActive,
  // a dispatched child tracks its own run status.
  const composerContext = selectedItem.nodeId === 'outcome-plan'
    ? 'Context: Outcome plan'
    : selectedItem.isCoordinator
      ? 'Context: Whole run'
      : `Context: ${selectedItem.label}${selectedItem.agentName ? ` with ${selectedItem.agentName}` : ''}`;
  const composerAvailabilityMessage = coordinatorActive
    ? null
    : 'Messaging is unavailable because this coordinator run is not active.';
  // Product decision: you can MESSAGE the Coordinator (root + work/outcome plan scopes),
  // but only VIEW other agents — steer them through the Coordinator.
  const isNonCoordinatorAgentScope = !selectedItem.isCoordinator
    && selectedItem.nodeId !== 'work-plan'
    && selectedItem.nodeId !== 'outcome-plan';
  const readOnlyComposerNote = `Viewing ${selectedItem.agentName ?? selectedItem.label} — steer via the Coordinator`;

  return (
    <>
      {!docked && (
        <div
          className={mergeClasses(styles.backdrop, open && styles.backdropOpen)}
          aria-hidden="true"
          onClick={open ? onClose : undefined}
        />
      )}
      <div
        className={mergeClasses(styles.panel, docked && styles.dockedPanel, open && styles.panelOpen)}
        role={docked ? 'region' : 'dialog'}
        aria-label="Session"
        aria-hidden={!open}
      >
        {!docked && (
          <div className={styles.dragHandleWrap}>
            <div className={styles.dragHandle} aria-hidden="true" />
          </div>
        )}

        <div className={mergeClasses(styles.shell, docked && styles.shellNoSidebar)}>
          {!docked && <aside className={styles.sidebar}>
            <div className={styles.sidebarHeader}>
              <Text className={styles.sidebarTitle}>Agent Sessions</Text>
              <Button appearance="subtle" size="small" icon={<DismissRegular />} aria-label="Close panel" onClick={onClose} />
            </div>
            <div className={styles.treeScroll}>
              {flatTree.map((item) => {
                const selected = item.nodeId === selectedItem.nodeId;
                const identity = participantIdentityForNode(item);
                const secondary = item.agentName || item.agentRole
                  ? identity.displayName
                  : '';
                const kind = statusKind(item.status);
                const glyphClass = mergeClasses(
                  styles.statusGlyph,
                  kind === 'success' && styles.statusGlyphSuccess,
                  kind === 'danger' && styles.statusGlyphDanger,
                  kind === 'awaiting' && styles.statusGlyphWarning,
                  kind === 'running' && styles.statusGlyphRunning,
                  kind === 'pending' && styles.statusGlyphPending,
                );
                const duration = formatNodeDuration(item.startedAt, item.completedAt);
                return (
                  <button
                    key={item.nodeId}
                    className={mergeClasses(styles.treeItem, selected && styles.treeItemSelected)}
                    onClick={() => onSelectNode(item.nodeId)}
                    title={secondary || item.label}
                  >
                    {item.level > 0 && (
                      <span className={styles.guides} aria-hidden="true">
                        {Array.from({ length: item.level }).map((_, i) => {
                          const isElbow = i === item.level - 1;
                          return (
                            <span key={i} className={styles.guideCol}>
                              {isElbow ? (
                                <>
                                  <span className={styles.elbowTop} />
                                  <span className={styles.elbowHorizontal} />
                                  {!item.isLast && <span className={styles.elbowBottom} />}
                                </>
                              ) : (
                                item.guides[i] && <span className={styles.guideVertical} />
                              )}
                            </span>
                          );
                        })}
                      </span>
                    )}
                    <span className={glyphClass} aria-hidden="true">
                      <StatusGlyph status={item.status} className={glyphClass} />
                    </span>
                    <span className={styles.treeLabelCol}>
                      <Text className={mergeClasses(styles.treeLinePrimary, selected && styles.treeLinePrimarySelected)}>
                        {item.label}
                      </Text>
                      {secondary && (
                        <Text className={styles.treeLineSecondary}>
                          {secondary}
                        </Text>
                      )}
                    </span>
                    {duration && (
                      <Text className={mergeClasses(styles.treeMeta, kind === 'danger' && styles.treeMetaDanger)}>
                        {duration}
                      </Text>
                    )}
                  </button>
                );
              })}
            </div>
          </aside>}

          <section className={styles.main}>
            <div className={styles.mainHeader}>
              <div className={styles.mainHeaderInfo}>
                <div className={styles.badgeRow}>
                  <span className={styles.statusChip}>
                    <StatusGlyph status={selectedItem.status} />
                    {statusLabel(selectedItem.status)}
                  </span>
                </div>
                <div className={styles.identityRow}>
                  <AgentAvatar name={selectedIdentity.avatarName} size={28} circle />
                  <div className={styles.identityText}>
                    <Text className={styles.agentName}>{selectedItem.label}</Text>
                    <Text className={styles.agentRole}>{selectedIdentity.displayName}</Text>
                  </div>
                </div>
                <Text className={styles.metaText}>
                  {formatStartedMeta(runDetail?.started_at ?? undefined, runDetail?.status ?? selectedItem.status)}
                  {runDetailError ? ' · Metadata unavailable' : ''}
                </Text>
              </div>
              <div className={styles.headerActions}>
                {/* Docked (merged single-surface) layout always has a selection, so there is no
                    empty center to close to — only the modal variant shows a close affordance. */}
                {!docked && (
                  <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close panel" onClick={onClose} />
                )}
              </div>
            </div>

            <div className={styles.content}>
              <div
                className={styles.tabBody}
                ref={messagesScrollRef}
                tabIndex={0}
                data-testid="session-message-scroll"
                aria-label="Session messages"
              >
                {selectedRunUnavailableReason && (
                  <MessageBar intent="info">
                    <MessageBarBody>{selectedRunUnavailableReason}</MessageBarBody>
                  </MessageBar>
                )}
                {runDetailError && !selectedRunUnavailableReason && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{runDetailError}</MessageBarBody>
                  </MessageBar>
                )}
                {runDetailLoading && (
                  <div className={styles.loadingWrap} style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
                    <Spinner size="tiny" />
                    <Text>Loading session details...</Text>
                  </div>
                )}
                {selectedItem.nodeId === 'outcome-plan' ? (
                  <OutcomePlanPanel
                    runId={coordinatorRunId}
                    projectId={projectId}
                    events={events}
                    streamStatus="streaming"
                    runStatus={runDetail?.status ?? undefined}
                    onReconnect={onCoordinatorFollowUp}
                    onClarifyPlan={focusOutcomePlanClarification}
                    clarificationSent={selectedItem.status === 'needs_clarification'}
                    dispatched={outcomePlanDispatched}
                  />
                ) : (
                  <>
                    {selectedRaiVerdict && <RaiVerdictCard verdict={selectedRaiVerdict} />}
                    {!runDetailLoading && turns.length === 0 && !selectedRaiVerdict && (
                      <EmptySessionStatusFallback item={selectedItem} />
                    )}
                    <RunTimeline
                      embedded
                      steps={timelineModel.steps}
                      running={timelineModel.running}
                      emptyHint="Messages, tool calls, and activity will appear here as the run emits events."
                    />
                    {timelineApprovals.length > 0 && (
                      <div className={styles.timelineApprovals}>
                        {timelineApprovals.map((approval) => (
                          <InThreadApprovalGate
                            key={`approval-${approval.event.sequence}`}
                            event={approval.event}
                            runId={selectedRunId}
                            isResolved={approval.isResolved}
                            resolvedScope={approval.resolvedScope}
                          />
                        ))}
                      </div>
                    )}
                  </>
                )}
                <div ref={messagesEndRef} data-testid="session-message-end" />
                {selectedItem.nodeId !== 'outcome-plan' && turns.length > 0 && (
                  <div className={styles.jumpToLatestBar}>
                    <Button
                      appearance="secondary"
                      size="small"
                      icon={<ChevronDownRegular />}
                      onClick={jumpToLatestMessage}
                      data-testid="jump-to-latest-messages"
                    >
                      Jump to latest
                    </Button>
                  </div>
                )}
              </div>
            </div>
            {runChips && (
              <div className={styles.runChipsBar} data-testid="run-summary-chips">
                {runChips}
              </div>
            )}
            <div className={styles.composerStack}>
              {(pendingApprovalCount > 0 || pendingQuestionCount > 0) && (
                <MessageBar intent="warning" className={styles.stickyNeedInput}>
                  <MessageBarBody>
                    Needs input: {pendingApprovalCount} approval{pendingApprovalCount === 1 ? '' : 's'}
                    {pendingQuestionCount > 0 ? `, ${pendingQuestionCount} question${pendingQuestionCount === 1 ? '' : 's'}` : ''}.
                  </MessageBarBody>
                </MessageBar>
              )}
              {followUpError && (
                <MessageBar intent="error" className={styles.stickyNeedInput}>
                  <MessageBarBody>{followUpError}</MessageBarBody>
                </MessageBar>
              )}
              <Text className={styles.composerContext}>{composerContext}</Text>
              <div className={styles.stickyComposer} ref={composerRef}>
                <Composer
                  value={followUp}
                  placeholder="Message coordinator..."
                  readOnly={isNonCoordinatorAgentScope}
                  readOnlyNote={readOnlyComposerNote}
                  onChange={(value) => {
                    setFollowUp(value);
                    setFollowUpError(null);
                    setFollowUpNotice(null);
                  }}
                  onSubmit={(_, data) => {
                    if (data.value.trim()) void handleSendFollowUp();
                  }}
                  disabled={!coordinatorActive || followUpBusy}
                  disableSend={!coordinatorActive || followUpBusy || !followUp.trim()}
                  contentBefore={null}
                  actions={credits ? (
                    <AiCredits
                      totalNanoAiu={credits.totalNanoAiu}
                      detail={credits.detail}
                      showZero
                      data-testid="composer-credits"
                    />
                  ) : null}
                />
              </div>
              {automation && !isNonCoordinatorAgentScope && (
                <div className={styles.composerUtilityRow} data-testid="composer-automation-toggles">
                  <AutomationToggle
                    label="Autopilot"
                    info={AUTOMATION_HELP.autopilotOrchestration}
                    checked={automation.autopilot}
                    disabled={!automation.canToggle || automation.autopilotBusy}
                    onChange={() => automation.onToggleAutopilot()}
                  />
                  <AutomationToggle
                    label="Auto-approve safe tools"
                    info={AUTOMATION_HELP.autoApproveOrchestration}
                    checked={automation.autoApprove}
                    disabled={!automation.canToggle || automation.autoApproveBusy}
                    onChange={() => automation.onToggleAutoApprove()}
                  />
                </div>
              )}
              <div id="coordinator-message-status" aria-live="polite">
                {composerAvailabilityMessage && (
                  <Text className={styles.composerStatus}>{composerAvailabilityMessage}</Text>
                )}
                {!composerAvailabilityMessage && !followUpError && !followUpNotice && (
                  <Text className={styles.composerStatus}>
                    Sends through the coordinator steering API; replies appear when the run stream updates.
                  </Text>
                )}
                {followUpNotice && (
                  <Text className={mergeClasses(styles.composerStatus, styles.composerStatusSuccess)}>
                    {followUpNotice}
                  </Text>
                )}
              </div>
            </div>
          </section>
        </div>
      </div>

      <FileViewerModal
        runId={selectedRunId}
        filePath={selectedPath}
        onClose={clearSelection}
        diff={selectedDiff}
        diffLoading={selectedDiffLoading}
        diffError={selectedDiffError}
        isChanged={selectedPathIsChanged}
        getContent={effectiveAdapter?.getContent}
      />
    </>
  );
}

// Renders an in-thread approval using the native ApprovalGate primitive (components/ui/agentic).
// Plain-worded Approve/Deny wired to the SAME approval handlers the legacy card used
// (apiClient.approveTool/denyTool for tool approvals, approveShell/denyShell for shell
// approvals). Bubbled child approvals target the child run id from the event payload.
function InThreadApprovalGate({
  event,
  runId,
  isResolved,
  resolvedScope,
}: {
  event: RunStreamEvent;
  runId: string;
  isResolved: boolean;
  resolvedScope: string | null;
}) {
  const styles = useStyles();
  const [localResolution, setLocalResolution] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  // Server-driven resolution (replay / SSE) is read straight from props; local state only
  // holds the optimistic outcome of a click. This avoids a set-state-in-effect sync.
  const resolution = localResolution ?? (isResolved ? (resolvedScope ?? 'expired') : null);

  const isShell = event.type === 'shell.approval_required';
  const requestId = readString(event.payload, ['requestId', 'request_id']) ?? '';
  const commandHash = readString(event.payload, ['commandHash', 'command_hash']) ?? '';
  const toolName = readString(event.payload, ['toolName', 'tool_name']) ?? (isShell ? 'run_command' : 'tool');
  const rawUrl = readString(event.payload, ['url']) ?? null;
  const url = rawUrl && rawUrl.length > 80 ? `${rawUrl.slice(0, 80)}…` : rawUrl;
  const command = readString(event.payload, ['command']);
  const intention = readString(event.payload, ['intention', 'message']) ?? null;
  // Bubbled child approvals must target the child subtask run, never the coordinator run.
  const targetRunId = readString(event.payload, ['childRunId', 'child_run_id']) ?? runId;

  const settle = async (fn: () => Promise<void>, outcome: string) => {
    if (!targetRunId || resolution !== null || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await fn();
      setLocalResolution(outcome);
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const handleApprove = () => {
    void settle(
      () => (isShell
        ? apiClient.approveShell(targetRunId, commandHash)
        : apiClient.approveTool(targetRunId, requestId, 'once')),
      'once',
    );
  };
  const handleDeny = () => {
    void settle(
      () => (isShell
        ? apiClient.denyShell(targetRunId, commandHash)
        : apiClient.denyTool(targetRunId, requestId)),
      'deny',
    );
  };

  if (resolution !== null) {
    const label = resolution === 'expired'
      ? `This approval request expired · ${toolName}`
      : resolution === 'deny'
        ? `Denied · ${toolName}`
        : `Allowed · ${toolName}`;
    return (
      <div className={styles.approvalResolved} data-testid="session-approval-resolved">
        <Text className={styles.fileMeta}>{label}</Text>
      </div>
    );
  }

  const target = isShell ? (command ?? 'a shell command') : toolName;
  const detail = isShell ? null : url;
  const riskText = [
    `Allow ${target}${detail ? ` to reach ${detail}` : ''}?`,
    intention ?? undefined,
    'Nothing runs until you approve. You can review the results afterwards.',
  ].filter(Boolean).join(' ');

  return (
    <div className={styles.approvalGateWrap} data-testid="session-approval-gate">
      <Text className={styles.approvalGateHeading} weight="semibold">
        {isShell ? 'Command approval required' : 'Tool Approval Required'}
      </Text>
      <ApprovalGate
        stepId={requestId || commandHash || `approval-${event.sequence}`}
        riskText={riskText}
        approveLabel="Allow once"
        denyLabel="Deny"
        onApprove={handleApprove}
        onDeny={handleDeny}
      />
      {actionError && (
        <Text className={styles.composerStatus} role="alert">Approval failed: {actionError}</Text>
      )}
    </div>
  );
}
