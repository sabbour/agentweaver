/**
 * Cross-project Overview in the 0.x layout: Operations card, Recent projects,
 * project metrics and usage, Recent runs, and Needs attention. Counts come from
 * the project list, the newest runs of each project, and your notifications. The
 * service reports metrics and usage per project only, so the metrics section
 * shows one chosen project and says so. Personal Priorities, new chat and private
 * conversations sit alongside these project-owned views without controlling them.
 */
import { Button, Select, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular, ClockRegular, FolderRegular, Pulse24Regular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { AttentionList } from './AttentionList';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
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
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  projectCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
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
  mainGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
});

const RECENT_PROJECT_LIMIT = 6;

/** Renders the signed-in user's cross-project home. */
export function OverviewPage() {
  const styles = useStyles();
  const { apiCall } = useAuth();
  // Shared with the rail bell, so a dismiss in either place shows in both.
  const attention = useSharedRunAttention();
  const conversations = useChiefOfStaffConversations();
  const priorities = useChiefOfStaffPriorities();
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  // Each project list load also reads the newest runs of every project.
  const runs = useCrossProjectRunList(projects, 5);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [loading, setLoading] = useState(true);
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
      setProjects(await apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null));
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

  // Only active projects count as recent. Archived projects are counted in the
  // Operations card but not listed. ISO timestamps sort correctly as strings,
  // so newest first is a reverse string compare.
  const active = (projects ?? []).filter((project) => project.state === 'active');
  const recent = [...active].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, RECENT_PROJECT_LIMIT);
  // Metrics show one active project. The default is the most recently updated
  // one. A chosen project that is no longer active falls back to the default.
  const chosen = active.some((project) => project.projectId === metricsProjectId) ? metricsProjectId : null;
  const shownProjectId = chosen ?? recent[0]?.projectId ?? null;
  const metrics = useProjectMetrics(shownProjectId, range);
  const tiles = metricTiles(metrics.metrics, range, metrics.loading);
  // Project names for the attention list. Unknown projects show their ID.
  const projectNames = new Map((projects ?? []).map((project) => [project.projectId, project.name]));
  // Refresh re-reads the project list, which also re-reads the runs, the
  // notifications, and the shown project's metrics.
  const refresh = () => { void load(); attention.reload(); metrics.reload(); conversations.reload(); void priorities.reload(); };

  return (
    <div className={styles.root}>
      <PageHeading
        title="Overview"
        description="Your projects, project metrics and usage, the newest runs, and what needs your input."
        breadcrumb={false}
        actions={(
          <div className={styles.headerActions}>
            {loadedAt && <Text className={styles.generated}>Updated {loadedAt.toLocaleTimeString()}</Text>}
            <Button appearance="subtle" icon={<ArrowSyncRegular />} disabled={loading} onClick={refresh}>Refresh</Button>
          </div>
        )}
      />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {/* Show the spinner only on the first load. A refresh keeps the old data visible. */}
      {loading && !projects && <Loading label="Loading overview" />}
      {projects && (
        <>
          {/* Personal planning stays separate from execution and project metrics. */}
          <PrioritiesPanel priorities={priorities} projects={projects} conversations={conversations} />
          <NewChatPanel projects={projects} onChanged={conversations.reload} />
          <ConversationList conversations={conversations} projectNames={projectNames} />
          <section className={styles.card} aria-labelledby="overview-operations">
            <div className={styles.cardHead}>
              <span className={styles.cardHeadIcon}><Pulse24Regular /></span>
              <Text as="h2" id="overview-operations" className={styles.cardHeadTitle}>Operations</Text>
            </div>
            <div className={styles.glanceGrid}>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Active projects</Text>
                <Text className={styles.glanceValue}>{active.length}</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Archived projects</Text>
                <Text className={styles.glanceValue}>{projects.length - active.length}</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Recent runs</Text>
                <Text className={styles.glanceValue}>{runs.items.length}{runs.hasMore ? '+' : ''}</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Needs your input</Text>
                <Text className={styles.glanceValue}>{attention.error ? '–' : attention.count}</Text>
              </div>
            </div>
          </section>
          <section className={styles.card} aria-labelledby="overview-projects">
            <div className={styles.sectionHead}>
              <Text as="h2" id="overview-projects" className={styles.sectionTitle}>Recent projects</Text>
              <Link className={styles.link} to="/projects">View all projects</Link>
            </div>
            {recent.length === 0 ? (
              <Text className={styles.muted}>{projects.length === 0 ? 'No projects yet. Create one in Projects.' : 'No active projects.'}</Text>
            ) : (
              <div className={styles.projectList}>
                {recent.map((project) => (
                  <Link key={project.projectId} to={`/projects/${encodeURIComponent(project.projectId)}`} className={styles.projectCard}>
                    <div className={styles.projectIdentity}>
                      <span className={styles.iconBubble}><FolderRegular /></span>
                      <Text className={styles.projectName}>{project.name}</Text>
                    </div>
                    <span className={styles.stat}><ClockRegular />Updated {new Date(project.updatedAt).toLocaleString()}</span>
                  </Link>
                ))}
              </div>
            )}
          </section>
          <section className={styles.card} aria-labelledby="overview-metrics">
            <div className={styles.sectionHead}>
              <Text as="h2" id="overview-metrics" className={styles.sectionTitle}>Project metrics</Text>
              {shownProjectId && <Link className={styles.link} to={`/projects/${encodeURIComponent(shownProjectId)}`}>Open project dashboard</Link>}
            </div>
            <Text className={styles.muted}>Metrics are per project. The service does not total them across projects, so this section shows one project at a time.</Text>
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
                title="Recent runs"
                description="The newest runs across your projects."
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
