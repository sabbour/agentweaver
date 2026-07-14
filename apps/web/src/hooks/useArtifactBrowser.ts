import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiErrorMessage } from '../api/errors';
import { isTerminalRunStatus, normalizeRunStatus } from '../utils/runStatus';
import { useEffect, useState } from 'react';
import type {
  CommitResponse,
  RequestChangesResponse,
  ReviewResponse,
  WorkspaceFileContent,
  WorkspaceFileDiff,
  WorkspaceFileEntry,
  WorkspaceNode,
} from '../api/types';
const POLL_INTERVAL_MS = 3000;

export const FILTERS = [
  { label: 'All', value: 'all' },
  { label: 'Committed', value: 'committed' },
  { label: 'Uncommitted', value: 'uncommitted' },
  { label: 'Last commit', value: 'last-commit' },
] as const;

export type FilterValue = (typeof FILTERS)[number]['value'];

/**
 * Optional adapter that redirects the artifact browser at a non-standard run's artifacts and review
 * gate. Used by the Coordinator session: it owns no worktree, so files/diff come from the integration
 * branch and the three review actions are delivered to the collective assembly gate instead of the
 * per-run review endpoints. When omitted the hook uses the standard per-run endpoints unchanged.
 */
export interface ArtifactBrowserAdapter {
  getFiles?: (runId: string, filter: string) => Promise<WorkspaceFileEntry[]>;
  getFileDiff?: (runId: string, path: string) => Promise<WorkspaceFileDiff>;
  getWorkspace?: (runId: string) => Promise<WorkspaceNode[]>;
  /** Per-file content for the Preview/source tab. Coordinator assembly reads it from the integration
   *  branch tip (no worktree); when omitted the modal uses the standard worktree-backed endpoint. */
  getContent?: (runId: string, path: string) => Promise<WorkspaceFileContent>;
  approve?: (runId: string) => Promise<void>;
  approveLabel?: string;
  approveAriaLabel?: string;
  approveAcceptedStatus?: string;
  requestChanges?: (runId: string, comment: string) => Promise<void>;
  decline?: (runId: string) => Promise<void>;
}

function extractErrorMessage(err: unknown): string {
  return formatApiErrorMessage(err);
}

export interface ArtifactBrowserState {
  runStatus: string;
  commitMessage: string | null;
  filter: FilterValue;
  activeFilter: FilterValue;
  isHistorical: boolean;
  handleFilterChange: (f: FilterValue) => void;
  files: WorkspaceFileEntry[];
  filesLoading: boolean;
  filesError: string | null;
  selectedPath: string | null;
  selectedPathIsChanged: boolean;
  diff: WorkspaceFileDiff | null;
  diffLoading: boolean;
  diffError: string | null;
  handleFileSelect: (path: string, isChanged?: boolean) => void;
  clearSelection: () => void;
  reviewPending: boolean;
  reviewResult: ReviewResponse | null;
  reviewError: string | null;
  submitReview: (approved: boolean) => Promise<void>;
  activeTab: 'changes' | 'files';
  setActiveTab: (tab: 'changes' | 'files') => void;
  workspaceFiles: WorkspaceNode[];
  workspaceLoading: boolean;
  workspaceError: string | null;
  commitPending: boolean;
  commitResult: CommitResponse | null;
  commitError: string | null;
  commitRun: () => Promise<void>;
  requestChangesPending: boolean;
  requestChangesResult: RequestChangesResponse | null;
  requestChangesError: string | null;
  requestChanges: (comment: string) => Promise<void>;
  approveLabel: string;
  approveAriaLabel: string;
}

