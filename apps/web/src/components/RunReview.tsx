import { useEffect, useState } from 'react';
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
import { ApiError } from '../api/client';
import type { ReviewResponse, RunDetail } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  diff: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    maxHeight: '60vh',
    overflow: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    whiteSpace: 'pre',
  },
  line: {
    display: 'block',
  },
  add: {
    color: tokens.colorPaletteGreenForeground1,
  },
  remove: {
    color: tokens.colorPaletteRedForeground1,
  },
  meta: {
    color: tokens.colorPaletteBlueForeground2,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
  },
});

interface RunReviewProps {
  runId: string;
}

function DiffView({ diff }: { diff: string }) {
  const styles = useStyles();
  const lines = diff.replace(/\r\n/g, '\n').split('\n');
  return (
    <div className={styles.diff}>
      {lines.map((line, index) => {
        let cls: string | undefined;
        if (line.startsWith('+++') || line.startsWith('---')) {
          cls = undefined;
        } else if (line.startsWith('@@')) {
          cls = styles.meta;
        } else if (line.startsWith('+')) {
          cls = styles.add;
        } else if (line.startsWith('-')) {
          cls = styles.remove;
        }
        return (
          <span key={index} className={`${styles.line} ${cls ?? ''}`}>
            {line === '' ? ' ' : line}
          </span>
        );
      })}
    </div>
  );
}

export function RunReview({ runId }: RunReviewProps) {
  const styles = useStyles();
  const [run, setRun] = useState<RunDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<ReviewResponse | null>(null);

  useEffect(() => {
    let active = true;
    apiClient
      .getRun(runId)
      .then((value) => {
        if (active) {
          setRun(value);
          setError(null);
        }
      })
      .catch((err: unknown) => {
        if (active) {
          setError(
            err instanceof ApiError
              ? `API error ${err.status}: ${err.body}`
              : err instanceof Error
                ? err.message
                : String(err),
          );
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });
    return () => {
      active = false;
    };
  }, [runId]);

  const submitDecision = async (approved: boolean) => {
    setSubmitting(true);
    setError(null);
    try {
      const response = await apiClient.submitReview(runId, { approved });
      setResult(response);
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
      );
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return <Spinner label="Loading run" />;
  }

  return (
    <div className={styles.root}>
      <Text weight="semibold">Review run {runId}</Text>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {run && (
        <Text>
          Status: <Badge appearance="tint">{run.status}</Badge>
        </Text>
      )}

      {run && run.diff ? (
        <DiffView diff={run.diff} />
      ) : (
        <Text>No diff is available for this run.</Text>
      )}

      {result ? (
        <MessageBar intent="success">
          <MessageBarBody>
            Decision recorded. Status: {result.status}
            {result.merge_result ? `, merge result: ${result.merge_result}` : ''}
          </MessageBarBody>
        </MessageBar>
      ) : (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            disabled={submitting}
            onClick={() => submitDecision(true)}
          >
            Approve
          </Button>
          <Button disabled={submitting} onClick={() => submitDecision(false)}>
            Decline
          </Button>
        </div>
      )}
    </div>
  );
}
