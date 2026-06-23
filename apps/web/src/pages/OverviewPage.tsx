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
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { OverviewDto } from '../api/types';

// Overview ("Now") — the global, cross-project live view at /overview (also the
// landing page at '/'). Consumes GET /api/overview (real data only; no cost).

const REFRESH_MS = 30000;

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
  },
  headerActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
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
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  activityLabel: { flex: 1, minWidth: 0 },
  muted: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  projectLink: { color: tokens.colorBrandForeground1, textDecoration: 'none' },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
});

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

export function OverviewPage() {
  const styles = useStyles();
  const [data, setData] = useState<OverviewDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    try {
      const dto = await apiClient.getOverview();
      if (!signal.cancelled) {
        setData(dto);
        setError(null);
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

  return (
    <div className={styles.root}>
      <div className={styles.pageHeader}>
        <Title2>Overview</Title2>
        <div className={styles.headerActions}>
          {data && (
            <Text className={styles.generated}>
              Updated {new Date(data.generated_utc).toLocaleTimeString()}
            </Text>
          )}
          <Button
            appearance="secondary"
            icon={<ArrowSyncRegular />}
            disabled={loading}
            onClick={() => { setLoading(true); void load({ cancelled: false }); }}
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
                    <Badge appearance="outline" color="subtle">{a.kind}</Badge>
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
    </div>
  );
}
