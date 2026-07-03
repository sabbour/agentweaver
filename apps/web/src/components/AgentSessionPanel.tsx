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
  CopyRegular,
  DismissRegular,
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
import { formatModelLabel } from '../utils/agentIdentity';
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
    left: 0,
    right: '420px',
    bottom: 0,
    backgroundColor: 'rgba(15, 23, 42, 0.08)',
    pointerEvents: 'none',
    zIndex: 900,
  },
  panel: {
    position: 'fixed',
    top: PANEL_TOP,
    right: 0,
    bottom: 0,
    width: '420px',
    maxWidth: '92vw',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow28,
    transform: 'translateX(100%)',
    transitionProperty: 'transform',
    transitionDuration: tokens.durationNormal,
    transitionTimingFunction: tokens.curveEasyEase,
    zIndex: 901,
  },
  panelOpen: {
    transform: 'translateX(0)',
  },
  panelInner: {
    flex: 1,
    minHeight: 0,
    display: 'grid',
    gridTemplateColumns: '180px minmax(0, 1fr)',
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
    padding: tokens.spacingHorizontalM,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  sidebarTitle: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
  },
  sidebarScroll: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    padding: tokens.spacingHorizontalXS,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  group: {
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  groupButton: {
    width: '100%',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: 'none',
    textAlign: 'left',
    cursor: 'pointer',
  },
  groupLabel: {
    flex: 1,
    minWidth: 0,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  groupItems: {
    display: 'flex',
    flexDirection: 'column',
    padding: tokens.spacingHorizontalXXS,
    gap: '2px',
  },
  itemButton: {
    width: '100%',
    display: 'grid',
    gridTemplateColumns: '10px minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    border: 'none',
    backgroundColor: 'transparent',
    textAlign: 'left',
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  itemSelected: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  itemTitle: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: tokens.fontSizeBase200,
  },
  itemTime: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  statusDot: {
    width: '8px',
    height: '8px',
    borderRadius: '999px',
    display: 'inline-block',
  },
  dotPending: { backgroundColor: tokens.colorNeutralForeground4 },
  dotRunning: { backgroundColor: tokens.colorBrandForeground1 },
  dotCompleted: { backgroundColor: tokens.colorPaletteGreenForeground1 },
  dotFailed: { backgroundColor: tokens.colorPaletteRedForeground1 },
  dotWarning: { backgroundColor: tokens.colorPaletteMarigoldForeground1 },
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
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingHorizontalM,
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
    paddingLeft: tokens.spacingHorizontalS,
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
    padding: tokens.spacingHorizontalM,
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
    padding: tokens.spacingHorizontalM,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  composerInput: {
    flex: 1,
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

export interface AgentSessionPanelItem {
  nodeId: string;
  runId: string;
  title: string;
  agentName?: string;
  agentRole?: string;
  model?: string;
  status: string;
  startedAt?: number;
  completedAt?: number;
  columnIndex: number;
  columnLabel: string;
  isCoordinator?: boolean;
}

export interface AgentSessionPanelColumn {
  index: number;
  label: string;
  items: AgentSessionPanelItem[];
}

interface AgentSessionPanelProps {
  nodeId: string | null;
  coordinatorRunId: string;
  projectId?: string;
  columns: AgentSessionPanelColumn[];
  tabIndex: 0 | 1 | 2;
  onClose: () => void;
  onSelectNode: (nodeId: string) => void;
  onTabChange: (index: 0 | 1 | 2) => void;
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

function formatElapsed(startedAt?: number, completedAt?: number): string {
  if (!startedAt) return '\u2014';
  return formatDurationMs(Math.max(0, (completedAt ?? Date.now()) - startedAt));
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
    case 'dispatched': return 'Dispatched';
    case 'running':
    case 'in_progress': return 'Running';
    case 'assemble_ready': return 'Awaiting assembly';
    case 'rai_flagged': return 'RAI flagged';
    case 'completed':
    case 'merged': return 'Completed';
    case 'failed':
    case 'merge_failed': return 'Failed';
    case 'declined': return 'Declined';
    default: return status ? status.replace(/_/g, ' ') : 'Pending';
  }
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

function statusDotClass(status: string, styles: ReturnType<typeof useStyles>): string {
  if (status === 'failed' || status === 'merge_failed' || status === 'declined') return styles.dotFailed;
  if (status === 'completed' || status === 'merged') return styles.dotCompleted;
  if (status === 'running' || status === 'dispatched' || status === 'in_progress') return styles.dotRunning;
  if (status === 'assemble_ready' || status === 'rai_flagged') return styles.dotWarning;
  return styles.dotPending;
}

export function AgentSessionPanel({
  nodeId,
  coordinatorRunId,
  projectId,
  columns,
  tabIndex,
  onClose,
  onSelectNode,
  onTabChange,
  onCoordinatorFollowUp,
  coordinatorActive = false,
}: AgentSessionPanelProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [collapsedColumns, setCollapsedColumns] = useState<Set<number>>(new Set());
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

  const items = useMemo(() => columns.flatMap((column) => column.items), [columns]);
  const selectedItem = useMemo(
    () => items.find((item) => item.nodeId === nodeId) ?? null,
    [items, nodeId],
  );
  const selectedRunId = selectedItem?.runId ?? '';
  const { events: liveEvents } = useRunStream(selectedRunId);
  const events = useMemo(
    () => mergeRunEvents(seedEvents, liveEvents),
    [seedEvents, liveEvents],
  );
  const turns = useMemo(() => buildTurns(events), [events]);
  const selectedValue = tabIndex === 0 ? 'messages' : tabIndex === 1 ? 'changes' : 'files';

  useEffect(() => {
    setSeedEvents([]);
    setFiles([]);
    setFilesError(null);
    setExpandedPaths(new Set());
    setDiffs({});
    setLoadingDiffs(new Set());
    setPreviewPath(null);
  }, [selectedRunId]);

  useEffect(() => {
    if (!selectedRunId) {
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
  }, [selectedRunId]);

  useEffect(() => {
    if (!selectedRunId) return;
    if (tabIndex === 0) return;
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
  }, [selectedRunId, tabIndex]);

  const toggleAll = useCallback(() => {
    setCollapsedColumns((prev) => (
      prev.size === columns.length
        ? new Set()
        : new Set(columns.map((column) => column.index))
    ));
  }, [columns]);

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

  const openPreview = useCallback((path: string) => {
    setPreviewPath(path);
    setPreviewLoading(true);
    void loadDiff(path);
    setTimeout(() => setPreviewLoading(false), 0);
  }, [loadDiff]);

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
  const statusChip = selectedItem ? statusLabel(selectedItem.status) : 'Pending';
  const dotClass = statusDotClass(selectedItem?.status ?? 'pending', styles);
  const runLink = selectedItem?.isCoordinator
    ? `/projects/${projectId ?? ''}/orchestrations/${selectedRunId}`
    : `/projects/${projectId ?? ''}/runs/${selectedRunId}/workflow`;

  if (!selectedItem) return null;

  return (
    <>
      <div className={styles.backdrop} aria-hidden="true" />
      <div className={mergeClasses(styles.panel, styles.panelOpen)} role="dialog" aria-label="Agent session details">
        <div className={styles.panelInner}>
          <aside className={styles.sidebar}>
            <div className={styles.sidebarHeader}>
              <Text className={styles.sidebarTitle}>Agent Sessions</Text>
              <Button
                appearance="subtle"
                size="small"
                aria-label={collapsedColumns.size === columns.length ? 'Expand all sessions' : 'Collapse all sessions'}
                icon={collapsedColumns.size === columns.length ? <ChevronRightRegular /> : <ChevronDownRegular />}
                onClick={toggleAll}
              />
            </div>
            <div className={styles.sidebarScroll}>
              {columns.map((column) => {
                const collapsed = collapsedColumns.has(column.index);
                const first = column.items[0];
                const groupElapsed = first ? formatElapsed(first.startedAt, first.completedAt) : '\u2014';
                return (
                  <div key={column.index} className={styles.group}>
                    <button
                      className={styles.groupButton}
                      onClick={() => setCollapsedColumns((prev) => {
                        const next = new Set(prev);
                        if (next.has(column.index)) next.delete(column.index);
                        else next.add(column.index);
                        return next;
                      })}
                      aria-expanded={!collapsed}
                    >
                      {collapsed ? <ChevronRightRegular /> : <ChevronDownRegular />}
                      <Text className={styles.groupLabel}>{column.label} · {groupElapsed}</Text>
                    </button>
                    {!collapsed && (
                      <div className={styles.groupItems}>
                        {column.items.map((item) => (
                          <button
                            key={item.nodeId}
                            className={mergeClasses(
                              styles.itemButton,
                              item.nodeId === selectedItem.nodeId && styles.itemSelected,
                            )}
                            onClick={() => onSelectNode(item.nodeId)}
                          >
                            <span className={mergeClasses(styles.statusDot, statusDotClass(item.status, styles))} aria-hidden="true" />
                            <Text className={styles.itemTitle}>{item.title}</Text>
                            <Text className={styles.itemTime}>{formatElapsed(item.startedAt, item.completedAt)}</Text>
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </aside>

          <section className={styles.main}>
            <div className={styles.mainHeader}>
              <div className={styles.mainHeaderInfo}>
                <div className={styles.badgeRow}>
                  <Badge appearance="tint" color={selectedItem.status === 'failed' ? 'danger' : selectedItem.status === 'completed' ? 'success' : 'informative'}>
                    <span className={mergeClasses(styles.statusDot, dotClass)} aria-hidden="true" /> {statusChip}
                  </Badge>
                </div>
                <div className={styles.identityRow}>
                  <AgentAvatar name={selectedItem.agentName ?? selectedItem.title} size={28} circle />
                  <div className={styles.identityText}>
                    <Text className={styles.agentName}>{selectedItem.agentName ?? selectedItem.title}</Text>
                    <Text className={styles.agentRole}>{selectedItem.agentRole ?? selectedItem.columnLabel}</Text>
                  </div>
                </div>
                <Text className={styles.metaText}>
                  {formatStartedMeta(runDetail?.started_at ?? undefined)}
                  {selectedItem.startedAt ? ` · Duration ${formatElapsed(selectedItem.startedAt, selectedItem.completedAt)}` : ''}
                  {selectedItem.model ? ` · Model: ${formatModelLabel(selectedItem.model)}` : ''}
                </Text>
                {runDetailError && <Text className={styles.metaText}>Metadata unavailable</Text>}
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
              selectedValue={selectedValue}
              onTabSelect={(_, data) => onTabChange(data.value === 'messages' ? 0 : data.value === 'changes' ? 1 : 2)}
            >
              <Tab value="messages">Messages</Tab>
              <Tab value="changes">Changes ({files.length})</Tab>
              <Tab value="files">Files ({files.length})</Tab>
            </TabList>

            <div className={styles.content}>
              {tabIndex === 0 && (
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
                  )}
                  {selectedItem.isCoordinator && followUpError && (
                    <MessageBar intent="error">
                      <MessageBarBody>{followUpError}</MessageBarBody>
                    </MessageBar>
                  )}
                </>
              )}

              {tabIndex === 1 && (
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

              {tabIndex === 2 && (
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
                          <Button appearance="subtle" size="small" icon={<OpenRegular />} onClick={() => openPreview(file.path)}>
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
