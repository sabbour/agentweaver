/**
 * Project pages that the 0.x client had as Flow, Workflows, Workspace,
 * Observability, Diagnostics, Heartbeat, and Cluster. Each page shows the parts
 * that current project reads can drive (runs, notifications, backlog, metrics,
 * usage, configuration, recorded pull requests, synced issues, and platform
 * components). A short note on each page names what this version cannot show
 * yet. No page shows placeholder data or controls that do nothing.
 */
import { Badge, Button, MessageBar, MessageBarBody, MessageBarTitle, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useMemo, useState, type ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { PageSection } from '../components/ui/PageSection';
import { AttentionList } from './AttentionList';
import { boardColumns, boardErrorMessage, useProjectBoard } from './boardData';
import { GatewayError } from './api';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
import { PlatformComponentsSection, usePlatformComponentReport } from './PlatformSettingsPage';
import { useProjectConfiguration, useProjectIssues, useProjectPullRequests } from './projectData';
import { metricTiles, useProjectMetrics, type MetricsRangeKey } from './projectMetrics';
import { ProjectMetricsSections } from './ProjectMetricsSections';
import { RunList } from './RunList';
import { useSharedRunAttention } from './RunAttentionContext';
import { runListErrorMessage, useProjectRunList } from './runListData';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXL },
  tiles: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(120px, 1fr))',
    gap: tokens.spacingHorizontalM,
    margin: 0,
    '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  tileLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, margin: 0 },
  tileValue: {
    margin: 0,
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  tileMeta: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, margin: 0, padding: 0, listStyle: 'none' },
  row: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Project base path for links on these pages. */
function projectBase(projectId: string): string {
  return `/projects/${encodeURIComponent(projectId)}`;
}

/**
 * Plain message for a failed project read. 403 and 404 get fixed sentences;
 * other errors use the service message.
 */
function readErrorMessage(error: unknown, subject: string): string {
  if (error instanceof GatewayError && error.status === 403) return `You do not have permission to read ${subject} in this project.`;
  if (error instanceof GatewayError && error.status === 404) return 'This project was not found. It may have been deleted.';
  return errorMessage(error);
}

/** Info note that names the parts of a page this version cannot show. */
function NotInThisVersion({ children }: { children: ReactNode }) {
  return (
    <MessageBar intent="info">
      <MessageBarBody>
        <MessageBarTitle>Not available in this version</MessageBarTitle>
        {children}
      </MessageBarBody>
    </MessageBar>
  );
}

/** Refresh button for a page heading. Disabled while the page's first read runs. */
function RefreshButton({ loading, onClick }: { loading: boolean; onClick: () => void }) {
  return <Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={onClick}>Refresh</Button>;
}

/** Four run metric tiles for the selected range, in the Dashboard order. */
function MetricTiles({ projectId, range }: { projectId: string; range: MetricsRangeKey }) {
  const styles = useStyles();
  const metrics = useProjectMetrics(projectId || null, range);
  // Tiles show dashes while loading or when metrics fail; the error shows above them.
  const tiles = metricTiles(metrics.metrics, range, metrics.loading);
  return (
    <>
      {metrics.metricsError != null && <ErrorNotice code={errorCode(metrics.metricsError)}>{readErrorMessage(metrics.metricsError, 'run metrics')}</ErrorNotice>}
      <dl className={styles.tiles} aria-label="Run summary">
        {tiles.map((tile) => (
          <div key={tile.key} className={styles.tile}>
            <dt className={styles.tileLabel}>{tile.label}</dt>
            <dd className={styles.tileValue}>{tile.value}</dd>
            <dd className={styles.tileMeta}>{tile.meta}</dd>
          </div>
        ))}
      </dl>
    </>
  );
}

/**
 * Task counts for each Board column, read from the project backlog. Uses the
 * same column rules as the Board, so archived tasks are not counted and closed
 * tasks count as Done. A failed read shows the Board error copy instead of
 * zeros, because zero would claim the queue is empty.
 */
function TaskStages({ projectId, board }: { projectId: string; board: ReturnType<typeof useProjectBoard> }) {
  const styles = useStyles();
  const columns = useMemo(() => (board.graph ? boardColumns(board.graph) : null), [board.graph]);
  let body: ReactNode;
  // Order: first load, then a read error without data, then the counts.
  if (board.loading && !columns) body = <Loading label="Loading task stages" />;
  else if (board.error != null && !columns)
    body = <ErrorNotice code={errorCode(board.error)}>{boardErrorMessage(board.error, 'read')}</ErrorNotice>;
  else if (columns) body = (
    <>
      {board.error != null && <ErrorNotice code={errorCode(board.error)}>{boardErrorMessage(board.error, 'read')}</ErrorNotice>}
      <dl className={styles.tiles} aria-label="Tasks by stage">
        {columns.map((column) => (
          <div key={column.id} className={styles.tile}>
            <dt className={styles.tileLabel}>{column.label}</dt>
            <dd className={styles.tileValue}>{column.tasks.length}</dd>
            <dd className={styles.tileMeta}>{column.description}</dd>
          </div>
        ))}
      </dl>
    </>
  );
  return (
    <PageSection
      title="Task stages"
      description="Tasks in each Board column."
      actions={<Link to={`${projectBase(projectId)}/board`}>Open Board</Link>}
    >
      {body}
    </PageSection>
  );
}

