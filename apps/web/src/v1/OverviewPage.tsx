/**
 * Cross-project Overview in the 0.x layout: Operations card, Recent projects,
 * project metrics and usage, Recent runs, and Needs attention. Counts come from
 * the project list, project metrics, the newest runs, and your notifications. The
 * service reports metrics and usage per project only, so the metrics section
 * shows one chosen project and says so. Chief of Staff groups personal Priorities,
 * new chat, and private conversations without controlling project-owned views.
 */
import { Badge, Button, Select, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular, ClockRegular, FolderRegular, Pulse24Regular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { AttentionList } from './AttentionList';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading, ProjectRoleBadge } from './pageParts';
import { metricTiles, useProjectMetrics, type MetricsRangeKey } from './projectMetrics';
import { ProjectMetricsSections } from './ProjectMetricsSections';
import { useSharedRunAttention } from './RunAttentionContext';
import { RunList } from './RunList';
import { runListErrorMessage, useCrossProjectRunList } from './runListData';
import { ConversationList, NewChatPanel } from './ChiefOfStaffPanels';
import { useChiefOfStaffConversations, useChiefOfStaffPriorities } from './chiefOfStaff';
import { PrioritiesPanel } from './PrioritiesPanel';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXL },
  chiefOfStaff: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  headerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
  },
  cardHead: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  cardHeadIcon: { display: 'inline-flex', color: tokens.colorBrandForeground1 },
  cardHeadTitle: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  glanceGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  glance: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  glanceLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  glanceValue: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  sectionHead: { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
  sectionTitle: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  link: { color: tokens.colorBrandForegroundLink, fontWeight: tokens.fontWeightSemibold, textDecorationLine: 'none', ':hover': { textDecorationLine: 'underline' } },
  projectList: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  projectGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(240px, .8fr) minmax(0, 1.6fr)',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  focusProject: {
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingVerticalXL,
    backgroundColor: tokens.colorNeutralBackground2,
    minWidth: 0,
  },
  projectCard: {
    minHeight: '72px',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) max-content',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
    '@media (max-width: 720px)': { gridTemplateColumns: '1fr', alignItems: 'start' },
  },
  projectIdentity: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, minWidth: 0 },
  iconBubble: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '32px',
    height: '32px',
    flexShrink: 0,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
  },
  projectName: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  stat: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  projectStats: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gridColumn: '1 / -1',
    gap: tokens.spacingHorizontalM,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  mainGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
});

const RECENT_PROJECT_LIMIT = 4;
const OVERVIEW_REFRESH_MS = 10_000;

interface OverviewProjectDetails {
  projectId: string;
  repository: string | null;
  activeRunCount: number | null;
  queuedTaskCount: number | null;
  runCount: number | null;
  agentCount: number | null;
  issueCount: number | null;
  unavailable: boolean;
}

