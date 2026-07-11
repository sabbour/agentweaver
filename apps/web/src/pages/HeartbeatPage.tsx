import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureDataGrid,
  BladeHeader,
  Button,
  CommandBar,
  EmptyState,
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
  tokens,
} from '../copilot-fluent-system';
import { ArrowClockwiseRegular } from '../copilot-fluent-system';
import { useCallback, useEffect, useState } from 'react';
import type { HeartbeatAutomationDto, HeartbeatStatusDto, HeartbeatTickDto } from '../api/types';
// Heartbeat (Spec 011, FR-017) — service status, last error, the real automations
// catalog (exactly two: Coordinator Heartbeat + Checkpoint GC), and the recent
// tick activity timeline (acted/errors/duration). Real data only — no invented rows.

const REFRESH_MS = 15000;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  commandSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  summaryCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  summaryLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  summaryValue: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  automations: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  automationCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  automationHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  automationName: { fontWeight: tokens.fontWeightSemibold },
  automationDesc: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  meta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
});

function relativeTime(iso: string): string {
  const diffMs = Date.now() - new Date(iso).getTime();
  const seconds = Math.floor(diffMs / 1000);
  if (Number.isNaN(seconds)) return iso;
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

function heartbeatTone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  if (status === 'running' || status === 'idle') return 'success';
  if (status === 'waiting_first_tick') return 'warning';
  if (status === 'disabled') return 'neutral';
  return 'danger';
}

function AutomationCard({
  automation,
  styles,
}: {
  automation: HeartbeatAutomationDto;
  styles: ReturnType<typeof useStyles>;
}) {
  return (
    <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.automationCard].filter(Boolean).join(' ')}>
      <div className={styles.automationHeader}>
        <Text className={styles.automationName}>{automation.name}</Text>
        <StatusIconText status={heartbeatTone(automation.status)}>{automation.status}</StatusIconText>
      </div>
      <Text className={styles.automationDesc}>{automation.description}</Text>
      <Text className={styles.meta}>Cadence: every {Math.round(automation.cadence_seconds)}s</Text>
      <Text className={styles.meta}>
        Last run: {automation.last_run_utc ? relativeTime(automation.last_run_utc) : '—'}
        {automation.last_acted_count != null && ` · acted ${automation.last_acted_count}`}
      </Text>
    </div>
  );
}

export function HeartbeatPage() {
  const styles = useStyles();
  const [data, setData] = useState<HeartbeatStatusDto | null>(null);
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
      const dto = await apiClient.getHeartbeatStatus();
      if (!signal.cancelled) { setData(dto); setError(null); setLastRefreshedAt(Date.now()); }
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
    <div className={['azf-stack azf-page azf-pattern-shell', styles.root].filter(Boolean).join(' ')}>
      <PageHeader
        title="Heartbeat"
        subtitle="Background automation status and recent ticks."
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

      {loading && !data && <Spinner label="Loading heartbeat status" />}

      {data && (
        <>
          <section className={['azf-surface azf-surface--raised azf-surface--padding-comfortable', styles.commandSurface].filter(Boolean).join(' ')} aria-label="Heartbeat service resource summary">
            <CommandBar
              title="Automation command surface"
              description="Azure resource monitor for background coordinator and checkpoint automations."
            >
              <div className={styles.statusRow}>
                <StatusIconText status={heartbeatTone(data.service_status)}>{data.service_status}</StatusIconText>
                <Text className={styles.meta}>
                  {data.enabled ? 'Enabled' : 'Disabled'} · interval {Math.round(data.interval_seconds)}s
                </Text>
                <Text className={styles.meta}>
                  Last tick: {data.last_tick_utc ? relativeTime(data.last_tick_utc) : '—'}
                </Text>
              </div>
            </CommandBar>
            <div className={styles.summaryGrid}>
              <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
                <Text className={styles.summaryLabel}>Service state</Text>
                <Text className={styles.summaryValue}>{data.enabled ? 'Enabled' : 'Disabled'}</Text>
                <Text className={styles.automationDesc}>{data.enabled ? 'Background processing enabled' : 'Background processing disabled'}</Text>
              </div>
              <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
                <Text className={styles.summaryLabel}>Cadence</Text>
                <Text className={styles.summaryValue}>{Math.round(data.interval_seconds)}s</Text>
                <Text className={styles.automationDesc}>Coordinator heartbeat interval</Text>
              </div>
              <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.summaryCard].filter(Boolean).join(' ')}>
                <Text className={styles.summaryLabel}>Automations</Text>
                <Text className={styles.summaryValue}>{data.automations.length}</Text>
                <Text className={styles.automationDesc}>Catalogued background jobs</Text>
              </div>
            </div>
          </section>

          {data.last_error && (
            <MessageBar intent="error">
              <MessageBarBody>Last error: {data.last_error}</MessageBarBody>
            </MessageBar>
          )}

          <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable', styles.section].filter(Boolean).join(' ')}>
            <BladeHeader size="compact" title="Automations" />
            <div className={styles.automations}>
              {data.automations.map((a) => (
                <AutomationCard key={a.name} automation={a} styles={styles} />
              ))}
            </div>
          </div>

          <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable', styles.section].filter(Boolean).join(' ')}>
            <BladeHeader size="compact" title="Recent activity" />
            {data.recent_activity.length === 0 ? (
              <EmptyState title="No ticks recorded yet" body="Recent heartbeat activity will appear after the next automation cycle." />
            ) : (
              <AzureDataGrid<HeartbeatTickDto>
                ariaLabel="Recent heartbeat ticks"
                items={data.recent_activity}
                getRowId={(tick, index) => `${tick.timestamp_utc}-${index}`}
                columns={[
                  { columnId: 'automation', header: 'Automation', renderCell: (tick) => tick.automation_name, sortable: true, sortValue: (tick) => tick.automation_name },
                  { columnId: 'when', header: 'When', renderCell: (tick) => relativeTime(tick.timestamp_utc), sortable: true, sortValue: (tick) => tick.timestamp_utc },
                  { columnId: 'acted', header: 'Acted', renderCell: (tick) => tick.acted_count, sortable: true, sortValue: (tick) => tick.acted_count },
                  {
                    columnId: 'errors',
                    header: 'Errors',
                    renderCell: (tick) => (
                      tick.error_count > 0
                        ? <StatusIconText status="danger">{tick.error_count}</StatusIconText>
                        : tick.error_count
                    ),
                    sortable: true,
                    sortValue: (tick) => tick.error_count,
                  },
                  { columnId: 'duration', header: 'Duration', renderCell: (tick) => `${Math.round(tick.duration_ms)} ms`, sortable: true, sortValue: (tick) => tick.duration_ms },
                  { columnId: 'error', header: 'Error', renderCell: (tick) => tick.error ?? '—' },
                ]}
              />
            )}
          </div>
        </>
      )}
    </div>
  );
}
