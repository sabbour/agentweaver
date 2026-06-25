import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Dropdown,
  Option,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { BranchRegular, TasksAppRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project, ProposedBacklogItem, WorkspaceNode, WorkspaceRef } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { FilesTabPanel } from '../components/ArtifactBrowser';
import { FileViewer } from '../components/FileViewer';
import { DecomposePreviewDialog } from '../components/DecomposePreviewDialog';

// Project-scoped, read-only Workspace browser (WORK section). Browses the project
// repo at its current branch and lets the user switch to active run worktrees or
// coordinator assembly branches
// branch. The file tree and syntax-highlighted viewer are reused from the run
// Files experience; there is no diff, commit, or review chrome here.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    height: '100%',
    minHeight: 0,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  toolbar: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  branchIndicator: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase300,
  },
  refDropdown: {
    minWidth: '280px',
  },
  optionRow: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  panels: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    flex: 1,
    minHeight: 0,
    overflow: 'hidden',
  },
  leftPanel: {
    width: '320px',
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
    minHeight: 0,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  rightPanelEmpty: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForeground3,
    padding: tokens.spacingHorizontalXXL,
    textAlign: 'center',
  },
  fileViewerWrapper: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minHeight: 0,
  },
  fileViewerToolbar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    flexShrink: 0,
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalXXL,
  },
});

// Short, human-readable status badge color for a worktree's owning run.
function runStatusColor(status: string | undefined): 'success' | 'danger' | 'warning' | 'informative' | 'subtle' {
  if (status === 'completed' || status === 'merged') return 'success';
  if (status === 'failed' || status === 'merge_failed') return 'danger';
  if (status === 'blocked' || status === 'parked') return 'warning';
  if (status === 'running' || status === 'dispatched') return 'informative';
  return 'subtle';
}

