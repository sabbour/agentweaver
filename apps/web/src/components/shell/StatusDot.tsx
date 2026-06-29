import { useEffect, useRef, useState } from 'react';
import { Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import { apiClient } from '../../api/apiClient';

// Spec 011, FR-013 — top-bar health indicator representing API reachability only
// (not coordinator heartbeat liveness). Polls a lightweight health check and
// shows green when the API responds, red when it does not, grey while unknown.
type HealthState = 'unknown' | 'reachable' | 'unreachable';

const POLL_INTERVAL_MS = 30_000;

const useStyles = makeStyles({
  dot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
    display: 'inline-block',
    flexShrink: 0,
  },
  unknown: { backgroundColor: tokens.colorNeutralForeground4 },
  reachable: { backgroundColor: tokens.colorPaletteGreenBackground3 },
  unreachable: { backgroundColor: tokens.colorPaletteRedBackground3 },
});

const LABELS: Record<HealthState, string> = {
  unknown: 'Checking API status',
  reachable: 'API reachable',
  unreachable: 'API unreachable',
};

export function StatusDot() {
  const styles = useStyles();
  const [state, setState] = useState<HealthState>('unknown');
  const timerRef = useRef<ReturnType<typeof setInterval> | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;

    const probe = async () => {
      const ok = await apiClient.checkHealth();
      if (!cancelled) setState(ok ? 'reachable' : 'unreachable');
    };

    void probe();
    timerRef.current = setInterval(() => void probe(), POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, []);

  return (
    <Tooltip content={LABELS[state]} relationship="label" positioning="below">
      <span
        role="status"
        aria-label={LABELS[state]}
        className={`${styles.dot} ${styles[state]}`}
      />
    </Tooltip>
  );
}
