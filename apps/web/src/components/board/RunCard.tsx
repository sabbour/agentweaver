import {
  apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArchiveRegular, WarningRegular } from '@fluentui/react-icons';
import { AgentAvatar } from '../AgentAvatar';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import type { RunCardDto } from '../../api/types';
const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    borderTopWidth: '1px',
    borderRightWidth: '1px',
    borderBottomWidth: '1px',
    borderLeftWidth: '1px',
    borderTopStyle: 'solid',
    borderRightStyle: 'solid',
    borderBottomStyle: 'solid',
    borderLeftStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    transitionProperty: 'border-color, box-shadow, background-color',
    transitionDuration: tokens.durationFast,
    transitionTimingFunction: tokens.curveEasyEase,
    ':hover': {
      borderTopColor: tokens.colorNeutralStroke1,
      borderRightColor: tokens.colorNeutralStroke1,
      borderBottomColor: tokens.colorNeutralStroke1,
      borderLeftColor: tokens.colorNeutralStroke1,
      boxShadow: tokens.shadow4,
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0ms',
    },
  },
  header: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalXS,
  },
  task: {
    flex: '1 1 180px',
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    overflowWrap: 'anywhere',
    wordBreak: 'normal',
    minWidth: 0,
    // Task text is captured as a free-text prompt and can run to many paragraphs — clamp it to
    // a few lines so a long prompt can't blow up the card's height on the board. The full text
    // is still available via the native `title` tooltip on hover/focus.
    display: '-webkit-box',
    WebkitLineClamp: 3,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  meta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  progressLine: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  ownerLine: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    justifyContent: 'space-between',
  },
  agentChip: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  headerActions: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalXS,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});


function humanize(value: string | null | undefined): string {
  if (!value) return 'Unknown';
  return value
    .replace(/[_:-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/\b\w/g, (m) => m.toUpperCase());
}

function badgeColor(status: string): 'success' | 'danger' | 'warning' | 'subtle' {
  const s = status.toLowerCase();
  if (s.includes('merged') || s.includes('complete')) return 'success';
  if (s.includes('fail') || s.includes('declin') || s.includes('block')) return 'danger';
  if (s.includes('review') || s.includes('await')) return 'warning';
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
  const started = new Date(card.started_at).toLocaleString();

  const isRetryable = card.status === 'failed' || card.status === 'merge_failed';
  const retriedFromShort = card.retried_from ? card.retried_from.slice(0, 8) : null;

  const handleRetry = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (retrying) return;
    setRetrying(true);
    setError(null);
    try {
      const res = await apiClient.retryRun(card.run_id);
      navigate(`/projects/${projectId}/orchestrations/${res.run_id}`);
    } catch (err) {
      setError(err instanceof ApiError
        ? `Retry failed: API error ${err.status}: ${err.body}`
        : err instanceof Error
          ? `Retry failed: ${err.message}`
          : `Retry failed: ${String(err)}`);
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
        <Text className={styles.task} title={card.task || '(coordinator run)'} data-testid="run-card-task">{card.task || '(coordinator run)'}</Text>
        <div className={styles.headerActions}>
          {card.has_pending_approval && (
            <Badge appearance="tint" color="warning" icon={<WarningRegular />} size="small">
              Approval needed
            </Badge>
          )}
          <Badge appearance="tint" color={badgeColor(card.status)} size="small">{humanize(card.status)}</Badge>
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
      <div className={styles.progressLine}>
        <Caption1 className={styles.meta}>Progress: {humanize(stage)}</Caption1>
        <Caption1 className={styles.meta}>Started {started}</Caption1>
      </div>
      <div className={styles.ownerLine}>
        {card.agent_name ? (
          <div className={styles.agentChip} data-testid="run-card-agent">
            <AgentAvatar name={card.agent_name} size={16} />
            <Caption1 className={styles.meta}>Owner: {card.agent_name}</Caption1>
          </div>
        ) : (
          <Caption1 className={styles.meta}>Owner: Coordinator assigning agent</Caption1>
        )}
      </div>
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
