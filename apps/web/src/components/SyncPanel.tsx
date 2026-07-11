import {
  useEffect,
  useState } from 'react';
import type { SyncStatusDto } from '../api/types';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { EmptyState, Body, TitleText } from './ui';
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
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  headerBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
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

function changeBadgeColor(kind: SyncChange['kind']): 'success' | 'danger' | 'warning' {
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

  if (loading) return <Spinner label="Loading sync status" />;

  return (
    <div className={styles.root}>
      <div className={styles.headerBlock}>
        <TitleText>Sync</TitleText>
        <Body tone="muted">Review file changes and commit with the current change-set hash.</Body>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {status && status.nothing_to_sync && (
        <EmptyState title="Nothing to sync" description="The files are up to date." />
      )}

      {status && !status.nothing_to_sync && (
        <>
          <Table aria-label="Sync changes" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell style={{ width: '140px' }}>Change</TableHeaderCell>
                <TableHeaderCell>Path</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {status.changes.map((change) => (
                <TableRow key={change.path}>
                  <TableCell>
                    <Badge color={changeBadgeColor(change.kind)} size="small">
                      {change.kind}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <Text className={styles.changePath}>{change.path}</Text>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <Text className={styles.hash}>Hash: {status.change_set_hash}</Text>
          <Field label="Commit message (optional)">
            <Input
              value={commitMessage}
              onChange={(_, v) => setCommitMessage(v.value)}
              placeholder="Describe this sync…"
            />
          </Field>
          <div className={styles.toolbar} role="toolbar" aria-label="Sync actions">
            <Button
              appearance="primary"
              size="small"
              disabled={committing}
              onClick={() => void handleCommit()}
            >
              {committing ? 'Committing…' : 'Commit'}
            </Button>
            {commitResult && (
              <Text className={styles.commitResult}>Committed: {commitResult}</Text>
            )}
          </div>
        </>
      )}
    </div>
  );
}
