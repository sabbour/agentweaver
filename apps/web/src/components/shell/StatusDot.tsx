import { useEffect, useRef, useState } from 'react';
import { Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import { apiClient } from '../../api/apiClient';
import { aggregateStatus } from '../../pages/DiagnosticsPage';

// Spec 011, FR-013 — top-bar health indicator. Polls the diagnostics endpoint and
// reflects the aggregate check health: green when all checks healthy, amber when
// any warning, red when any critical or when the API is unreachable.
type HealthState = 'unknown' | 'healthy' | 'warning' | 'critical' | 'unreachable';

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
  healthy: { backgroundColor: tokens.colorPaletteGreenBackground3 },
  warning: { backgroundColor: tokens.colorPaletteYellowBackground3 },
  critical: { backgroundColor: tokens.colorPaletteRedBackground3 },
  unreachable: { backgroundColor: tokens.colorPaletteRedBackground3 },
  // Legacy alias kept for test compatibility
  reachable: { backgroundColor: tokens.colorPaletteGreenBackground3 },
});

const LABELS: Record<HealthState, string> = {
  unknown: 'Checking API status',
  healthy: 'All checks healthy',
  warning: 'Some checks need attention',
  critical: 'Critical health issues detected',
  unreachable: 'API unreachable',
};

export function StatusDot() {
  const styles = useStyles();
  const [state, setState] = useState<HealthState>('unknown');
  const timerRef = useRef<ReturnType<typeof setInterval> | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;

    const probe = async () => {
      try {
        // Try detailed endpoint first (spec-018); fall back to basic diagnostics.
        const detailed = await apiClient.getDetailedDiagnostics();
        if (cancelled) return;
        if (detailed) {
          const agg = aggregateStatus(
            detailed.checks.map((c) => ({ name: c.name, status: c.status }))
          );
          setState(agg === 'healthy' ? 'healthy' : agg === 'warning' ? 'warning' : agg === 'critical' ? 'critical' : 'unknown');
          return;
        }
        const basic = await apiClient.getDiagnostics();
        if (cancelled) return;
        const mapped = basic.checks.map((c) => ({
          name: c.name,
          status: (c.status === 'pass' ? 'healthy' : c.status === 'warn' ? 'warning' : 'critical') as 'healthy' | 'warning' | 'critical' | 'unknown',
        }));
        const agg = aggregateStatus(mapped);
        setState(agg === 'healthy' ? 'healthy' : agg === 'warning' ? 'warning' : agg === 'critical' ? 'critical' : 'unknown');
      } catch {
        if (!cancelled) setState('unreachable');
      }
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