/**
 * Flow: what is moving in the project right now. Shows notifications for this
 * project (questions, approvals, promoted tasks), task counts for each Board
 * column, and the open runs. The 0.x live agent graph needs a read this version
 * does not have.
 */
export function FlowPage() {
  const { projectId = '' } = useParams();
  const attention = useSharedRunAttention();
  const runs = useProjectRunList(projectId, { states: ['open'], pageSize: 25 });
  // The shared notification list covers every project. Keep only this project's
  // items and count, so the empty state and the "more" hint are about this project.
  const projectAttention = useMemo(() => {
    const items = attention.items.filter((item) => item.projectId === projectId);
    return { ...attention, items, count: items.length };
  }, [attention, projectId]);
  const board = useProjectBoard(projectId);
  const refresh = () => { attention.reload(); runs.reload(); board.reload(); };
  return (
    <>
      <PageHeading
        title="Flow"
        description="Work that is running or waiting for an answer in this project."
        page="Flow"
        actions={<RefreshButton loading={runs.loading && runs.items.length === 0} onClick={refresh} />}
      />
      <PageSection title="Needs attention" description="Questions, approvals, and new tasks from runs in this project.">
        <AttentionList attention={projectAttention} />
      </PageSection>
      <TaskStages projectId={projectId} board={board} />
      {runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{runListErrorMessage(runs.error)}</ErrorNotice>}
      <RunList
        title="Open runs"
        description="Runs that are working, idle, or blocked. Open a run to see its session graph."
        data={runs}
        emptyText={runs.loading ? 'Loading runs…' : 'No runs are open.'}
      />
      <NotInThisVersion>
        The live agent graph for the whole project is not available. Open a run to see its session graph.
      </NotInThisVersion>
    </>
  );
}

/**
 * Workflows: the workflow references saved in the project configuration. They
 * are read-only here; Project settings changes them. The 0.x workflow catalog,
 * graph editor, and sync need reads and writes this version does not have.
 */
