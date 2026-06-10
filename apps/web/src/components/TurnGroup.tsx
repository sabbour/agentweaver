import { memo, useState } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDownRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { AgentMessageBubble } from './AgentMessageBubble';
import { ToolCallCard } from './ToolCallCard';
import type { TurnGroupItem, TurnStep } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
  steps: {
    paddingLeft: tokens.spacingHorizontalM,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  toolsHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    cursor: 'pointer',
    background: 'none',
    border: 'none',
    padding: '1px 0',
    textAlign: 'left',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    ':hover': { opacity: 0.75 },
  },
  chevron: {
    color: tokens.colorNeutralForeground4,
    fontSize: '10px',
    flexShrink: 0,
  },
  toolsList: {
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

// report_intent steps are shown inline as intent lines, not counted as "tools used"
function isToolStep(step: TurnStep): boolean {
  return step.kind === 'tool-call' && step.toolName !== 'report_intent';
}

export const TurnGroup = memo(function TurnGroup({ item, isLiveRun, streamStatus }: TurnGroupProps) {
  const styles = useStyles();

  // Collapse completed tool groups by default; active turns always expanded
  const [expanded, setExpanded] = useState(item.active);

  if (item.steps.length === 0 && item.active === false) {
    return null;
  }

  const toolSteps = item.steps.filter(isToolStep) as import('../timeline/types').ToolCallItem[];
  const otherSteps = item.steps.filter(s => !isToolStep(s));
  const isComplete = !item.active;
  const collapseTools = isComplete && toolSteps.length > 0;

  return (
    <div className={styles.steps}>
      {/* Non-tool steps (agent messages, report_intent) always shown */}
      {otherSteps.map((step, i) => {
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
        // report_intent tool steps
        return (
          <ToolCallCard
            key={step.callId != null ? String(step.callId) : "ri-" + i}
            item={step}
            streamStatus={streamStatus}
          />
        );
      })}

      {/* Tool steps: collapsible when turn is complete */}
      {collapseTools ? (
        <>
          <button
            className={styles.toolsHeader}
            onClick={() => setExpanded(e => !e)}
            aria-expanded={expanded}
          >
            {expanded
              ? <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
              : <ChevronRightRegular className={styles.chevron} aria-hidden="true" />}
            <Text size={100} style={{ color: 'inherit' }}>
              Used {toolSteps.length} tool{toolSteps.length === 1 ? '' : 's'}
            </Text>
          </button>
          {expanded && (
            <div className={styles.toolsList}>
              {toolSteps.map((step, i) => (
                <ToolCallCard
                  key={step.callId != null ? String(step.callId) : "tc-" + i}
                  item={step}
                  streamStatus={streamStatus}
                />
              ))}
            </div>
          )}
        </>
      ) : (
        // Active turn: show tools inline
        toolSteps.map((step, i) => (
          <ToolCallCard
            key={step.callId != null ? String(step.callId) : "tc-" + i}
            item={step}
            streamStatus={streamStatus}
          />
        ))
      )}
    </div>
  );
});