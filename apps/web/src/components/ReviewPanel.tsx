import {
  apiClient } from '../api/apiClient';
import { ApiError,
  RetriableReviewError } from '../api/client';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  DismissCircleRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useState } from 'react';
import type { ReviewResponse } from '../api/types';
const useStyles = makeStyles({
  root: {
    padding: tokens.spacingVerticalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  subtitle: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  meta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
  resultRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  statusIconSuccess: { color: tokens.colorPaletteGreenForeground1 },
  statusIconWarning: { color: tokens.colorPaletteMarigoldForeground2 },
  statusIconDanger: { color: tokens.colorPaletteRedForeground1 },
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
        <div className={styles.root}>
          <div className={styles.header}>
            <Text className={styles.title}>Merge failed</Text>
            <Text className={styles.subtitle}>The changes have been preserved for manual resolution.</Text>
          </div>
          {result.merge_result && (
            <Text className={styles.mergeResult}>{result.merge_result}</Text>
          )}
        </div>
      );
    }
    const statusIcon = result.status === 'merged'
      ? <CheckmarkCircleRegular className={styles.statusIconSuccess} aria-hidden="true" />
      : result.status === 'declined'
        ? <DismissCircleRegular aria-hidden="true" />
        : <DismissCircleRegular className={styles.statusIconDanger} aria-hidden="true" />;
    return (
      <div className={styles.root}>
        <div className={styles.header}>
          <Text className={styles.title}>Review submitted</Text>
        </div>
        <div className={styles.resultRow}>
          <Text>Status:</Text>
          <span className={styles.statusRow}>
            {statusIcon}
            <Text>{result.status}</Text>
          </span>
        </div>
        {result.merge_result && (
          <Text className={styles.mergeResult}>{result.merge_result}</Text>
        )}
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text className={styles.title}>Review required</Text>
        <Text className={styles.subtitle}>Review the diff above and approve or decline the merge.</Text>
      </div>
      {treeHash && <Text className={styles.meta}>Tree: {treeHash}</Text>}
      <span className={styles.statusRow}>
        <WarningRegular className={styles.statusIconWarning} aria-hidden="true" />
        <Text>Waiting for review</Text>
      </span>
      {retriableMessage && (
        <MessageBar intent="warning">
          <MessageBarBody>{retriableMessage}</MessageBarBody>
        </MessageBar>
      )}
      {error && <Text className={styles.error}>{error}</Text>}
      <div className={styles.actions} role="group" aria-label="Review actions">
        <Button
          appearance="primary"
          icon={pending ? <Spinner size="tiny" /> : undefined}
          disabled={pending}
          onClick={() => void submit(true)}
        >
          Approve
        </Button>
        <Button
          appearance="secondary"
          disabled={pending}
          onClick={() => void submit(false)}
        >
          Decline
        </Button>
      </div>
    </div>
  );
}
