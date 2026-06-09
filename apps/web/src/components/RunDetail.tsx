import { useEffect, useState } from 'react';
import {
  Badge,
  Divider,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { ReviewResponse, RunDetail as RunDetailModel, RunStatus } from '../api/types';
import { DiffViewer } from './DiffViewer';
import { ReviewPanel } from './ReviewPanel';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '720px',
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    width: '160px',
  },
  reviewSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  mergeResult: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
});

interface RunDetailProps {
  runId: string;
}

export function RunDetail({ runId }: RunDetailProps) {
  const styles = useStyles();
  const [run, setRun] = useState<RunDetailModel | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

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

  const handleReviewComplete = (resp: ReviewResponse) => {
    setRun((prev) =>
      prev
        ? { ...prev, status: resp.status as RunStatus, result: resp.merge_result }
        : null,
    );
  };

  if (loading) {
    return <Spinner label="Loading run" />;
  }

  if (error) {
    return <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>;
  }

  if (!run) {
    return <Text>Run not found.</Text>;
  }

  const rows: Array<[string, string]> = [
    ['Run id', run.run_id],
    ['Status', run.status],
    ['Model source', run.model_source],
    ['Started', run.started_at],
    ['Ended', run.ended_at ?? '-'],
    ['Steps', run.step_count.toString()],
    ['Result', run.result ?? '-'],
  ];

  return (
    <div className={styles.root}>
      <Table aria-label="Run details">
        <TableBody>
          {rows.map(([label, value]) => (
            <TableRow key={label}>
              <TableCell className={styles.label}>{label}</TableCell>
              <TableCell>{value}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {run.status === 'awaiting_review' && (
        <div className={styles.reviewSection}>
          <Divider />
          <Badge color="warning">Awaiting review</Badge>
          <DiffViewer diff={run.diff} />
          <ReviewPanel
            runId={runId}
            treeHash={run.tree_hash}
            onReviewComplete={handleReviewComplete}
          />
        </div>
      )}
      {run.status === 'merge_failed' && (
        <div className={styles.reviewSection}>
          <Divider />
          <Badge color="danger">Merge failed</Badge>
          <DiffViewer diff={run.diff} />
          {run.result && <Text className={styles.mergeResult}>{run.result}</Text>}
          <Text>The worktree has been preserved for manual resolution.</Text>
        </div>
      )}
    </div>
  );
}
