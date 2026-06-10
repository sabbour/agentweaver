import { memo } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { TurnDivider } from './TurnDivider';
import { AgentMessageBubble } from './AgentMessageBubble';
import { ToolCallCard } from './ToolCallCard';
import type { TurnGroupItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  steps: {
    paddingLeft: tokens.spacingHorizontalM,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
});

interface TurnGroupProps {
  item: TurnGroupItem;
  isLiveRun: boolean;
  streamStatus: StreamStatus;
}

export const TurnGroup = memo(function TurnGroup({ item, isLiveRun, streamStatus }: TurnGroupProps) {
  const styles = useStyles();

  // Suppress closed turns with no steps — a Foundry backend bug can produce them.
  // Active turns are excluded from suppression: a just-opened turn legitimately has
  // zero steps during live streaming before its first delta arrives.
  if (item.steps.length === 0 && item.active === false) {
    return null;
  }

  return (
    <div className={styles.root}>
      <TurnDivider
        turnIndex={item.turnIndex}
        stepCount={item.steps.length}
        active={item.active}
      />
      <div className={styles.steps}>
        {item.steps.map((step, i) => {
          if (step.kind === 'agent-message') {
            return (
              <AgentMessageBubble
                key={step.messageId != null ? String(step.messageId) : `msg-${i}`}
                content={step.content}
                streaming={step.streaming}
                isLiveRun={isLiveRun}
              />
            );
          }
          // tool-call
          return (
            <ToolCallCard
              key={step.callId != null ? String(step.callId) : `call-${i}`}
              item={step}
              streamStatus={streamStatus}
            />
          );
        })}
      </div>
    </div>
  );
});