export function WorkflowsPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const configuration = useProjectConfiguration(projectId);
  const config = configuration.data?.configuration;
  const references = config?.blueprintWorkflowReferences ?? [];
  let body: ReactNode;
  // Order: first load, then error, then the saved references.
  if (configuration.loading && !config) body = <Loading label="Loading workflow references" />;
  else if (configuration.error != null && !config)
    body = <ErrorNotice code={errorCode(configuration.error)}>{readErrorMessage(configuration.error, 'the configuration')}</ErrorNotice>;
  else if (config) body = (
    <>
      <div className={styles.row}>
        <Text weight="semibold">Default workflow</Text>
        {config.defaultWorkflowId ? <code>{config.defaultWorkflowId}</code> : <Text className={styles.muted}>None set. Runs use the platform default.</Text>}
      </div>
      {references.length === 0 ? <Text>No blueprint uses a specific workflow.</Text> : (
        <div className="v1-table-wrap">
          <table className="v1-table">
            <caption>Workflow by blueprint</caption>
            <thead><tr><th scope="col">Blueprint</th><th scope="col">Workflow</th></tr></thead>
            <tbody>
              {references.map((reference) => (
                <tr key={reference.blueprintId}>
                  <th scope="row"><code>{reference.blueprintId}</code></th>
                  <td><code>{reference.workflowId}</code></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {configuration.data && <Text className={styles.muted}>Configuration revision {configuration.data.revision}</Text>}
    </>
  );
  return (
    <>
      <PageHeading
        title="Workflows"
        description="Workflow choices saved for this project. New runs use them."
        page="Workflows"
        actions={<RefreshButton loading={configuration.loading && !config} onClick={configuration.reload} />}
      />
      <PageSection
        title="Saved workflow references"
        actions={<Link className="v1-link-button" to={`${projectBase(projectId)}/settings`}>Open Project settings</Link>}
      >
        {body}
      </PageSection>
      <NotInThisVersion>
        The workflow catalog, the workflow graph, workflow sync, and per-task workflow overrides are not available.
      </NotInThisVersion>
    </>
  );
}

/** Badge color for a pull request or issue state. */
function stateColor(state: string): 'success' | 'informative' | 'subtle' {
  if (state === 'open') return 'success';
  if (state === 'merged') return 'informative';
  return 'subtle';
}

/**
 * Workspace: the project repository, the pull requests its runs recorded, and
 * the open issues synced into the backlog. Both lists come from stored records,
 * so titles and states can be out of date. Browsing files needs reads this
 * version does not have.
 */
export function WorkspacePage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const configuration = useProjectConfiguration(projectId);
  const pulls = useProjectPullRequests(projectId);
  const issues = useProjectIssues(projectId);
  const repository = configuration.data?.configuration.sourceControl?.repository;
  const refresh = () => { configuration.reload(); pulls.reload(); issues.reload(); };

  let pullBody: ReactNode;
  // A 503 with this code means the server has not started recording pull requests.
  if (pulls.loading && !pulls.data) pullBody = <Loading label="Loading pull requests" />;
  else if (pulls.error != null && errorCode(pulls.error) === 'pull_request_records_unavailable')
    pullBody = <Text>This server does not record pull requests yet.</Text>;
  else if (pulls.error != null) pullBody = <ErrorNotice code={errorCode(pulls.error)}>{readErrorMessage(pulls.error, 'pull requests')}</ErrorNotice>;
  else if (pulls.data) pullBody = pulls.data.items.length === 0 ? <Text>No run has opened a pull request yet.</Text> : (
    <>
      <ul className={styles.list} aria-label="Recorded pull requests">
        {pulls.data.items.map((pull) => (
          <li key={`${pull.repository.owner}/${pull.repository.name}#${pull.number}`} className={styles.row}>
            <Badge appearance="tint" color={stateColor(pull.state)}>{pull.state}</Badge>
            <a href={pull.htmlUrl} target="_blank" rel="noreferrer">#{pull.number} {pull.title ?? `${pull.headBranch} → ${pull.baseBranch}`}</a>
            <Link to={`${projectBase(projectId)}/runs/${encodeURIComponent(pull.runId)}`}>Open run</Link>
          </li>
        ))}
      </ul>
      <Text className={styles.muted}>Recorded since {new Date(pulls.data.coverage.recordedSince).toLocaleDateString()}. Earlier pull requests are not listed.</Text>
    </>
  );

  let issueBody: ReactNode;
  if (issues.loading && !issues.data) issueBody = <Loading label="Loading issues" />;
  else if (issues.error != null) issueBody = <ErrorNotice code={errorCode(issues.error)}>{readErrorMessage(issues.error, 'issues')}</ErrorNotice>;
  else if (issues.data) issueBody = issues.data.items.length === 0 ? <Text>No open issues are synced into the backlog.</Text> : (
    <ul className={styles.list} aria-label="Synced issues">
      {issues.data.items.map((issue) => (
        <li key={issue.taskId} className={styles.row}>
          <Badge appearance="tint" color={stateColor(issue.state)}>{issue.state}</Badge>
          <Text>#{issue.number} {issue.title}</Text>
          <Text className={styles.muted}>{issue.repository.owner}/{issue.repository.name} · task {issue.taskState}</Text>
        </li>
      ))}
    </ul>
  );

  return (
    <>
      <PageHeading
        title="Workspace"
        description="The project repository and the pull requests and issues linked to it."
        page="Workspace"
        actions={<RefreshButton loading={configuration.loading && !configuration.data} onClick={refresh} />}
      />
      <PageSection title="Repository">
        {configuration.loading && !configuration.data ? <Loading label="Loading repository" />
          : configuration.error != null && !configuration.data
            ? <ErrorNotice code={errorCode(configuration.error)}>{readErrorMessage(configuration.error, 'the configuration')}</ErrorNotice>
            : repository ? <Text><code>{repository.owner}/{repository.name}</code></Text>
              : <Text>No repository is connected. <Link to={`${projectBase(projectId)}/settings`}>Connect one in Project settings</Link>.</Text>}
      </PageSection>
      <PageSection title="Pull requests" description="Pull requests that runs in this project opened or reused, newest first.">
        {pullBody}
      </PageSection>
      <PageSection title="Issues" description="Open repository issues synced into the project backlog.">
        {issueBody}
      </PageSection>
      <NotInThisVersion>
        Browsing branches and repository files is not available. A run shows its own changes in its Changes tab.
      </NotInThisVersion>
    </>
  );
}

/**
 * Observability: run counts, the activity chart, and model and agent usage for
 * a range, plus the newest runs. Per-run traces and response-time percentiles
 * need reads this version does not have.
 */
export function ProjectObservabilityPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const [range, setRange] = useState<MetricsRangeKey>('30d');
  const metrics = useProjectMetrics(projectId || null, range);
  const runs = useProjectRunList(projectId, { pageSize: 10 });
  const configuration = useProjectConfiguration(projectId);
  // Usage rows name agents by ID; map them to the team's display names when known.
  const agentNames = useMemo(
    () => new Map((configuration.data?.configuration.agentCharters ?? []).map((agent) => [agent.agentId, agent.name])),
    [configuration.data],
  );
  return (
    <div className={styles.root}>
      <PageHeading
        title="Observability"
        description="Run activity, success, and token use for this project."
        page="Observability"
        actions={<RefreshButton loading={metrics.loading && !metrics.metrics} onClick={() => { metrics.reload(); runs.reload(); }} />}
      />
      <ProjectMetricsSections data={metrics} range={range} onRangeChange={setRange} agentNames={agentNames} />
      <div>
        {runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{runListErrorMessage(runs.error)}</ErrorNotice>}
        <RunList
          title="Recent runs"
          description="Open a run for its Activity and Usage views."
          data={runs}
          emptyText={runs.loading ? 'Loading runs…' : 'No runs yet.'}
          limit={10}
        />
      </div>
      <NotInThisVersion>
        Per-run traces and response time percentiles (p50 and p95) are not available.
      </NotInThisVersion>
    </div>
  );
}

/**
 * Diagnostics: platform areas that are not ready, and project runs that failed
 * or ended in an unknown state. Only tenant or platform administrators can read
 * the platform report; others see that message and still see the runs.
 */
export function DiagnosticsPage() {
  const { projectId = '' } = useParams();
  const platform = usePlatformComponentReport();
  const runs = useProjectRunList(projectId, { states: ['failed', 'indeterminate'], pageSize: 10 });
  return (
    <>
      <PageHeading
        title="Diagnostics"
        description="Platform areas that need attention and runs that did not finish cleanly."
        page="Diagnostics"
        actions={<RefreshButton loading={platform.loading && !platform.report} onClick={() => { void platform.load(); runs.reload(); }} />}
      />
      <PlatformComponentsSection
        state={platform}
        title="Areas that are not ready"
        description="Platform areas whose status is not Ready, with the reason the service reported."
        filter={(area) => area.readiness !== 'ready' && area.readiness !== 'disabled'}
        emptyText="Every enabled platform area reports Ready."
      />
      <div>
        {runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{runListErrorMessage(runs.error)}</ErrorNotice>}
        <RunList
          title="Failed runs"
          description="Runs that failed or whose end state is unknown. Open a run for its Activity view."
          data={runs}
          emptyText={runs.loading ? 'Loading runs…' : 'No failed runs.'}
        />
      </div>
      <NotInThisVersion>
        Service self-checks, detailed diagnostics, and project diagnostic reports are not available.
      </NotInThisVersion>
    </>
  );
}

/**
 * Heartbeat: a live summary of current work. Shows the run tiles for the last
 * seven days and the runs that are active or blocked now. Per-process heartbeat
 * records need a read this version does not have.
 */
export function HeartbeatPage() {
  const { projectId = '' } = useParams();
  const runs = useProjectRunList(projectId, { states: ['active', 'blocked'], pageSize: 25 });
  // Bumping the key remounts the tiles, which reads the metrics again.
  const [tilesKey, setTilesKey] = useState(0);
  return (
    <>
      <PageHeading
        title="Heartbeat"
        description="Runs that are working or blocked right now."
        page="Heartbeat"
        actions={<RefreshButton loading={runs.loading && runs.items.length === 0} onClick={() => { runs.reload(); setTilesKey((value) => value + 1); }} />}
      />
      <MetricTiles key={tilesKey} projectId={projectId} range="7d" />
      {runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{runListErrorMessage(runs.error)}</ErrorNotice>}
      <RunList
        title="Active and blocked runs"
        description="A blocked run is waiting for an answer or an approval."
        data={runs}
        emptyText={runs.loading ? 'Loading runs…' : 'No run is active or blocked.'}
      />
      <NotInThisVersion>
        Heartbeat records from each service process are not available.
      </NotInThisVersion>
    </>
  );
}

/**
 * Cluster: the platform component report, which lists each area, the service
 * that owns it, and its active implementations. Node and replica membership
 * needs a read this version does not have.
 */
export function ClusterPage() {
  const platform = usePlatformComponentReport();
  return (
    <>
      <PageHeading
        title="Cluster"
        description="Platform services for this deployment and their status."
        page="Cluster"
        actions={<RefreshButton loading={platform.loading && !platform.report} onClick={() => void platform.load()} />}
      />
      <PlatformComponentsSection state={platform} />
      <NotInThisVersion>
        Node and replica membership for the cluster is not available.
      </NotInThisVersion>
    </>
  );
}
