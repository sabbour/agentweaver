/**
 * Cross-project Overview in the 0.x layout: Operations card, Recent projects,
 * Recent runs in this browser, and Needs attention. Counts come from the project
 * list, browser run history, and the open run. The 0.x usage charts and
 * activity feed need a service metrics API that does not exist yet, so they are
 * not shown. This page
 * is the planned home for Chief of Staff priorities.
 */
import { Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular, ClockRegular, FolderRegular, Pulse24Regular } from '@fluentui/react-icons';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { AttentionList } from './AttentionList';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
import { useRunAttention } from './runAttention';
import { RunList } from './RunList';
import { useBrowserRunList } from './runListData';

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
  const attention = useRunAttention();
  const runs = useBrowserRunList();
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

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
      setError(reason);
    } finally {
      setLoading(false);
    }
  }, [apiCall]);

  // Start the first load in a zero-delay timer instead of calling it directly
  // in the effect. This keeps state updates out of the effect body. The cleanup
  // cancels the timer if the page unmounts before it fires.
  useEffect(() => {
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  // Project ID to name lookup, so the cross-project run list can show names.
  const projectNames = useMemo(
    () => new Map((projects ?? []).map((project) => [project.projectId, project.name])),
    [projects],
  );
  // Only active projects count as recent. Archived projects are counted in the
  // Operations card but not listed. ISO timestamps sort correctly as strings,
  // so newest first is a reverse string compare.
  const active = (projects ?? []).filter((project) => project.state === 'active');
  const recent = [...active].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, RECENT_PROJECT_LIMIT);
  // Refresh re-reads both the project list and the open run's pending items.
  // Browser run history is local, so it needs no reload here.
  const refresh = () => { void load(); attention.reload(); };

  return (
    <div className={styles.root}>
      <PageHeading
        title="Overview"
        description="Your projects, the runs you opened, and what needs your input."
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
                <Text className={styles.glanceLabel}>Recent runs in this browser</Text>
                <Text className={styles.glanceValue}>{runs.items.length}</Text>
              </div>
              <div className={styles.glance}>
                <Text className={styles.glanceLabel}>Needs your input</Text>
                <Text className={styles.glanceValue}>{attention.items.length}</Text>
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
          <div className={styles.mainGrid}>
            <RunList
              title="Recent runs in this browser"
              description="Runs you opened in any project. A full run list needs a service update."
              data={runs}
              emptyText="Runs you open appear here."
              projectNames={projectNames}
              limit={8}
            />
            <section className={styles.card} aria-labelledby="overview-attention">
              <Text as="h2" id="overview-attention" className={styles.sectionTitle}>Needs attention</Text>
              <AttentionList attention={attention} />
            </section>
          </div>
        </>
      )}
    </div>
  );
}
