import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AgentAvatar } from '../components/AgentAvatar';
import { fromDto } from '../api/agentQueues';
import type { AgentQueueItem } from '../api/agentQueues';
import type { Project } from '../api/types';

// Flow — the live "what each agent is working on" view for a project. This is the
// home of live agent activity (moved out of the per-run coordinator page, which
// keeps only a compact per-run presence rail). Data comes from the project board's
// agent_queues aggregate (real data; no mocks). Auto-refreshes every 5s.

const REFRESH_MS = 5000;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  pageHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  list: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  agentName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  badges: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  titles: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    margin: 0,
    paddingLeft: tokens.spacingHorizontalL,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  runLinks: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  runLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
    fontSize: tokens.fontSizeBase200,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    textAlign: 'center',
  },
});

function AgentCard({ agent, projectId }: { agent: AgentQueueItem; projectId: string }) {
  const styles = useStyles();
  return (
    <div className={styles.card}>
      <div className={styles.cardHeader}>
        <AgentAvatar name={agent.agentName} size={24} />
        <span className={styles.agentName}>{agent.agentName}</span>
      </div>

      <div className={styles.badges}>
        {agent.active > 0 && <Badge appearance="tint" color="informative">{agent.active} active</Badge>}
        {agent.queued > 0 && <Badge appearance="tint" color="subtle">{agent.queued} queued</Badge>}
        {agent.blocked > 0 && <Badge appearance="tint" color="danger">{agent.blocked} blocked</Badge>}
        {agent.done > 0 && <Badge appearance="tint" color="success">{agent.done} done</Badge>}
        {agent.active === 0 && agent.queued === 0 && agent.blocked === 0 && (
          <Badge appearance="outline" color="subtle">Idle</Badge>
        )}
      </div>

      {agent.sampleTitles && agent.sampleTitles.length > 0 && (
        <ul className={styles.titles}>
          {agent.sampleTitles.map((title, i) => (
            <li key={i}>{title}</li>
          ))}
        </ul>
      )}

      {agent.runIds && agent.runIds.length > 0 && (
        <div className={styles.runLinks}>
          {agent.runIds.map((runId) => (
            <Link
              key={runId}
              to={`/projects/${projectId}/orchestrations/${runId}`}
              className={styles.runLink}
            >
              View orchestration
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}

export function FlowPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [agents, setAgents] = useState<AgentQueueItem[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;

    apiClient.getProject(projectId)
      .then((p) => { if (!cancelled) setProject(p); })
      .catch(() => {});

    const load = async () => {
      try {
        const board = await apiClient.getBoard(projectId);
        if (!cancelled) {
          setAgents((board.agent_queues ?? []).map(fromDto));
          setError(null);
        }
      } catch (err) {
        if (!cancelled) setError(formatError(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void load();
    const iv = setInterval(() => { void load(); }, REFRESH_MS);
    return () => {
      cancelled = true;
      clearInterval(iv);
    };
  }, [projectId]);

  const sorted = useMemo(
    () =>
      [...agents].sort(
        (a, b) =>
          (b.active * 4 + b.queued * 2 + b.blocked) - (a.active * 4 + a.queued * 2 + a.blocked),
      ),
    [agents],
  );

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
          {project?.name ?? projectId}
        </Link>
        <span>/</span>
        <span>Flow</span>
      </div>

      <div className={styles.pageHeader}>
        <div>
          <Title2>Flow</Title2>
          <Text as="p" className={styles.subtitle}>What each agent is working on right now.</Text>
        </div>
        <div className={styles.actions}>
          {loading && <Spinner size="extra-tiny" aria-label="Refreshing" />}
          <Button
            appearance="secondary"
            icon={<ArrowSyncRegular />}
            onClick={() => {
              setLoading(true);
              apiClient.getBoard(projectId)
                .then((board) => { setAgents((board.agent_queues ?? []).map(fromDto)); setError(null); })
                .catch((err) => setError(formatError(err)))
                .finally(() => setLoading(false));
            }}
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

      {!loading && !error && sorted.length === 0 && (
        <div className={styles.emptyState}>
          <Title3>No active agents</Title3>
          <Text>No agents are currently working in this project. Start an orchestration to see live activity here.</Text>
        </div>
      )}

      {sorted.length > 0 && (
        <div className={styles.list}>
          {sorted.map((agent) => (
            <AgentCard key={agent.agentName} agent={agent} projectId={projectId} />
          ))}
        </div>
      )}
    </div>
  );
}
