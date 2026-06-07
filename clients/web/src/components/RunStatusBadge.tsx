import { Badge } from '@fluentui/react-components';
import type { RunStatus } from '../api/client';

interface RunStatusBadgeProps {
  status: RunStatus | string;
}

/**
 * T062: RunStatusBadge — maps every RunStatus value to a distinct Fluent 2 badge.
 * No emojis (NFR-002). Text labels only.
 */
export function RunStatusBadge({ status }: RunStatusBadgeProps) {
  const { color, label } = getStatusAppearance(status);
  return (
    <Badge color={color} appearance="filled" size="large">
      {label}
    </Badge>
  );
}

function getStatusAppearance(status: RunStatus | string): {
  color: 'brand' | 'success' | 'danger' | 'warning' | 'informative' | 'important' | 'severe';
  label: string;
} {
  switch (status) {
    case 'Queued':
      return { color: 'informative', label: 'Queued' };
    case 'Running':
      return { color: 'brand', label: 'Running' };
    case 'Completed':
      return { color: 'success', label: 'Completed' };
    case 'Failed':
      return { color: 'danger', label: 'Failed' };
    case 'Bounded':
      return { color: 'warning', label: 'Bounded' };
    case 'AwaitingReview':
      return { color: 'important', label: 'Awaiting Review' };
    case 'Approved':
      return { color: 'success', label: 'Approved' };
    case 'Declined':
      return { color: 'severe', label: 'Declined' };
    case 'Merged':
      return { color: 'success', label: 'Merged' };
    case 'MergeConflict':
      return { color: 'danger', label: 'Merge Conflict' };
    default:
      return { color: 'informative', label: status };
  }
}
