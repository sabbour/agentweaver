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
  AgentPodInfoDto,
  ClusterDiagnosticsDto,
  DetailedHealthCheckDto,
  PendingCapacityRunDto,
  WarmPoolStatusDto,
  SandboxObjectDto,
  SandboxClaimObjectDto,
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
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  emptyState: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
    padding: `${tokens.spacingVerticalM} 0`,
  },
});

function formatAge(ageSeconds: number | null | undefined): string {
  if (ageSeconds == null) return '—';
  if (ageSeconds < 60) return `${Math.floor(ageSeconds)}s`;
  if (ageSeconds < 3600) return `${Math.floor(ageSeconds / 60)}m`;
  return `${Math.floor(ageSeconds / 3600)}h`;
}

function healthBadgeColor(
  status: string,
): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'healthy') return 'success';
  if (status === 'warning') return 'warning';
  if (status === 'degraded' || status === 'critical') return 'danger';
  return 'subtle';
}

function podBadgeColor(status: string): 'success' | 'warning' | 'informative' {
  if (status === 'ready') return 'success';
  if (status === 'pending') return 'warning';
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

function HealthChecksTable({ rows }: { rows: DetailedHealthCheckDto[] }) {
  const styles = useStyles();
  if (rows.length === 0) return <Text className={styles.emptyState}>No health checks.</Text>;
  return (
    <Table aria-label="Health checks" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Name</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Message</TableHeaderCell>
          <TableHeaderCell>Latency (ms)</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.name}>
            <TableCell>{r.name}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={healthBadgeColor(r.status)}>{r.status}</Badge>
            </TableCell>
            <TableCell>{r.message}</TableCell>
            <TableCell>{r.latencyMs}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function AgentPodsTable({ pods, label }: { pods: AgentPodInfoDto[]; label: string }) {
  const styles = useStyles();
  if (pods.length === 0) return <Text className={styles.emptyState}>No {label.toLowerCase()}.</Text>;
  return (
    <Table aria-label={label} size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Claim</TableHeaderCell>
          <TableHeaderCell>Pod name</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {pods.map((p) => (
          <TableRow key={p.claim_name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.claim_name}</TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.pod_name ?? '—'}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={podBadgeColor(p.status)}>{p.status}</Badge>
            </TableCell>
            <TableCell>{formatAge(p.age_seconds)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function PendingCapacityTable({ rows }: { rows: PendingCapacityRunDto[] }) {
  const styles = useStyles();
  if (rows.length === 0) return <Text className={styles.emptyState}>No pending capacity runs.</Text>;
  return (
    <Table aria-label="Pending capacity runs" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Subtask ID</TableHeaderCell>
          <TableHeaderCell>Work plan</TableHeaderCell>
          <TableHeaderCell>Child run</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Reason</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.subtask_id}>
            <TableCell>{r.subtask_id}</TableCell>
            <TableCell>{r.work_plan_id}</TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{r.child_run_id ?? '—'}</TableCell>
            <TableCell>{r.status}</TableCell>
            <TableCell>{r.reason ?? '—'}</TableCell>
            <TableCell>{formatAge(r.age_seconds)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function WarmPoolsTable({ rows }: { rows: WarmPoolStatusDto[] }) {
  const styles = useStyles();
  if (rows.length === 0) return <Text className={styles.emptyState}>No SandboxWarmPool objects found.</Text>;
  return (
    <Table aria-label="Warm pools" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Name</TableHeaderCell>
          <TableHeaderCell>Status</TableHeaderCell>
          <TableHeaderCell>Replicas (ready/desired)</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((p) => (
          <TableRow key={p.name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{p.name}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={healthBadgeColor(p.status)}>{p.status}</Badge>
            </TableCell>
            <TableCell>{p.ready_replicas} / {p.desired_replicas}</TableCell>
            <TableCell>{formatAge(p.age_seconds)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function SandboxObjectsTable({ rows }: { rows: SandboxObjectDto[] }) {
  const styles = useStyles();
  if (rows.length === 0) return <Text className={styles.emptyState}>No Sandbox objects found.</Text>;
  return (
    <Table aria-label="Sandbox objects" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Name</TableHeaderCell>
          <TableHeaderCell>Phase</TableHeaderCell>
          <TableHeaderCell>Ready</TableHeaderCell>
          <TableHeaderCell>Warm pool</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((s) => (
          <TableRow key={s.name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{s.name}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={s.phase === 'running' ? 'success' : s.phase === 'pending' ? 'warning' : 'subtle'}>{s.phase}</Badge>
            </TableCell>
            <TableCell>
              <Badge appearance="tint" color={s.ready ? 'success' : 'warning'}>{s.ready ? 'yes' : 'no'}</Badge>
            </TableCell>
            <TableCell style={{ fontSize: tokens.fontSizeBase200 }}>{s.warm_pool ?? '—'}</TableCell>
            <TableCell>{formatAge(s.age_seconds)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function SandboxClaimsTable({ rows }: { rows: SandboxClaimObjectDto[] }) {
  const styles = useStyles();
  if (rows.length === 0) return <Text className={styles.emptyState}>No SandboxClaim objects.</Text>;
  return (
    <Table aria-label="Sandbox claims" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Claim name</TableHeaderCell>
          <TableHeaderCell>Phase</TableHeaderCell>
          <TableHeaderCell>Bound sandbox</TableHeaderCell>
          <TableHeaderCell>Warm pool used</TableHeaderCell>
          <TableHeaderCell>Run ID (prefix)</TableHeaderCell>
          <TableHeaderCell>Age</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((c) => (
          <TableRow key={c.name}>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{c.name}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={c.phase === 'bound' ? 'success' : 'warning'}>{c.phase}</Badge>
            </TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{c.bound_sandbox ?? '—'}</TableCell>
            <TableCell style={{ fontSize: tokens.fontSizeBase200 }}>{c.warm_pool ?? '—'}</TableCell>
            <TableCell style={{ fontFamily: 'monospace', fontSize: tokens.fontSizeBase200 }}>{c.run_id ?? '—'}</TableCell>
            <TableCell>{formatAge(c.age_seconds)}</TableCell>
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
            <KpiCard label="Active" value={data.active_agent_pods.length} />
            <KpiCard label="Orphaned" value={data.orphaned_agent_pods.length} />
            <KpiCard label="Pending capacity" value={data.pending_capacity_runs.length} />
            <KpiCard
              label="Checks OK"
              value={`${data.checks.filter(c => c.status === 'healthy').length} / ${data.checks.length}`}
            />
            {(data.warm_pools?.length ?? 0) > 0 && (
              <KpiCard
                label="Warm pool"
                value={`${data.warm_pools!.reduce((s, p) => s + p.ready_replicas, 0)} / ${data.warm_pools!.reduce((s, p) => s + p.desired_replicas, 0)} ready`}
              />
            )}
          </div>

          {/* Health checks */}
          <div className={styles.section}>
            <Title3>Health checks</Title3>
            <HealthChecksTable rows={data.checks} />
          </div>

          {/* Active agent pods */}
          <div className={styles.section}>
            <Title3>Active agent pods ({data.active_agent_pods.length})</Title3>
            <AgentPodsTable pods={data.active_agent_pods} label="Active agent pods" />
          </div>

          {/* Orphaned agent pods */}
          {data.orphaned_agent_pods.length > 0 && (
            <div className={styles.section}>
              <Title3>Orphaned agent pods ({data.orphaned_agent_pods.length})</Title3>
              <AgentPodsTable pods={data.orphaned_agent_pods} label="Orphaned agent pods" />
            </div>
          )}

          {/* Pending capacity runs */}
          <div className={styles.section}>
            <Title3>Pending capacity ({data.pending_capacity_runs.length})</Title3>
            <PendingCapacityTable rows={data.pending_capacity_runs} />
          </div>

          {/* Warm pools */}
          <div className={styles.section}>
            <Title3>Warm pools ({data.warm_pools?.length ?? 0})</Title3>
            <WarmPoolsTable rows={data.warm_pools ?? []} />
          </div>

          {/* Sandbox objects */}
          <div className={styles.section}>
            <Title3>Sandbox objects ({data.sandbox_objects?.length ?? 0})</Title3>
            <SandboxObjectsTable rows={data.sandbox_objects ?? []} />
          </div>

          {/* Sandbox claims */}
          <div className={styles.section}>
            <Title3>Sandbox claims ({data.sandbox_claims?.length ?? 0})</Title3>
            <SandboxClaimsTable rows={data.sandbox_claims ?? []} />
          </div>

          <Text className={styles.generated}>Generated {data.generated_utc} · {data.total_duration_ms.toFixed(0)} ms</Text>
        </>
      )}
    </div>
  );
}
