import { fromDto } from '../api/agentQueues';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { AgentAvatar } from '../components/AgentAvatar';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import {
  AppCard,
  EmptyState,
  PageContainer,
  PageHeader,
  PageSection,
} from '../components/ui';
import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
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
    color: tokens.colorNeutralForeground1,
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
    alignItems: 'stretch',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    height: '100%',
    minWidth: 0,
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
  // Task preview line — clamped to 2 lines so a long/raw goal string can never blow
  // out card height or repeat identically across every agent's tile unbounded.
  taskPreview: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
  },
  moreCount: {
    color: tokens.colorNeutralForeground3,
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
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
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
    marginTop: 'auto',
  },
  runLink: {
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    fontSize: tokens.fontSizeBase200,
  },
  filterNote: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground2,
  },
  workbenchSurface: {
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
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
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
  },
  summaryValue: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  archivePanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
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

// Renders up to 2 sample task titles, clamped to 2 lines each, with a "+N more" tail
// instead of an unbounded raw list — long/duplicate goal strings from thin backend
// data can never blow out card height this way.
function TaskPreview({ titles, styles }: { titles: string[]; styles: ReturnType<typeof useStyles> }) {
  if (titles.length === 0) return null;
  const visible = titles.slice(0, 2);
  const extra = titles.length - visible.length;
  return (
    <>
      {visible.map((title, i) => (
        <p key={i} className={styles.taskPreview} title={title}>{title}</p>
      ))}
      {extra > 0 && <span className={styles.moreCount}>+{extra} more task{extra === 1 ? '' : 's'}</span>}
    </>
  );
}

function AgentCard({ agent, projectId }: { agent: AgentQueueItem; projectId: string }) {
  const styles = useStyles();
  const hasGroups = agent.orchestrations && agent.orchestrations.length > 0;
  // Per-orchestration badges only earn their place once there's more than one group —
  // with a single orchestration they just repeat the card-level totals above.
  const showOrchestrationBadges = agent.orchestrations.length > 1;
  return (
    <AppCard className={styles.card}>
      <div className={styles.cardHeader}>
        <AgentAvatar name={agent.agentName} size={24} />
        <span className={styles.agentName}>{agent.agentName}</span>
      </div>

      <div className={styles.badges}>
        {agent.active > 0 && <Badge appearance="tint" color="subtle">{agent.active} active</Badge>}
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
              <span className={styles.orchestrationTitle} title={orch.title ?? undefined}>
                {orch.title ?? `Orchestration ${orch.runId.slice(0, 8)}`}
              </span>
              {showOrchestrationBadges && (
                <div className={styles.orchestrationBadges}>
                  {orch.active > 0 && <Badge appearance="tint" color="subtle">{orch.active} active</Badge>}
                  {orch.queued > 0 && <Badge appearance="tint" color="subtle">{orch.queued} queued</Badge>}
                  {orch.blocked > 0 && <Badge appearance="tint" color="danger">{orch.blocked} blocked</Badge>}
                  {orch.done > 0 && <Badge appearance="tint" color="success">{orch.done} done</Badge>}
                </div>
              )}
              {orch.sampleTitles && <TaskPreview titles={orch.sampleTitles} styles={styles} />}
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
          {agent.sampleTitles && <TaskPreview titles={agent.sampleTitles} styles={styles} />}

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
    </AppCard>
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
    let cancelled = false;
    const loadHistory = async () => {
      if (!projectId || !selectedAgent) {
        setHistory([]);
        setHistoryError(null);
        setHistoryLoading(false);
        return;
      }
      setHistoryLoading(true);
      setHistoryError(null);
      try {
        const result = await apiClient.getProjectRuns(projectId, {
          agentName: selectedAgent,
          terminalOnly: true,
          includeChildren: true,
          pageSize: 20,
        });
        if (!cancelled) setHistory(result.items);
      } catch (err) {
        if (!cancelled) setHistoryError(formatError(err));
      } finally {
        if (!cancelled) setHistoryLoading(false);
      }
    };
    void loadHistory();

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
  const totals = visibleAgents.reduce(
    (acc, agent) => ({
      active: acc.active + agent.active,
      queued: acc.queued + agent.queued,
      blocked: acc.blocked + agent.blocked,
      done: acc.done + agent.done,
    }),
    { active: 0, queued: 0, blocked: 0, done: 0 },
  );

  if (!projectId) return null;

  return (
    <PageContainer>
      <PageHeader
        title="Flow"
        description={selectedAgent
          ? `Live work and terminal-run archive for ${selectedAgent}.`
          : 'What each agent is working on right now.'}
        breadcrumbs={
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
              appearance="outline"
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
          <Badge appearance="tint" color="subtle">Agent filter</Badge>
          <Text>{selectedAgent}</Text>
          <Link to={`/projects/${projectId}/flow`} className={styles.runLink}>Clear filter</Link>
        </div>
      )}

      {!loading && !error && visibleAgents.length === 0 && (
        <EmptyState
          title={selectedAgent ? `No active work for ${selectedAgent}` : 'No active agents'}
          description={
            selectedAgent
              ? 'This agent has no current in-flight subtasks. Its completed work remains in the archive below.'
              : 'No agents are currently working in this project. Start an orchestration to see live activity here.'
          }
        />
      )}

      {visibleAgents.length > 0 && (
        <PageSection
          title={selectedAgent ? `${selectedAgent} workbench` : 'Live agent workbench'}
          description={selectedAgent ? 'Filtered to one agent.' : 'All active agents in this project.'}
        >
          <div className={styles.statusPills}>
            <span className={styles.statusPill}>Active: {totals.active}</span>
            <span className={styles.statusPill}>Queued: {totals.queued}</span>
            <span className={styles.statusPill}>Blocked: {totals.blocked}</span>
          </div>
          <div className={styles.summaryGrid}>
            <div className={styles.summaryCard}>
              <Text className={styles.summaryLabel}>Agents</Text>
              <Text className={styles.summaryValue}>{visibleAgents.length}</Text>
            </div>
            <div className={styles.summaryCard}>
              <Text className={styles.summaryLabel}>Active</Text>
              <Text className={styles.summaryValue}>{totals.active}</Text>
            </div>
            <div className={styles.summaryCard}>
              <Text className={styles.summaryLabel}>Done</Text>
              <Text className={styles.summaryValue}>{totals.done}</Text>
            </div>
          </div>
          <div className={styles.list}>
            {visibleAgents.map((agent) => (
              <AgentCard key={agent.agentName} agent={agent} projectId={projectId} />
            ))}
          </div>
        </PageSection>
      )}

      {selectedAgent && (
        <div role="region" aria-label="Previous work archive" className={styles.archivePanel}>
          <PageSection
            title="Previous work"
            description={`Terminal runs for ${selectedAgent}: completed, merged, assemble-ready, declined, failed, and merge-failed work.`}
          >
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
                    <Text>{run.task || `Run ${run.execution_id.slice(0, 8)}`}</Text>
                    <div className={styles.archiveMeta}>
                      <Badge appearance="tint" color={terminalStatusColor(run.status)}>{run.status}</Badge>
                      <span>{formatEndedAt(run)}</span>
                      {run.model_id && <span>{run.model_id}</span>}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </PageSection>
        </div>
      )}
    </PageContainer>
  );
}
