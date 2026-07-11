import {
  apiClient } from '../api/apiClient';
import { ApiError,
  RetriableReviewError } from '../api/client';
import { AzureToolbar,
  BladeHeader,
  MessageBar,
  MessageBarBody,
  StatusIconText,
  Text,
  } from '../copilot-fluent-system';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import { useState } from 'react';
import type { ReviewResponse } from '../api/types';
const useStyles = makeStyles({
  root: {
    padding: tokens.spacingVerticalM,
  },
  meta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    minWidth: 0,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
  resultRow: {
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
        <div className={mergeClasses('azf-surface azf-stack azf-gap-m', styles.root)}>
          <BladeHeader size="compact" title="Merge failed" subtitle="The worktree has been preserved for manual resolution." />
          {result.merge_result && (
            <Text className={styles.mergeResult}>{result.merge_result}</Text>
          )}
        </div>
      );
    }
    return (
      <div className={mergeClasses('azf-surface azf-stack azf-gap-m', styles.root)}>
        <BladeHeader size="compact" title="Review submitted" />
        <div className={mergeClasses('azf-row azf-gap-s', styles.resultRow)}>
          <Text>Status:</Text>
          <StatusIconText status={result.status === 'merged' ? 'success' : result.status === 'declined' ? 'neutral' : 'danger'}>
            {result.status}
          </StatusIconText>
        </div>
        {result.merge_result && (
          <Text className={styles.mergeResult}>{result.merge_result}</Text>
        )}
      </div>
    );
  }

  return (
    <div className={mergeClasses('azf-surface azf-stack azf-gap-m', styles.root)}>
      <BladeHeader size="compact" title="Review required" subtitle="Review the diff above and approve or decline the merge." />
      {treeHash && <Text className={styles.meta}>Tree: {treeHash}</Text>}
      <StatusIconText status="warning">Waiting for review</StatusIconText>
      {retriableMessage && (
        <MessageBar intent="warning">
          <MessageBarBody>{retriableMessage}</MessageBarBody>
        </MessageBar>
      )}
      {error && <Text className={styles.error}>{error}</Text>}
      <AzureToolbar
        className={styles.actions}
        ariaLabel="Review actions"
        actions={[
          {
            id: 'approve',
            label: 'Approve',
            appearance: 'primary',
            disabled: pending,
            loading: pending,
            onClick: () => void submit(true),
          },
          {
            id: 'decline',
            label: 'Decline',
            appearance: 'secondary',
            disabled: pending,
            onClick: () => void submit(false),
          },
        ]}
      />
    </div>
  );
}
