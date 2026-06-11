import { useMemo, useState } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  Spinner,
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

function buildFileTree(
  entries: Array<{ path: string; status: 'added' | 'modified' | 'deleted' }>,
): TreeNode[] {
  const rootMap = new Map<string, TreeNodeInternal>();

  for (const entry of entries) {
    const segments = entry.path.split('/');
    let currentMap = rootMap;

    for (let i = 0; i < segments.length; i++) {
      const name = segments[i];
      const isLast = i === segments.length - 1;
      const pathSoFar = segments.slice(0, i + 1).join('/');

      if (!currentMap.has(name)) {
        currentMap.set(name, {
          name,
          fullPath: pathSoFar,
          isFolder: !isLast,
          status: isLast ? entry.status : undefined,
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
  tabListWrapper: {
    display: 'none',
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
    fontSize: '12px',
    color: tokens.colorPaletteGreenForeground1,
  },
  statusIconModified: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    fontSize: '12px',
    color: tokens.colorPaletteDarkOrangeForeground1,
  },
  statusIconDeleted: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    fontSize: '12px',
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
// FileTreePanel
// ---------------------------------------------------------------------------

interface FileTreePanelProps {
  state: ArtifactBrowserState;
}

export function FileTreePanel({ state }: FileTreePanelProps) {
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
  } = state;

  const tree = useMemo(() => buildFileTree(files), [files]);

  // Tracks folders toggled from their default state.
  // Top-level folders (depth 0) start expanded; nested folders start collapsed.
  // Each toggle flips the folder from its default.
  const [toggledFolders, setToggledFolders] = useState<Set<string>>(() => new Set<string>());

  const isFolderExpanded = (fullPath: string, depth: number): boolean =>
    depth === 0 ? !toggledFolders.has(fullPath) : toggledFolders.has(fullPath);

  const toggleFolder = (fullPath: string) => {
    setToggledFolders((prev) => {
      const next = new Set(prev);
      if (next.has(fullPath)) {
        next.delete(fullPath);
      } else {
        next.add(fullPath);
      }
      return next;
    });
  };

  const renderTree = (nodes: TreeNode[], depth: number) => {
    return nodes.map((node) => {
      if (node.isFolder) {
        const expanded = isFolderExpanded(node.fullPath, depth);
        return (
          <div key={node.fullPath}>
            <div
              className={styles.treeRow}
              style={{ paddingLeft: `${depth * 12 + 8}px` }}
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
            {expanded && renderTree(node.children, depth + 1)}
          </div>
        );
      }

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
          style={{ paddingLeft: `${depth * 12 + 8}px` }}
          onClick={() => handleFileSelect(node.fullPath)}
          role="button"
          tabIndex={0}
          title={node.fullPath}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ' ') handleFileSelect(node.fullPath);
          }}
        >
          <span className={statusIconClass} aria-label={node.status}>
            <FileStatusIcon />
          </span>
          <Text className={styles.fileName}>{node.name}</Text>
        </div>
      );
    });
  };

  const showReviewBar = runStatus === 'awaiting_review' && reviewResult === null;

  return (
    <div className={styles.root}>
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
          renderTree(tree, 0)
        )}
      </div>
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
