import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
  CodeRegular,
  DismissRegular,
  DismissCircleFilled,
  DocumentRegular,
  DocumentAddRegular,
  DocumentEditRegular,
  EyeRegular,
  GlobeRegular,
  OpenRegular,
  SendRegular,
  WindowConsoleRegular,
} from '@fluentui/react-icons';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
import { apiClient } from '../api/apiClient';
import { useRunStream, type EventType, type RunStreamEvent } from '../api/sse';
import { useArtifactBrowser, type ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import { mergeRunEvents as sharedMergeRunEvents } from '../timeline/mergeRunEvents';
import { AgentAvatar } from './AgentAvatar';
import { CompactChangesList, FilesTabPanel } from './ArtifactBrowser';
import { FileViewerModal } from './FileViewerModal';
import { LifecycleEventCard } from './LifecycleEventCard';
import { deriveHumanTitle } from '../timeline/reducer';
import { OutcomePlanPanel } from './OutcomePlanPanel';

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
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  emptyState: {
    padding: tokens.spacingVerticalXL,
    color: tokens.colorNeutralForeground3,
  },
  conversationTurn: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  messageRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  messageCard: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  messageMeta: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  authorBlock: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  authorName: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
  },
  messageRole: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  messageBubble: {
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} 0`,
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
      paddingLeft: tokens.spacingHorizontalM,
      borderLeft: `3px solid ${tokens.colorNeutralStroke2}`,
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
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    padding: tokens.spacingVerticalS,
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
  },
  fileRow: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
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
  fileCardInfo: {
    display: 'flex',
    flexDirection: 'column',
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
  loadingWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
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
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    flexDirection: 'column',
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
  variant?: 'modal' | 'docked';
  composerFocusSignal?: number;
  onOutcomePlanClarify?: () => void;
  /** Points the shared artifact browser at the coordinator's collective assembly (integration
   *  branch) when a coordinator-aggregate node is selected. Per-subtask runs use the standard
   *  per-run endpoints (no adapter). Mirrors the coordAdapter passed to RunLayout. */
  artifactAdapter?: ArtifactBrowserAdapter;
}

interface ConversationRow {
  key: string;
  role: 'system' | 'user' | 'agent' | 'activity';
  content: string;
  timestamp?: number;
}

interface ConversationTool {
  callId: string;
  toolName: string;
  title: string;
  settled: boolean;
  args: Record<string, unknown>;
}

interface ConversationTurn {
  key: string;
  rows: ConversationRow[];
  toolCalls: ConversationTool[];
  approvals: Array<{ event: RunStreamEvent; isResolved: boolean; resolvedScope: string | null }>;
  filePaths: string[];
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

function normalizeWorkspacePath(value: string, runId?: string): string {
  let path = value.trim().replace(/^file:\/\//i, '').replace(/\\/g, '/');
  path = path.replace(/%2F/gi, '/').replace(/%5C/gi, '/');
  const escapedRunId = runId?.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const runSpecific = escapedRunId
    ? new RegExp(`(?:^|/|[A-Za-z]:/).*/worktrees/${escapedRunId}/(.+)$`, 'i')
    : null;
  const byRun = runSpecific?.exec(path);
  if (byRun?.[1]) return byRun[1].replace(/^\/+/, '');

  const anyRun = /(?:^|\/|[A-Za-z]:\/).*\/worktrees\/[0-9a-f]{8}-[0-9a-f-]{27}\/(.+)$/i.exec(path);
  if (anyRun?.[1]) return anyRun[1].replace(/^\/+/, '');

  const workspace = /(?:^|\/)workspace\/.+?\/worktrees\/[^/]+\/(.+)$/i.exec(path);
  if (workspace?.[1]) return workspace[1].replace(/^\/+/, '');

  return path.replace(/^\/+/, '');
}

function normalizeCommand(command: string, runId?: string): string {
  let normalized = command.trim().replace(/\\/g, '/');
  normalized = normalizeWorkspacePath(normalized, runId);
  normalized = normalized
    .replace(new RegExp(`(?:^|\\s)(?:cd|Set-Location)(?:\\s+-Path)?\\s+['"]?[^;&|]*?/worktrees/${runId ?? '[0-9a-f-]+'}['"]?\\s*(?:&&|;)?\\s*`, 'ig'), ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return normalized.length > 180 ? `${normalized.slice(0, 177)}...` : normalized;
}

