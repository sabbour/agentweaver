/**
 * Shared relative-time formatting for message/event timestamps.
 *
 * This consolidates a pattern that was previously duplicated inline in a few pages
 * (e.g. `HeartbeatPage.tsx`, `OverviewPage.tsx`) — new call sites should use this
 * instead of re-implementing the seconds/minutes/hours/days ladder.
 *
 * Accepts either an epoch-ms number or an ISO 8601 string so callers can pass
 * whichever they already have on hand.
 */
export function formatRelativeTime(value: number | string): string {
  const ms = typeof value === 'number' ? value : new Date(value).getTime();
  if (Number.isNaN(ms)) return typeof value === 'string' ? value : '';
  const diffMs = Date.now() - ms;
  const seconds = Math.floor(diffMs / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

/** Full absolute local time string, suitable for a tooltip/title on a relative-time label. */
export function formatAbsoluteTime(value: number | string): string {
  const ms = typeof value === 'number' ? value : new Date(value).getTime();
  if (Number.isNaN(ms)) return typeof value === 'string' ? value : '';
  return new Date(ms).toLocaleString();
}
