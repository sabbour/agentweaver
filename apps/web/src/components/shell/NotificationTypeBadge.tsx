import { Text, makeStyles, tokens } from '@fluentui/react-components';
import {
  PersonQuestionMark24Regular,
  QuestionCircle24Regular,
  WrenchScrewdriver24Regular,
} from '@fluentui/react-icons';
import { memo, type ReactElement } from 'react';
import type { NotificationDto } from '../../api/types';

// #319 — dropdown entries all looked identical regardless of what action was requested.
// This maps the API's `type` field to a compact icon + label pill so a user scanning the
// list can tell Human Review from Tool Approval (and anything else, present or future)
// at a glance. Falls back to a generic badge for unrecognized/future type values so the
// UI never renders blank — see companion backend issue #321 which will start emitting
// `tool_approval` (not yet live at the time of this fix).
const TYPE_BADGES: Record<string, { icon: ReactElement; label: string }> = {
  human_review: { icon: <PersonQuestionMark24Regular />, label: 'Human Review' },
  tool_approval: { icon: <WrenchScrewdriver24Regular />, label: 'Tool Approval' },
};

const FALLBACK_BADGE = { icon: <QuestionCircle24Regular />, label: 'Action Needed' };

const useStyles = makeStyles({
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    padding: `2px ${tokens.spacingHorizontalXS}`,
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    width: 'fit-content',
  },
  icon: {
    fontSize: '14px',
    flexShrink: 0,
  },
});

interface NotificationTypeBadgeProps {
  type: NotificationDto['type'];
}

export const NotificationTypeBadge = memo(function NotificationTypeBadge({
  type,
}: NotificationTypeBadgeProps) {
  const styles = useStyles();
  const badge = TYPE_BADGES[type] ?? FALLBACK_BADGE;

  return (
    <span className={styles.pill} data-testid="notification-type-badge" data-notification-type={type}>
      <span className={styles.icon} aria-hidden="true">
        {badge.icon}
      </span>
      <Text size={200}>{badge.label}</Text>
    </span>
  );
});
