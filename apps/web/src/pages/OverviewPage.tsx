import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
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
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { AppUsage, OverviewDto } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { TokenUsagePanel } from '../components/TokenUsagePanel';
import { formatAic } from '../components/CostChip';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';

// Overview ("Now") — the global, cross-project live view at /overview (also the
// landing page at '/'). Consumes GET /api/overview (real data only; no cost).

const REFRESH_MS = 10000;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  cards: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  cardLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  cardValue: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  projectCards: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },
  projectCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
  },
  projectName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  projectMeta: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  activityList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    margin: 0,
    padding: 0,
    listStyle: 'none',
  },
  activityRow: {
    display: 'grid',
    gridTemplateColumns: '112px 1fr auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  activityKind: {
    justifySelf: 'start',
  },
  activityLabel: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  muted: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  costDashboard: {
    display: 'grid',
    gridTemplateColumns: 'minmax(220px, 1fr) minmax(260px, 2fr)',
    gap: tokens.spacingHorizontalL,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
    },
  },
  costHero: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  costValue: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero800,
  },
  costBreakdown: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  costBarRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(120px, 1fr) minmax(120px, 2fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  costBarTrack: {
    height: '8px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  costBarFill: {
    height: '100%',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground,
  },
  projectLink: { color: tokens.colorBrandForeground1, textDecoration: 'none' },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  numericCell: { textAlign: 'right' as const },
});

// Relative "time ago" for data timestamps (last activity, activity rows). This is
// for displayed DATA, distinct from the header's refresh countdown.
function timeAgo(iso: string): string {
  const then = new Date(iso).getTime();
  const diff = Date.now() - then;
  if (Number.isNaN(diff)) return '';
  const s = Math.round(diff / 1000);
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}