function fileName(path: string): string {
  const parts = path.replace(/\\/g, '/').split('/').filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

type ToolOpKind = 'read' | 'write' | 'edit' | 'command' | 'web' | 'other';

interface FriendlyTool {
  label: string;
  muted: boolean;
  kind: ToolOpKind;
  // Full, untruncated detail for the hover title (e.g. the raw command).
  detail?: string;
}

const stripQuotes = (value: string): string => value.replace(/^['"]|['"]$/g, '');

// Turn a raw shell command into a compact, classified operation. Reads/listings are surfaced
// as reads with just the target path (dropping pipe/redirection noise); multi-statement or long
// commands are summarised to the leading program with a "(+N more)" hint. The full command is
// returned as `detail` for the row's hover title.
function summarizeShellCommand(rawCommand: string, runId?: string): FriendlyTool {
  const detail = normalizeCommand(rawCommand, runId);
  const command = detail;

  if (command.length === 0 || /^(pwd|cd\s+\.?|true)$/i.test(command)) {
    return { label: 'Set working directory', muted: true, kind: 'command', detail };
  }

  // A file read via cat/head/tail/less/sed -n anywhere in the pipeline.
  const readMatch = /(?:^|[|;&]\s*)(?:cat|head|tail|less|sed\s+-n\s+\S+)\s+([^\s|;&<>]+)/i.exec(command);
  if (readMatch?.[1]) {
    return { label: `Read ${normalizeWorkspacePath(stripQuotes(readMatch[1]), runId)}`, muted: false, kind: 'read', detail };
  }
  const listMatch = /^(?:ls|find)\s+([^\s|;&<>]+)/i.exec(command);
  if (listMatch?.[1]) {
    return { label: `List ${normalizeWorkspacePath(stripQuotes(listMatch[1]), runId)}`, muted: false, kind: 'read', detail };
  }

  // Otherwise summarise: show the first statement, hint how many more were chained.
  const segments = command.split(/\s*(?:&&|\|\||;|\|)\s*/).filter(Boolean);
  const first = (segments[0] ?? command).trim();
  const shortFirst = first.length > 64 ? `${first.slice(0, 61)}...` : first;
  const label = segments.length > 1 ? `${shortFirst} (+${segments.length - 1} more)` : shortFirst;
  return { label, muted: false, kind: 'command', detail };
}

function friendlyToolLabel(tool: ConversationTool, runId?: string): FriendlyTool {
  const lowerName = tool.toolName.toLowerCase();

  // Web fetch — surface the host, not the full URL.
  const rawUrl = tool.args['url'] ?? tool.args['uri'];
  const urlStr = typeof rawUrl === 'string' ? rawUrl.trim() : '';
  if (lowerName.includes('fetch') || lowerName.includes('web') || /^https?:\/\//i.test(urlStr)) {
    let host = urlStr;
    try { host = new URL(urlStr).host || urlStr; } catch { /* keep raw */ }
    return { label: host ? `Fetch ${host}` : 'Web fetch', muted: false, kind: 'web', detail: urlStr || undefined };
  }

  const rawPath = tool.args['path'] ?? tool.args['file'] ?? tool.args['filePath'] ?? tool.args['filename'];
  if (typeof rawPath === 'string' && rawPath.trim() !== '') {
    const rel = normalizeWorkspacePath(rawPath, runId);
    if (lowerName.includes('write') || lowerName.includes('create')) return { label: `Create ${rel}`, muted: false, kind: 'write', detail: rel };
    if (lowerName.includes('edit') || lowerName.includes('patch') || lowerName.includes('apply')) return { label: `Edit ${rel}`, muted: false, kind: 'edit', detail: rel };
    // Any other path-scoped tool (read_file, view, cat, open, …) is a harmless read.
    return { label: `Read ${rel}`, muted: false, kind: 'read', detail: rel };
  }

  const rawCommand = tool.args['command'] ?? tool.args['cmd'] ?? tool.args['script'];
  if (typeof rawCommand === 'string' && rawCommand.trim() !== '') {
    return summarizeShellCommand(rawCommand, runId);
  }

  return { label: tool.title, muted: false, kind: 'other' };
}

// True for tools that PRODUCE or MODIFY a file — only these deserve a full preview card in the
// session pane. Reads/views are shown compactly as tool-call rows instead, avoiding the noisy
// double-render of a single read as both a row and a large "Workspace file" card.
function isFileWriteTool(toolName: string): boolean {
  const n = toolName.toLowerCase();
  return n.includes('write') || n.includes('create') || n.includes('edit') || n.includes('patch') || n.includes('apply');
}

// A FluentUI glyph that makes the operation type obvious at a glance.
function toolKindIcon(kind: ToolOpKind) {
  switch (kind) {
    case 'read': return <DocumentRegular />;
    case 'write': return <DocumentAddRegular />;
    case 'edit': return <DocumentEditRegular />;
    case 'web': return <GlobeRegular />;
    case 'command': return <WindowConsoleRegular />;
    default: return <CodeRegular />;
  }
}

function authorForRole(role: ConversationRow['role']): { name: string; role: string; collapsedLabel?: string } {
  if (role === 'system') return { name: 'System', role: 'Prompt', collapsedLabel: 'System prompt' };
  if (role === 'user') return { name: 'Coordinator', role: 'Instruction', collapsedLabel: 'Coordinator instruction' };
  if (role === 'activity') return { name: 'Coordinator', role: 'Activity' };
  return { name: 'Agent', role: 'Worker response' };
}

function MarkdownMessage({ content }: { content: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      rehypePlugins={[rehypeSanitize]}
      components={{
        a: ({ href, children, ...props }) => (
          <a href={href} target="_blank" rel="noreferrer" {...props}>
            {children}
          </a>
        ),
      }}
    >
      {content}
    </ReactMarkdown>
  );
}

function statusLabel(status: string): string {
  switch (status) {
    case 'dispatched': return 'Dispatching';
    case 'running':
    case 'in_progress': return 'Running';
    case 'assemble_ready': return 'Awaiting assembly';
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
    case 'rai_flagged':
    case 'waiting':
    case 'awaiting_assembly':
    case 'awaiting_confirmation':
    case 'needs_clarification':
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
      if (tool) tool.settled = true;
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
  const actor = agent ? ` (${agent}${role ? ` · ${role}` : ''})` : '';
  return `${title}${actor}`;
}

function coordinatorActivityLine(evt: RunStreamEvent, subtasks: Map<string, SubtaskNarrativeInfo>): string | null {
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
    case 'coordinator.assembly_changes_requested':
      return 'Human review requested changes; coordinator will redispatch affected subtasks.';
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

function buildCoordinatorTurns(events: RunStreamEvent[]): ConversationTurn[] {
  const subtasks = buildSubtaskInfo(events);
  const turns: ConversationTurn[] = [];
  const resolvedApprovals = new Map<string, string>();
  let firstSystem: ConversationRow | null = null;
  let firstTask: ConversationRow | null = null;

  for (const evt of events) {
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
    const line = coordinatorActivityLine(evt, subtasks);
    if (!line) continue;
    const requestId = readString(evt.payload, ['requestId', 'request_id']) ?? '';
    const resolvedScope = requestId ? (resolvedApprovals.get(requestId) ?? null) : null;
    const approvals = evt.type === 'coordinator.child_approval_required'
      ? [{ event: evt, isResolved: resolvedScope !== null, resolvedScope }]
      : [];
    turns.push({
      key: `coordinator-activity-${evt.sequence}`,
      rows: [{ key: `activity-${evt.sequence}`, role: 'activity', content: line, timestamp: readTimestamp(evt) }],
      toolCalls: [],
      approvals,
      filePaths: [],
    });
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
  variant = 'modal',
  composerFocusSignal = 0,
  onOutcomePlanClarify,
  artifactAdapter,
}: AgentSessionPanelProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const composerRef = useRef<HTMLInputElement>(null);
  const docked = variant === 'docked';
  const [isVisible, setIsVisible] = useState(open);
  const [activeTab, setActiveTab] = useState<'messages' | 'changes' | 'files'>('messages');
  const [seedEvents, setSeedEvents] = useState<RunStreamEvent[]>([]);
  const [runDetailLoading, setRunDetailLoading] = useState(false);
  const [runDetailError, setRunDetailError] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<{ started_at?: string | null; ended_at?: string | null; status?: string | null } | null>(null);
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
    ? (selectedItem.isCoordinator || selectedItem.nodeId === 'outcome-plan' || selectedItem.nodeId === 'work-plan' ? coordinatorRunId : (selectedItem.childRunId ?? ''))
    : '';

  // Coordinator-aggregate nodes (coordinator itself, work-plan, outcome-plan) own no worktree —
  // their artifacts live on the integration branch, so route through the assembly adapter. Per
  // subtask runs use the standard per-run endpoints (undefined adapter).
  const isCoordinatorAggregate = !!selectedItem
    && (selectedItem.isCoordinator
      || selectedItem.nodeId === 'work-plan'
      || selectedItem.nodeId === 'outcome-plan');
  const effectiveAdapter = useMemo(
    () => (isCoordinatorAggregate ? artifactAdapter : undefined),
    [isCoordinatorAggregate, artifactAdapter],
  );

  // Reuse the shared artifact browser hook so the Changes tab renders the dense changed-files list
  // and the Files tab renders the full workspace FOLDER TREE (getRunWorkspace / assembly workspace),
  // not just the changed files. This is the same hook RunLayout and WorkspacePage drive.
  const artifactState = useArtifactBrowser(
    open ? selectedRunId : '',
    runDetail?.status ?? '',
    undefined,
    undefined,
    undefined,
    undefined,
    effectiveAdapter,
  );
  const {
    files,
    filesLoading,
    filesError,
    workspaceFiles,
    workspaceLoading,
    workspaceError,
    selectedPath,
    diff: selectedDiff,
    diffLoading: selectedDiffLoading,
    diffError: selectedDiffError,
    selectedPathIsChanged,
    handleFileSelect,
    clearSelection,
  } = artifactState;

  const { events: liveEvents } = useRunStream(open && selectedRunId ? selectedRunId : '');
  const events = useMemo(() => mergeRunEvents(seedEvents, liveEvents), [seedEvents, liveEvents]);
  const turns = useMemo(
    () => selectedItem?.isCoordinator || selectedItem?.nodeId === 'work-plan' ? buildCoordinatorTurns(events) : buildTurns(events),
    [events, selectedItem?.isCoordinator, selectedItem?.nodeId],
  );

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
    setActiveTab('messages');
    setSeedEvents([]);
    setFollowUpError(null);
  }, [selectedRunId]);

  // Keep the shared artifact hook's internal tab in sync with the panel tab so the workspace
  // FOLDER TREE is fetched when the user opens the Files tab. The hook's setActiveTab identity
  // changes every render, so key the effect on the panel tab only (mirrors RunLayout).
  useEffect(() => {
    artifactState.setActiveTab(activeTab === 'files' ? 'files' : 'changes');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab]);

  useEffect(() => {
    if (composerFocusSignal > 0) {
      if (selectedItem?.nodeId === 'outcome-plan') {
        setFollowUp((value) => value.trim() ? value : 'Clarify the outcome plan: ');
      }
      composerRef.current?.focus();
    }
  }, [composerFocusSignal, selectedItem?.nodeId]);

  const focusOutcomePlanClarification = useCallback(() => {
    setFollowUp((value) => value.trim() ? value : 'Clarify the outcome plan: ');
    setActiveTab('messages');
    onOutcomePlanClarify?.();
    window.setTimeout(() => composerRef.current?.focus(), 0);
  }, [onOutcomePlanClarify]);

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

  // File list, diffs and the workspace tree are now fetched by the shared useArtifactBrowser hook
  // (see artifactState above), so the previous bespoke getRunFiles/getRunFileContent/getRunFileDiff
  // effects and their state have been removed. Opening a file selects it in the shared hook, which
  // drives the FileViewerModal below.
  const openPreview = useCallback((path: string) => {
    const relPath = normalizeWorkspacePath(path, selectedRunId);
    handleFileSelect(relPath, true);
  }, [handleFileSelect, selectedRunId]);

  const handleSendFollowUp = useCallback(async () => {
    const instruction = followUp.trim();
    if (!instruction || followUpBusy) return;
    setFollowUpBusy(true);
    setFollowUpError(null);
    try {
      await apiClient.steerCoordinator(coordinatorRunId, {
        kind: 'send',
        instruction,
        ...(selectedItem && !selectedItem.isCoordinator && selectedItem.childRunId
          ? { target_child_run_id: selectedItem.childRunId }
          : {}),
      });
      setFollowUp('');
      onCoordinatorFollowUp?.();
    } catch (err: unknown) {
      setFollowUpError(err instanceof Error ? err.message : String(err));
    } finally {
      setFollowUpBusy(false);
    }
  }, [coordinatorRunId, followUp, followUpBusy, onCoordinatorFollowUp, selectedItem]);

  const runLink = selectedItem?.isCoordinator
    ? `/projects/${projectId}/orchestrations/${selectedRunId}`
    : `/projects/${projectId}/runs/${selectedRunId}/workflow`;

  if (!selectedItem || !isVisible) return null;

  const pendingApprovalCount = turns.reduce(
    (sum, turn) => sum + turn.approvals.filter((approval) => !approval.isResolved).length,
    0,
  );
  const pendingQuestionCount = events.filter((evt) =>
    evt.type === 'agent.question_asked' || evt.type === 'coordinator.child_question'
  ).length;
  const composerContext = selectedItem.nodeId === 'outcome-plan'
    ? 'Context: Outcome plan'
    : selectedItem.isCoordinator
      ? 'Context: Whole run'
      : `Context: ${selectedItem.label}${selectedItem.agentName ? ` with ${selectedItem.agentName}` : ''}`;

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
          </aside>}

          <section className={styles.main}>
            <div className={styles.mainHeader}>
              <div className={styles.mainHeaderInfo}>
                <div className={styles.badgeRow}>
                  <Badge appearance="tint" color={badgeColor(selectedItem.status)}>
                    {statusLabel(selectedItem.status)}
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
              onClickCapture={(evt) => {
                const target = evt.target as HTMLElement;
                if (target.closest('[data-testid="session-tab-messages"]')) setActiveTab('messages');
                if (target.closest('[data-testid="session-tab-changes"]')) setActiveTab('changes');
                if (target.closest('[data-testid="session-tab-files"]')) setActiveTab('files');
              }}
            >
              <Tab value="messages" data-testid="session-tab-messages" onClick={() => setActiveTab('messages')}>Messages</Tab>
              <Tab value="changes" data-testid="session-tab-changes" onClick={() => setActiveTab('changes')}>Changes ({files.length})</Tab>
              <Tab value="files" data-testid="session-tab-files" onClick={() => setActiveTab('files')}>Files ({files.length})</Tab>
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
                      />
                    ) : !runDetailLoading && turns.length === 0 && (
                      <Text className={styles.emptyState}>No streamed messages yet for this session.</Text>
                    )}
                    {selectedItem.nodeId !== 'outcome-plan' && turns.map((turn) => (
                      <ConversationTurnBlock key={turn.key} turn={turn} runId={selectedRunId} onPreviewFile={openPreview} />
                    ))}
                  </div>
                  <>
                      <div className={styles.composerStack}>
                        {(pendingApprovalCount > 0 || pendingQuestionCount > 0) && (
                          <MessageBar intent="warning" className={styles.stickyNeedInput}>
                            <MessageBarBody>
                              Needs input: {pendingApprovalCount} approval{pendingApprovalCount === 1 ? '' : 's'}
                              {pendingQuestionCount > 0 ? `, ${pendingQuestionCount} question${pendingQuestionCount === 1 ? '' : 's'}` : ''}.
                            </MessageBarBody>
                          </MessageBar>
                        )}
                        <Text className={styles.composerContext}>{composerContext}</Text>
                      <div className={styles.stickyComposer}>
                        <Input
                          ref={composerRef}
                          className={styles.composerInput}
                          placeholder="Message coordinator..."
                          value={followUp}
                          onChange={(_, data) => setFollowUp(data.value)}
                          disabled={!coordinatorActive || followUpBusy}
                        />
                        <Button
                          appearance="primary"
                          aria-label="Send message"
                          icon={followUpBusy ? <Spinner size="tiny" /> : <SendRegular />}
                          disabled={!coordinatorActive || !followUp.trim()}
                          onClick={() => { void handleSendFollowUp(); }}
                        />
                      </div>
                      </div>
                      {followUpError && (
                        <div className={styles.composerError}>
                          <MessageBar intent="error">
                            <MessageBarBody>{followUpError}</MessageBarBody>
                          </MessageBar>
                        </div>
                      )}
                    </>
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
                    <CompactChangesList
                      files={files}
                      selectedPath={selectedPath}
                      onFileClick={(path) => handleFileSelect(path, true)}
                    />
                  )}
                </div>
              )}

              {activeTab === 'files' && (
                <div className={styles.tabBody}>
                  <FilesTabPanel
                    workspaceFiles={workspaceFiles}
                    workspaceLoading={workspaceLoading}
                    workspaceError={workspaceError}
                    selectedPath={selectedPath}
                    onFileClick={(path, isChanged) => handleFileSelect(path, isChanged)}
                  />
                </div>
              )}
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

