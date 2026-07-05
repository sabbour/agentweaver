import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  Button,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  ChevronDownRegular,
  ChevronRightRegular,
  CheckmarkCircleFilled,
  CircleRegular,
  ClockRegular,
  CopyRegular,
  DismissRegular,
  DismissCircleFilled,
  DocumentRegular,
  OpenRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import type { WorkspaceFileDiff, WorkspaceFileEntry } from '../api/types';
import { useRunStream, type EventType, type RunStreamEvent } from '../api/sse';
import { AgentAvatar } from './AgentAvatar';
import { DiffViewer } from './DiffViewer';
import { FileViewerModal } from './FileViewerModal';
import { deriveHumanTitle } from '../timeline/reducer';

const PANEL_TOP = '48px';
const SEED_STATUSES: ReadonlySet<string> = new Set([
  'completed', 'failed', 'merged', 'declined', 'merge_failed',
  'parked', 'assemble_ready', 'assembled', 'cancelled', 'stopped',
]);

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
    borderRadius: '999px',
    backgroundColor: tokens.colorNeutralStroke3,
  },
  shell: {
    flex: 1,
    minHeight: 0,
    display: 'grid',
    gridTemplateColumns: '260px minmax(0, 1fr)',
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
    backgroundColor: tokens.colorBrandBackground2,
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
  statusGlyphRunning: { color: tokens.colorBrandForeground1 },
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
  content: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
  },
  tabBody: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
  },
  emptyState: {
    padding: tokens.spacingVerticalXL,
    color: tokens.colorNeutralForeground3,
  },
  conversationTurn: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  messageRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  messageMeta: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  messageRole: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  messageBubble: {
    borderRadius: tokens.borderRadiusLarge,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  bubbleSystem: {
    backgroundColor: tokens.colorNeutralBackground2,
  },
  bubbleUser: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  bubbleAgent: {
    backgroundColor: tokens.colorNeutralBackground3,
  },
  toolsBox: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke2}`,
    paddingLeft: tokens.spacingHorizontalM,
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
  toolsList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  toolRow: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground2,
  },
  fileRows: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  fileRow: {
    display: 'grid',
    gridTemplateColumns: '16px minmax(0, 1fr) auto auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
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
  },
  stickyComposer: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingHorizontalL,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  composerInput: {
    flex: 1,
  },
  composerError: {
    padding: `0 ${tokens.spacingHorizontalL} ${tokens.spacingVerticalS}`,
  },
  summaryRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  summaryText: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
  },
  diffList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  diffCard: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  diffHeader: {
    width: '100%',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    backgroundColor: tokens.colorNeutralBackground2,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
  },
  diffHeaderToggle: {
    width: '100%',
    display: 'grid',
    gridTemplateColumns: '16px minmax(0, 1fr) auto auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    border: 'none',
    backgroundColor: 'transparent',
    textAlign: 'left',
    padding: 0,
    cursor: 'pointer',
  },
  diffPath: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontWeight: tokens.fontWeightSemibold,
  },
  diffMode: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  diffContent: {
    minHeight: '140px',
    maxHeight: '320px',
  },
  filesList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  filesListRow: {
    display: 'grid',
    gridTemplateColumns: '16px minmax(0, 1fr) auto auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  loadingWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalXL,
  },
  footerLink: {
    alignSelf: 'flex-start',
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
}

interface ConversationRow {
  key: string;
  role: 'system' | 'user' | 'agent';
  content: string;
  timestamp?: number;
}

interface ConversationTool {
  callId: string;
  title: string;
  settled: boolean;
  args: Record<string, unknown>;
}

interface ConversationTurn {
  key: string;
  rows: ConversationRow[];
  toolCalls: ConversationTool[];
  filePaths: string[];
}

interface FlatTreeNode extends RunSessionTree {
  guides: boolean[];
  isLast: boolean;
  level: number;
  isCoordinator: boolean;
}

function mergeRunEvents(seed: RunStreamEvent[], live: RunStreamEvent[]): RunStreamEvent[] {
  if (seed.length === 0) return live;
  const merged = [...seed];
  const seenSeq = new Set(seed.filter((e) => e.sequence > 0).map((e) => e.sequence));
  const seenType = new Set(seed.map((e) => e.type));
  for (const evt of live) {
    if (evt.sequence > 0) {
      if (seenSeq.has(evt.sequence)) continue;
      seenSeq.add(evt.sequence);
    } else if (seenType.has(evt.type)) {
      continue;
    }
    merged.push(evt);
  }
  return merged;
}

function readString(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (value != null && String(value).trim() !== '') return String(value);
  }
  return undefined;
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

function formatStartedMeta(startedAt?: string | null): string {
  if (!startedAt) return 'Started just now';
  const elapsed = Math.max(0, Date.now() - new Date(startedAt).getTime());
  return `Started ${formatDurationMs(elapsed)} ago`;
}

function formatTimestamp(ms?: number): string {
  if (!ms) return '';
  return new Intl.DateTimeFormat(undefined, {
    hour: 'numeric',
    minute: '2-digit',
  }).format(ms);
}

function statusLabel(status: string): string {
  switch (status) {
    case 'dispatched': return 'Dispatching';
    case 'running':
    case 'in_progress': return 'Running';
    case 'assemble_ready': return 'Awaiting assembly';
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

function statusIcon(status: string): string {
  switch (status) {
    case 'completed':
    case 'merged':
      return '✅';
    case 'running':
    case 'dispatched':
    case 'dispatching':
    case 'in_progress':
      return '🔄';
    case 'waiting':
    case 'pending':
    case 'assemble_ready':
      return '⏳';
    case 'failed':
    case 'merge_failed':
    case 'declined':
    case 'rai_flagged':
      return '❌';
    default:
      return '⚪';
  }
}

type StatusKind = 'success' | 'danger' | 'awaiting' | 'running' | 'pending';

function statusKind(status: string): StatusKind {
  switch (status) {
    case 'completed':
    case 'merged':
    case 'assemble_ready':
      return 'success';
    case 'failed':
    case 'merge_failed':
    case 'declined':
      return 'danger';
    case 'rai_flagged':
    case 'waiting':
    case 'awaiting_assembly':
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
  let current: ConversationTurn | null = null;
  let syntheticIndex = 0;

  const ensureTurn = () => {
    if (current) return current;
    syntheticIndex += 1;
    current = { key: `synthetic-${syntheticIndex}`, rows: [], toolCalls: [], filePaths: [] };
    turns.push(current);
    return current;
  };

  const addFilePath = (turn: ConversationTurn, args: Record<string, unknown>) => {
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
    if (evt.type === 'agent.message') {
      const content = readString(evt.payload, ['content']);
      if (!content) continue;
      ensureTurn().rows.push({
        key: `agent-${evt.sequence}`,
        role: 'agent',
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
        title: deriveHumanTitle(toolName, args),
        settled: false,
        args,
      };
      const turn = ensureTurn();
      turn.toolCalls.push(call);
      addFilePath(turn, args);
      pendingTools.set(call.callId, call);
      continue;
    }
    if (evt.type === 'tool.result' || evt.type === 'tool.error') {
      const callId = String(evt.payload['callId'] ?? '');
      const tool = pendingTools.get(callId);
      if (tool) tool.settled = true;
    }
  }
  return turns.filter((turn) => turn.rows.length > 0 || turn.toolCalls.length > 0);
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

function badgeColor(status: string): 'danger' | 'success' | 'informative' | 'subtle' {
  if (status === 'failed' || status === 'merge_failed' || status === 'declined' || status === 'rai_flagged') return 'danger';
  if (status === 'completed' || status === 'merged') return 'success';
  if (status === 'running' || status === 'dispatched' || status === 'dispatching' || status === 'in_progress') return 'informative';
  return 'subtle';
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
}: AgentSessionPanelProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [isVisible, setIsVisible] = useState(open);
  const [activeTab, setActiveTab] = useState<'messages' | 'changes' | 'files'>('messages');
  const [seedEvents, setSeedEvents] = useState<RunStreamEvent[]>([]);
  const [runDetailLoading, setRunDetailLoading] = useState(false);
  const [runDetailError, setRunDetailError] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<{ started_at?: string | null; ended_at?: string | null; status?: string | null } | null>(null);
  const [filesLoading, setFilesLoading] = useState(false);
  const [filesError, setFilesError] = useState<string | null>(null);
  const [files, setFiles] = useState<WorkspaceFileEntry[]>([]);
  const [expandedPaths, setExpandedPaths] = useState<Set<string>>(new Set());
  const [diffs, setDiffs] = useState<Record<string, WorkspaceFileDiff | null | undefined>>({});
  const [loadingDiffs, setLoadingDiffs] = useState<Set<string>>(new Set());
  const [previewPath, setPreviewPath] = useState<string | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [followUp, setFollowUp] = useState('');
  const [followUpBusy, setFollowUpBusy] = useState(false);
  const [followUpError, setFollowUpError] = useState<string | null>(null);

  const coordinatorNodeId = tree[0]?.nodeId ?? null;
  const flatTree = useMemo(
    () => flattenTree(tree, coordinatorNodeId),
    [tree, coordinatorNodeId],
  );
  const selectedItem = useMemo(
    () => flatTree.find((item) => item.nodeId === selectedNodeId) ?? flatTree[0] ?? null,
    [flatTree, selectedNodeId],
  );
  const selectedRunId = selectedItem
    ? (selectedItem.isCoordinator ? coordinatorRunId : (selectedItem.childRunId ?? ''))
    : '';
  const { events: liveEvents } = useRunStream(open && selectedRunId ? selectedRunId : '');
  const events = useMemo(() => mergeRunEvents(seedEvents, liveEvents), [seedEvents, liveEvents]);
  const turns = useMemo(() => buildTurns(events), [events]);

  useEffect(() => {
    if (open) {
      setIsVisible(true);
      return undefined;
    }
    const timeoutId = window.setTimeout(() => setIsVisible(false), 220);
    return () => window.clearTimeout(timeoutId);
  }, [open]);

  useEffect(() => {
    if (!open) return undefined;
    const onKeyDown = (evt: KeyboardEvent) => {
      if (evt.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open, onClose]);

  useEffect(() => {
    setActiveTab('messages');
    setSeedEvents([]);
    setFiles([]);
    setFilesError(null);
    setExpandedPaths(new Set());
    setDiffs({});
    setLoadingDiffs(new Set());
    setPreviewPath(null);
    setFollowUpError(null);
  }, [selectedRunId]);

  useEffect(() => {
    if (!open || !selectedRunId) {
      setRunDetail(null);
      setRunDetailError(null);
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
        setRunDetailError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!cancelled) setRunDetailLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [open, selectedRunId]);

  useEffect(() => {
    if (!open || !selectedRunId || activeTab === 'messages') return undefined;
    let cancelled = false;
    setFilesLoading(true);
    setFilesError(null);
    apiClient.getRunFiles(selectedRunId)
      .then((result) => {
        if (!cancelled) setFiles(result);
      })
      .catch((err: unknown) => {
        if (!cancelled) setFilesError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!cancelled) setFilesLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [activeTab, open, selectedRunId]);

  const loadDiff = useCallback(async (path: string) => {
    if (!selectedRunId || diffs[path] !== undefined || loadingDiffs.has(path)) return;
    setLoadingDiffs((prev) => new Set(prev).add(path));
    try {
      const diff = await apiClient.getRunFileDiff(selectedRunId, path);
      setDiffs((prev) => ({ ...prev, [path]: diff }));
    } catch {
      setDiffs((prev) => ({ ...prev, [path]: null }));
    } finally {
      setLoadingDiffs((prev) => {
        const next = new Set(prev);
        next.delete(path);
        return next;
      });
    }
  }, [diffs, loadingDiffs, selectedRunId]);

  const toggleDiff = useCallback((path: string) => {
    setExpandedPaths((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
    void loadDiff(path);
  }, [loadDiff]);

  const openPreview = useCallback(async (path: string) => {
    setPreviewPath(path);
    if (diffs[path] !== undefined || loadingDiffs.has(path)) return;
    setPreviewLoading(true);
    try {
      await loadDiff(path);
    } finally {
      setPreviewLoading(false);
    }
  }, [diffs, loadDiff, loadingDiffs]);

  const handleSendFollowUp = useCallback(async () => {
    const instruction = followUp.trim();
    if (!instruction || followUpBusy) return;
    setFollowUpBusy(true);
    setFollowUpError(null);
    try {
      await apiClient.steerCoordinator(coordinatorRunId, { kind: 'send', instruction });
      setFollowUp('');
      onCoordinatorFollowUp?.();
    } catch (err: unknown) {
      setFollowUpError(err instanceof Error ? err.message : String(err));
    } finally {
      setFollowUpBusy(false);
    }
  }, [coordinatorRunId, followUp, followUpBusy, onCoordinatorFollowUp]);

  const totalAdded = files.reduce((sum, file) => sum + file.added_lines, 0);
  const totalRemoved = files.reduce((sum, file) => sum + file.removed_lines, 0);
  const runLink = selectedItem?.isCoordinator
    ? `/projects/${projectId}/orchestrations/${selectedRunId}`
    : `/projects/${projectId}/runs/${selectedRunId}/workflow`;

  if (!selectedItem || !isVisible) return null;

  return (
    <>
      <div
        className={mergeClasses(styles.backdrop, open && styles.backdropOpen)}
        aria-hidden="true"
        onClick={open ? onClose : undefined}
      />
      <div
        className={mergeClasses(styles.panel, open && styles.panelOpen)}
        role="dialog"
        aria-label="Agent session details"
        aria-hidden={!open}
      >
        <div className={styles.dragHandleWrap}>
          <div className={styles.dragHandle} aria-hidden="true" />
        </div>

        <div className={styles.shell}>
          <aside className={styles.sidebar}>
            <div className={styles.sidebarHeader}>
              <Text className={styles.sidebarTitle}>Agent Sessions</Text>
              <Button appearance="subtle" size="small" icon={<DismissRegular />} aria-label="Close panel" onClick={onClose} />
            </div>
            <div className={styles.treeScroll}>
              {flatTree.map((item) => {
                const selected = item.nodeId === selectedItem.nodeId;
                const secondary = item.agentName || item.agentRole
                  ? `${item.agentName ?? item.label}${item.agentRole ? ` · ${item.agentRole}` : ''}`
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
                    <span className={glyphClass}>
                      <StatusGlyph status={item.status} />
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
          </aside>

          <section className={styles.main}>
            <div className={styles.mainHeader}>
              <div className={styles.mainHeaderInfo}>
                <div className={styles.badgeRow}>
                  <Badge appearance="tint" color={badgeColor(selectedItem.status)}>
                    {statusIcon(selectedItem.status)} {statusLabel(selectedItem.status)}
                  </Badge>
                </div>
                <div className={styles.identityRow}>
                  <AgentAvatar name={selectedItem.agentName ?? selectedItem.label} size={28} circle />
                  <div className={styles.identityText}>
                    <Text className={styles.agentName}>{selectedItem.label}</Text>
                    <Text className={styles.agentRole}>
                      {selectedItem.agentName ?? 'Coordinator'}
                      {selectedItem.agentRole ? ` · ${selectedItem.agentRole}` : ''}
                    </Text>
                  </div>
                </div>
                <Text className={styles.metaText}>
                  {formatStartedMeta(runDetail?.started_at ?? undefined)}
                  {runDetailError ? ' · Metadata unavailable' : ''}
                </Text>
              </div>
              <div className={styles.headerActions}>
                <Button
                  appearance="subtle"
                  icon={<OpenRegular />}
                  aria-label="Open full run page"
                  onClick={() => navigate(runLink)}
                />
                <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close panel" onClick={onClose} />
              </div>
            </div>

            <TabList
              className={styles.tabList}
              selectedValue={activeTab}
              onTabSelect={(_, data) => setActiveTab(data.value as 'messages' | 'changes' | 'files')}
            >
              <Tab value="messages">Messages</Tab>
              <Tab value="changes">Changes ({files.length})</Tab>
              <Tab value="files">Files ({files.length})</Tab>
            </TabList>

            <div className={styles.content}>
              {activeTab === 'messages' && (
                <>
                  <div className={styles.tabBody}>
                    {runDetailLoading && (
                      <div className={styles.loadingWrap}>
                        <Spinner size="tiny" />
                        <Text>Loading session details...</Text>
                      </div>
                    )}
                    {!runDetailLoading && turns.length === 0 && (
                      <Text className={styles.emptyState}>No streamed messages yet for this session.</Text>
                    )}
                    {turns.map((turn) => (
                      <ConversationTurnBlock key={turn.key} turn={turn} onPreviewFile={openPreview} />
                    ))}
                  </div>
                  {selectedItem.isCoordinator && (
                    <>
                      <div className={styles.stickyComposer}>
                        <Input
                          className={styles.composerInput}
                          placeholder="Ask a follow-up..."
                          value={followUp}
                          onChange={(_, data) => setFollowUp(data.value)}
                          disabled={!coordinatorActive || followUpBusy}
                        />
                        <Button
                          appearance="primary"
                          icon={followUpBusy ? <Spinner size="tiny" /> : <SendRegular />}
                          disabled={!coordinatorActive || !followUp.trim()}
                          onClick={() => { void handleSendFollowUp(); }}
                        />
                      </div>
                      {followUpError && (
                        <div className={styles.composerError}>
                          <MessageBar intent="error">
                            <MessageBarBody>{followUpError}</MessageBarBody>
                          </MessageBar>
                        </div>
                      )}
                    </>
                  )}
                </>
              )}

              {activeTab === 'changes' && (
                <div className={styles.tabBody}>
                  {filesLoading ? (
                    <div className={styles.loadingWrap}>
                      <Spinner size="tiny" />
                      <Text>Loading changes...</Text>
                    </div>
                  ) : filesError ? (
                    <MessageBar intent="warning">
                      <MessageBarBody>{filesError}</MessageBarBody>
                    </MessageBar>
                  ) : files.length === 0 ? (
                    <Text className={styles.emptyState}>No diff artifacts available for this session yet.</Text>
                  ) : (
                    <>
                      <div className={styles.summaryRow}>
                        <Text className={styles.summaryText}>
                          {files.length} file{files.length === 1 ? '' : 's'} changed · +{totalAdded} -{totalRemoved}
                        </Text>
                      </div>
                      <div className={styles.diffList}>
                        {files.map((file) => {
                          const expanded = expandedPaths.has(file.path);
                          const diff = diffs[file.path];
                          const loading = loadingDiffs.has(file.path);
                          return (
                            <div key={file.path} className={styles.diffCard}>
                              <div className={styles.diffHeader}>
                                <button className={styles.diffHeaderToggle} onClick={() => toggleDiff(file.path)} aria-expanded={expanded}>
                                  {expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
                                  <Text className={styles.diffPath}>{file.path}</Text>
                                  <Text className={styles.fileMeta}>+{file.added_lines} -{file.removed_lines}</Text>
                                  <Text className={styles.diffMode}>Unified</Text>
                                </button>
                                <div style={{ display: 'flex', gap: tokens.spacingHorizontalXXS }}>
                                  <Button
                                    appearance="subtle"
                                    size="small"
                                    icon={<OpenRegular />}
                                    onClick={() => { void openPreview(file.path); }}
                                  >
                                    Preview
                                  </Button>
                                  <Button
                                    appearance="subtle"
                                    size="small"
                                    icon={<CopyRegular />}
                                    aria-label={`Copy diff for ${file.path}`}
                                    onClick={() => {
                                      const text = diff?.diff ?? file.path;
                                      void navigator.clipboard?.writeText(text);
                                    }}
                                  />
                                </div>
                              </div>
                              {expanded && (
                                <div className={styles.diffContent}>
                                  {loading ? (
                                    <div className={styles.loadingWrap}>
                                      <Spinner size="tiny" />
                                      <Text>Loading diff...</Text>
                                    </div>
                                  ) : diff?.diff ? (
                                    <DiffViewer diff={diff.diff} filename={file.path} />
                                  ) : (
                                    <Text className={styles.emptyState}>Diff preview unavailable for this file.</Text>
                                  )}
                                </div>
                              )}
                            </div>
                          );
                        })}
                      </div>
                      <Button className={styles.footerLink} appearance="subtle" icon={<OpenRegular />} onClick={() => navigate(runLink)}>
                        View all changes
                      </Button>
                    </>
                  )}
                </div>
              )}

              {activeTab === 'files' && (
                <div className={styles.tabBody}>
                  {filesLoading ? (
                    <div className={styles.loadingWrap}>
                      <Spinner size="tiny" />
                      <Text>Loading files...</Text>
                    </div>
                  ) : filesError ? (
                    <MessageBar intent="warning">
                      <MessageBarBody>{filesError}</MessageBarBody>
                    </MessageBar>
                  ) : files.length === 0 ? (
                    <Text className={styles.emptyState}>No output files available for this session yet.</Text>
                  ) : (
                    <div className={styles.filesList}>
                      {files.map((file) => (
                        <div key={file.path} className={styles.filesListRow}>
                          <DocumentRegular />
                          <Text className={styles.fileName}>{file.path}</Text>
                          <Text className={styles.fileMeta}>Size unavailable</Text>
                          <Button appearance="subtle" size="small" icon={<OpenRegular />} onClick={() => { void openPreview(file.path); }}>
                            Preview
                          </Button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          </section>
        </div>
      </div>

      <FileViewerModal
        runId={selectedRunId}
        filePath={previewPath}
        onClose={() => setPreviewPath(null)}
        diff={previewPath ? diffs[previewPath] ?? null : null}
        diffLoading={previewLoading || (previewPath ? loadingDiffs.has(previewPath) : false)}
        diffError={null}
        isChanged
      />
    </>
  );
}

function ConversationTurnBlock({
  turn,
  onPreviewFile,
}: {
  turn: ConversationTurn;
  onPreviewFile: (path: string) => void;
}) {
  const styles = useStyles();
  const [toolsOpen, setToolsOpen] = useState(false);
  const completedTools = turn.toolCalls.filter((tool) => tool.settled).length;

  return (
    <div className={styles.conversationTurn}>
      {turn.rows.map((row) => (
        <div key={row.key} className={styles.messageRow}>
          <div className={styles.messageMeta}>
            <Text className={styles.messageRole}>
              {row.role === 'user' ? 'User (Coordinator)' : row.role}
            </Text>
            <Text className={styles.fileMeta}>{formatTimestamp(row.timestamp)}</Text>
          </div>
          <div
            className={mergeClasses(
              styles.messageBubble,
              row.role === 'system'
                ? styles.bubbleSystem
                : row.role === 'user'
                  ? styles.bubbleUser
                  : styles.bubbleAgent,
            )}
          >
            {row.content}
          </div>
        </div>
      ))}

      {turn.toolCalls.length > 0 && (
        <div className={styles.toolsBox}>
          <button className={styles.toolsButton} onClick={() => setToolsOpen((value) => !value)} aria-expanded={toolsOpen}>
            {toolsOpen ? <ChevronDownRegular /> : <ChevronRightRegular />}
            <Text>Tool calls · {completedTools}/{turn.toolCalls.length} completed</Text>
          </button>
          {toolsOpen && (
            <div className={styles.toolsList}>
              {turn.toolCalls.map((tool) => (
                <Text key={tool.callId} className={styles.toolRow}>
                  {tool.title}
                </Text>
              ))}
            </div>
          )}
        </div>
      )}

      {turn.filePaths.length > 0 && (
        <div className={styles.fileRows}>
          {turn.filePaths.map((path) => (
            <div key={path} className={styles.fileRow}>
              <DocumentRegular />
              <Text className={styles.fileName}>{path}</Text>
              <Text className={styles.fileMeta}>Size unavailable</Text>
              <Button appearance="subtle" size="small" icon={<OpenRegular />} onClick={() => onPreviewFile(path)}>
                Preview
              </Button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
