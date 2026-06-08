import { Badge, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { API_KEY, API_URL } from '../config';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  header: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM },
  output: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    minHeight: '100px',
  },
});

interface RunWatcherProps { runId: string; }

export function RunWatcher({ runId }: RunWatcherProps) {
  const styles = useStyles();
  const { text, status, error } = useRunStream(runId, API_KEY, API_URL);

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text weight="semibold">Run {runId}</Text>
        {(status === 'connecting' || status === 'streaming') && (
          <Spinner size="tiny" label={status === 'connecting' ? 'Connecting' : 'Streaming'} />
        )}
        {status === 'done' && <Badge color="success">done</Badge>}
        {status === 'error' && <Badge color="danger">error</Badge>}
      </div>
      {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
      <Text className={styles.output}>{text || (status === 'connecting' ? 'Waiting for agent...' : '')}</Text>
    </div>
  );
}
