/**
 * Cross-project Overview and Chief of Staff home: New chat, My conversations,
 * Priorities, and Needs attention (the caller's notifications), then the 0.x
 * Operations card, Recent projects, and Recent runs in this browser. The 0.x
 * usage charts and activity feed need the metrics contract (gap G3) and are not
 * shown.
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
import { useNotificationAttention } from './runAttention';
import { ConversationList, NewChatPanel } from './ChiefOfStaffPanels';
import { useChiefOfStaffConversations, useChiefOfStaffPriorities } from './chiefOfStaff';
import { PrioritiesPanel } from './PrioritiesPanel';
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
  column: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, minWidth: 0 },
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
  const attention = useNotificationAttention();
  const conversations = useChiefOfStaffConversations();
  const priorities = useChiefOfStaffPriorities();
  const runs = useBrowserRunList();
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

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

  useEffect(() => {
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  const projectNames = useMemo(
    () => new Map((projects ?? []).map((project) => [project.projectId, project.name])),
    [projects],
  );
  const active = (projects ?? []).filter((project) => project.state === 'active');
  const recent = [...active].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, RECENT_PROJECT_LIMIT);
  const refresh = () => { void load(); attention.reload(); conversations.reload(); void priorities.reload(); };

  return (
    <div className={styles.root}>
      <PageHeading
        title="Overview"
        description="Your Priorities, chats, and what needs your input."
        breadcrumb={false}
        actions={(
          <div className={styles.headerActions}>
            {loadedAt && <Text className={styles.generated}>Updated {loadedAt.toLocaleTimeString()}</Text>}
            <Button appearance="subtle" icon={<ArrowSyncRegular />} disabled={loading} onClick={refresh}>Refresh</Button>
          </div>
        )}
      />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {loading && !projects && <Loading label="Loading overview" />}
      {projects && (
        <>
          <div className={styles.mainGrid}>
            <PrioritiesPanel priorities={priorities} projects={projects} conversations={conversations} />
            <div className={styles.column}>
              <section className={styles.card} aria-labelledby="overview-attention">
                <Text as="h2" id="overview-attention" className={styles.sectionTitle}>Needs attention</Text>
                <AttentionList attention={attention} />
              </section>
              <NewChatPanel projects={projects} onChanged={conversations.reload} />
              <ConversationList conversations={conversations} projectNames={projectNames} />
            </div>
          </div>
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
          <RunList
            title="Recent runs in this browser"
            description="Runs you opened in any project. A full run list needs a service update."
            data={runs}
            emptyText="Runs you open appear here."
            projectNames={projectNames}
            limit={8}
          />
        </>
      )}
    </div>
  );
}
