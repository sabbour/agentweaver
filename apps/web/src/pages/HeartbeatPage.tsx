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
import type { HeartbeatAutomationDto, HeartbeatStatusDto, HeartbeatTickDto } from '../api/types';
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
  TitleText,
} from '../components/ui';
// Heartbeat (Spec 011, FR-017) — service status, last error, the real automations
// catalog (exactly two: Coordinator Heartbeat + Checkpoint GC), and the recent
// tick activity timeline (acted/errors/duration). Real data only — no invented rows.

const REFRESH_MS = 15000;

const useStyles = makeStyles({
  statusRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  automations: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  automationHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
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

function heartbeatStatusColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
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
    <AppCard>
      <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS }}>
        <div className={styles.automationHeader}>
          <TitleText as="span">{automation.name}</TitleText>
          <Badge appearance="tint" color={heartbeatStatusColor(automation.status)}>
            {automation.status}
          </Badge>
        </div>
        <Body as="p" tone="muted">{automation.description}</Body>
        <Label as="span" tone="quiet">Cadence: every {Math.round(automation.cadence_seconds)}s</Label>
        <Label as="span" tone="quiet">
          Last run: {automation.last_run_utc ? relativeTime(automation.last_run_utc) : '—'}
          {automation.last_acted_count != null && ` · acted ${automation.last_acted_count}`}
        </Label>
      </div>
    </AppCard>
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
    <PageContainer>
      <PageHeader
        title="Heartbeat"
        description="Background automation status and recent ticks."
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

      {loading && !data && <LoadingState label="Loading heartbeat status" />}

      {data && (
        <>
          <PageSection
            title="Service status"
            description="Background coordinator and checkpoint automation status."
          >
            <div className={styles.statusRow}>
              <Badge appearance="tint" color={heartbeatStatusColor(data.service_status)}>
                {data.service_status}
              </Badge>
              <Label as="span" tone="muted">
                {data.enabled ? 'Enabled' : 'Disabled'} · interval {Math.round(data.interval_seconds)}s
              </Label>
              <Label as="span" tone="muted">
                Last tick: {data.last_tick_utc ? relativeTime(data.last_tick_utc) : '—'}
              </Label>
            </div>
            <MetricRow items={[
              { label: 'Status', value: data.enabled ? 'Enabled' : 'Disabled' },
              { label: 'Cadence', value: `${Math.round(data.interval_seconds)}s` },
              { label: 'Automations', value: String(data.automations.length) },
            ]} />
          </PageSection>

          {data.last_error && (
            <MessageBar intent="error">
              <MessageBarBody>Last error: {data.last_error}</MessageBarBody>
            </MessageBar>
          )}

          <PageSection title="Automations">
            <div className={styles.automations}>
              {data.automations.map((a) => (
                <AutomationCard key={a.name} automation={a} styles={styles} />
              ))}
            </div>
          </PageSection>

          <PageSection title="Recent activity">
            {data.recent_activity.length === 0 ? (
              <EmptyState
                title="No ticks recorded yet"
                description="Recent heartbeat activity will appear after the next automation cycle."
              />
            ) : (
              <Table aria-label="Recent heartbeat ticks" size="small">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Automation</TableHeaderCell>
                    <TableHeaderCell>When</TableHeaderCell>
                    <TableHeaderCell>Acted</TableHeaderCell>
                    <TableHeaderCell>Errors</TableHeaderCell>
                    <TableHeaderCell>Duration</TableHeaderCell>
                    <TableHeaderCell>Error</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.recent_activity.map((tick: HeartbeatTickDto, index: number) => (
                    <TableRow key={`${tick.timestamp_utc}-${index}`}>
                      <TableCell>{tick.automation_name}</TableCell>
                      <TableCell>{relativeTime(tick.timestamp_utc)}</TableCell>
                      <TableCell>{tick.acted_count}</TableCell>
                      <TableCell>
                        {tick.error_count > 0 ? (
                          <Badge appearance="tint" color="danger">{tick.error_count}</Badge>
                        ) : tick.error_count}
                      </TableCell>
                      <TableCell>{Math.round(tick.duration_ms)} ms</TableCell>
                      <TableCell>{tick.error ?? '—'}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </PageSection>
        </>
      )}
    </PageContainer>
  );
}