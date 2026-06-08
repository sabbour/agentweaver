import { useEffect, useState } from 'react';
import {
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
import type { RunDetail as RunDetailModel } from '../api/types';

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
    </div>
  );
}
