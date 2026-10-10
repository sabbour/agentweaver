import { Badge } from '@fluentui/react-components';
import { costChipLabel } from './costChipFormat';

export function CostChip({
  totalNanoAiu,
  totalTokens,
  ariaLabel,
}: {
  totalNanoAiu?: number | null;
  totalTokens?: number | null;
  ariaLabel?: string;
}) {
  const label = costChipLabel(totalNanoAiu, totalTokens);
  if (!label) return null;
  return (
    <Badge appearance="tint" color="subtle" size="small" aria-label={ariaLabel ?? `Cost ${label}`}>
      {label}
    </Badge>
  );
}
