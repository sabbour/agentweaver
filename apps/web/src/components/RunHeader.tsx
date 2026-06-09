import { memo } from 'react';
import { Badge, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  errorText: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

interface RunHeaderProps {
  runId: string;
  streamStatus: StreamStatus;
  error?: string;
}

export const RunHeader = memo(function RunHeader({ runId, streamStatus, error }: RunHeaderProps) {
  const styles = useStyles();
  return (
    // role="status" + aria-live="polite" so status changes are announced (§6.1)
    <div className={styles.root} role="status" aria-live="polite">
      <Text weight="semibold">Run {runId.slice(0, 8)}</Text>
      {(streamStatus === 'connecting' || streamStatus === 'streaming') && (
        <Spinner
          size="tiny"
          label={streamStatus === 'connecting' ? 'Connecting' : 'Streaming'}
        />
      )}
      {streamStatus === 'done' && <Badge color="success">done</Badge>}
      {streamStatus === 'error' && <Badge color="danger">error</Badge>}
      {error && <Text className={styles.errorText}>{error}</Text>}
    </div>
  );
});
