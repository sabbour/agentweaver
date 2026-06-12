import { memo } from 'react';
import { Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { TurnGroup } from './TurnGroup';
import { LifecycleEventCard } from './LifecycleEventCard';
import type { TimelineItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  connecting: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingTop: tokens.spacingVerticalS,
  },
});

interface TimelineProps {
  items: TimelineItem[];
  streamStatus: StreamStatus;
  isLiveRun: boolean;
  runId?: string;
  runOutcome?: { achieved: boolean; reason: string };
}

export const Timeline = memo(function Timeline({ items, streamStatus, isLiveRun, runId, runOutcome }: TimelineProps) {
  const styles = useStyles();

  return (
    // role="log" announces new items; aria-live="polite" only when live (fix #6)
    <div
      className={styles.root}
      role="log"
      aria-label="Run timeline"
      aria-live={isLiveRun ? 'polite' : undefined}
    >
      {items.map((item, i) => {
        if (item.kind === 'turn-group') {
          return (
            <TurnGroup
              key={item.turnId != null ? String(item.turnId) : `turn-${i}`}
              item={item}
              isLiveRun={isLiveRun}
              streamStatus={streamStatus}
              runId={runId}
            />
          );
        }
        // lifecycle
        return (
          <LifecycleEventCard
            key={`lc-${item.event.sequence > 0 ? item.event.sequence : i}`}
            event={item.event}
            runId={runId}
            runOutcome={item.event.type === 'run.completed' ? runOutcome : undefined}
          />
        );
      })}

      {streamStatus === 'connecting' && (
        <div className={styles.connecting}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text>Waiting for agent...</Text>
        </div>
      )}
    </div>
  );
});
