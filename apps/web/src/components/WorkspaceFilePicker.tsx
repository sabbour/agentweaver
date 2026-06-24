import { useEffect, useState } from 'react';
import {
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ChevronDownRegular,
  ChevronRightRegular,
  DocumentRegular,
  FolderRegular,
  FolderOpenRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { WorkspaceFileNode } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  tree: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    maxHeight: '320px',
    overflowY: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  nodeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `2px ${tokens.spacingHorizontalXS}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'pointer',
    userSelect: 'none',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  nodeRowSelected: {
    backgroundColor: tokens.colorBrandBackground2,
    ':hover': {
      backgroundColor: tokens.colorBrandBackground2Hover,
    },
  },
  nodeRowDirectory: {
    cursor: 'pointer',
  },
  expandIcon: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    flexShrink: 0,
  },
  nodeIcon: {
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  nodeName: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  selectedPath: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    fontFamily: tokens.fontFamilyMonospace,
    marginTop: tokens.spacingVerticalXXS,
  },
  loadingRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalS,
  },
  emptyMsg: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    padding: tokens.spacingVerticalS,
    fontStyle: 'italic',
  },
});

interface FileTreeNodeProps {
  node: WorkspaceFileNode;
  depth: number;
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

function FileTreeNode({ node, depth, selectedPath, onSelect }: FileTreeNodeProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const indent = depth * 16;

  if (node.isDirectory) {
    return (
      <>
        <div
          className={styles.nodeRow}
          style={{ paddingLeft: `${indent + 4}px` }}
          onClick={() => setExpanded((v) => !v)}
          role="button"
          aria-expanded={expanded}
          aria-label={node.name}
          tabIndex={0}
          onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setExpanded((v) => !v); }}
        >
          <span className={styles.expandIcon}>
            {expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
          </span>
          <span className={styles.nodeIcon}>
            {expanded ? <FolderOpenRegular /> : <FolderRegular />}
          </span>
          <Text className={styles.nodeName}>{node.name}</Text>
        </div>
        {expanded && node.children?.map((child) => (
          <FileTreeNode
            key={child.relativePath}
            node={child}
            depth={depth + 1}
            selectedPath={selectedPath}
            onSelect={onSelect}
          />
        ))}
      </>
    );
  }

  const isSelected = selectedPath === node.relativePath;
  return (
    <div
      className={`${styles.nodeRow} ${isSelected ? styles.nodeRowSelected : ''}`}
      style={{ paddingLeft: `${indent + 20}px` }}
      onClick={() => onSelect(node.relativePath)}
      role="option"
      aria-selected={isSelected}
      aria-label={node.name}
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') onSelect(node.relativePath); }}
    >
      <span className={styles.nodeIcon}><DocumentRegular /></span>
      <Text className={styles.nodeName}>{node.name}</Text>
    </div>
  );
}

export interface WorkspaceFilePickerProps {
  projectId: string;
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

export function WorkspaceFilePicker({ projectId, selectedPath, onSelect }: WorkspaceFilePickerProps) {
  const styles = useStyles();
  const [nodes, setNodes] = useState<WorkspaceFileNode[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    apiClient.getWorkspaceFiles(projectId)
      .then((files) => { if (!cancelled) { setNodes(files); setLoading(false); } })
      .catch((err) => {
        if (!cancelled) {
          setError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
          setLoading(false);
        }
      });
    return () => { cancelled = true; };
  }, [projectId]);

  return (
    <div className={styles.root}>
      {loading && (
        <div className={styles.loadingRow}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text>Loading workspace files...</Text>
        </div>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {!loading && !error && (
        <div className={styles.tree} role="listbox" aria-label="Workspace files">
          {nodes.length === 0 ? (
            <Text className={styles.emptyMsg}>No files found in workspace.</Text>
          ) : (
            nodes.map((node) => (
              <FileTreeNode
                key={node.relativePath}
                node={node}
                depth={0}
                selectedPath={selectedPath}
                onSelect={onSelect}
              />
            ))
          )}
        </div>
      )}
      {selectedPath && (
        <Text className={styles.selectedPath}>Selected: {selectedPath}</Text>
      )}
    </div>
  );
}

// Re-export for convenience in the workspace import dialog.
export type { WorkspaceFileNode };
