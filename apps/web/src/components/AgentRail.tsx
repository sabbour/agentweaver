import { makeStyles, tokens, Badge, Text } from '@fluentui/react-components';
import { AgentAvatar } from './AgentAvatar';
import type { AgentQueueItem } from '../api/agentQueues';

// Re-export so consumers can use this one import for both type and component.
export type { AgentQueueItem } from '../api/agentQueues';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  title: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalXS}`,
    borderRadius: tokens.borderRadiusSmall,
    cursor: 'default',
    border: '1px solid transparent',
    minHeight: '32px',
  },
  rowClickable: {
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2Hover,
    },
  },
  rowSelected: {
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  agentName: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    flex: '0 0 auto',
    minWidth: '60px',
    maxWidth: '120px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  chips: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flex: 1,
    flexWrap: 'wrap',
  },
  doneText: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  emptyText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

export interface AgentRailProps {
  agents: AgentQueueItem[];
  selectedAgent?: string;
  onSelectAgent?: (name: string | null) => void;
  title?: string;
}

export function AgentRail({ agents, selectedAgent, onSelectAgent, title = 'Agents' }: AgentRailProps) {
  const styles = useStyles();
  const isSelectable = Boolean(onSelectAgent);

  return (
    <div className={styles.root} aria-label={title} data-testid="agent-rail">
      {title && <Text className={styles.title}>{title}</Text>}
      <div className={styles.list} role="list">
        {agents.length === 0 ? (
          <Text className={styles.emptyText} role="listitem">No active agents</Text>
        ) : (
          agents.map((agent) => {
            const isSelected = agent.agentName === selectedAgent;
            const handleClick = isSelectable
              ? () => onSelectAgent!(isSelected ? null : agent.agentName)
              : undefined;

            return (
              <div
                key={agent.agentName}
                className={`${styles.row}${isSelectable ? ' ' + styles.rowClickable : ''}${isSelected ? ' ' + styles.rowSelected : ''}`}
                role={isSelectable ? 'button' : 'listitem'}
                aria-pressed={isSelectable ? isSelected : undefined}
                tabIndex={isSelectable ? 0 : undefined}
                onClick={handleClick}
                onKeyDown={isSelectable ? (e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onSelectAgent!(isSelected ? null : agent.agentName);
                  }
                } : undefined}
                data-testid={`agent-rail-row-${agent.agentName}`}
              >
                <AgentAvatar name={agent.agentName} size={20} />
                <Text className={styles.agentName}>{agent.agentName}</Text>
                <div className={styles.chips}>
                  {agent.active > 0 && (
                    <Badge appearance="tint" color="informative" size="small">
                      {agent.active} active
                    </Badge>
                  )}
                  {agent.queued > 0 && (
                    <Badge appearance="tint" color="subtle" size="small">
                      {agent.queued} queued
                    </Badge>
                  )}
                  {agent.blocked > 0 && (
                    <Badge appearance="tint" color="danger" size="small">
                      {agent.blocked} blocked
                    </Badge>
                  )}
                  {agent.done > 0 && (
                    <Text className={styles.doneText}>{agent.done} done</Text>
                  )}
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