function ConversationTurnBlock({
  turn,
  runId,
  onPreviewFile,
}: {
  turn: ConversationTurn;
  runId: string;
  onPreviewFile: (path: string) => void;
}) {
  const styles = useStyles();
  const [toolsOpen, setToolsOpen] = useState(false);
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set());
  const completedTools = turn.toolCalls.filter((tool) => tool.settled).length;
  const toggleRow = (key: string) => {
    setExpandedRows((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  return (
    <div className={styles.conversationTurn}>
      {turn.rows.map((row) => {
        const author = authorForRole(row.role);
        const collapsible = row.role === 'system' || row.role === 'user';
        const expanded = !collapsible || expandedRows.has(row.key);
        return (
          <div key={row.key} className={styles.messageCard}>
            <AgentAvatar name={author.name} size={28} circle />
            <div className={styles.messageRow}>
              <div className={styles.messageMeta}>
                <div className={styles.authorBlock}>
                  <Text className={styles.authorName}>{author.name}</Text>
                  <Text className={styles.messageRole}>{author.role}</Text>
                </div>
                <Text className={styles.fileMeta}>{formatTimestamp(row.timestamp)}</Text>
              </div>
              {collapsible ? (
                <>
                  <button className={styles.disclosure} onClick={() => toggleRow(row.key)} aria-expanded={expanded}>
                    {expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
                    <Text>{author.collapsedLabel}</Text>
                  </button>
                  {expanded && (
                    <div className={mergeClasses(styles.messageBubble, styles.markdownBody, row.role === 'system' ? styles.bubbleSystem : styles.bubbleUser)}>
                      <MarkdownMessage content={row.content} />
                    </div>
                  )}
                </>
              ) : (
                <div className={mergeClasses(styles.messageBubble, styles.markdownBody, styles.bubbleAgent)}>
                  <MarkdownMessage content={row.content} />
                </div>
              )}
            </div>
          </div>
        );
      })}

      {turn.toolCalls.length > 0 && (
        <div className={styles.toolsBox}>
          <button className={styles.toolsButton} onClick={() => setToolsOpen((value) => !value)} aria-expanded={toolsOpen}>
            {toolsOpen ? <ChevronDownRegular /> : <ChevronRightRegular />}
            <Text>Tool calls · {completedTools}/{turn.toolCalls.length} completed</Text>
          </button>
          {toolsOpen && (
            <div className={styles.toolsList}>
              {turn.toolCalls.map((tool) => {
                const friendly = friendlyToolLabel(tool, runId);
                return (
                  <Text key={tool.callId} className={mergeClasses(styles.toolRow, friendly.muted && styles.toolRowMuted)} title={friendly.detail ?? friendly.label}>
                    <span className={styles.toolKind} aria-hidden="true">{toolKindIcon(friendly.kind)}</span>
                    <span className={styles.toolLabel}>{friendly.label}</span>
                    <span aria-hidden="true">{tool.settled ? <CheckmarkCircleFilled className={styles.toolCheck} /> : <ClockRegular />}</span>
                  </Text>
                );
              })}
            </div>
          )}
        </div>
      )}

      {turn.approvals.map((approval) => (
        <LifecycleEventCard
          key={`approval-${approval.event.sequence}`}
          event={approval.event}
          runId={runId}
          isResolved={approval.isResolved}
          resolvedScope={approval.resolvedScope}
        />
      ))}

      {turn.filePaths.length > 0 && (
        <div className={styles.fileRows}>
          {turn.filePaths.map((path) => {
            const relPath = normalizeWorkspacePath(path, runId);
            return (
            <div key={path} className={styles.fileRow}>
              <DocumentRegular />
              <div className={styles.fileCardInfo}>
                <Text className={styles.fileName}>{fileName(relPath)}</Text>
                <Text className={styles.fileMeta}>{relPath}</Text>
              </div>
              <Text className={styles.fileMeta}>Workspace file</Text>
              <Button appearance="subtle" size="small" icon={<EyeRegular />} onClick={() => onPreviewFile(relPath)}>
                Preview
              </Button>
            </div>
          );})}
        </div>
      )}
    </div>
  );
}