export function useArtifactBrowser(
  runId: string,
  runStatus: string,
  onRequestChangesSuccess?: () => void,
  onCommitSuccess?: () => void,
  onSubmitReviewSuccess?: () => void,
  commitMessage?: string | null,
  adapter?: ArtifactBrowserAdapter,
  initialTab: 'changes' | 'files' = 'changes',
): ArtifactBrowserState {
  const normalizedRunStatus = normalizeRunStatus(runStatus);
  const isHistorical = isTerminalRunStatus(normalizedRunStatus);
  const isLive = normalizedRunStatus === 'in_progress';

  const [filter, setFilter] = useState<FilterValue>('all');
  const [files, setFiles] = useState<WorkspaceFileEntry[]>([]);
  const [filesLoading, setFilesLoading] = useState(true);
  const [filesError, setFilesError] = useState<string | null>(null);

  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [selectedPathIsChanged, setSelectedPathIsChanged] = useState(true);
  const [diff, setDiff] = useState<WorkspaceFileDiff | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);

  const [reviewPending, setReviewPending] = useState(false);
  const [reviewResult, setReviewResult] = useState<ReviewResponse | null>(null);
  const [reviewError, setReviewError] = useState<string | null>(null);

  const [activeTab, setActiveTab] = useState<'changes' | 'files'>(initialTab);
  const [workspaceFiles, setWorkspaceFiles] = useState<WorkspaceNode[]>([]);
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [workspaceError, setWorkspaceError] = useState<string | null>(null);

  const [commitPending, setCommitPending] = useState(false);
  const [commitResult, setCommitResult] = useState<CommitResponse | null>(null);
  const [commitError, setCommitError] = useState<string | null>(null);

  const [requestChangesPending, setRequestChangesPending] = useState(false);
  const [requestChangesResult, setRequestChangesResult] = useState<RequestChangesResponse | null>(null);
  const [requestChangesError, setRequestChangesError] = useState<string | null>(null);

  const activeFilter = isHistorical ? 'all' : filter;

  // When the run enters a new revision cycle the derived status becomes
  // in_progress again. Clear any stale requestChangesResult so it does not
  // suppress the review bar when the second review gate arrives.
  useEffect(() => {
    if (normalizedRunStatus === 'in_progress') {
      setRequestChangesResult(null); // eslint-disable-line react-hooks/set-state-in-effect
    }
  }, [normalizedRunStatus]);

  // Clear all local state when runId changes so stale data from the previous run
  // is never visible while the new fetch is in flight.
  useEffect(() => {
    setFiles([]); // eslint-disable-line react-hooks/set-state-in-effect
    setSelectedPath(null);
    setSelectedPathIsChanged(true);
    setDiff(null);
    setFilter('all');
    setReviewResult(null);
    setReviewError(null);
    setActiveTab('changes');
    setWorkspaceFiles([]);
    setWorkspaceError(null);
    setCommitResult(null);
    setCommitError(null);
    setRequestChangesResult(null);
    setRequestChangesError(null);
  }, [runId]);

  // Fetch file list whenever filter or runId changes.
  // Loading/error state is reset in event handlers to avoid synchronous setState in effect body.
  useEffect(() => {
    if (!runId) {
      setFilesLoading(false);
      return;
    }
    let active = true;
    // eslint-disable-next-line prefer-const
    let intervalId: ReturnType<typeof setInterval> | undefined;

    const doFetch = () => {
      (adapter?.getFiles ?? apiClient.getRunFiles.bind(apiClient))(runId, activeFilter)
        .then((data) => {
          if (active) {
            setFiles(data);
            setFilesError(null);
            setFilesLoading(false);
          }
        })
        .catch((err: unknown) => {
          if (active) {
            if (err instanceof ApiError && err.status === 409) {
              setFilesError('Workspace files unavailable for this run state.');
              setFilesLoading(false);
              // 409 is permanent — stop polling
              clearInterval(intervalId);
              active = false;
            } else {
              setFilesError(extractErrorMessage(err));
              setFilesLoading(false);
            }
          }
        });
    };

    doFetch();

    if (!isLive) {
      return () => {
        active = false;
      };
    }

    intervalId = setInterval(doFetch, POLL_INTERVAL_MS);
    return () => {
      active = false;
      clearInterval(intervalId);
    };
  }, [runId, activeFilter, isLive, adapter]);

  // Fetch workspace files when the Files tab is active, and keep polling while the run
  // is live (#280) — previously this was a one-time fetch on tab-open, so newly created
  // files never showed up until the tab was closed and reopened.
  useEffect(() => {
    if (activeTab !== 'files') return;
    if (!runId) {
      setWorkspaceFiles([]);
      setWorkspaceLoading(false);
      setWorkspaceError(null);
      return;
    }

    let active = true;
    let workspaceIntervalId: ReturnType<typeof setInterval> | undefined;

    const doFetch = () => {
      (adapter?.getWorkspace ?? apiClient.getRunWorkspace.bind(apiClient))(runId)
        .then((data) => {
          if (active) {
            setWorkspaceFiles(data);
            setWorkspaceError(null);
            setWorkspaceLoading(false);
          }
        })
        .catch((err: unknown) => {
          if (active) {
            setWorkspaceError(extractErrorMessage(err));
            setWorkspaceLoading(false);
          }
        });
    };

    doFetch();

    if (!isLive) {
      return () => {
        active = false;
      };
    }

    // eslint-disable-next-line prefer-const
    workspaceIntervalId = setInterval(doFetch, POLL_INTERVAL_MS);
    return () => {
      active = false;
      clearInterval(workspaceIntervalId);
    };
  }, [runId, activeTab, adapter, isLive]);

  // Fetch diff when selected file changes (only for changed files).
  // Loading state is reset in the file selection handler, not here.
  useEffect(() => {
    if (!runId || !selectedPath || !selectedPathIsChanged) return;

    let active = true;

    (adapter?.getFileDiff ?? apiClient.getRunFileDiff.bind(apiClient))(runId, selectedPath)
      .then((data) => {
        if (active) {
          setDiff(data);
          setDiffError(null);
          setDiffLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (active) {
          setDiffError(extractErrorMessage(err));
          setDiffLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [runId, selectedPath, selectedPathIsChanged, adapter]);

  const handleSetActiveTab = (tab: 'changes' | 'files') => {
    if (tab === 'files') {
      setWorkspaceLoading(true);
      setWorkspaceError(null);
      setWorkspaceFiles([]);
    }
    setActiveTab(tab);
  };

  const handleFilterChange = (newFilter: FilterValue) => {
    if (isHistorical) return;
    setFilter(newFilter);
    setSelectedPath(null);
    setFilesLoading(true);
    setFilesError(null);
  };

  const handleFileSelect = (path: string, isChanged = true) => {
    setSelectedPath(path);
    setSelectedPathIsChanged(isChanged);
    if (isChanged) {
      setDiffLoading(true);
      setDiffError(null);
      setDiff(null);
    } else {
      setDiff(null);
      setDiffLoading(false);
      setDiffError(null);
    }
  };

  const clearSelection = () => {
    setSelectedPath(null);
  };

  const submitReview = async (approved: boolean): Promise<void> => {
    if (runStatus !== 'awaiting_review') return;
    setReviewPending(true);
    setReviewError(null);
    try {
      if (adapter?.decline && !approved) {
        await adapter.decline(runId);
        setReviewResult({ run_id: runId, status: 'declined', merge_result: null });
      } else {
        const resp = await apiClient.submitReview(runId, approved);
        setReviewResult(resp);
      }
      onSubmitReviewSuccess?.();
    } catch (err) {
      if (err instanceof ApiError) {
        setReviewError(
          err.status === 403
            ? 'Not authorized to review this run.'
            : formatApiErrorMessage(err, 'Review failed.'),
        );
      } else {
        setReviewError(formatApiErrorMessage(err, 'Review failed.'));
      }
    } finally {
      setReviewPending(false);
    }
  };

  const commitRun = async (): Promise<void> => {
    if (runStatus !== 'awaiting_review') return;
    setCommitPending(true);
    setCommitError(null);
    try {
      if (adapter?.approve) {
        await adapter.approve(runId);
        setCommitResult({ run_id: runId, status: adapter.approveAcceptedStatus ?? 'review_accepted', merge_result: null, conflicting_files: null });
      } else {
        const resp = await apiClient.commitRun(runId);
        setCommitResult(resp);
      }
      onCommitSuccess?.();
    } catch (err) {
      setCommitError(formatApiErrorMessage(err, 'Approval failed.'));
    } finally {
      setCommitPending(false);
    }
  };

  const requestChanges = async (comment: string): Promise<void> => {
    if (runStatus !== 'awaiting_review') return;
    setRequestChangesPending(true);
    setRequestChangesError(null);
    try {
      if (adapter?.requestChanges) {
        await adapter.requestChanges(runId, comment);
        setRequestChangesResult({ run_id: runId, status: 'changes_requested' });
      } else {
        const resp = await apiClient.requestChanges(runId, comment);
        setRequestChangesResult(resp);
      }
      onRequestChangesSuccess?.();
    } catch (err) {
      if (err instanceof ApiError) {
        setRequestChangesError(
          err.status === 403
            ? 'Not authorized to request changes on this run.'
            : formatApiErrorMessage(err, 'Could not request changes.'),
        );
      } else {
        setRequestChangesError(formatApiErrorMessage(err, 'Could not request changes.'));
      }
    } finally {
      setRequestChangesPending(false);
    }
  };

  return {
    runStatus,
    commitMessage: commitMessage ?? null,
    filter,
    activeFilter,
    isHistorical,
    handleFilterChange,
    files,
    filesLoading,
    filesError,
    selectedPath,
    selectedPathIsChanged,
    diff,
    diffLoading,
    diffError,
    handleFileSelect,
    clearSelection,
    reviewPending,
    reviewResult,
    reviewError,
    submitReview,
    activeTab,
    setActiveTab: handleSetActiveTab,
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
    approveLabel: adapter?.approveLabel ?? 'Commit and Merge',
    approveAriaLabel: adapter?.approveAriaLabel ?? 'Commit and merge to originating branch',
  };
}
