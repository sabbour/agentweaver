/** Formats backend AI-usage values for the shared cost badges and credit popovers. */

/** Converts backend nano-AIU to a compact AIC display value. */
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

/**
 * Chooses the visible cost label from nano-AIU usage, falling back to token count.
 * Returns `null` when neither value is positive so callers can hide an empty badge.
 */
export function costChipLabel(totalNanoAiu?: number | null, totalTokens?: number | null): string | null {
  if (totalNanoAiu != null && totalNanoAiu > 0) return `${formatAic(totalNanoAiu)} AIC`;
  if (totalTokens != null && totalTokens > 0) return `${formatCompactNumber(totalTokens)} tok`;
  return null;
}
