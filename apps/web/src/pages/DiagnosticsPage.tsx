import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Tab,
  TabList,
  Text,
  Title3,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  HelpCircleRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import type {
  DetailedDiagnosticsCheckDto,
  DiagnosticsCheckDto,
  ProjectDiagnosticsDto,
  SystemDiagnosticsDto,
} from '../api/types';

// Diagnostics (Spec 011, FR-016) — renders the backend's real executed checks as
// pass/warn/fail cards with per-check duration. A Global vs This-project tab
// switches between GET /api/diagnostics and GET /api/projects/{id}/diagnostics.
// When GET /api/diagnostics/detailed is available it is preferred: it provides
// healthy/warning/critical/unknown status plus latency, quota (used/limit/unit),
// and pending-count fields for richer display.

const REFRESH_MS = 30_000;

// ---------------------------------------------------------------------------
// Unified internal check shape — normalises both the legacy DiagnosticsCheckDto
// (pass/warn/fail) and the new DetailedDiagnosticsCheckDto (healthy/warning/critical).
// ---------------------------------------------------------------------------

interface NormalisedCheck {
  name: string;
  /** Canonical status used for icons / colours. */
  status: 'healthy' | 'warning' | 'critical' | 'unknown';
  message?: string;
  latencyMs?: number;
  used?: number;
  limit?: number;
  unit?: string;
  pendingCount?: number;
}

function fromLegacy(c: DiagnosticsCheckDto): NormalisedCheck {
  const statusMap: Record<string, NormalisedCheck['status']> = {
    pass: 'healthy',
    warn: 'warning',
    fail: 'critical',
  };
  return {
    name: c.name,
    status: statusMap[c.status] ?? 'unknown',
    message: c.detail || undefined,
    latencyMs: c.duration_ms,
  };
}

function fromDetailed(c: DetailedDiagnosticsCheckDto): NormalisedCheck {
  return {
    name: c.name,
    status: c.status,
    message: c.message,
    latencyMs: c.latencyMs,
    used: c.used,
    limit: c.limit,
    unit: c.unit,
    pendingCount: c.pendingCount,
  };
}

// Aggregate status across all checks for the summary dot / header badge.
export function aggregateStatus(checks: NormalisedCheck[]): 'healthy' | 'warning' | 'critical' | 'unknown' {
  if (checks.length === 0) return 'unknown';
  if (checks.some((c) => c.status === 'critical')) return 'critical';
  if (checks.some((c) => c.status === 'warning')) return 'warning';
  if (checks.every((c) => c.status === 'healthy')) return 'healthy';
  return 'unknown';
}

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
  summaryCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
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
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderLeftWidth: '3px',
    borderRadius: tokens.borderRadiusMedium,
  },
  checkHealthy: { borderLeftColor: tokens.colorPaletteGreenBorderActive },
  checkWarning: { borderLeftColor: tokens.colorPaletteYellowBorderActive },
  checkCritical: { borderLeftColor: tokens.colorPaletteRedBorderActive },
  checkUnknown: { borderLeftColor: tokens.colorNeutralStroke2 },
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
  checkMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  duration: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap' },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  iconHealthy: { color: tokens.colorPaletteGreenForeground1, fontSize: '20px' },
  iconWarning: { color: tokens.colorPaletteYellowForeground1, fontSize: '20px' },
  iconCritical: { color: tokens.colorPaletteRedForeground1, fontSize: '20px' },
  iconUnknown: { color: tokens.colorNeutralForeground4, fontSize: '20px' },
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

function badgeColor(status: NormalisedCheck['status']): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'healthy') return 'success';
  if (status === 'warning') return 'warning';
  if (status === 'critical') return 'danger';
  return 'subtle';
}

