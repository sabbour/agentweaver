import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Switch,
  Tab,
  TabList,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import type { DiagnosticsCheckDto, ProjectDiagnosticsDto, SystemDiagnosticsDto } from '../api/types';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import {
  AppCard,
  Body,
  EmptyState,
  Label,
  LoadingState,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
} from '../components/ui';
// Diagnostics (Spec 011, FR-016) — renders the backend's real executed checks as
// pass/warn/fail cards with per-check duration. A Global vs This-project tab
// switches between GET /api/diagnostics and GET /api/projects/{id}/diagnostics.

const REFRESH_MS = 15000;

const useStyles = makeStyles({
  checkCard: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalM,
  },
  checkBody: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    flex: 1,
    minWidth: 0,
  },
  checkHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  iconPass: { color: tokens.colorPaletteGreenForeground1, fontSize: '20px' },
  iconWarn: { color: tokens.colorPaletteYellowForeground1, fontSize: '20px' },
  iconFail: { color: tokens.colorPaletteRedForeground1, fontSize: '20px' },
});

type Scope = 'global' | 'project';

function humanizeUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  const parts: string[] = [];
  if (d > 0) parts.push(`${d}d`);
  if (h > 0) parts.push(`${h}h`);
  if (m > 0) parts.push(`${m}m`);
  parts.push(`${s}s`);
  return parts.join(' ');
}

function badgeColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'pass') return 'success';
  if (status === 'warn') return 'warning';
  if (status === 'fail') return 'danger';
  return 'subtle';
}

function CheckCard({ check, styles }: { check: DiagnosticsCheckDto; styles: ReturnType<typeof useStyles> }) {
  const icon =
    check.status === 'pass' ? (
      <CheckmarkCircleRegular className={styles.iconPass} aria-hidden="true" />
    ) : check.status === 'warn' ? (
      <WarningRegular className={styles.iconWarn} aria-hidden="true" />
    ) : (
      <DismissCircleRegular className={styles.iconFail} aria-hidden="true" />
    );
  return (
    <AppCard>
      <div className={styles.checkCard}>
        {icon}
        <div className={styles.checkBody}>
          <div className={styles.checkHeader}>
            <Body as="span" style={{ fontWeight: tokens.fontWeightSemibold }}>{check.name}</Body>
            <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
              <Badge appearance="tint" color={badgeColor(check.status)}>{check.status}</Badge>
              <Label as="span" tone="quiet" style={{ whiteSpace: 'nowrap' }}>{Math.round(check.duration_ms)} ms</Label>
            </div>
          </div>
          <Label as="span" tone="muted">{check.detail}</Label>
        </div>
      </div>
    </AppCard>
  );
}

export function DiagnosticsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [scope, setScope] = useState<Scope>('global');
  const [global, setGlobal] = useState<SystemDiagnosticsDto | null>(null);
  const [project, setProject] = useState<ProjectDiagnosticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    try {
      if (scope === 'project' && projectId) {
        const dto = await apiClient.getProjectDiagnostics(projectId);
        if (!signal.cancelled) { setProject(dto); setError(null); }
      } else {
        const dto = await apiClient.getDiagnostics();
        if (!signal.cancelled) { setGlobal(dto); setError(null); }
      }
    } catch (err) {
      if (!signal.cancelled) setError(formatError(err));
    } finally {
      if (!signal.cancelled) setLoading(false);
    }
  }, [scope, projectId]);

  useEffect(() => {
    const signal = { cancelled: false };
    const runLoad = () => {
      setLoading(true);
      void load(signal);
    };
    runLoad();
    const iv = autoRefresh ? setInterval(() => { void load(signal); }, REFRESH_MS) : undefined;
    return () => {
      signal.cancelled = true;
      if (iv) clearInterval(iv);
    };
  }, [load, autoRefresh]);

  const active = scope === 'project' ? project : global;
  const checks = active?.checks ?? [];
  const passCount = checks.filter((check) => check.status === 'pass').length;
  const warnCount = checks.filter((check) => check.status === 'warn').length;
  const failCount = checks.filter((check) => check.status === 'fail').length;

  return (
    <PageContainer>
      <PageHeader
        title="Diagnostics"
        description="System and project health checks."
        actions={
          <>
            {active && (
              <Label as="span" tone="quiet">
                Updated {new Date(active.generated_utc).toLocaleTimeString()}
              </Label>
            )}
            {active && autoRefresh && (
              <RefreshCountdown
                intervalMs={REFRESH_MS}
                lastRefreshedAt={new Date(active.generated_utc)}
              />
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
              Re-run
            </Button>
          </>
        }
      />

      <TabList
        selectedValue={scope}
        onTabSelect={(_, data) => setScope(data.value as Scope)}
        aria-label="Diagnostics scope"
      >
        <Tab value="global">Global</Tab>
        <Tab value="project" disabled={!projectId}>This project</Tab>
      </TabList>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !active && <LoadingState label="Loading diagnostics" />}

      {active && (
        <PageSection
          title={scope === 'project' ? 'Project diagnostics' : 'System diagnostics'}
          description="Dependency health checks run against the local environment."
        >
          <MetricRow items={[
            { label: 'Checks', value: String(checks.length) },
            { label: 'Pass', value: String(passCount) },
            { label: 'Warn', value: String(warnCount) },
            { label: 'Fail', value: String(failCount) },
            { label: 'Duration', value: `${Math.round(active.total_duration_ms)} ms` },
          ]} />
        </PageSection>
      )}

      {scope === 'global' && global && (
        <PageSection title="System summary">
          <MetricRow items={[
            { label: 'API version', value: global.api_version },
            { label: 'Uptime', value: humanizeUptime(global.uptime_seconds) },
            { label: 'Projects', value: String(global.total_projects) },
            { label: 'Total runs', value: String(global.total_runs) },
            { label: 'Active runs', value: String(global.active_runs) },
          ]} />
        </PageSection>
      )}

      {scope === 'project' && project && (
        <PageSection title="Project summary">
          <MetricRow items={[
            { label: 'Project', value: project.project_name },
            { label: 'Checks', value: String(project.checks.length) },
          ]} />
        </PageSection>
      )}

      {active && (
        <PageSection title={`Checks (${checks.length}) · ${Math.round(active.total_duration_ms)} ms`}>
          <div role="list" aria-label="Diagnostics checks" style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS }}>
            {checks.length === 0 ? (
              <EmptyState title="No checks reported" description="Health checks will appear once diagnostics run." />
            ) : (
              checks.map((c) => <CheckCard key={c.name} check={c} styles={styles} />)
            )}
          </div>
        </PageSection>
      )}
    </PageContainer>
  );
}