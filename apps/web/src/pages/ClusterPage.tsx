import { useCallback, useEffect, useState } from 'react';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  ClusterAgentPodDto,
  ClusterComponentHealthDto,
  ClusterDiagnosticsDto,
  ClusterPendingPodDto,
} from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';

// Cluster (spec-018) — Kubernetes cluster health and capacity view.
// Calls GET /api/diagnostics/cluster; shows a "Not available" placeholder until
// the backend endpoint is deployed (404 response).

const REFRESH_MS = 30_000;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  kpiRow: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  kpiCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  kpiLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  kpiValue: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: 1.1,
  },
  kpiSub: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  quotaRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  quotaBar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  quotaBarLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  quotaBarTrack: {
    height: '8px',
    backgroundColor: tokens.colorNeutralBackground4,
    borderRadius: '4px',
    overflow: 'hidden',
  },
  quotaBarFill: {
    height: '100%',
    borderRadius: '4px',
    backgroundColor: tokens.colorBrandForeground1,
    transition: 'width 0.3s ease',
  },
  quotaBarFillWarn: {
    backgroundColor: tokens.colorPaletteYellowForeground1,
  },
  quotaBarFillCrit: {
    backgroundColor: tokens.colorPaletteRedForeground1,
  },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  emptyState: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
    padding: `${tokens.spacingVerticalM} 0`,
  },
});

function relativeTime(iso: string): string {
  const diffMs = Date.now() - new Date(iso).getTime();
  const seconds = Math.floor(diffMs / 1000);
  if (Number.isNaN(seconds)) return iso;
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}

function componentBadgeColor(
  status: ClusterComponentHealthDto['status'],
): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'ok') return 'success';
  if (status === 'warning') return 'warning';
  if (status === 'error') return 'danger';
  if (status === 'missing') return 'subtle';
  return 'subtle';
}

function podBadgeColor(status: string): 'success' | 'warning' | 'danger' | 'informative' {
  if (status === 'Running') return 'success';
  if (status === 'Pending' || status === 'ContainerCreating') return 'warning';
  if (status === 'Failed' || status === 'CrashLoopBackOff') return 'danger';
  return 'informative';
}

function KpiCard({ label, value, sub }: { label: string; value: number | string; sub?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.kpiCard}>
      <Text className={styles.kpiLabel}>{label}</Text>
      <Text className={styles.kpiValue}>{value}</Text>
      {sub && <Text className={styles.kpiSub}>{sub}</Text>}
    </div>
  );
}

function QuotaBar({ label, used, limit, unit }: { label: string; used: number; limit: number; unit: string }) {
  const styles = useStyles();
  const pct = limit > 0 ? Math.min(100, (used / limit) * 100) : 0;
  const fillClass = pct >= 90 ? styles.quotaBarFillCrit : pct >= 75 ? styles.quotaBarFillWarn : styles.quotaBarFill;
  return (
    <div className={styles.quotaBar}>
      <div className={styles.quotaBarLabel}>
        <Text>{label}</Text>
        <Text>{used} / {limit} {unit}</Text>
      </div>
      <div className={styles.quotaBarTrack}>
        <div className={fillClass} style={{ width: `${pct}%` }} role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100} aria-label={`${label}: ${used} of ${limit} ${unit}`} />
      </div>
    </div>
  );
}

