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
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  DiagnosticsCheckDto,
  ProjectDiagnosticsDto,
  SystemDiagnosticsDto,
} from '../api/types';

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
  pageHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
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
    <div className={`${styles.checkCard} ${accent}`} role="listitem">
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

  return (
    <div className={styles.root}>
      <div className={styles.pageHeader}>
        <Title2>Diagnostics</Title2>
        <div className={styles.headerActions}>
          {active && (
            <Text className={styles.generated}>
              Updated {new Date(active.generated_utc).toLocaleTimeString()}
            </Text>
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
        </div>
      </div>

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

      {loading && !active && <Spinner label="Loading diagnostics" />}

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

      {active && (
        <div>
          <Title3>Checks ({checks.length}) · {Math.round(active.total_duration_ms)} ms</Title3>
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
