import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureTabList,
  Badge,
  BladeHeader,
  Button,
  CommandBar,
  MessageBar,
  MessageBarBody,
  Spinner,
  StatusIconText,
  Switch,
  Text,
  } from '../copilot-fluent-system';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import { ArrowClockwiseRegular, CheckmarkCircleRegular, DismissCircleRegular, WarningRegular } from '../copilot-fluent-system';
import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import type { DiagnosticsCheckDto, ProjectDiagnosticsDto, SystemDiagnosticsDto } from '../api/types';
// Diagnostics (Spec 011, FR-016) — renders the backend's real executed checks as
// pass/warn/fail cards with per-check duration. A Global vs This-project tab
// switches between GET /api/diagnostics and GET /api/projects/{id}/diagnostics.

const REFRESH_MS = 15000;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  summaryCards: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  commandSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  statusPills: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    minHeight: '28px',
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontSize: tokens.fontSizeBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  summaryCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  summaryLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  summaryValue: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  checks: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  checkCard: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalM,
    borderLeftWidth: '3px',
  },
  checkPass: { borderLeftColor: tokens.colorPaletteGreenBorderActive },
  checkWarn: { borderLeftColor: tokens.colorPaletteYellowBorderActive },
  checkFail: { borderLeftColor: tokens.colorPaletteRedBorderActive },
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
  checkName: { fontWeight: tokens.fontWeightSemibold },
  checkDetail: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  duration: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap' },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
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
  const accent =
    check.status === 'pass' ? styles.checkPass : check.status === 'warn' ? styles.checkWarn : styles.checkFail;
  const icon =
    check.status === 'pass' ? (
      <CheckmarkCircleRegular className={styles.iconPass} aria-hidden="true" />
    ) : check.status === 'warn' ? (
      <WarningRegular className={styles.iconWarn} aria-hidden="true" />
    ) : (
      <DismissCircleRegular className={styles.iconFail} aria-hidden="true" />
    );
  return (
    <div role="listitem" className={['azf-surface azf-surface--panel azf-surface--padding-compact', mergeClasses(styles.checkCard, accent)].filter(Boolean).join(' ')}>
      {icon}
      <div className={styles.checkBody}>
        <div className={styles.checkHeader}>
          <Text className={styles.checkName}>{check.name}</Text>
          <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
            <Badge appearance="tint" color={badgeColor(check.status)}>{check.status}</Badge>
            <Text className={styles.duration}>{Math.round(check.duration_ms)} ms</Text>
          </div>
        </div>
        <Text className={styles.checkDetail}>{check.detail}</Text>
      </div>
    </div>
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
    setLoading(true);
    void load(signal);
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
    <div className={['azf-stack azf-page azf-pattern-shell', styles.root].filter(Boolean).join(' ')}>
      <PageHeader
        title="Diagnostics"
        subtitle="System and project health checks."
        actions={
          <>
            {active && (
              <Text className={styles.generated}>
                Updated {new Date(active.generated_utc).toLocaleTimeString()}
              </Text>
            )}
            {active && autoRefresh && (
              <RefreshCountdown
                className={styles.generated}
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

      <AzureTabList
        ariaLabel="Diagnostics scope"
        selectedValue={scope}
        onTabSelect={(value) => setScope(value as Scope)}
        tabs={[
          { id: 'global', label: 'Global' },
          { id: 'project', label: 'This project', disabled: !projectId },
        ]}
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !active && <Spinner label="Loading diagnostics" />}

      {active && (
        <section className={['azf-surface azf-surface--raised azf-surface--padding-comfortable', styles.commandSurface].filter(Boolean).join(' ')} aria-label="Diagnostics resource command surface">
          <CommandBar
            title={scope === 'project' ? 'Project diagnostics blade' : 'Global diagnostics blade'}
            description="Azure health surface for dependency checks, duration, and remediation status."
          >
            <div className={styles.statusPills}>
              <StatusIconText className={styles.statusPill} status={failCount > 0 ? 'danger' : warnCount > 0 ? 'warning' : 'success'}>
                {checks.length} checks
              </StatusIconText>
              <StatusIconText className={styles.statusPill} status="success">{passCount} pass</StatusIconText>
              <StatusIconText className={styles.statusPill} status="warning">{warnCount} warn</StatusIconText>
              <StatusIconText className={styles.statusPill} status="danger">{failCount} fail</StatusIconText>
            </div>
          </CommandBar>
          <div className={styles.summaryCards}>
            <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
              <Text className={styles.summaryLabel}>Scope</Text>
              <Text className={styles.summaryValue}>{scope === 'project' ? 'Project' : 'Global'}</Text>
            </div>
            <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
              <Text className={styles.summaryLabel}>Total duration</Text>
              <Text className={styles.summaryValue}>{Math.round(active.total_duration_ms)} ms</Text>
            </div>
            <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
              <Text className={styles.summaryLabel}>Generated</Text>
              <Text className={styles.summaryValue}>{new Date(active.generated_utc).toLocaleTimeString()}</Text>
            </div>
          </div>
        </section>
      )}

      {scope === 'global' && global && (
        <div className={styles.summaryCards}>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>API version</Text>
            <Text className={styles.summaryValue}>{global.api_version}</Text>
          </div>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Uptime</Text>
            <Text className={styles.summaryValue}>{humanizeUptime(global.uptime_seconds)}</Text>
          </div>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Total projects</Text>
            <Text className={styles.summaryValue}>{global.total_projects}</Text>
          </div>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Total runs</Text>
            <Text className={styles.summaryValue}>{global.total_runs}</Text>
          </div>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Active runs</Text>
            <Text className={styles.summaryValue}>{global.active_runs}</Text>
          </div>
        </div>
      )}

      {scope === 'project' && project && (
        <div className={styles.summaryCards}>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Project</Text>
            <Text className={styles.summaryValue}>{project.project_name}</Text>
          </div>
          <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
            <Text className={styles.summaryLabel}>Checks</Text>
            <Text className={styles.summaryValue}>{project.checks.length}</Text>
          </div>
        </div>
      )}

      {active && (
        <div className="azf-surface azf-surface--panel azf-surface--padding-comfortable">
          <BladeHeader size="compact" title={`Checks (${checks.length}) · ${Math.round(active.total_duration_ms)} ms`} />
          <div
            className={styles.checks}
            role="list"
            aria-label="Diagnostics checks"
            style={{ marginTop: tokens.spacingVerticalM }}
          >
            {checks.length === 0 ? (
              <Text>No checks reported.</Text>
            ) : (
              checks.map((c) => <CheckCard key={c.name} check={c} styles={styles} />)
            )}
          </div>
        </div>
      )}
    </div>
  );
}
