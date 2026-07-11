import {
  Badge,
  Button,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
  } from '@fluentui/react-components';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { DiffViewer } from './DiffViewer';
import {
  BracesRegular,
  CheckmarkRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  CodeRegular,
  ColorRegular,
  CommentRegular,
  DismissRegular,
  DocumentAddRegular,
  DocumentDismissRegular,
  DocumentEditRegular,
  DocumentPdfRegular,
  DocumentRegular,
  DocumentTextRegular,
  FolderOpenRegular,
  FolderRegular,
  ImageRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
import { EmptyState } from './ui';
import { useMemo, useState, type ReactNode } from 'react';
import type { WorkspaceFileEntry, WorkspaceNode } from '../api/types';
import type { ArtifactBrowserState } from '../hooks/useArtifactBrowser';
// ---------------------------------------------------------------------------
// Tree data model
// ---------------------------------------------------------------------------

interface TreeNode {
  name: string;
  fullPath: string;
  isFolder: boolean;
  status?: 'added' | 'modified' | 'deleted';
  children: TreeNode[];
}

interface TreeNodeInternal {
  name: string;
  fullPath: string;
  isFolder: boolean;
  status?: 'added' | 'modified' | 'deleted';
  childrenMap: Map<string, TreeNodeInternal>;
}

function buildWorkspaceTree(nodes: WorkspaceNode[]): TreeNode[] {
  const rootMap = new Map<string, TreeNodeInternal>();

  for (const node of nodes) {
    const segments = node.path.split('/');
    let currentMap = rootMap;

    for (let i = 0; i < segments.length; i++) {
      const name = segments[i];
      const isLast = i === segments.length - 1;
      const pathSoFar = segments.slice(0, i + 1).join('/');

      if (!currentMap.has(name)) {
        currentMap.set(name, {
          name,
          fullPath: pathSoFar,
          isFolder: isLast ? node.is_folder : true,
          status: isLast && node.status ? node.status : undefined,
          childrenMap: new Map(),
        });
      }

      if (!isLast) {
        currentMap = currentMap.get(name)!.childrenMap;
      }
    }
  }

  function toSortedArray(map: Map<string, TreeNodeInternal>): TreeNode[] {
    const nodes = [...map.values()];
    nodes.sort((a, b) => {
      if (a.isFolder !== b.isFolder) return a.isFolder ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
    return nodes.map((n) => ({
      name: n.name,
      fullPath: n.fullPath,
      isFolder: n.isFolder,
      status: n.status,
      children: toSortedArray(n.childrenMap),
    }));
  }

  return toSortedArray(rootMap);
}

// ---------------------------------------------------------------------------
// Icon helpers
// ---------------------------------------------------------------------------

// Returns a single combined document icon that represents both file and change status.
// Added -> DocumentAddRegular (green), modified -> DocumentEditRegular (orange),
// deleted -> DocumentDismissRegular (red), no status -> DocumentRegular (neutral).
function getFileStatusIcon(status?: string): FluentIcon {
  if (status === 'added') return DocumentAddRegular;
  if (status === 'modified') return DocumentEditRegular;
  if (status === 'deleted') return DocumentDismissRegular;
  return DocumentRegular;
}

// Maps a filename to an extension-specific icon for the file tree. The returned `kind`
// is surfaced as data-file-icon for styling/testing. In the review/diff tree the change
// status COLOR is layered on top via the status icon class — this only picks the glyph.
function fileIconForName(name: string): { Icon: FluentIcon; kind: string } {
  const ext = name.includes('.') ? (name.split('.').pop() ?? '').toLowerCase() : '';
  switch (ext) {
    case 'md':
    case 'markdown':
      return { Icon: DocumentTextRegular, kind: 'markdown' };
    case 'ts':
    case 'tsx':
    case 'js':
    case 'jsx':
    case 'cs':
    case 'py':
    case 'sh':
    case 'ps1':
      return { Icon: CodeRegular, kind: 'code' };
    case 'json':
      return { Icon: BracesRegular, kind: 'json' };
    case 'css':
    case 'scss':
      return { Icon: ColorRegular, kind: 'style' };
    case 'png':
    case 'jpg':
    case 'jpeg':
    case 'gif':
    case 'svg':
    case 'webp':
      return { Icon: ImageRegular, kind: 'image' };
    case 'pdf':
      return { Icon: DocumentPdfRegular, kind: 'pdf' };
    case 'lock':
      return { Icon: LockClosedRegular, kind: 'lock' };
    default:
      // html, yml, yaml, and any unknown extension fall back to the neutral document icon.
      return { Icon: DocumentRegular, kind: 'document' };
  }
}

// ---------------------------------------------------------------------------
// Flat list helpers
// ---------------------------------------------------------------------------

function filename(path: string): string {
  return path.split('/').pop() ?? path;
}

function reviewResultBadgeColor(
  status: string,
): 'success' | 'subtle' | 'danger' | 'warning' {
  if (status === 'review_accepted') return 'subtle';
  if (status === 'merged') return 'success';
  if (status === 'declined') return 'subtle';
  if (status === 'merge_failed') return 'danger';
  if (status === 'merging') return 'subtle';
  return 'danger';
}

function formatReviewResultStatus(status: string): string {
  switch (status) {
    case 'review_accepted': return 'Review accepted';
    case 'changes_requested': return 'Changes requested';
    default: return status.replace(/_/g, ' ');
  }
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useFileTreeStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    height: '100%',
    minHeight: 0,
    minWidth: 0,
    overflow: 'hidden',
  },
  tabListWrapper: {
    flexShrink: 0,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: `0 ${tokens.spacingHorizontalXS}`,
  },
  commitError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  reviewBar: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    flexShrink: 0,
  },
  reviewBarSplitActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    '& button': { flex: 1, whiteSpace: 'nowrap' },
  },
  reviewBarDecline: {
    color: tokens.colorPaletteRedForeground1,
  },
  reviewError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  requestChangesBox: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    marginTop: tokens.spacingVerticalXS,
  },
  requestChangesActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  commitMessageBox: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalXS}`,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    marginBottom: tokens.spacingVerticalXS,
    maxHeight: '120px',
    overflowY: 'auto',
  },
  commitMessageLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.04em',
  },
  commitMessageText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    fontFamily: tokens.fontFamilyMonospace,
  },
  requestChangesLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  requestChangesError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  reviewResultBar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    flexShrink: 0,
  },
  fileList: {
    overflowY: 'auto',
    flex: 1,
    minHeight: 0,
  },
  treeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  treeRowSelected: {
    backgroundColor: tokens.colorNeutralBackground3,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3Hover,
    },
  },
  chevronIcon: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  folderIcon: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground2,
  },
  folderName: {
    color: tokens.colorNeutralForeground1,
    flex: 1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fileIcon: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground3,
  },
  statusIconAdded: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteGreenForeground1,
  },
  statusIconModified: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteMarigoldForeground2,
  },
  statusIconDeleted: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
  },
  fileName: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase300,
    flex: 1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fileNameAdded: {
    color: tokens.colorPaletteGreenForeground1,
  },
  fileNameModified: {
    color: tokens.colorPaletteMarigoldForeground2,
  },
  fileNameDeleted: {
    color: tokens.colorPaletteRedForeground1,
  },
  emptyState: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalL,
  },
  changeHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    flexShrink: 0,
  },
  changeHeaderTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    flex: 1,
  },
  addedCount: {
    color: tokens.colorPaletteGreenForeground1,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    flexShrink: 0,
  },
  removedCount: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    flexShrink: 0,
  },
  flatRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  flatRowSelected: {
    backgroundColor: tokens.colorNeutralBackground3,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3Hover,
    },
  },
  flatFileName: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
    minWidth: 0,
  },
});

const useDiffPanelStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
  },
  content: {
    flex: 1,
    overflow: 'auto',
  },
  placeholder: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    height: '100%',
    padding: tokens.spacingHorizontalM,
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalL,
  },
  binaryNotice: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
  },
});

// Legacy styles used by the combined ArtifactBrowser component.
const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  panels: {
    display: 'flex',
    alignItems: 'stretch',
    gap: tokens.spacingHorizontalM,
    height: '600px',
    overflow: 'hidden',
  },
  leftPanel: {
    display: 'flex',
    flexDirection: 'column',
    width: '280px',
    flexShrink: 0,
    minHeight: 0,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  rightPanel: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

// ---------------------------------------------------------------------------
// Shared tree renderer
// ---------------------------------------------------------------------------

interface TreeRendererProps {
  nodes: TreeNode[];
  depth: number;
  selectedPath: string | null;
  onFileClick: (path: string, isChanged: boolean) => void;
  styles: ReturnType<typeof useFileTreeStyles>;
  toggledFolders: Set<string>;
  toggleFolder: (path: string) => void;
  defaultChangedFlag?: boolean;
}

function renderTreeNodes({
  nodes,
  depth,
  selectedPath,
  onFileClick,
  styles,
  toggledFolders,
  toggleFolder,
  defaultChangedFlag = true,
}: TreeRendererProps): ReactNode[] {
  return nodes.map((node) => {
    if (node.isFolder) {
      const expanded = depth === 0 ? !toggledFolders.has(node.fullPath) : toggledFolders.has(node.fullPath);
      return (
        <div key={node.fullPath}>
          <div
            className={styles.treeRow}
            style={{ paddingLeft: `${depth * 16 + 8}px` }}
            onClick={() => toggleFolder(node.fullPath)}
            role="button"
            tabIndex={0}
            aria-expanded={expanded}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') toggleFolder(node.fullPath);
            }}
          >
            <span className={styles.chevronIcon}>
              {expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
            </span>
            <span className={styles.folderIcon}>
              {expanded ? <FolderOpenRegular /> : <FolderRegular />}
            </span>
            <Text className={styles.folderName}>{node.name}</Text>
          </div>
          {expanded && renderTreeNodes({
            nodes: node.children,
            depth: depth + 1,
            selectedPath,
            onFileClick,
            styles,
            toggledFolders,
            toggleFolder,
            defaultChangedFlag,
          })}
        </div>
      );
    }

    const isChanged = node.status != null || defaultChangedFlag;
    // Base glyph comes from the file extension; the status color class below is layered on
    // top for changed files so the review/diff tree keeps its added/modified/deleted coloring.
    const { Icon: FileExtIcon, kind: fileIconKind } = fileIconForName(node.name);
    const statusIconClass =
      node.status === 'added'
        ? styles.statusIconAdded
        : node.status === 'modified'
          ? styles.statusIconModified
          : node.status === 'deleted'
            ? styles.statusIconDeleted
            : styles.fileIcon;
    const isSelected = node.fullPath === selectedPath;

    return (
      <div
        key={node.fullPath}
        className={mergeClasses(styles.treeRow, isSelected ? styles.treeRowSelected : undefined)}
        style={{ paddingLeft: `${depth * 16 + 8}px` }}
        onClick={() => onFileClick(node.fullPath, isChanged)}
        role="button"
        tabIndex={0}
        title={node.fullPath}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') onFileClick(node.fullPath, isChanged);
        }}
      >
        <span className={statusIconClass} aria-label={node.status ?? undefined} data-file-icon={fileIconKind}>
          <FileExtIcon />
        </span>
        <Text
          className={mergeClasses(
            styles.fileName,
            node.status === 'added'
              ? styles.fileNameAdded
              : node.status === 'modified'
                ? styles.fileNameModified
                : node.status === 'deleted'
                  ? styles.fileNameDeleted
                  : undefined,
          )}
        >
          {node.name}
        </Text>
      </div>
    );
  });
}

// ---------------------------------------------------------------------------
// FlatChangesList
// ---------------------------------------------------------------------------

interface FlatChangesListProps {
  files: WorkspaceFileEntry[];
  selectedPath: string | null;
  onFileClick: (path: string, isChanged: boolean) => void;
  styles: ReturnType<typeof useFileTreeStyles>;
}

function renderFlatChangesList({
  files,
  selectedPath,
  onFileClick,
  styles,
}: FlatChangesListProps): ReactNode[] {
  return files.map((file) => {
    const name = filename(file.path);
    const FileIcon = getFileStatusIcon(file.status);
    const iconClass =
      file.status === 'added'
        ? styles.statusIconAdded
        : file.status === 'modified'
          ? styles.statusIconModified
          : styles.statusIconDeleted;
    const badgeColor: 'success' | 'warning' | 'danger' =
      file.status === 'added' ? 'success' : file.status === 'modified' ? 'warning' : 'danger';
    const statusLetter = file.status === 'added' ? 'A' : file.status === 'modified' ? 'M' : 'D';
    const isSelected = file.path === selectedPath;

    return (
      <div
        key={file.path}
        className={mergeClasses(styles.flatRow, isSelected ? styles.flatRowSelected : undefined)}
        onClick={() => onFileClick(file.path, true)}
        role="button"
        tabIndex={0}
        title={file.path}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') onFileClick(file.path, true);
        }}
      >
        <span className={iconClass} aria-label={file.status}>
          <FileIcon />
        </span>
        <Text className={styles.flatFileName}>{name}</Text>
        <Text className={styles.addedCount}>+{file.added_lines}</Text>
        <Text className={styles.removedCount}>-{file.removed_lines}</Text>
        <Badge color={badgeColor} size="small">{statusLetter}</Badge>
      </div>
    );
  });
}

// ---------------------------------------------------------------------------
// CompactChangesList — shared dense changed-files list (status icon + bold
// filename + right-aligned +N -M + status badge). Reused by the Changes tab of
// both FileTreePanel and AgentSessionPanel so there is a single source of truth
// for the "dense list" look. Clicking a row invokes onFileClick(path, true),
// which callers wire to open the shared diff/file viewer.
// ---------------------------------------------------------------------------

interface CompactChangesListProps {
  files: WorkspaceFileEntry[];
  selectedPath?: string | null;
  onFileClick: (path: string, isChanged: boolean) => void;
  /** Show the "Branch Changes" header with the aggregate +added/-removed totals. */
  showHeader?: boolean;
}

export function CompactChangesList({
  files,
  selectedPath = null,
  onFileClick,
  showHeader = true,
}: CompactChangesListProps) {
  const styles = useFileTreeStyles();
  const totalAdded = files.reduce((acc, f) => acc + (f.added_lines ?? 0), 0);
  const totalRemoved = files.reduce((acc, f) => acc + (f.removed_lines ?? 0), 0);

  return (
    <>
      {showHeader && (
        <div className={styles.changeHeader}>
          <Text className={styles.changeHeaderTitle}>Branch Changes</Text>
          <Text className={styles.addedCount}>+{totalAdded}</Text>
          <Text className={styles.removedCount}>-{totalRemoved}</Text>
        </div>
      )}
      {renderFlatChangesList({ files, selectedPath, onFileClick, styles })}
    </>
  );
}

// ---------------------------------------------------------------------------
// FilesTabPanel
// ---------------------------------------------------------------------------

interface FilesTabPanelProps {
  workspaceFiles: WorkspaceNode[];
  workspaceLoading: boolean;
  workspaceError: string | null;
  selectedPath: string | null;
  onFileClick: (path: string, isChanged: boolean) => void;
}

export function FilesTabPanel({
  workspaceFiles,
  workspaceLoading,
  workspaceError,
  selectedPath,
  onFileClick,
}: FilesTabPanelProps) {
  const styles = useFileTreeStyles();
  const [toggledFolders, setToggledFolders] = useState<Set<string>>(() => new Set<string>());

  const tree = useMemo(() => buildWorkspaceTree(workspaceFiles), [workspaceFiles]);

  const toggleFolder = (fullPath: string) => {
    setToggledFolders((prev) => {
      const next = new Set(prev);
      if (next.has(fullPath)) next.delete(fullPath);
      else next.add(fullPath);
      return next;
    });
  };

  if (workspaceLoading) {
    return (
      <div className={styles.spinnerWrapper}>
        <Spinner size="tiny" />
      </div>
    );
  }

  if (workspaceError) {
    return (
      <EmptyState title="Unable to load files" description={workspaceError} className={styles.emptyState} />
    );
  }

  if (workspaceFiles.length === 0) {
    return (
      <EmptyState title="No files" className={styles.emptyState} />
    );
  }

  return (
    <>
      {renderTreeNodes({
        nodes: tree,
        depth: 0,
        selectedPath,
        onFileClick,
        styles,
        toggledFolders,
        toggleFolder,
        defaultChangedFlag: false,
      })}
    </>
  );
}

// ---------------------------------------------------------------------------
// FileTreePanel
// ---------------------------------------------------------------------------

interface FileTreePanelProps {
  state: ArtifactBrowserState;
  onFileClick?: (path: string, isChanged?: boolean) => void;
  /** True when the run emitted run.no_changes_produced (zero committed changes at review/assembly). */
  noChangesProduced?: boolean;
  /** Optional ids/titles of subtasks that produced nothing, surfaced in the explanation. */
  noChangeSubtaskIds?: string[];
}

export function FileTreePanel({ state, onFileClick, noChangesProduced, noChangeSubtaskIds }: FileTreePanelProps) {
  const styles = useFileTreeStyles();
  const {
    runStatus,
    commitMessage,
    files,
    filesLoading,
    filesError,
    selectedPath,
    handleFileSelect,
    reviewPending,
    reviewResult,
    reviewError,
    submitReview,
    activeTab,
    setActiveTab,
    workspaceFiles,
    workspaceLoading,
    workspaceError,
    commitPending,
    commitResult,
    commitError,
    commitRun,
    requestChangesPending,
    requestChangesResult,
    requestChangesError,
    requestChanges,
    approveLabel,
    approveAriaLabel,
  } = state;

  const fileClickHandler = (path: string, isChanged = true) => {
    if (onFileClick) {
      onFileClick(path, isChanged);
    } else {
      handleFileSelect(path, isChanged);
    }
  };

  const [requestChangesOpen, setRequestChangesOpen] = useState(false);
  const [requestChangesComment, setRequestChangesComment] = useState('');

  const showReviewBar = runStatus === 'awaiting_review' && reviewResult === null && commitResult === null;
  // At a review/assembly gate an empty file list means the run reached review with zero committed
  // changes — surface a clear explanation rather than a bare "No changes" label. The explicit
  // run.no_changes_produced signal (when threaded in) forces the explanation regardless of status.
  const atReviewGate = runStatus === 'awaiting_review';
  const showNoChangesExplanation = files.length === 0 && (noChangesProduced === true || atReviewGate);
  const totalAdded = files.reduce((acc, f) => acc + (f.added_lines ?? 0), 0);
  const totalRemoved = files.reduce((acc, f) => acc + (f.removed_lines ?? 0), 0);
  const tabs = [
    { id: 'changes', label: 'Changes' },
    { id: 'files', label: 'Files' },
  ];

  return (
    <div className={styles.root}>
      {/* Tab list */}
      <div className={styles.tabListWrapper}>
        <TabList
          selectedValue={activeTab}
          onTabSelect={(_, data) => setActiveTab(data.value as 'changes' | 'files')}
          aria-label="Artifact browser tabs"
        >
          {tabs.map((t) => (
            <Tab key={t.id} value={t.id}>{t.label}</Tab>
          ))}
        </TabList>
      </div>

      {/* Review bar — visible on both tabs when awaiting review */}
      {showReviewBar && (
        <div className={styles.reviewBar}>
          {commitMessage && (
            <div className={styles.commitMessageBox}>
              <Text className={styles.commitMessageLabel}>Commit message</Text>
              <Text className={styles.commitMessageText}>{commitMessage}</Text>
            </div>
          )}
          {(commitPending || reviewPending || requestChangesPending) ? (
            <Spinner size="tiny" aria-label="Processing" />
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS }}>
              <Button
                appearance="primary"
                size="small"
                icon={<CheckmarkRegular />}
                aria-label={approveAriaLabel}
                style={{ width: '100%', whiteSpace: 'nowrap' }}
                disabled={commitPending || reviewPending || requestChangesPending}
                onClick={() => void commitRun()}
              >
                {approveLabel}
              </Button>
              <div className={styles.reviewBarSplitActions}>
                <Button
                  appearance="secondary"
                  size="small"
                  icon={<CommentRegular />}
                  aria-label="Request change"
                  disabled={commitPending || reviewPending || requestChangesPending}
                  onClick={() => {
                    setRequestChangesOpen((open) => !open);
                    setRequestChangesComment('');
                  }}
                >
                  Change
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  icon={<DismissRegular />}
                  aria-label="Decline run"
                  className={styles.reviewBarDecline}
                  disabled={commitPending || reviewPending || requestChangesPending}
                  onClick={() => void submitReview(false)}
                >
                  Decline
                </Button>
              </div>
            </div>
          )}
          {commitError && <Text className={styles.commitError}>{commitError}</Text>}
          {reviewError && <Text className={styles.reviewError}>{reviewError}</Text>}
          {requestChangesOpen && !requestChangesPending && (
            <div className={styles.requestChangesBox}>
              <Text className={styles.requestChangesLabel}>
                Describe what the agent should change
              </Text>
              <Textarea
                placeholder="Describe what the agent should change"
                value={requestChangesComment}
                onChange={(_, data) => setRequestChangesComment(data.value)}
                rows={3}
                resize="vertical"
                aria-label="Changes requested comment"
              />
              {requestChangesError && (
                <Text className={styles.requestChangesError}>{requestChangesError}</Text>
              )}
              <div className={styles.requestChangesActions}>
                <Button
                  appearance="primary"
                  size="small"
                  disabled={requestChangesComment.trim().length === 0}
                  aria-label="Send change request to agent"
                  onClick={() => {
                    void requestChanges(requestChangesComment.trim()).then(() => {
                      setRequestChangesOpen(false);
                      setRequestChangesComment('');
                    });
                  }}
                >
                  Send
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  aria-label="Cancel request changes"
                  onClick={() => {
                    setRequestChangesOpen(false);
                    setRequestChangesComment('');
                  }}
                >
                  Cancel
                </Button>
              </div>
            </div>
          )}
        </div>
      )}
      {commitResult !== null && (
        <div className={styles.reviewResultBar}>
          <Badge color={reviewResultBadgeColor(commitResult.status)}>{formatReviewResultStatus(commitResult.status)}</Badge>
        </div>
      )}
      {requestChangesResult !== null && (
        <div className={styles.reviewResultBar}>
          <Badge color="subtle">{formatReviewResultStatus(requestChangesResult.status)}</Badge>
        </div>
      )}
      {reviewResult !== null && (
        <div className={styles.reviewResultBar}>
          <Badge color={reviewResultBadgeColor(reviewResult.status)}>{formatReviewResultStatus(reviewResult.status)}</Badge>
        </div>
      )}

      {activeTab === 'changes' && (
        <div className={styles.fileList}>
          {filesLoading ? (
            <div className={styles.spinnerWrapper}>
              <Spinner size="tiny" />
            </div>
          ) : filesError ? (
            <EmptyState title="Unable to load changes" description={filesError} className={styles.emptyState} />
          ) : files.length === 0 ? (
            <div className={styles.emptyState} data-testid="changes-empty-state">
              <EmptyState
                title={showNoChangesExplanation ? 'This run produced no changes to review.' : 'No changes'}
                description={showNoChangesExplanation ? (
                  <>
                    The agents may have written output outside the repository, or there was nothing to change.
                    {noChangeSubtaskIds && noChangeSubtaskIds.length > 0 && (
                      <> Subtasks with no changes: {noChangeSubtaskIds.join(', ')}.</>
                    )}
                  </>
                ) : undefined}
              />
            </div>
          ) : (
            <>
              <div className={styles.changeHeader}>
                <Text className={styles.changeHeaderTitle}>Branch Changes</Text>
                <Text className={styles.addedCount}>+{totalAdded}</Text>
                <Text className={styles.removedCount}>-{totalRemoved}</Text>
              </div>
              {renderFlatChangesList({
                files,
                selectedPath,
                onFileClick: fileClickHandler,
                styles,
              })}
            </>
          )}
        </div>
      )}

      {activeTab === 'files' && (
        <div className={styles.fileList}>
          <FilesTabPanel
            workspaceFiles={workspaceFiles}
            workspaceLoading={workspaceLoading}
            workspaceError={workspaceError}
            selectedPath={selectedPath}
            onFileClick={fileClickHandler}
          />
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// DiffPanel
// ---------------------------------------------------------------------------

interface DiffPanelProps {
  state: ArtifactBrowserState;
}

export function DiffPanel({ state }: DiffPanelProps) {
  const styles = useDiffPanelStyles();
  const { selectedPath, diff, diffLoading, diffError } = state;

  return (
    <div className={styles.root}>
      <div className={styles.content}>
        {!selectedPath ? (
          <EmptyState
            title="Select a file"
            description="Choose a changed file to view its diff."
            className={styles.placeholder}
          />
        ) : diffLoading ? (
          <div className={styles.spinnerWrapper}>
            <Spinner size="tiny" />
          </div>
        ) : diffError ? (
          <EmptyState title="Unable to load diff" description={diffError ?? undefined} className={styles.binaryNotice} />
        ) : diff?.is_binary ? (
          <EmptyState title="Binary file — diff not available" className={styles.binaryNotice} />
        ) : (
          <DiffViewer diff={diff?.diff ?? null} filename={selectedPath ?? undefined} />
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// ArtifactBrowser — combined component kept for backward compatibility.
// Tests import this component and expect both panels rendered together.
// ---------------------------------------------------------------------------

interface ArtifactBrowserProps {
  runId: string;
  runStatus: string;
  onCommitSuccess?: () => void;
  /** True when the run emitted run.no_changes_produced (zero committed changes at review/assembly). */
  noChangesProduced?: boolean;
  /** Optional ids/titles of subtasks that produced nothing, surfaced in the explanation. */
  noChangeSubtaskIds?: string[];
}

export function ArtifactBrowser({ runId, runStatus, onCommitSuccess, noChangesProduced, noChangeSubtaskIds }: ArtifactBrowserProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus, undefined, onCommitSuccess);

  return (
    <div className={styles.root}>
      {state.isHistorical && (
        <MessageBar intent="info">
          <MessageBarBody>Showing the artifact state at run completion.</MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.panels}>
        <div className={styles.leftPanel}>
          <FileTreePanel state={state} noChangesProduced={noChangesProduced} noChangeSubtaskIds={noChangeSubtaskIds} />
        </div>
        <div className={styles.rightPanel}>
          <DiffPanel state={state} />
        </div>
      </div>
    </div>
  );
}