/** Renders the signed-in user's cross-project home. */
export function OverviewPage() {
  const styles = useStyles();
  const location = useLocation();
  const { apiCall } = useAuth();
  // Shared with the rail bell, so a dismiss in either place shows in both.
  const attention = useSharedRunAttention();
  const conversations = useChiefOfStaffConversations();
  const priorities = useChiefOfStaffPriorities();
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  const [projectDetails, setProjectDetails] = useState<OverviewProjectDetails[]>([]);
  const [projectDetailsUnavailable, setProjectDetailsUnavailable] = useState(false);
  // Each project list load also reads the newest runs of every project.
  const runs = useCrossProjectRunList(projects, 5);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  // Project chosen for the metrics section, or null for the default project.
  const [metricsProjectId, setMetricsProjectId] = useState<string | null>(null);
  const [range, setRange] = useState<MetricsRangeKey>('30d');

  // Loads the project list. The project list is not bound to a project or run,
  // so the binding argument is null. On failure we keep the last good list on
  // screen and only show the error banner.
  const load = useCallback(async () => {
    setLoading(true);
    try {
      const nextProjects = await apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null);
      const recentProjects = nextProjects
        .filter((project) => project.state === 'active')
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
        .slice(0, RECENT_PROJECT_LIMIT);
      // These owner reads restore project-card counts without inventing totals.
      // Each read is independent so a partial failure does not hide other panels.
      const details = await Promise.all(recentProjects.map(async (project): Promise<OverviewProjectDetails> => {
        const [configuration, metrics, backlog] = await Promise.allSettled([
          apiCall((token, tenant) => gatewayClient.getProjectConfiguration(token, project.projectId, tenant), null),
          apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, project.projectId, {}, tenant), null),
          apiCall((token, tenant) => gatewayClient.readProjectBacklog(token, project.projectId, tenant), null),
        ]);
        const configurationData = configuration.status === 'fulfilled' ? configuration.value.configuration : null;
        const metricsData = metrics.status === 'fulfilled' ? metrics.value : null;
        const backlogData = backlog.status === 'fulfilled' ? backlog.value : null;
        const repository = configurationData?.sourceControl?.repository;
        return {
          projectId: project.projectId,
          repository: repository ? `${repository.owner}/${repository.name}` : null,
          activeRunCount: metricsData?.activeRuns ?? null,
          queuedTaskCount: backlogData?.tasks.filter((task) => !task.isArchived && task.state === 'ready').length ?? null,
          runCount: metricsData?.runsTotal ?? null,
          agentCount: configurationData?.agentCharters.length ?? null,
          issueCount: backlogData?.tasks.filter((task) => !task.isArchived && task.repositoryIssue != null).length ?? null,
          unavailable: configuration.status === 'rejected'
            || metrics.status === 'rejected'
            || backlog.status === 'rejected',
        };
      }));
      setProjects(nextProjects);
      setProjectDetails(details);
      setProjectDetailsUnavailable(details.some((detail) => detail.unavailable));
      setLoadedAt(new Date());
      setError(null);
    } catch (reason) {
      // Keep the last project list on failure so the page stays usable; only show the error.
      setError(reason);
    } finally {
      setLoading(false);
    }
  }, [apiCall]);

  useEffect(() => {
    // Start the first load on the next tick instead of inside the effect body.
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  useEffect(() => {
    // The page panels wait for projects; scroll after that gate mounts the linked section.
    if (location.hash !== '#chief-of-staff' || !projects) return;
    const target = document.getElementById('chief-of-staff');
    if (target && typeof target.scrollIntoView === 'function') target.scrollIntoView({ block: 'start' });
  }, [location.hash, projects]);

  // Only active projects count as recent. Archived projects are counted in the
  // Operations card but not listed. ISO timestamps sort correctly as strings,
  // so newest first is a reverse string compare.
  const active = (projects ?? []).filter((project) => project.state === 'active');
  const recent = [...active].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, RECENT_PROJECT_LIMIT);
  const focusProject = recent[0] ?? null;
  const detailsByProject = new Map(projectDetails.map((detail) => [detail.projectId, detail]));
  // Metrics show one active project. The default is the most recently updated
  // one. A chosen project that is no longer active falls back to the default.
  const chosen = active.some((project) => project.projectId === metricsProjectId) ? metricsProjectId : null;
  const shownProjectId = chosen ?? recent[0]?.projectId ?? null;
  const metrics = useProjectMetrics(shownProjectId, range);
  const tiles = metricTiles(metrics.metrics, range, metrics.loading);
  const reloadAttention = attention.reload;
  const reloadMetrics = metrics.reload;
  const reloadConversations = conversations.reload;
  const reloadPriorities = priorities.reload;
  const pendingInitialReads = attention.loading || metrics.loading || runs.loading
    || conversations.loading || priorities.loading;
  // Project names for the attention list. Unknown projects show their ID.
  const projectNames = new Map((projects ?? []).map((project) => [project.projectId, project.name]));
  // Refresh re-reads the project list, which also re-reads the runs, the
  // notifications, and the shown project's metrics.
  const refreshInFlightRef = useRef(false);
  const refresh = useCallback(async () => {
    if (refreshInFlightRef.current || loading || pendingInitialReads) return;
    refreshInFlightRef.current = true;
    setRefreshing(true);
    try {
      // Keep the cycle locked until every refreshed panel has settled.
      const results = await Promise.allSettled([
        load(),
        reloadAttention(),
        reloadMetrics(),
        reloadConversations(),
        reloadPriorities(),
      ]);
      const failedRead = results.find((result) => result.status === 'rejected');
      if (failedRead?.status === 'rejected') setError(failedRead.reason);
    } finally {
      refreshInFlightRef.current = false;
      setRefreshing(false);
    }
  }, [load, loading, pendingInitialReads, reloadAttention, reloadConversations, reloadMetrics, reloadPriorities]);
  const refreshRef = useRef(refresh);
  useEffect(() => { refreshRef.current = refresh; }, [refresh]);

  useEffect(() => {
    // Skip ticks during initial reads or until the previous full cycle settles.
    const interval = window.setInterval(() => {
      if (!refreshInFlightRef.current) void refreshRef.current();
    }, OVERVIEW_REFRESH_MS);
    return () => window.clearInterval(interval);
  }, []);

  return (
    <div className={styles.root}>
      <PageHeading
        title="Overview"
        description="Your projects, project metrics and usage, the newest runs, and what needs your input."
        breadcrumb={false}
        actions={(
          <div className={styles.headerActions}>
            {loadedAt && <Text className={styles.generated}>Updated {loadedAt.toLocaleTimeString()}</Text>}
            <Button
              appearance="subtle"
              icon={<ArrowSyncRegular />}
              disabled={loading || refreshing || pendingInitialReads}
              onClick={refresh}
            >
              Refresh
            </Button>
          </div>
        )}
      />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {/* Show the spinner only on the first load. A refresh keeps the old data visible. */}
      {loading && !projects && <Loading label="Loading overview" />}
      {projects && (
        <>
          {/* Personal planning stays separate from execution and project metrics. */}
          <section className={styles.chiefOfStaff} aria-labelledby="chief-of-staff">
            <Text as="h2" id="chief-of-staff" className={styles.sectionTitle}>Chief of Staff</Text>
            <PrioritiesPanel priorities={priorities} projects={projects} conversations={conversations} />
            <NewChatPanel projects={projects} onChanged={conversations.reload} />
            <ConversationList conversations={conversations} projectNames={projectNames} />
          </section>
          <section className={styles.card} aria-labelledby="overview-operations">
            <div className={styles.cardHead}>
              <span className={styles.cardHeadIcon}><Pulse24Regular /></span>
              <Text as="h2" id="overview-operations" className={styles.cardHeadTitle}>Operations</Text>
              <Badge appearance="tint" color="subtle">Health unavailable</Badge>
            </div>
            <Text className={styles.muted}>In-flight, queued-work, and done-today totals and service health need GET /api/v1/overview, which is not published.</Text>
            <div className={styles.glanceGrid}>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>In flight</Text>
                <Text className={styles.glanceValue}>—</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Queued work</Text>
                <Text className={styles.glanceValue}>—</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Done today</Text>
                <Text className={styles.glanceValue}>—</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Active projects</Text>
                <Text className={styles.glanceValue}>{active.length}</Text>
              </div>
            </div>
          </section>
          <section className={styles.card} aria-labelledby="overview-projects">
            <div className={styles.sectionHead}>
              <Text as="h2" id="overview-projects" className={styles.sectionTitle}>Recent projects</Text>
              <Link className={styles.link} to="/projects">View all projects</Link>
            </div>
            {projectDetailsUnavailable && <Text role="status" className={styles.muted}>Some project counts could not load. Refresh to try again.</Text>}
            {recent.length === 0 ? (
              <Text className={styles.muted}>{projects.length === 0 ? 'No projects yet. Create one in Projects.' : 'No active projects.'}</Text>
            ) : (
              <div className={styles.projectGrid}>
                <div className={styles.focusProject}>
                  <Text as="h3" className={styles.sectionTitle}>Current focus</Text>
                  {focusProject ? (
                    <>
                      <div>
                        <Text className={styles.projectName}>{focusProject.name}</Text>
                        <Text className={styles.muted}>Most recently updated active project</Text>
                      </div>
                      <Link className={styles.link} to={`/projects/${encodeURIComponent(focusProject.projectId)}`}>Open focus project</Link>
                    </>
                  ) : <Text className={styles.muted}>No active project is available.</Text>}
                </div>
                <div className={styles.projectList}>
                  {recent.map((project) => {
                    const details = detailsByProject.get(project.projectId);
                    const status = details?.activeRunCount == null || details.queuedTaskCount == null
                      ? 'Unavailable'
                      : details.activeRunCount > 0 ? 'Active' : details.queuedTaskCount > 0 ? 'Queued' : 'Idle';
                    const statusColor = status === 'Active' ? 'success' : status === 'Queued' ? 'warning' : 'subtle';
                    return (
                      <Link key={project.projectId} to={`/projects/${encodeURIComponent(project.projectId)}`} className={styles.projectCard}>
                        <div className={styles.projectIdentity}>
                          <span className={styles.iconBubble}><FolderRegular /></span>
                          <div>
                            <Text className={styles.projectName}>{project.name}</Text>
                            {details?.repository && <Text className={styles.muted}>{details.repository}</Text>}
                          </div>
                        </div>
                        <ProjectRoleBadge roles={project.effectiveRoles} />
                        <Badge appearance="tint" color={statusColor}>{status}</Badge>
                        <div className={styles.projectStats} aria-label={`${project.name} project counts`}>
                          <span>Agents {details?.agentCount ?? '—'}</span>
                          <span>Runs {details?.runCount ?? '—'}</span>
                          <span>Issues {details?.issueCount ?? '—'}</span>
                          <span className={styles.stat}><ClockRegular />Updated {new Date(project.updatedAt).toLocaleString()}</span>
                        </div>
                      </Link>
                    );
                  })}
                </div>
              </div>
            )}
          </section>
          <section className={styles.card} aria-labelledby="overview-metrics">
            <div className={styles.sectionHead}>
              <Text as="h2" id="overview-metrics" className={styles.sectionTitle}>AI usage &amp; performance</Text>
              {shownProjectId && <Link className={styles.link} to={`/projects/${encodeURIComponent(shownProjectId)}`}>Open project dashboard</Link>}
            </div>
            <Text className={styles.muted}>The Gateway has no GET /api/v1/overview route for aggregate metrics or usage, so this section shows one project at a time.</Text>
            {!shownProjectId ? <Text className={styles.muted}>Metrics appear when you have an active project.</Text> : (
              <>
                <div className={styles.glanceGrid} aria-label="Project metric summary">
                  {tiles.map((tile) => (
                    <div key={tile.key} className={styles.glance}>
                      <Text className={styles.glanceLabel}>{tile.label}</Text>
                      <Text className={styles.glanceValue}>{tile.value}</Text>
                      <Text className={styles.glanceLabel}>{tile.meta}</Text>
                    </div>
                  ))}
                </div>
                <ProjectMetricsSections
                  data={metrics}
                  range={range}
                  onRangeChange={setRange}
                  controls={(
                    <div className={styles.headerActions}>
                      <Text>Project</Text>
                      <Select
                        value={shownProjectId}
                        onChange={(_event, data) => setMetricsProjectId(data.value)}
                        aria-label="Metrics project"
                        size="small"
                      >
                        {active.map((project) => <option key={project.projectId} value={project.projectId}>{project.name}</option>)}
                      </Select>
                    </div>
                  )}
                />
              </>
            )}
          </section>
          <div className={styles.mainGrid}>
            <div>
              {runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{runListErrorMessage(runs.error)}</ErrorNotice>}
              <RunList
                title="Activity feed"
                description="Recent run summaries across your projects. The Gateway has no GET /api/v1/overview route with a recentActivity feed."
                data={runs}
                emptyText={runs.loading ? 'Loading runs…' : 'No runs yet.'}
                projectNames={runs.projectNames}
                limit={8}
              />
              <Link className={styles.link} to="/sessions">View all sessions</Link>
            </div>
            <section className={styles.card} aria-labelledby="overview-attention">
              <Text as="h2" id="overview-attention" className={styles.sectionTitle}>Needs attention</Text>
              <AttentionList attention={attention} projectNames={projectNames} />
            </section>
          </div>
        </>
      )}
    </div>
  );
}
