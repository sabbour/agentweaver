import { memo } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { AgentMessageBubble } from './AgentMessageBubble';
import { ToolCallCard } from './ToolCallCard';
import type { TurnGroupItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
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

  if (item.steps.length === 0 && item.active === false) {
    return null;
  }

  return (
    <div className={styles.steps}>
      {item.steps.map((step, i) => {
        if (step.kind === 'agent-message') {
          return (
            <AgentMessageBubble
              key={step.messageId != null ? String(step.messageId) : "msg-" + i}
              content={step.content}
              streaming={step.streaming}
              isLiveRun={isLiveRun}
            />
          );
        }
        return (
          <ToolCallCard
            key={step.callId != null ? String(step.callId) : "call-" + i}
            item={step}
            streamStatus={streamStatus}
          />
        );
      })}
    </div>
  );
});
