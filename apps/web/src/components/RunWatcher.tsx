import { useEffect, useMemo, useRef } from 'react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Button,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { relativeTime, summarizeEvent } from '../api/eventFormat';
import { API_KEY, API_URL } from '../config';
import type { RunEvent } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    maxHeight: '60vh',
    overflowY: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  row: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'baseline',
  },
  type: {
    fontFamily: tokens.fontFamilyMonospace,
    minWidth: '160px',
    color: tokens.colorBrandForeground1,
  },
  time: {
    minWidth: '80px',
    color: tokens.colorNeutralForeground3,
  },
  summary: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  reviewBar: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
});

interface RunWatcherProps {
  runId: string;
}

function terminalBadge(events: RunEvent[]) {
  const types = new Set(events.map((e) => e.type));
  if (types.has('run.failed')) {
    return <Badge color="danger">failed</Badge>;
  }
  if (types.has('run.bounded')) {
    return <Badge color="warning">bounded</Badge>;
  }
  if (types.has('merge.completed')) {
    return <Badge color="success">merged</Badge>;
  }
  if (types.has('merge.failed')) {
    return <Badge color="danger">merge failed</Badge>;
  }
  if (types.has('review.declined')) {
    return <Badge color="warning">declined</Badge>;
  }
  if (types.has('review.approved')) {
    return <Badge color="success">approved</Badge>;
  }
  if (types.has('run.completed') || types.has('review.requested')) {
    return <Badge color="brand">completed</Badge>;
  }
  return null;
}

export function RunWatcher({ runId }: RunWatcherProps) {
  const styles = useStyles();
  const { events, status, error } = useRunStream(runId, API_KEY, API_URL);
  const listRef = useRef<HTMLDivElement>(null);

  const awaitingReview = useMemo(() => {
    const types = new Set(events.map((e) => e.type));
    if (!types.has('review.requested')) {
      return false;
    }
    return !(
      types.has('review.approved') ||
      types.has('review.declined') ||
      types.has('merge.completed') ||
      types.has('merge.failed')
    );
  }, [events]);

  useEffect(() => {
    const node = listRef.current;
    if (node) {
      node.scrollTop = node.scrollHeight;
    }
  }, [events]);

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text weight="semibold">Run {runId}</Text>
        {status === 'connecting' && <Spinner size="tiny" label="Connecting" />}
        {status === 'streaming' && <Spinner size="tiny" label="Streaming" />}
        {terminalBadge(events)}
      </div>

      {error && (
        <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>
      )}

      {awaitingReview && (
        <div className={styles.reviewBar}>
          <Text>This run is awaiting your review.</Text>
          <Link to={`/review/${runId}`}>
            <Button appearance="primary">Review changes</Button>
          </Link>
        </div>
      )}

      <div className={styles.list} ref={listRef}>
        {events.length === 0 && status !== 'error' && (
          <Text>Waiting for events.</Text>
        )}
        {events.map((evt) => (
          <div className={styles.row} key={evt.sequence}>
            <Text className={styles.type}>{evt.type}</Text>
            <Text className={styles.time}>{relativeTime(evt.timestamp)}</Text>
            <Text className={styles.summary}>{summarizeEvent(evt)}</Text>
          </div>
        ))}
      </div>
    </div>
  );
}
