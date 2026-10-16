import { Badge } from '@fluentui/react-components';
import { costChipLabel } from './costChipFormat';

/**
 * Renders a compact cost badge from backend nano-AIU usage, falling back to token count.
 * It uses `costChipLabel` for shared formatting and returns nothing when both values are absent;
 * it has no API calls or local state.
 */
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
