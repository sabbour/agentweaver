import { useEffect, useState } from 'react';
import {
  Badge,
  MessageBar,
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { WorkspaceFileDiff, WorkspaceFileEntry } from '../api/types';
import { DiffViewer } from './DiffViewer';

const POLL_INTERVAL_MS = 3000;

const FILTERS = [
  { label: 'All', value: 'all' },
  { label: 'Committed', value: 'committed' },
  { label: 'Uncommitted', value: 'uncommitted' },
  { label: 'Last commit', value: 'last-commit' },
] as const;

type FilterValue = (typeof FILTERS)[number]['value'];

const HISTORICAL_STATUSES = new Set(['merged', 'declined', 'merge_failed', 'failed']);

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
  tabListWrapper: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    flexShrink: 0,
  },
  fileList: {
    overflowY: 'auto',
    flex: 1,
  },
  fileEntry: {
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
  fileEntrySelected: {
    backgroundColor: tokens.colorNeutralBackground3,
  },
  filePath: {
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
  rightPanel: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  rightPanelContent: {
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
  binaryNotice: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
});

function statusBadgeColor(
  status: 'added' | 'modified' | 'deleted',
): 'success' | 'warning' | 'danger' {
  if (status === 'added') return 'success';
  if (status === 'modified') return 'warning';
  return 'danger';
}

function extractErrorMessage(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body}`;
  if (err instanceof Error) return err.message;
  return String(err);
}

interface ArtifactBrowserProps {
  runId: string;
  runStatus: string;
}

export function ArtifactBrowser({ runId, runStatus }: ArtifactBrowserProps) {
  const styles = useStyles();
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

  const activeFilter = isHistorical ? 'all' : filter;

  // Clear all local state when runId changes so stale data from the previous run
  // is never visible while the new fetch is in flight.
  useEffect(() => {
    setFiles([]); // eslint-disable-line react-hooks/set-state-in-effect
    setSelectedPath(null); // eslint-disable-line react-hooks/set-state-in-effect
    setDiff(null); // eslint-disable-line react-hooks/set-state-in-effect
    setFilter('all'); // eslint-disable-line react-hooks/set-state-in-effect
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

  return (
    <div className={styles.root}>
      {isHistorical && (
        <MessageBar intent="info">
          Showing the artifact state at run completion.
        </MessageBar>
      )}
      <div className={styles.panels}>
        {/* Left panel — file tree */}
        <div className={styles.leftPanel}>
          <div className={styles.tabListWrapper}>
            <TabList
              size="small"
              selectedValue={activeFilter}
              onTabSelect={(_e, data) => handleFilterChange(data.value as FilterValue)}
            >
              {FILTERS.map((f) => (
                <Tab key={f.value} value={f.value} disabled={isHistorical && f.value !== 'all'}>
                  {f.label}
                </Tab>
              ))}
            </TabList>
          </div>
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
              files.map((entry) => (
                <div
                  key={entry.path}
                  className={
                    entry.path === selectedPath
                      ? `${styles.fileEntry} ${styles.fileEntrySelected}`
                      : styles.fileEntry
                  }
                  onClick={() => handleFileSelect(entry.path)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      handleFileSelect(entry.path);
                    }
                  }}
                >
                  <Text className={styles.filePath} title={entry.path}>
                    {entry.path}
                  </Text>
                  <Badge color={statusBadgeColor(entry.status)} size="small">
                    {entry.status}
                  </Badge>
                </div>
              ))
            )}
          </div>
        </div>

        {/* Right panel — diff viewer */}
        <div className={styles.rightPanel}>
          <div className={styles.rightPanelContent}>
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
              <DiffViewer diff={diff?.diff ?? null} />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
