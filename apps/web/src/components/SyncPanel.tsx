import {
  useEffect,
  useState } from 'react';
import type { SyncStatusDto } from '../api/types';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureDataGrid,
  AzureEmptyState,
  AzureToolbar,
  BladeHeader,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  StatusIconText,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
  type AzfColumn,
  type AzfTone,
} from '../copilot-fluent-system';
export interface SyncPanelProps {
  projectId: string;
}

const useStyles = makeStyles({
  root: {
    minWidth: 0,
  },
  changePath: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    flex: 1,
    wordBreak: 'break-all',
  },
  hash: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  commitResult: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
});

type SyncChange = SyncStatusDto['changes'][number];

function changeTone(kind: SyncChange['kind']): AzfTone {
  if (kind === 'added') return 'success';
  if (kind === 'removed') return 'danger';
  return 'warning';
}

export function SyncPanel({ projectId }: SyncPanelProps) {
  const styles = useStyles();
  const [status, setStatus] = useState<SyncStatusDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [committing, setCommitting] = useState(false);
  const [commitMessage, setCommitMessage] = useState('');
  const [commitResult, setCommitResult] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiClient.getSyncStatus(projectId)
      .then((s) => { if (!cancelled) setStatus(s); })
      .catch((err) => {
        if (!cancelled) setError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId]);

  const handleCommit = async () => {
    if (!status) return;
    setCommitting(true);
    setError(null);
    try {
      const result = await apiClient.commitSync(projectId, {
        expected_change_set_hash: status.change_set_hash,
        message: commitMessage.trim() || undefined,
      });
      setCommitResult(result.commit_id);
      const updated = await apiClient.getSyncStatus(projectId);
      setStatus(updated);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setCommitting(false);
    }
  };

  const changeColumns: AzfColumn<SyncChange>[] = [
    {
      columnId: 'kind',
      header: 'Change',
      width: '140px',
      sortable: true,
      sortValue: (change) => change.kind,
      renderCell: (change) => (
        <StatusIconText status={changeTone(change.kind)}>
          {change.kind}
        </StatusIconText>
      ),
    },
    {
      columnId: 'path',
      header: 'Path',
      sortable: true,
      sortValue: (change) => change.path,
      renderCell: (change) => <Text className={styles.changePath}>{change.path}</Text>,
    },
  ];

  if (loading) return <Spinner label="Loading sync status" />;

  return (
    <div className={mergeClasses('azf-surface azf-surface--panel azf-surface--padding-comfortable azf-stack azf-gap-m', styles.root)}>
      <BladeHeader
        size="compact"
        title="Sync"
        subtitle="Review local team-file changes and commit them with the current change-set hash."
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {status && status.nothing_to_sync && (
        <AzureEmptyState compact title="Nothing to sync." body="The team files are up to date." />
      )}

      {status && !status.nothing_to_sync && (
        <>
          <AzureDataGrid
            items={status.changes}
            columns={changeColumns}
            getRowId={(change) => change.path}
            ariaLabel="Sync changes"
            emptyState={<AzureEmptyState compact title="No file changes in this change set." />}
          />
          <StatusIconText status="neutral" className={styles.hash}>Hash: {status.change_set_hash}</StatusIconText>
          <Field label="Commit message (optional)">
            <Input
              value={commitMessage}
              onChange={(_, v) => setCommitMessage(v.value)}
              placeholder="Describe this sync..."
            />
          </Field>
          <AzureToolbar
            actions={[{
              id: 'commit-sync',
              label: committing ? 'Committing' : 'Commit',
              appearance: 'primary',
              disabled: committing,
              loading: committing,
              onClick: () => void handleCommit(),
            }]}
            ariaLabel="Sync actions"
          >
            {commitResult && (
              <StatusIconText status="success" className={styles.commitResult}>Committed: {commitResult}</StatusIconText>
            )}
          </AzureToolbar>
        </>
      )}
    </div>
  );
}