export function WorkspacePage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [searchParams] = useSearchParams();
  const requestedRef = searchParams.get('ref') ?? undefined;
  const requestedRun = searchParams.get('run') ?? undefined;

  const [refs, setRefs] = useState<WorkspaceRef[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [currentBranch, setCurrentBranch] = useState<string>('');
  const [selectedRef, setSelectedRef] = useState<string | undefined>(undefined);

  const [nodes, setNodes] = useState<WorkspaceNode[]>([]);
  const [nodesLoading, setNodesLoading] = useState(false);
  const [nodesError, setNodesError] = useState<string | null>(null);

  const [selectedPath, setSelectedPath] = useState<string | null>(null);

  const [decomposePreviewOpen, setDecomposePreviewOpen] = useState(false);
  const [decomposeItems, setDecomposeItems] = useState<ProposedBacklogItem[]>([]);
  const [decomposeWasCapped, setDecomposeWasCapped] = useState(false);
  const [decomposeTotal, setDecomposeTotal] = useState(0);
  const [decomposeLoading, setDecomposeLoading] = useState(false);
  const [decomposeError, setDecomposeError] = useState<string | null>(null);

  const handleImport = async () => {
    if (!selectedPath || !projectId) return;
    setDecomposeLoading(true);
    setDecomposeError(null);
    setDecomposeItems([]);
    setDecomposePreviewOpen(true);
    try {
      const result = await apiClient.decomposeSpec(projectId, selectedPath, false);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  const handleDecomposeConfirm = async () => {
    if (!selectedPath || !projectId) return;
    setDecomposeLoading(true);
    setDecomposeError(null);
    try {
      const result = await apiClient.decomposeSpec(projectId, selectedPath, true);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
      setDecomposePreviewOpen(false);
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  // Load the available refs (base branch + active run worktrees) for the project.
  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    setProject(null);
    apiClient
      .getProject(projectId)
      .then((p) => {
        if (!cancelled) setProject(p);
      })
      .catch(() => {});
    apiClient
      .getProjectWorkspaceRefs(projectId)
      .then((res) => {
        if (cancelled) return;
        setRefs(res.refs);
        setCurrentBranch(res.current_branch);
        const queryRef =
          (requestedRun ? res.refs.find((r) => r.run_id === requestedRun)?.branch : undefined) ??
          (requestedRef && res.refs.some((r) => r.branch === requestedRef) ? requestedRef : undefined);
        const base = res.refs.find((r) => r.kind === 'base')?.branch ?? res.current_branch;
        setSelectedRef(queryRef ?? base);
      })
      .catch(() => {
        if (!cancelled) {
          setRefs([]);
          setCurrentBranch('');
        }
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, requestedRef, requestedRun]);

  // Load the file tree for the selected ref. Switching refs clears any open file.
  useEffect(() => {
    if (!projectId || selectedRef === undefined) return;
    let cancelled = false;
    setNodesLoading(true);
    setNodesError(null);
    setSelectedPath(null);
    apiClient
      .getProjectWorkspace(projectId, selectedRef)
      .then((list) => {
        if (cancelled) return;
        setNodes(list);
        setNodesLoading(false);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setNodes([]);
        setNodesError(err instanceof Error ? err.message : String(err));
        setNodesLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, selectedRef]);

  const selectedRefObj = useMemo(
    () => refs.find((r) => r.branch === selectedRef) ?? null,
    [refs, selectedRef],
  );

  // Dropdown display text for the active selection.
  const dropdownValue = selectedRefObj
    ? selectedRefObj.label
    : selectedRef ?? '';

  const getContent = useMemo(
    () => (_id: string, path: string) =>
      apiClient.getProjectWorkspaceFileContent(projectId!, path, selectedRef),
    [projectId, selectedRef],
  );

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Workspace"
        subtitle="Browse the project repository and active run worktrees, read-only."
        breadcrumb={
          <nav className={styles.breadcrumb} aria-label="Breadcrumb">
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {project?.name ?? 'Project'}
            </Link>
            <span>/</span>
            <span>Workspace</span>
          </nav>
        }
        actions={
          <div className={styles.toolbar}>
            <span className={styles.branchIndicator} aria-label="Current branch">
              <BranchRegular />
              <Text className={styles.branchIndicator}>{currentBranch || '—'}</Text>
            </span>
            <Dropdown
              className={styles.refDropdown}
              aria-label="Branch or worktree"
              value={dropdownValue}
              selectedOptions={selectedRef ? [selectedRef] : []}
              onOptionSelect={(_, data) => {
                if (data.optionValue) setSelectedRef(data.optionValue);
              }}
            >
              {refs.map((r) => (
                <Option key={r.branch} value={r.branch} text={r.label}>
                  <span className={styles.optionRow}>
                    <Text>{r.label}</Text>
                    {r.kind !== 'base' && r.run_status && (
                      <Badge size="small" color={runStatusColor(r.run_status)} appearance="tint">
                        {r.run_status}
                      </Badge>
                    )}
                  </span>
                </Option>
              ))}
            </Dropdown>
          </div>
        }
      />

      <div className={styles.panels}>
        <div className={styles.leftPanel}>
          {nodesLoading ? (
            <div className={styles.spinnerWrapper}>
              <Spinner size="tiny" />
            </div>
          ) : (
            <FilesTabPanel
              workspaceFiles={nodes}
              workspaceLoading={false}
              workspaceError={nodesError}
              selectedPath={selectedPath}
              onFileClick={(path) => setSelectedPath(path)}
            />
          )}
        </div>
        <div className={styles.rightPanel}>
          {selectedPath !== null ? (
            <div className={styles.fileViewerWrapper}>
              {selectedPath.endsWith('.md') && (
                <div className={styles.fileViewerToolbar}>
                  <Button
                    appearance="primary"
                    size="small"
                    icon={<TasksAppRegular />}
                    onClick={() => void handleImport()}
                  >
                    Import to backlog
                  </Button>
                </div>
              )}
              <FileViewer
                runId={projectId}
                filePath={selectedPath}
                getContent={getContent}
              />
            </div>
          ) : (
            <div className={styles.rightPanelEmpty}>
              <Text>
                {nodes.length === 0 && !nodesLoading && !nodesError
                  ? 'No files in this branch yet.'
                  : 'Select a file to view its contents.'}
              </Text>
            </div>
          )}
        </div>
      </div>

      <DecomposePreviewDialog
        isOpen={decomposePreviewOpen}
        onClose={() => setDecomposePreviewOpen(false)}
        onConfirm={handleDecomposeConfirm}
        proposedItems={decomposeItems}
        wasCapped={decomposeWasCapped}
        totalFound={decomposeTotal}
        isLoading={decomposeLoading}
        error={decomposeError}
      />
    </div>
  );
}
