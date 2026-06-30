import { Badge } from '@fluentui/react-components';

export function formatAic(nanoAiu: number | null | undefined, digits = 2): string {
  if (nanoAiu == null || !Number.isFinite(nanoAiu)) return '0';
  const value = nanoAiu / 1_000_000_000;
  if (value >= 100) return value.toFixed(1);
  if (value >= 10) return value.toFixed(Math.min(digits, 2));
  if (value >= 1) return value.toFixed(digits);
  return value.toFixed(4);
}

function formatCompactNumber(value: number): string {
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)}K`;
  return value.toLocaleString();
}

export function costChipLabel(totalNanoAiu?: number | null, totalTokens?: number | null): string | null {
  if (totalNanoAiu != null && totalNanoAiu > 0) return `${formatAic(totalNanoAiu)} AIC`;
  if (totalTokens != null && totalTokens > 0) return `${formatCompactNumber(totalTokens)} tok`;
  return null;
}

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
    <Badge appearance="tint" color="informative" size="small" aria-label={ariaLabel ?? `Cost ${label}`}>
      {label}
    </Badge>
  );
}
