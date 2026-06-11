import { useMemo, useState } from 'react';
import {
  Badge,
  Button,
  MessageBar,
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
  DocumentAddRegular,
  DocumentDismissRegular,
  DocumentEditRegular,
  DocumentRegular,
  FolderOpenRegular,
  FolderRegular,
  type FluentIcon,
} from '@fluentui/react-icons';
import {
  useArtifactBrowser,
  type ArtifactBrowserState,
} from '../hooks/useArtifactBrowser';
import { DiffViewer } from './DiffViewer';
import type { WorkspaceFileEntry, WorkspaceNode } from '../api/types';

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

// ---------------------------------------------------------------------------
// Flat list helpers
// ---------------------------------------------------------------------------

function parentFolder(path: string): string {
  const parts = path.split('/');
  return parts.length > 1 ? parts.slice(0, -1).join('/') : '';
}

function filename(path: string): string {
  return path.split('/').pop() ?? path;
}

function reviewResultBadgeColor(
  status: string,
): 'success' | 'subtle' | 'danger' | 'warning' | 'informative' {
  if (status === 'merged') return 'success';
  if (status === 'declined') return 'subtle';
  if (status === 'merge_failed') return 'danger';
  if (status === 'merging') return 'informative';
  return 'danger';
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useFileTreeStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
  },
  tabListWrapper: {
    flexShrink: 0,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    padding: `0 ${tokens.spacingHorizontalXS}`,
  },
  commitBar: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    flexShrink: 0,
  },
  commitError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  reviewBar: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    flexShrink: 0,
  },
  reviewError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  reviewResultBar: {
    display: 'flex',
    flexDirection: 'row',
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
  },
  treeRow: {
    display: 'flex',
    flexDirection: 'row',
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
    color: tokens.colorPaletteDarkOrangeForeground1,
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
    fontSize: tokens.fontSizeBase200,
    flex: 1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fileNameAdded: {
    color: tokens.colorPaletteGreenForeground1,
  },
  fileNameModified: {
    color: tokens.colorPaletteDarkOrangeForeground1,
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
    flexDirection: 'row',
    alignItems: 'center',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  changeHeaderTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    flex: 1,
  },
  addedCount: {
    color: tokens.colorPaletteGreenForeground1,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
  },
  removedCount: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
  },
  flatRow: {
    display: 'flex',
    flexDirection: 'row',
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
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flexShrink: 0,
  },
  flatParentFolder: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
    flexShrink: 2,
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
    color: tokens.colorNeutralForeground3,
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
    color: tokens.colorNeutralForeground3,
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
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    height: '600px',
    overflow: 'hidden',
  },
  leftPanel: {
    width: '280px',
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  rightPanel: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
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
}: TreeRendererProps): React.ReactNode[] {
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

    const isChanged = node.status !== undefined || defaultChangedFlag;
    const FileStatusIcon = getFileStatusIcon(node.status);
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
        <span className={statusIconClass} aria-label={node.status ?? undefined}>
          <FileStatusIcon />
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
}: FlatChangesListProps): React.ReactNode[] {
  return files.map((file) => {
    const name = filename(file.path);
    const folder = parentFolder(file.path);
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
        {folder && <Text className={styles.flatParentFolder}>{folder}</Text>}
        <Text className={styles.addedCount}>+{file.added_lines}</Text>
        <Text className={styles.removedCount}>-{file.removed_lines}</Text>
        <Badge color={badgeColor} size="small">{statusLetter}</Badge>
      </div>
    );
  });
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
      <div className={styles.emptyState}>
        <Text>{workspaceError}</Text>
      </div>
    );
  }

  if (workspaceFiles.length === 0) {
    return (
      <div className={styles.emptyState}>
        <Text>No files</Text>
      </div>
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
}

export function FileTreePanel({ state, onFileClick }: FileTreePanelProps) {
  const styles = useFileTreeStyles();
  const {
    runStatus,
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
  } = state;

  const fileClickHandler = (path: string, isChanged = true) => {
    if (onFileClick) {
      onFileClick(path, isChanged);
    } else {
      handleFileSelect(path, isChanged);
    }
  };

  const showReviewBar = runStatus === 'awaiting_review' && reviewResult === null;
  const totalAdded = files.reduce((acc, f) => acc + (f.added_lines ?? 0), 0);
  const totalRemoved = files.reduce((acc, f) => acc + (f.removed_lines ?? 0), 0);

  return (
    <div className={styles.root}>
      {/* Tab list */}
      <div className={styles.tabListWrapper}>
        <TabList
          selectedValue={activeTab}
          onTabSelect={(_, data) => setActiveTab(data.value as 'changes' | 'files')}
          size="small"
        >
          <Tab value="changes">Changes</Tab>
          <Tab value="files">Files</Tab>
        </TabList>
      </div>

      {activeTab === 'changes' && (
        <>
          {/* Commit Changes button */}
          {runStatus === 'awaiting_review' && commitResult === null && (
            <div className={styles.commitBar}>
              {commitPending ? (
                <Spinner size="tiny" />
              ) : (
                <Button
                  appearance="primary"
                  size="small"
                  style={{ width: '100%' }}
                  disabled={commitPending}
                  onClick={() => void commitRun()}
                >
                  Commit Changes
                </Button>
              )}
              {commitError && <Text className={styles.commitError}>{commitError}</Text>}
            </div>
          )}
          {commitResult !== null && (
            <div className={styles.reviewResultBar}>
              <Badge color="success">{commitResult.status}</Badge>
            </div>
          )}

          {/* Approve/Decline bar */}
          {showReviewBar && (
            <div className={styles.reviewBar}>
              {reviewPending ? (
                <Spinner size="tiny" />
              ) : (
                <>
                  <Button
                    appearance="primary"
                    size="small"
                    disabled={reviewPending}
                    onClick={() => void submitReview(true)}
                  >
                    Approve
                  </Button>
                  <Button
                    appearance="secondary"
                    size="small"
                    disabled={reviewPending}
                    onClick={() => void submitReview(false)}
                  >
                    Decline
                  </Button>
                </>
              )}
              {reviewError && <Text className={styles.reviewError}>{reviewError}</Text>}
            </div>
          )}
          {reviewResult !== null && (
            <div className={styles.reviewResultBar}>
              <Badge color={reviewResultBadgeColor(reviewResult.status)}>{reviewResult.status}</Badge>
            </div>
          )}

          {/* Changes file list */}
          <div className={styles.fileList}>
            {filesLoading ? (
              <div className={styles.spinnerWrapper}>
                <Spinner size="tiny" />
              </div>
            ) : filesError ? (
              <div className={styles.emptyState}>
                <Text>{filesError}</Text>
              </div>
            ) : files.length === 0 ? (
              <div className={styles.emptyState}>
                <Text>No changes</Text>
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
        </>
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
          <div className={styles.placeholder}>
            <Text>Select a file to view its diff</Text>
          </div>
        ) : diffLoading ? (
          <div className={styles.spinnerWrapper}>
            <Spinner size="tiny" />
          </div>
        ) : diffError ? (
          <div className={styles.binaryNotice}>
            <Text>{diffError}</Text>
          </div>
        ) : diff?.is_binary ? (
          <div className={styles.binaryNotice}>
            <Text>Binary file — diff not available</Text>
          </div>
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
}

export function ArtifactBrowser({ runId, runStatus }: ArtifactBrowserProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus);

  return (
    <div className={styles.root}>
      {state.isHistorical && (
        <MessageBar intent="info">
          Showing the artifact state at run completion.
        </MessageBar>
      )}
      <div className={styles.panels}>
        <div className={styles.leftPanel}>
          <FileTreePanel state={state} />
        </div>
        <div className={styles.rightPanel}>
          <DiffPanel state={state} />
        </div>
      </div>
    </div>
  );
}
