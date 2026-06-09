import { memo } from 'react';
import { Badge, Text, makeStyles, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleFilled,
  ErrorCircleFilled,
  WarningFilled,
  BranchRegular,
  DismissCircleFilled,
} from '@fluentui/react-icons';
import type { RunStreamEvent } from '../api/sse';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },
  successIcon: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 },
  errorIcon: { color: tokens.colorPaletteRedForeground1, flexShrink: 0 },
  warningIcon: { color: tokens.colorPaletteYellowForeground1, flexShrink: 0 },
  subtleIcon: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
  summary: {
    fontSize: tokens.fontSizeBase200,
    wordBreak: 'break-word',
    flexGrow: 1,
  },
  badge: { flexShrink: 0 },
});

type BadgeColor = 'success' | 'warning' | 'danger' | 'subtle' | 'informative';

function lifecycleProps(event: RunStreamEvent): {
  icon: React.ReactNode;
  label: string;
  summary: string;
  badgeColor: BadgeColor;
} {
  // SECURITY (Y-3): all string values extracted and rendered as React text — no HTML
  const p = event.payload;
  switch (event.type) {
    case 'run.completed':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'run.completed',
        summary: String(p['summary'] ?? 'Run completed'),
        badgeColor: 'success',
      };
    case 'run.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'run.failed',
        summary: String(p['message'] ?? p['summary'] ?? 'Run failed'),
        badgeColor: 'danger',
      };
    case 'review.requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review.requested',
        summary: `Tree ${String(p['tree_hash'] ?? '')}`,
        badgeColor: 'warning',
      };
    case 'review.approved':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'review.approved',
        summary: `Approved by ${String(p['approved_by'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'review.declined':
      return {
        icon: <DismissCircleFilled aria-hidden="true" />,
        label: 'review.declined',
        summary: `Declined by ${String(p['declined_by'] ?? '')}`,
        badgeColor: 'subtle',
      };
    case 'merge.completed':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'merge.completed',
        summary: `Merged at ${String(p['merged_commit_hash'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'merge.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'merge.failed',
        summary: String(p['reason'] ?? 'Merge failed'),
        badgeColor: 'danger',
      };
    default:
      return {
        icon: null,
        label: event.type,
        summary: JSON.stringify(p),
        badgeColor: 'subtle',
      };
  }
}

interface LifecycleEventCardProps {
  event: RunStreamEvent;
}

export const LifecycleEventCard = memo(function LifecycleEventCard({ event }: LifecycleEventCardProps) {
  const styles = useStyles();
  const { icon, label, summary, badgeColor } = lifecycleProps(event);

  const iconClass =
    badgeColor === 'success' ? styles.successIcon :
    badgeColor === 'danger' ? styles.errorIcon :
    badgeColor === 'warning' ? styles.warningIcon :
    styles.subtleIcon;

  return (
    <div className={styles.card}>
      <span className={iconClass}>{icon}</span>
      <Badge className={styles.badge} color={badgeColor} shape="rounded" size="small">
        {label}
      </Badge>
      {/* SECURITY (Y-3): summary rendered as escaped text — no HTML interpretation */}
      <Text className={styles.summary}>{summary}</Text>
    </div>
  );
});
