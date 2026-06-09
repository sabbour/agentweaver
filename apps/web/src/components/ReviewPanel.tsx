import { useState } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError, RetriableReviewError } from '../api/client';
import type { ReviewResponse } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  meta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
  resultRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  mergeResult: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
});

interface ReviewPanelProps {
  runId: string;
  treeHash?: string | null;
  onReviewComplete?: (response: ReviewResponse) => void;
}

function statusBadgeColor(
  status: string,
): 'success' | 'subtle' | 'danger' | 'warning' | 'informative' {
  if (status === 'merged') return 'success';
  if (status === 'declined') return 'subtle';
  return 'danger';
}

export function ReviewPanel({ runId, treeHash, onReviewComplete }: ReviewPanelProps) {
  const styles = useStyles();
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<ReviewResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [retriableMessage, setRetriableMessage] = useState<string | null>(null);

  const submit = async (approved: boolean) => {
    setPending(true);
    setError(null);
    setRetriableMessage(null);
    try {
      const resp = await apiClient.submitReview(runId, approved);
      setResult(resp);
      onReviewComplete?.(resp);
    } catch (err) {
      if (err instanceof RetriableReviewError) {
        setRetriableMessage(err.serverMessage);
      } else if (err instanceof ApiError) {
        if (err.status === 403) {
          setError('You are not authorized to review this run.');
        } else {
          setError(`Error ${err.status}: ${err.body}`);
        }
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setPending(false);
    }
  };

  if (result) {
    if (result.status === 'merge_failed') {
      return (
        <div className={styles.root}>
          <Text weight="semibold">Merge failed</Text>
          {result.merge_result && (
            <Text className={styles.mergeResult}>{result.merge_result}</Text>
          )}
          <Text>The worktree has been preserved for manual resolution.</Text>
        </div>
      );
    }
    return (
      <div className={styles.root}>
        <Text weight="semibold">Review submitted</Text>
        <div className={styles.resultRow}>
          <Text>Status:</Text>
          <Badge color={statusBadgeColor(result.status)}>{result.status}</Badge>
        </div>
        {result.merge_result && (
          <Text className={styles.mergeResult}>{result.merge_result}</Text>
        )}
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <Text weight="semibold">Review required</Text>
      {treeHash && <Text className={styles.meta}>Tree: {treeHash}</Text>}
      <Text>Review the diff above and approve or decline the merge.</Text>
      {retriableMessage && (
        <MessageBar intent="warning">
          <MessageBarBody>{retriableMessage}</MessageBarBody>
        </MessageBar>
      )}
      {error && <Text className={styles.error}>{error}</Text>}
      <div className={styles.actions}>
        <Button appearance="primary" disabled={pending} onClick={() => void submit(true)}>
          Approve
        </Button>
        <Button appearance="secondary" disabled={pending} onClick={() => void submit(false)}>
          Decline
        </Button>
        {pending && <Spinner size="tiny" />}
      </div>
    </div>
  );
}