// Humanize a raw status/kind ("in_progress" -> "In progress", "awaiting_review" ->
// "Awaiting review") for the recent-activity status chip.
function humanizeKind(kind: string): string {
  if (!kind) return '';
  const spaced = kind.replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

// Color the status chip mirroring RunRow in ProjectPage.tsx.
function kindColor(kind: string): 'success' | 'danger' | 'informative' | 'subtle' {
  if (kind === 'completed' || kind === 'merged') return 'success';
  if (kind === 'failed' || kind === 'merge_failed' || kind === 'declined') return 'danger';
  if (kind === 'in_progress' || kind === 'awaiting_review' || kind === 'merging') return 'informative';
  return 'subtle';
}

export function OverviewPage() {
  const styles = useStyles();
  const [data, setData] = useState<OverviewDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [appUsage, setAppUsage] = useState<AppUsage | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    if (!signal.cancelled) setRefreshing(true);
    try {
      const dto = await apiClient.getOverview();
      if (!signal.cancelled) {
        setData(dto);
        // Use token_usage embedded in the overview response when present.
        if (dto.token_usage) setAppUsage(dto.token_usage);
        setLastUpdated(new Date().toISOString());
        setError(null);
      }
    } catch (err) {
      if (!signal.cancelled) setError(formatError(err));
    } finally {
      if (!signal.cancelled) {
        setLoading(false);
        setRefreshing(false);
      }
    }

    // Separately fetch app-level usage from the dedicated endpoint.
    // This endpoint is admin-only; a 403 means the section is hidden (no error shown).
    try {
      const usage = await apiClient.getAppUsage();
      if (!signal.cancelled) setAppUsage(usage);
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        // Admin-only — degrade gracefully.
      }
    }
  }, []);

  useEffect(() => {
    const signal = { cancelled: false };
    setLoading(true);
    void load(signal);
    const iv = setInterval(() => { void load(signal); }, REFRESH_MS);
    return () => {
      signal.cancelled = true;
      clearInterval(iv);
    };
  }, [load]);

  const glanceCards = data
    ? [
        { label: 'In flight', value: data.at_a_glance.in_flight },
        { label: 'Queued work', value: data.at_a_glance.queued_work },
        { label: 'Done today', value: data.at_a_glance.done_today },
        { label: 'Active projects', value: data.at_a_glance.active_projects },
      ]
    : [];

  const health = data?.at_a_glance.health;
  const topUsageProjects = appUsage
    ? [...appUsage.by_project].sort((a, b) => b.total_nano_aiu - a.total_nano_aiu).slice(0, 4)
    : [];
  const maxProjectAic = Math.max(1, ...topUsageProjects.map((p) => p.total_nano_aiu));

  return (
    <div className={styles.root}>
      <PageHeader
        title="Overview"
        subtitle="Fleet activity at a glance."
        actions={
          <>
            {lastUpdated && (
              <RefreshCountdown
                className={styles.generated}
                intervalMs={REFRESH_MS}
                lastRefreshedAt={lastUpdated ? new Date(lastUpdated) : null}
                refreshing={refreshing}
              />
            )}
            {refreshing && data && <Spinner size="extra-tiny" aria-label="Refreshing" />}
            <Button
              appearance="secondary"
              icon={<ArrowSyncRegular />}
              disabled={loading}
              onClick={() => { setLoading(true); void load({ cancelled: false }); }}
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

      {loading && !data && <Spinner label="Loading overview" />}

      {data && (
        <>
          <div className={styles.cards}>
            {glanceCards.map((c) => (
              <div key={c.label} className={styles.card}>
                <Text className={styles.cardLabel}>{c.label}</Text>
                <Text className={styles.cardValue}>{c.value}</Text>
              </div>
            ))}
            <div className={styles.card}>
              <Text className={styles.cardLabel}>Health</Text>
              <Badge
                appearance="filled"
                color={health === 'healthy' ? 'success' : 'danger'}
              >
                {health ?? 'unknown'}
              </Badge>
            </div>
          </div>

          <div className={styles.section}>
            <Title3>Live sessions</Title3>
            {data.live_sessions.length === 0 ? (
              <Text>No active sessions right now.</Text>
            ) : (
              <Table aria-label="Live sessions" size="small">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Project</TableHeaderCell>
                    <TableHeaderCell>Agent</TableHeaderCell>
                    <TableHeaderCell>Status</TableHeaderCell>
                    <TableHeaderCell>Started</TableHeaderCell>
                    <TableHeaderCell>Last activity</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.live_sessions.map((s, i) => (
                    <TableRow key={`${s.project_id}-${i}`}>
                      <TableCell>
                        <Link to={`/projects/${s.project_id}`} className={styles.projectLink}>{s.project_name}</Link>
                      </TableCell>
                      <TableCell>{s.agent ?? '—'}</TableCell>
                      <TableCell><Badge appearance="tint" color="informative">{s.status}</Badge></TableCell>
                      <TableCell>{new Date(s.started_utc).toLocaleString()}</TableCell>
                      <TableCell>{timeAgo(s.last_activity_utc)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </div>

          <div className={styles.section}>
            <Title3>Active workflow runs</Title3>
            {data.active_workflow_runs.length === 0 ? (
              <Text>No active workflow runs.</Text>
            ) : (
              <Table aria-label="Active workflow runs" size="small">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Project</TableHeaderCell>
                    <TableHeaderCell>Trigger</TableHeaderCell>
                    <TableHeaderCell>Status</TableHeaderCell>
                    <TableHeaderCell>Started</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.active_workflow_runs.map((r, i) => (
                    <TableRow key={`${r.project_id}-${i}`}>
                      <TableCell>
                        <Link to={`/projects/${r.project_id}`} className={styles.projectLink}>{r.project_name}</Link>
                      </TableCell>
                      <TableCell>{r.trigger}</TableCell>
                      <TableCell><Badge appearance="tint" color="informative">{r.status}</Badge></TableCell>
                      <TableCell>{new Date(r.started_utc).toLocaleString()}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </div>

          <div className={styles.section}>
            <Title3>Active projects</Title3>
            {data.active_projects.length === 0 ? (
              <Text>No active projects.</Text>
            ) : (
              <div className={styles.projectCards}>
                {data.active_projects.map((p) => (
                  <Link key={p.project_id} to={`/projects/${p.project_id}`} className={styles.projectCard}>
                    <Text className={styles.projectName}>{p.project_name}</Text>
                    <div className={styles.projectMeta}>
                      <Badge appearance="tint" color="informative">{p.active_count} active</Badge>
                      <Badge appearance="tint" color="subtle">{p.queued_count} queued</Badge>
                    </div>
                    {p.last_activity_utc && (
                      <Text className={styles.muted}>Last activity {timeAgo(p.last_activity_utc)}</Text>
                    )}
                  </Link>
                ))}
              </div>
            )}
          </div>

          <div className={styles.section}>
            <Title3>Recent activity</Title3>
            {data.recent_activity.length === 0 ? (
              <Text>No recent activity.</Text>
            ) : (
              <ul className={styles.activityList}>
                {data.recent_activity.map((a, i) => (
                  <li key={`${a.project_id}-${i}`} className={styles.activityRow}>
                    <Badge className={styles.activityKind} appearance="tint" color={kindColor(a.kind)}>{humanizeKind(a.kind)}</Badge>
                    <span className={styles.activityLabel}>
                      <Link to={`/projects/${a.project_id}`} className={styles.projectLink}>{a.project_name}</Link>
                      {' — '}{a.label}
                    </span>
                    <Text className={styles.muted}>{timeAgo(a.timestamp_utc)}</Text>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}

      {appUsage && (
        <>
          <div className={styles.section}>
            <Title3>Cost overview</Title3>
            <div className={styles.costDashboard}>
              <div className={styles.costHero}>
                <Text className={styles.cardLabel}>Total AICs</Text>
                <Text className={styles.costValue}>{formatAic(appUsage.total_nano_aiu)} AIC</Text>
                <Text className={styles.muted}>{appUsage.total_tokens.toLocaleString()} tokens across {appUsage.by_project.length} projects</Text>
              </div>
              <div className={styles.costBreakdown}>
                <Text className={styles.cardLabel}>Top project usage</Text>
                {topUsageProjects.length === 0 ? (
                  <Text>No project usage yet.</Text>
                ) : topUsageProjects.map((p) => (
                  <div key={p.project_id} className={styles.costBarRow}>
                    <Link to={`/projects/${p.project_id}`} className={styles.projectLink}>{p.project_name}</Link>
                    <div className={styles.costBarTrack} aria-hidden="true">
                      <div className={styles.costBarFill} style={{ width: `${Math.max(4, (p.total_nano_aiu / maxProjectAic) * 100)}%` }} />
                    </div>
                    <Text className={styles.muted}>{formatAic(p.total_nano_aiu)} AIC</Text>
                  </div>
                ))}
              </div>
            </div>

            <TokenUsagePanel usage={{
              input_tokens: appUsage.by_model.reduce((sum, model) => sum + model.input_tokens, 0),
              output_tokens: appUsage.by_model.reduce((sum, model) => sum + model.output_tokens, 0),
              total_tokens: appUsage.total_tokens,
              total_nano_aiu: appUsage.total_nano_aiu,
              by_model: appUsage.by_model,
            }} title="Token usage breakdown" />

            {appUsage.by_project.length > 0 && (
              <>
                <Title3>Usage by project</Title3>
                <Table aria-label="Usage by project" size="small">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Project</TableHeaderCell>
                      <TableHeaderCell className={styles.numericCell}>Total tokens</TableHeaderCell>
                      <TableHeaderCell className={styles.numericCell}>AICs</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {appUsage.by_project.map((p) => (
                      <TableRow key={p.project_id}>
                        <TableCell>
                          <Link to={`/projects/${p.project_id}`} className={styles.projectLink}>
                            {p.project_name}
                          </Link>
                        </TableCell>
                        <TableCell className={styles.numericCell}>{p.total_tokens.toLocaleString()}</TableCell>
                        <TableCell className={styles.numericCell}>{formatAic(p.total_nano_aiu)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}
