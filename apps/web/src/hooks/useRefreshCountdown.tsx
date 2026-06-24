import { useEffect, useState } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';

// Shared auto-refresh countdown. Pages that poll on an interval use this to show
// a live "Next refresh in Xs" indicator that counts DOWN to the next refresh,
// rather than a timestamp or a count-up "Updated Xs ago" stamp.

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

export interface RefreshCountdownState {
  secondsRemaining: number;
  label: string;
}

function toMillis(value: number | Date | null | undefined): number | null {
  if (value == null) return null;
  const ms = value instanceof Date ? value.getTime() : value;
  return Number.isNaN(ms) ? null : ms;
}

export function useRefreshCountdown({
  intervalMs,
  lastRefreshedAt,
  paused = false,
  refreshing = false,
}: UseRefreshCountdownOptions): RefreshCountdownState {
  const totalSeconds = Math.max(0, Math.ceil(intervalMs / 1000));
  const [, setTick] = useState(0);

  // Re-render once per second so the countdown visibly counts down. Paused timers
  // do not need the tick.
  useEffect(() => {
    if (paused) return;
    const iv = setInterval(() => setTick((t) => t + 1), 1000);
    return () => clearInterval(iv);
  }, [paused]);

  const base = toMillis(lastRefreshedAt);
  const secondsRemaining =
    base == null
      ? totalSeconds
      : Math.min(totalSeconds, Math.max(0, Math.ceil((base + intervalMs - Date.now()) / 1000)));

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

export interface RefreshCountdownProps extends UseRefreshCountdownOptions {
  className?: string;
}

export function RefreshCountdown({ className, ...options }: RefreshCountdownProps) {
  const styles = useStyles();
  const { label } = useRefreshCountdown(options);
  return (
    <Text className={className ?? styles.countdown} aria-live="off">
      {label}
    </Text>
  );
}
