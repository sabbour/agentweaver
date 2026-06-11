import { useEffect, useState } from 'react';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { CommitResponse, ReviewResponse, WorkspaceFileDiff, WorkspaceFileEntry, WorkspaceNode } from '../api/types';

const POLL_INTERVAL_MS = 3000;

export const FILTERS = [
  { label: 'All', value: 'all' },
  { label: 'Committed', value: 'committed' },
  { label: 'Uncommitted', value: 'uncommitted' },
  { label: 'Last commit', value: 'last-commit' },
] as const;

export type FilterValue = (typeof FILTERS)[number]['value'];

export const HISTORICAL_STATUSES = new Set(['merged', 'declined', 'merge_failed', 'failed']);

function extractErrorMessage(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body}`;
  if (err instanceof Error) return err.message;
  return String(err);
}

export interface ArtifactBrowserState {
  runStatus: string;
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
}

export function useArtifactBrowser(runId: string, runStatus: string): ArtifactBrowserState {
  const isHistorical = HISTORICAL_STATUSES.has(runStatus);
  const isLive = runStatus === 'in_progress';

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

  const [activeTab, setActiveTab] = useState<'changes' | 'files'>('changes');
  const [workspaceFiles, setWorkspaceFiles] = useState<WorkspaceNode[]>([]);
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [workspaceError, setWorkspaceError] = useState<string | null>(null);

  const [commitPending, setCommitPending] = useState(false);
  const [commitResult, setCommitResult] = useState<CommitResponse | null>(null);
  const [commitError, setCommitError] = useState<string | null>(null);

  const activeFilter = isHistorical ? 'all' : filter;

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
  }, [runId]);

  // Fetch file list whenever filter or runId changes.
  // Loading/error state is reset in event handlers to avoid synchronous setState in effect body.
  useEffect(() => {
    let active = true;

    const doFetch = () => {
      apiClient
        .getRunFiles(runId, activeFilter)
        .then((data) => {
          if (active) {
            setFiles(data);
            setFilesError(null);
            setFilesLoading(false);
          }
        })
        .catch((err: unknown) => {
          if (active) {
            setFilesError(extractErrorMessage(err));
            setFilesLoading(false);
          }
        });
    };

    doFetch();

    if (!isLive) {
      return () => {
        active = false;
      };
    }

    const intervalId = setInterval(doFetch, POLL_INTERVAL_MS);
    return () => {
      active = false;
      clearInterval(intervalId);
    };
  }, [runId, activeFilter, isLive]);

  // Fetch workspace files when the Files tab is active.
  useEffect(() => {
    if (activeTab !== 'files') return;

    let active = true;

    apiClient
      .getRunWorkspace(runId)
      .then((data) => {
        if (active) {
          setWorkspaceFiles(data);
          setWorkspaceLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (active) {
          setWorkspaceError(extractErrorMessage(err));
          setWorkspaceLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [runId, activeTab]);

  // Fetch diff when selected file changes (only for changed files).
  // Loading state is reset in the file selection handler, not here.
  useEffect(() => {
    if (!selectedPath || !selectedPathIsChanged) return;

    let active = true;

    apiClient
      .getRunFileDiff(runId, selectedPath)
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
  }, [runId, selectedPath, selectedPathIsChanged]);

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
      const resp = await apiClient.submitReview(runId, approved);
      setReviewResult(resp);
    } catch (err) {
      if (err instanceof ApiError) {
        setReviewError(
          err.status === 403
            ? 'Not authorized to review this run.'
            : `Error ${err.status}: ${err.body}`,
        );
      } else {
        setReviewError(err instanceof Error ? err.message : String(err));
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
      const resp = await apiClient.commitRun(runId);
      setCommitResult(resp);
    } catch (err) {
      setCommitError(extractErrorMessage(err));
    } finally {
      setCommitPending(false);
    }
  };

  return {
    runStatus,
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
  };
}
