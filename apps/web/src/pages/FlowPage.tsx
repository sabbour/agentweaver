import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AgentAvatar } from '../components/AgentAvatar';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import { fromDto } from '../api/agentQueues';
import type { AgentQueueItem } from '../api/agentQueues';
import type { Project, WorkflowRunDto } from '../api/types';

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
  orchestrations: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  orchestrationGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingLeft: tokens.spacingHorizontalS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke2}`,
  },
  orchestrationTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
  },
  orchestrationBadges: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    alignItems: 'center',
    flexWrap: 'wrap',
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
  filterNote: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground2,
  },
  archivePanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  archiveList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  archiveItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
  },
  archiveMeta: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
});

function terminalStatusColor(status: string): 'success' | 'danger' | 'warning' | 'subtle' {
  switch (status) {
    case 'merged':
    case 'completed':
    case 'assemble_ready':
      return 'success';
    case 'failed':
    case 'merge_failed':
      return 'danger';
    case 'declined':
      return 'warning';
    default:
      return 'subtle';
  }
}

function formatEndedAt(run: WorkflowRunDto): string {
  const timestamp = run.ended_at ?? run.started_at;
  return new Date(timestamp).toLocaleString();
}

function AgentCard({ agent, projectId }: { agent: AgentQueueItem; projectId: string }) {
  const styles = useStyles();
  const hasGroups = agent.orchestrations && agent.orchestrations.length > 0;
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

      {hasGroups ? (
        <div className={styles.orchestrations}>
          {agent.orchestrations.map((orch) => (
            <div key={orch.runId} className={styles.orchestrationGroup}>
              <span className={styles.orchestrationTitle}>
                {orch.title ?? `Orchestration ${orch.runId.slice(0, 8)}`}
              </span>
              <div className={styles.orchestrationBadges}>
                {orch.active > 0 && <Badge appearance="tint" color="informative">{orch.active} active</Badge>}
                {orch.queued > 0 && <Badge appearance="tint" color="subtle">{orch.queued} queued</Badge>}
                {orch.blocked > 0 && <Badge appearance="tint" color="danger">{orch.blocked} blocked</Badge>}
                {orch.done > 0 && <Badge appearance="tint" color="success">{orch.done} done</Badge>}
              </div>
              {orch.sampleTitles && orch.sampleTitles.length > 0 && (
                <ul className={styles.titles}>
                  {orch.sampleTitles.map((title, i) => (
                    <li key={i}>{title}</li>
                  ))}
                </ul>
              )}
              <Link
                to={`/projects/${projectId}/orchestrations/${orch.runId}`}
                className={styles.runLink}
              >
                View orchestration
              </Link>
            </div>
          ))}
        </div>
      ) : (
        <>
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
        </>
      )}
    </div>
  );
}

export function FlowPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [searchParams] = useSearchParams();
  const selectedAgent = searchParams.get('agent')?.trim() ?? '';

  const [agents, setAgents] = useState<AgentQueueItem[]>([]);
  const [history, setHistory] = useState<WorkflowRunDto[]>([]);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastRefreshedAt, setLastRefreshedAt] = useState<number | null>(null);

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
          setLastRefreshedAt(Date.now());
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

  useEffect(() => {
    if (!projectId || !selectedAgent) {
      setHistory([]);
      setHistoryError(null);
      setHistoryLoading(false);
      return;
    }

    let cancelled = false;
    setHistoryLoading(true);
    setHistoryError(null);
    apiClient
      .getProjectRuns(projectId, {
        agentName: selectedAgent,
        terminalOnly: true,
        includeChildren: true,
        limit: 20,
      })
      .then((runs) => {
        if (!cancelled) setHistory(runs);
      })
      .catch((err) => {
        if (!cancelled) setHistoryError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setHistoryLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [projectId, selectedAgent]);

  const sorted = useMemo(
    () =>
      [...agents].sort(
        (a, b) =>
          (b.active * 4 + b.queued * 2 + b.blocked) - (a.active * 4 + a.queued * 2 + a.blocked),
      ),
    [agents],
  );

  const visibleAgents = useMemo(
    () =>
      selectedAgent
        ? sorted.filter((agent) => agent.agentName === selectedAgent)
        : sorted,
    [selectedAgent, sorted],
  );

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Flow"
        subtitle={selectedAgent
          ? `Live work and terminal-run archive for ${selectedAgent}.`
          : 'What each agent is working on right now.'}
        breadcrumb={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {project?.name ?? projectId}
            </Link>
            <span>/</span>
            <span>Flow</span>
          </div>
        }
        actions={
          <>
            {lastRefreshedAt != null && (
              <RefreshCountdown intervalMs={REFRESH_MS} lastRefreshedAt={lastRefreshedAt} refreshing={loading} />
            )}
            {loading && <Spinner size="extra-tiny" aria-label="Refreshing" />}
            <Button
              appearance="secondary"
              icon={<ArrowSyncRegular />}
              onClick={() => {
                setLoading(true);
                apiClient.getBoard(projectId)
                  .then((board) => { setAgents((board.agent_queues ?? []).map(fromDto)); setError(null); setLastRefreshedAt(Date.now()); })
                  .catch((err) => setError(formatError(err)))
                  .finally(() => setLoading(false));
              }}
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

      {selectedAgent && (
        <div className={styles.filterNote}>
          <Badge appearance="tint" color="informative">Agent filter</Badge>
          <Text>{selectedAgent}</Text>
          <Link to={`/projects/${projectId}/flow`} className={styles.runLink}>Clear filter</Link>
        </div>
      )}

      {!loading && !error && visibleAgents.length === 0 && (
        <div className={styles.emptyState}>
          <Title3>{selectedAgent ? `No active work for ${selectedAgent}` : 'No active agents'}</Title3>
          <Text>
            {selectedAgent
              ? 'This agent has no current in-flight subtasks. Its completed work remains in the archive below.'
              : 'No agents are currently working in this project. Start an orchestration to see live activity here.'}
          </Text>
        </div>
      )}

      {visibleAgents.length > 0 && (
        <div className={styles.list}>
          {visibleAgents.map((agent) => (
            <AgentCard key={agent.agentName} agent={agent} projectId={projectId} />
          ))}
        </div>
      )}

      {selectedAgent && (
        <section className={styles.archivePanel} aria-label="Previous work archive">
          <Title3>Previous work archive</Title3>
          <Text>
            Terminal runs for {selectedAgent}: completed, merged, assemble-ready, declined, failed, and merge-failed work.
          </Text>
          {historyLoading && <Spinner size="tiny" label="Loading previous work" />}
          {historyError && (
            <MessageBar intent="error">
              <MessageBarBody>{historyError}</MessageBarBody>
            </MessageBar>
          )}
          {!historyLoading && !historyError && history.length === 0 && (
            <Text>No terminal runs found for this agent.</Text>
          )}
          {history.length > 0 && (
            <div className={styles.archiveList}>
              {history.map((run) => (
                <div key={run.execution_id} className={styles.archiveItem}>
                  <Link
                    to={`/projects/${projectId}/runs/${run.workflow_run_id}/execution/${run.execution_id}`}
                    className={styles.runLink}
                  >
                    {run.task || `Run ${run.execution_id.slice(0, 8)}`}
                  </Link>
                  <div className={styles.archiveMeta}>
                    <Badge appearance="tint" color={terminalStatusColor(run.status)}>{run.status}</Badge>
                    <span>{formatEndedAt(run)}</span>
                    {run.model_id && <span>{run.model_id}</span>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </section>
      )}
    </div>
  );
}
