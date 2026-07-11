import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import type {
  AgentPodInfoDto,
  ClusterDiagnosticsDto,
  DetailedHealthCheckDto,
  PendingCapacityRunDto,
  SandboxClaimObjectDto,
  SandboxObjectDto,
  WarmPoolStatusDto,
} from '../api/types';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import {
  EmptyState,
  Label,
  LoadingState,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
  StatTile,
} from '../components/ui';
// Cluster (spec-018) — Kubernetes cluster health and capacity view.
// Calls GET /api/diagnostics/cluster; shows a "Not available" placeholder until
// the backend endpoint is deployed (404 response).

const REFRESH_MS = 30_000;

const useStyles = makeStyles({
  kpiRow: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
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

function podBadgeColor(status: string): 'success' | 'warning' | 'subtle' {
  if (status === 'ready') return 'success';
  if (status === 'pending') return 'warning';
  return 'subtle';
}

function HealthChecksTable({ rows }: { rows: DetailedHealthCheckDto[] }) {
  if (rows.length === 0) return <EmptyState title="No health checks" />;
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
  if (pods.length === 0) return <EmptyState title={`No ${label.toLowerCase()}`} />;
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
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{p.claim_name}</TableCell>
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{p.pod_name ?? '—'}</TableCell>
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
  if (rows.length === 0) return <EmptyState title="No pending capacity runs" />;
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
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{r.child_run_id ?? '—'}</TableCell>
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
  if (rows.length === 0) return <EmptyState title="No warm pools configured" />;
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
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{p.name}</TableCell>
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
  if (rows.length === 0) return <EmptyState title="No sandbox objects" description="Sandbox objects will appear when agent runs are active." />;
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
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{s.name}</TableCell>
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
  if (rows.length === 0) return <EmptyState title="No sandbox claims" description="Claims will appear when runs are assigned to sandbox environments." />;
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
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{c.name}</TableCell>
            <TableCell>
              <Badge appearance="tint" color={c.phase === 'bound' ? 'success' : 'warning'}>{c.phase}</Badge>
            </TableCell>
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{c.bound_sandbox ?? '—'}</TableCell>
            <TableCell style={{ fontSize: tokens.fontSizeBase200 }}>{c.warm_pool ?? '—'}</TableCell>
            <TableCell style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 }}>{c.run_id ?? '—'}</TableCell>
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
    <PageContainer>
      <PageHeader
        title="Cluster"
        description="Kubernetes cluster health and capacity."
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

      {loading && !data && !notAvailable && <LoadingState label="Loading cluster diagnostics" />}

      {data && (
        <>
          <PageSection
            title="Cluster overview"
            description="Live diagnostics, capacity queues, and sandbox resource state."
          >
            <MetricRow items={[
              { label: 'Check summary', value: `${data.checks.filter(c => c.status === 'healthy').length} healthy / ${data.checks.length}` },
              { label: 'Sandbox claims', value: String(data.sandbox_claims?.length ?? 0) },
              { label: 'Capacity queue', value: String(data.pending_capacity_runs.length) },
              { label: 'Generated', value: data.generated_utc },
            ]} />
          </PageSection>

          <div className={styles.kpiRow}>
            <StatTile label="Orphaned pods" value={String(data.orphaned_agent_pods.length)} />
            <StatTile label="Pending capacity" value={String(data.pending_capacity_runs.length)} />
            <StatTile
              label="Checks healthy"
              value={`${data.checks.filter(c => c.status === 'healthy').length} / ${data.checks.length}`}
            />
            {(data.warm_pools?.length ?? 0) > 0 && (
              <StatTile
                label="Warm pool ready"
                value={`${data.warm_pools!.reduce((s, p) => s + p.ready_replicas, 0)} / ${data.warm_pools!.reduce((s, p) => s + p.desired_replicas, 0)}`}
              />
            )}
          </div>

          <PageSection title="Health checks">
            <HealthChecksTable rows={data.checks} />
          </PageSection>

          <PageSection title={`Sandbox claims (${data.sandbox_claims?.length ?? 0})`}>
            <SandboxClaimsTable rows={data.sandbox_claims ?? []} />
          </PageSection>

          {data.orphaned_agent_pods.length > 0 && (
            <PageSection title={`Orphaned agent pods (${data.orphaned_agent_pods.length})`}>
              <AgentPodsTable pods={data.orphaned_agent_pods} label="Orphaned agent pods" />
            </PageSection>
          )}

          <PageSection title={`Pending capacity (${data.pending_capacity_runs.length})`}>
            <PendingCapacityTable rows={data.pending_capacity_runs} />
          </PageSection>

          <PageSection title={`Warm pools (${data.warm_pools?.length ?? 0})`}>
            <WarmPoolsTable rows={data.warm_pools ?? []} />
          </PageSection>

          <PageSection title={`Sandbox objects (${data.sandbox_objects?.length ?? 0})`}>
            <SandboxObjectsTable rows={data.sandbox_objects ?? []} />
          </PageSection>

          <Label as="p" tone="quiet" className={styles.generated}>
            Generated {data.generated_utc} · {data.total_duration_ms.toFixed(0)} ms
          </Label>
        </>
      )}
    </PageContainer>
  );
}