import {
  makeStyles,
  Text,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
// Shared auto-refresh countdown. Pages that poll on an interval use this to show
// a live "Next refresh in Xs" indicator that counts DOWN to the next refresh,
// rather than a timestamp or a count-up "Updated Xs ago" stamp.

/** Inputs that determine the auto-refresh countdown and its status label. */
export interface UseRefreshCountdownOptions {
  intervalMs: number;
  // Timestamp of the last successful load (ms epoch or Date). When null/undefined
  // the countdown holds at the full interval until the first load lands.
  lastRefreshedAt: number | Date | null | undefined;
  // Auto-refresh is disabled (e.g. a toggle is off).
  paused?: boolean;
  // A refresh is currently in flight.
  refreshing?: boolean;
}

/** Remaining seconds and accessible label returned by the countdown hook. */
export interface RefreshCountdownState {
  secondsRemaining: number;
  label: string;
}

/** Converts a supported timestamp to epoch milliseconds or returns null if absent/invalid. */
function toMillis(value: number | Date | null | undefined): number | null {
  if (value == null) return null;
  const ms = value instanceof Date ? value.getTime() : value;
  return Number.isNaN(ms) ? null : ms;
}

/** Computes a live countdown from the last successful refresh and polling interval. */
export function useRefreshCountdown({
  intervalMs,
  lastRefreshedAt,
  paused = false,
  refreshing = false,
}: UseRefreshCountdownOptions): RefreshCountdownState {
  const totalSeconds = Math.max(0, Math.ceil(intervalMs / 1000));
  const [now, setNow] = useState(() => Date.now());

  // Re-render once per second so the countdown visibly counts down. Paused timers
  // do not need the tick.
  useEffect(() => {
    if (paused) return;
    const iv = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(iv);
  }, [paused]);

  const base = toMillis(lastRefreshedAt);
  const secondsRemaining =
    base == null
      ? totalSeconds
      : Math.min(totalSeconds, Math.max(0, Math.ceil((base + intervalMs - now) / 1000)));

  let label: string;
  if (refreshing) {
    label = 'Refreshing\u2026';
  } else if (paused) {
    label = 'Auto-refresh off';
  } else {
    label = `Next refresh in ${secondsRemaining}s`;
  }

  return { secondsRemaining, label };
}

const useStyles = makeStyles({
  countdown: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
});

/** Props for the shared auto-refresh countdown display component. */
export interface RefreshCountdownProps extends UseRefreshCountdownOptions {
  className?: string;
}

/** Renders the countdown label with shared subdued styling and live-region behavior. */
export function RefreshCountdown({ className, ...options }: RefreshCountdownProps) {
  const styles = useStyles();
  const { label } = useRefreshCountdown(options);
  return (
    <Text className={className ?? styles.countdown} aria-live="off">
      {label}
    </Text>
  );
}
