import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { SyncStatusDto } from '../api/types';

export interface SyncPanelProps {
  projectId: string;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  changeList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  changeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusSmall,
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
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  commitResult: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
});

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

  if (loading) return <Spinner label="Loading sync status" />;

  return (
    <div className={styles.root}>
      <Title3>Sync</Title3>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {status && status.nothing_to_sync && (
        <Text>Nothing to sync. The team files are up to date.</Text>
      )}

      {status && !status.nothing_to_sync && (
        <>
          <div className={styles.changeList}>
            {status.changes.map((change) => (
              <div key={change.path} className={styles.changeRow}>
                <Badge
                  appearance="tint"
                  color={
                    change.kind === 'added' ? 'success' :
                    change.kind === 'removed' ? 'danger' : 'warning'
                  }
                >
                  {change.kind}
                </Badge>
                <Text className={styles.changePath}>{change.path}</Text>
              </div>
            ))}
          </div>
          <Text className={styles.hash}>Hash: {status.change_set_hash}</Text>
          <Field label="Commit message (optional)">
            <Input
              value={commitMessage}
              onChange={(_, v) => setCommitMessage(v.value)}
              placeholder="Describe this sync..."
            />
          </Field>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              disabled={committing}
              onClick={() => void handleCommit()}
            >
              {committing ? 'Committing' : 'Commit'}
            </Button>
            {committing && <Spinner size="extra-tiny" aria-hidden="true" />}
            {commitResult && (
              <Text className={styles.commitResult}>Committed: {commitResult}</Text>
            )}
          </div>
        </>
      )}
    </div>
  );
}
