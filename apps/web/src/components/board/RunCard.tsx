import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Badge, Button, Caption1, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { RunCardDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';
import { AgentAvatar } from '../AgentAvatar';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalXS,
  },
  task: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    wordBreak: 'break-word',
  },
  meta: {
    color: tokens.colorNeutralForeground3,
  },
  agentChip: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
});

function badgeColor(status: string): 'success' | 'danger' | 'warning' | 'informative' | 'subtle' {
  const s = status.toLowerCase();
  if (s.includes('merged') || s.includes('complete')) return 'success';
  if (s.includes('fail') || s.includes('declin') || s.includes('block')) return 'danger';
  if (s.includes('review') || s.includes('await')) return 'warning';
  if (s.includes('progress') || s.includes('dispatch') || s.includes('assembl')) return 'informative';
  return 'subtle';
}

export interface RunCardProps {
  card: RunCardDto;
  projectId: string;
}

// Read-only coordinator-run card. Not draggable — the coordinator owns workflow movement.
// Links to the coordinator topology/graph page (FR-016).
export function RunCard({ card, projectId }: RunCardProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [retrying, setRetrying] = useState(false);

  // Coordinator-run detail pages (CoordinatorRunPage -> /api/runs/{id}/...) are run_id-keyed for
  // EVERY coordinator run, so navigate by the canonical run_id. (workflow_run_id is null for both
  // interactive and backlog-pickup coordinator runs and must not be used as the detail key.)
  const target = card.run_id;
  const stage = card.assembly_stage ?? card.work_plan_status ?? card.status;

  const isRetryable = card.status === 'failed' || card.status === 'merge_failed';
  const retriedFromShort = card.retried_from ? card.retried_from.slice(0, 8) : null;

  const handleRetry = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (retrying) return;
    setRetrying(true);
    try {
      const res = await apiClient.retryRun(card.run_id);
      navigate(`/projects/${projectId}/orchestrations/${res.run_id}`);
    } finally {
      setRetrying(false);
    }
  };

  return (
    <Link to={`/projects/${projectId}/orchestrations/${target}`} className={styles.card} data-testid={`run-card-${card.run_id}`}>
      <div className={styles.header}>
        <Text className={styles.task}>{card.task || '(coordinator run)'}</Text>
        <Badge appearance="tint" color={badgeColor(card.status)}>{card.status}</Badge>
      </div>
      {stage && <Caption1 className={styles.meta}>{stage}</Caption1>}
      {card.agent_name ? (
        <div className={styles.agentChip} data-testid="run-card-agent">
          <AgentAvatar name={card.agent_name} size={16} />
          <Caption1 className={styles.meta}>{card.agent_name}</Caption1>
        </div>
      ) : (
        <Caption1 className={styles.meta}>Coordinator</Caption1>
      )}
      {retriedFromShort && (
        <Caption1 className={styles.meta}>
          Retried from{' '}
          <Link
            to={`/projects/${projectId}/orchestrations/${card.retried_from}`}
            onClick={(e) => e.stopPropagation()}
          >
            {retriedFromShort}
          </Link>
        </Caption1>
      )}
      {isRetryable && (
        <Button
          appearance="subtle"
          size="small"
          disabled={retrying}
          onClick={handleRetry}
          data-testid="run-card-retry"
        >
          Retry
        </Button>
      )}
    </Link>
  );
}