function ComponentHealthTable({ rows }: { rows: ClusterComponentHealthDto[] }) {
  if (rows.length === 0) return null;
  return (
    <Table aria-label="Component health" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Component</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Detail</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.component}>
            <TableCell>{r.component}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={componentBadgeColor(r.status)}>{r.status}</Badge>
            </TableCell>
            <TableCell>{r.detail}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function ActivePodsTable({ pods }: { pods: ClusterAgentPodDto[] }) {
  const styles = useStyles();
  if (pods.length === 0) {
    return <Text className={styles.emptyState}>No active agent pods.</Text>;
  }
  return (
    <Table aria-label="Active agent pods" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Pod</TableHeaderCell>
          <TableHeaderCell>Run ID</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {pods.map((p) => (
          <TableRow key={p.pod_name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.pod_name}</TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.run_id ?? '—'}</TableCell>
            <TableCell>{relativeTime(p.started_at)}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={podBadgeColor(p.status)}>{p.status}</Badge>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function PendingPodsTable({ pods }: { pods: ClusterPendingPodDto[] }) {
  const styles = useStyles();
  if (pods.length === 0) {
    return <Text className={styles.emptyState}>No pending agent pods.</Text>;
  }
  return (
    <Table aria-label="Pending agent pods" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Pod</TableHeaderCell>
          <TableHeaderCell>Run ID</TableHeaderCell>
          <TableHeaderCell>Reason</TableHeaderCell>
          <TableHeaderCell>Retries</TableHeaderCell>
          <TableHeaderCell>Pending since</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {pods.map((p) => (
          <TableRow key={p.pod_name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.pod_name}</TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.run_id ?? '—'}</TableCell>
            <TableCell>{p.reason}</TableCell>
            <TableCell>{p.retry_count}</TableCell>
            <TableCell>{relativeTime(p.pending_since)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

export function ClusterPage() {
  const styles = useStyles();
  const [data, setData] = useState<ClusterDiagnosticsDto | null>(null);
  const [notAvailable, setNotAvailable] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [lastRefreshedAt, setLastRefreshedAt] = useState<number | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    try {
      const dto = await apiClient.getClusterDiagnostics();
      if (!signal.cancelled) {
        if (dto === null) {
          setNotAvailable(true);
        } else {
          setData(dto);
          setNotAvailable(false);
        }
        setError(null);
        setLastRefreshedAt(Date.now());
      }
    } catch (err) {
      if (!signal.cancelled) setError(formatError(err));
    } finally {
      if (!signal.cancelled) setLoading(false);
    }
  }, []);

  useEffect(() => {
    const signal = { cancelled: false };
    setLoading(true);
    void load(signal);
    const iv = autoRefresh ? setInterval(() => { void load(signal); }, REFRESH_MS) : undefined;
    return () => {
      signal.cancelled = true;
      if (iv) clearInterval(iv);
    };
  }, [load, autoRefresh]);

  return (
    <div className={styles.root}>
      <PageHeader
        title="Cluster"
        subtitle="Kubernetes cluster health and capacity."
        actions={
          <>
            {autoRefresh && lastRefreshedAt != null && (
              <RefreshCountdown intervalMs={REFRESH_MS} lastRefreshedAt={lastRefreshedAt} refreshing={loading} />
            )}
            <Switch
              label="Auto-refresh"
              checked={autoRefresh}
              onChange={(_, d) => setAutoRefresh(d.checked)}
            />
            <Button
              appearance="secondary"
              icon={<ArrowClockwiseRegular />}
              onClick={() => { setLoading(true); void load({ cancelled: false }); }}
              disabled={loading}
            >
              Refresh
            </Button>
          </>
        }
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {notAvailable && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Cluster diagnostics are not available in this environment. The endpoint will be enabled once the backend is updated.
          </MessageBarBody>
        </MessageBar>
      )}

      {loading && !data && !notAvailable && <Spinner label="Loading cluster diagnostics" />}

      {data && (
        <>
          {/* KPI row */}
          <div className={styles.kpiRow}>
            <KpiCard label="Warm" value={data.warm_pool_ready} sub={`/ ${data.warm_pool_total} total`} />
            <KpiCard label="Active" value={data.active_agent_pods} />
            <KpiCard label="Pending" value={data.pending_agent_pods} />
            <KpiCard label="Claimed" value={data.claimed_agent_pods} />
          </div>

          {/* Quota */}
          {data.quota && (
            <div className={styles.section}>
              <Title3>Quota</Title3>
              <div className={styles.quotaRow}>
                <QuotaBar label="CPU" used={data.quota.cpu_used} limit={data.quota.cpu_limit} unit="cores" />
                <QuotaBar label="Memory" used={data.quota.memory_used_gi} limit={data.quota.memory_limit_gi} unit="Gi" />
              </div>
            </div>
          )}

          {/* Component health */}
          <div className={styles.section}>
            <Title3>Component health</Title3>
            <ComponentHealthTable rows={data.component_health} />
          </div>

          {/* Active agent pods */}
          <div className={styles.section}>
            <Title3>Active agent pods ({data.active_pods.length})</Title3>
            <ActivePodsTable pods={data.active_pods} />
          </div>

          {/* Pending agent pods */}
          {(data.pending_pods.length > 0 || data.pending_agent_pods > 0) && (
            <div className={styles.section}>
              <Title3>Pending agent pods ({data.pending_pods.length})</Title3>
              <PendingPodsTable pods={data.pending_pods} />
            </div>
          )}
        </>
      )}
    </div>
  );
}
