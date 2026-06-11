import { useEffect, useState } from 'react';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { ReviewResponse, WorkspaceFileDiff, WorkspaceFileEntry } from '../api/types';

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
  diff: WorkspaceFileDiff | null;
  diffLoading: boolean;
  diffError: string | null;
  handleFileSelect: (path: string) => void;
  reviewPending: boolean;
  reviewResult: ReviewResponse | null;
  reviewError: string | null;
  submitReview: (approved: boolean) => Promise<void>;
}

export function useArtifactBrowser(runId: string, runStatus: string): ArtifactBrowserState {
  const isHistorical = HISTORICAL_STATUSES.has(runStatus);
  const isLive = runStatus === 'in_progress';

  const [filter, setFilter] = useState<FilterValue>('all');
  const [files, setFiles] = useState<WorkspaceFileEntry[]>([]);
  const [filesLoading, setFilesLoading] = useState(true);
  const [filesError, setFilesError] = useState<string | null>(null);

  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [diff, setDiff] = useState<WorkspaceFileDiff | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);

  const [reviewPending, setReviewPending] = useState(false);
  const [reviewResult, setReviewResult] = useState<ReviewResponse | null>(null);
  const [reviewError, setReviewError] = useState<string | null>(null);

  const activeFilter = isHistorical ? 'all' : filter;

  // Clear all local state when runId changes so stale data from the previous run
  // is never visible while the new fetch is in flight.
  useEffect(() => {
    setFiles([]); // eslint-disable-line react-hooks/set-state-in-effect
    setSelectedPath(null);
    setDiff(null);
    setFilter('all');
    setReviewResult(null);
    setReviewError(null);
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
            // Clear selection if the selected file no longer appears in the new list.
            setSelectedPath((prev) =>
              prev && !data.some((f) => f.path === prev) ? null : prev,
            );
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

  // Fetch diff when selected file changes.
  // Loading state is reset in the file selection handler, not here.
  useEffect(() => {
    if (!selectedPath) return;

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
  }, [runId, selectedPath]);

  const handleFilterChange = (newFilter: FilterValue) => {
    if (isHistorical) return;
    setFilter(newFilter);
    setSelectedPath(null);
    setFilesLoading(true);
    setFilesError(null);
  };

  const handleFileSelect = (path: string) => {
    setSelectedPath(path);
    setDiffLoading(true);
    setDiffError(null);
    setDiff(null);
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
    diff,
    diffLoading,
    diffError,
    handleFileSelect,
    reviewPending,
    reviewResult,
    reviewError,
    submitReview,
  };
}
