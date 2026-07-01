import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Badge, Button, Caption1, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArchiveRegular, WarningRegular } from '@fluentui/react-icons';
import type { RunCardDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { AgentAvatar } from '../AgentAvatar';
import { CostChip } from '../CostChip';

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
    cursor: 'pointer',
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
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    flexShrink: 0,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
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
  onMutated?: () => void | Promise<void>;
}

// Read-only coordinator-run card. Not draggable — the coordinator owns workflow movement.
// Links to the coordinator topology/graph page (FR-016).
export function RunCard({ card, projectId, onMutated }: RunCardProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [retrying, setRetrying] = useState(false);
  const [archiving, setArchiving] = useState(false);
  const [error, setError] = useState<string | null>(null);

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

  const handleArchive = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (archiving) return;
    setArchiving(true);
    setError(null);
    try {
      await apiClient.archiveRun(card.run_id);
      await onMutated?.();
    } catch (err) {
      setError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
      setArchiving(false);
    }
  };

  const handleCardClick = () => {
    navigate(`/projects/${projectId}/orchestrations/${target}`);
  };

  const handleCardKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      navigate(`/projects/${projectId}/orchestrations/${target}`);
    }
  };

  return (
    <div
      className={styles.card}
      data-testid={`run-card-${card.run_id}`}
      role="link"
      tabIndex={0}
      onClick={handleCardClick}
      onKeyDown={handleCardKeyDown}
    >
      <div className={styles.header}>
        <Text className={styles.task}>{card.task || '(coordinator run)'}</Text>
        <div className={styles.headerActions}>
          {card.has_pending_approval && (
            <Badge appearance="tint" color="warning" icon={<WarningRegular />} size="small">
              Approval needed
            </Badge>
          )}
          <CostChip totalNanoAiu={card.total_nano_aiu} totalTokens={card.total_tokens} />
          <Badge appearance="tint" color={badgeColor(card.status)}>{card.status}</Badge>
          <Button
            appearance="subtle"
            size="small"
            icon={<ArchiveRegular />}
            aria-label="Archive run"
            disabled={archiving}
            onClick={handleArchive}
          />
        </div>
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
      {error && <Text className={styles.error}>{error}</Text>}
    </div>
  );
}
