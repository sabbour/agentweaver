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
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { HeartbeatAutomationDto, HeartbeatStatusDto } from '../api/types';

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
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
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
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
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

function serviceBadgeColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'running') return 'success';
  if (status === 'waiting_first_tick') return 'warning';
  if (status === 'disabled') return 'subtle';
  return 'danger';
}

function automationBadgeColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'running' || status === 'idle') return 'success';
  if (status === 'waiting_first_tick') return 'warning';
  if (status === 'disabled') return 'subtle';
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
    <div className={styles.automationCard}>
      <div className={styles.automationHeader}>
        <Text className={styles.automationName}>{automation.name}</Text>
        <Badge appearance="tint" color={automationBadgeColor(automation.status)}>{automation.status}</Badge>
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

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    try {
      const dto = await apiClient.getHeartbeatStatus();
      if (!signal.cancelled) { setData(dto); setError(null); }
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
      <div className={styles.pageHeader}>
        <Title2>Heartbeat</Title2>
        <div className={styles.headerActions}>
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
        </div>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !data && <Spinner label="Loading heartbeat status" />}

      {data && (
        <>
          <div className={styles.statusRow}>
            <Badge appearance="filled" color={serviceBadgeColor(data.service_status)}>
              {data.service_status}
            </Badge>
            <Text className={styles.meta}>
              {data.enabled ? 'Enabled' : 'Disabled'} · interval {Math.round(data.interval_seconds)}s
            </Text>
            <Text className={styles.meta}>
              Last tick: {data.last_tick_utc ? relativeTime(data.last_tick_utc) : '—'}
            </Text>
          </div>

          {data.last_error && (
            <MessageBar intent="error">
              <MessageBarBody>Last error: {data.last_error}</MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.section}>
            <Title3>Automations</Title3>
            <div className={styles.automations}>
              {data.automations.map((a) => (
                <AutomationCard key={a.name} automation={a} styles={styles} />
              ))}
            </div>
          </div>

          <div className={styles.section}>
            <Title3>Recent activity</Title3>
            {data.recent_activity.length === 0 ? (
              <Text>No ticks recorded yet.</Text>
            ) : (
              <Table aria-label="Recent heartbeat ticks" size="small">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>When</TableHeaderCell>
                    <TableHeaderCell>Acted</TableHeaderCell>
                    <TableHeaderCell>Errors</TableHeaderCell>
                    <TableHeaderCell>Duration</TableHeaderCell>
                    <TableHeaderCell>Error</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.recent_activity.map((t, i) => (
                    <TableRow key={`${t.timestamp_utc}-${i}`}>
                      <TableCell>{relativeTime(t.timestamp_utc)}</TableCell>
                      <TableCell>{t.acted_count}</TableCell>
                      <TableCell>
                        {t.error_count > 0 ? (
                          <Badge appearance="tint" color="danger">{t.error_count}</Badge>
                        ) : (
                          t.error_count
                        )}
                      </TableCell>
                      <TableCell>{Math.round(t.duration_ms)} ms</TableCell>
                      <TableCell>{t.error ?? '—'}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </div>
        </>
      )}
    </div>
  );
}
