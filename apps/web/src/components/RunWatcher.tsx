import { Badge, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useRunPoll } from '../api/sse';
import { API_KEY, API_URL } from '../config';

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
  result: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
});

interface RunWatcherProps {
  runId: string;
}

export function RunWatcher({ runId }: RunWatcherProps) {
  const styles = useStyles();
  const { run, status, error } = useRunPoll(runId, API_KEY, API_URL);

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text weight="semibold">Run {runId}</Text>
        {status === 'polling' && <Spinner size="tiny" label="Running" />}
        {run?.status === 'completed' && <Badge color="success">completed</Badge>}
        {run?.status === 'failed' && <Badge color="danger">failed</Badge>}
        {run?.status === 'in_progress' && <Badge color="informative">in progress</Badge>}
      </div>

      {error && (
        <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>
      )}

      {status === 'polling' && !run && (
        <Text>Waiting for agent to start...</Text>
      )}

      {run?.result && (
        <Text className={styles.result}>{run.result}</Text>
      )}
    </div>
  );
}