function CheckCard({ check }: { check: NormalisedCheck }) {
  const styles = useStyles();

  const accentClass =
    check.status === 'healthy' ? styles.checkHealthy
    : check.status === 'warning' ? styles.checkWarning
    : check.status === 'critical' ? styles.checkCritical
    : styles.checkUnknown;

  const iconClass =
    check.status === 'healthy' ? styles.iconHealthy
    : check.status === 'warning' ? styles.iconWarning
    : check.status === 'critical' ? styles.iconCritical
    : styles.iconUnknown;

  const icon =
    check.status === 'healthy' ? <CheckmarkCircleRegular className={iconClass} aria-hidden="true" />
    : check.status === 'warning' ? <WarningRegular className={iconClass} aria-hidden="true" />
    : check.status === 'critical' ? <DismissCircleRegular className={iconClass} aria-hidden="true" />
    : <HelpCircleRegular className={iconClass} aria-hidden="true" />;

  // Quota detail: "used / limit unit  (N pending)"
  const quotaText =
    check.used != null && check.limit != null
      ? `${check.used} / ${check.limit}${check.unit ? ` ${check.unit}` : ''}`
      : undefined;

  const pendingText =
    check.pendingCount != null && check.pendingCount > 0
      ? `${check.pendingCount} pending`
      : undefined;

  return (
    <div className={`${styles.checkCard} ${accentClass}`} role="listitem">
      {icon}
      <div className={styles.checkBody}>
        <div className={styles.checkHeader}>
          <Text className={styles.checkName}>{check.name}</Text>
          <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
            <Badge appearance="tint" color={badgeColor(check.status)}>{check.status}</Badge>
            {check.latencyMs != null && (
              <Text className={styles.duration}>{Math.round(check.latencyMs)} ms</Text>
            )}
          </div>
        </div>
        {(check.message || quotaText || pendingText) && (
          <div className={styles.checkMeta}>
            {check.message && <Text className={styles.checkDetail}>{check.message}</Text>}
            {quotaText && <Text className={styles.checkDetail}>{quotaText}</Text>}
            {pendingText && (
              <Tooltip content="Number of agent pods waiting for capacity" relationship="description" withArrow>
                <Text className={styles.checkDetail} style={{ color: tokens.colorPaletteYellowForeground1 }}>
                  ⏳ {pendingText}
                </Text>
              </Tooltip>
            )}
          </div>
        )}
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
  const [detailedChecks, setDetailedChecks] = useState<NormalisedCheck[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [lastRefreshed, setLastRefreshed] = useState<Date | null>(null);

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
        if (!signal.cancelled) {
          setProject(dto);
          setDetailedChecks(null);
          setError(null);
          setLastRefreshed(new Date());
        }
      } else {
        // Try the detailed endpoint first; fall back to the basic snapshot.
        const detailed = await apiClient.getDetailedDiagnostics();
        if (!signal.cancelled) {
          if (detailed) {
            setDetailedChecks(detailed.checks.map(fromDetailed));
            setGlobal((prev) => prev ?? null);
          } else {
            const dto = await apiClient.getDiagnostics();
            if (!signal.cancelled) {
              setGlobal(dto);
              setDetailedChecks(dto.checks.map(fromLegacy));
            }
          }
          setError(null);
          setLastRefreshed(new Date());
        }
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

  // Derive displayed checks: prefer detailed (normalised) for the global scope;
  // for project scope fall back to legacy mapping.
  const displayChecks: NormalisedCheck[] =
    detailedChecks ?? (project?.checks.map(fromLegacy) ?? []);

  const active = scope === 'project' ? project : global;
  const generatedUtc = lastRefreshed?.toISOString() ?? active?.generated_utc;
  const totalDurationMs = active?.total_duration_ms ?? 0;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Diagnostics"
        subtitle="System and project health checks."
        actions={
          <>
            {generatedUtc && (
              <Text className={styles.generated}>
                Updated {new Date(generatedUtc).toLocaleTimeString()}
              </Text>
            )}
            {generatedUtc && autoRefresh && (
              <RefreshCountdown
                className={styles.generated}
                intervalMs={REFRESH_MS}
                lastRefreshedAt={new Date(generatedUtc)}
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
        onTabSelect={(_, d) => setScope(d.value as Scope)}
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

      {loading && displayChecks.length === 0 && <Spinner label="Loading diagnostics" />}

      {scope === 'global' && global && (
        <div className={styles.summaryCards}>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>API version</Text>
            <Text className={styles.summaryValue}>{global.api_version}</Text>
          </div>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Uptime</Text>
            <Text className={styles.summaryValue}>{humanizeUptime(global.uptime_seconds)}</Text>
          </div>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Total projects</Text>
            <Text className={styles.summaryValue}>{global.total_projects}</Text>
          </div>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Total runs</Text>
            <Text className={styles.summaryValue}>{global.total_runs}</Text>
          </div>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Active runs</Text>
            <Text className={styles.summaryValue}>{global.active_runs}</Text>
          </div>
        </div>
      )}

      {scope === 'project' && project && (
        <div className={styles.summaryCards}>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Project</Text>
            <Text className={styles.summaryValue}>{project.project_name}</Text>
          </div>
          <div className={styles.summaryCard}>
            <Text className={styles.summaryLabel}>Checks</Text>
            <Text className={styles.summaryValue}>{project.checks.length}</Text>
          </div>
        </div>
      )}

      {displayChecks.length > 0 && (
        <div>
          <Title3>Checks ({displayChecks.length}) · {Math.round(totalDurationMs)} ms</Title3>
          <div
            className={styles.checks}
            role="list"
            aria-label="Diagnostics checks"
            style={{ marginTop: tokens.spacingVerticalM }}
          >
            {displayChecks.map((c) => <CheckCard key={c.name} check={c} />)}
          </div>
        </div>
      )}
    </div>
  );
}
