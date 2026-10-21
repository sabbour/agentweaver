/**
 * Project Dashboard in the 0.x layout: breadcrumb header with update time and
 * Refresh, a status card with quick actions and summary tiles, recent runs, and
 * the team. Values come from the project summary, the saved configuration, and
 * the browser's recent runs. Run metrics and activity charts are not shown
 * because the service has no metrics API yet.
 */
import { Badge, Button, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { Link, useParams } from 'react-router-dom';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
import { useProjectConfiguration, useProjectSummary } from './projectData';
import { projectReadiness } from './projectReadiness';
import { RunList } from './RunList';
import { runListErrorMessage, useProjectRunList } from './runListData';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXL },
  headerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  commandStrip: {
    display: 'grid',
    gridTemplateColumns: 'minmax(280px, .75fr) minmax(0, 1.5fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    '@media (max-width: 1120px)': { gridTemplateColumns: '1fr' },
  },
  healthBlock: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minWidth: 0 },
  healthTitle: {
    display: 'block',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  healthCopy: { display: 'block', color: tokens.colorNeutralForeground2, maxWidth: '60ch' },
  checks: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, margin: 0, padding: 0, listStyle: 'none' },
  check: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground2 },
  quickActions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', marginTop: 'auto' },
  actionLink: {
    minHeight: '32px',
    display: 'inline-flex',
    alignItems: 'center',
    padding: `0 ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground1,
    textDecorationLine: 'none',
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  primaryActionLink: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorBrandBackground,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover },
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(120px, 1fr))',
    gap: tokens.spacingHorizontalM,
    alignSelf: 'start',
    '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  summaryTile: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minHeight: '112px',
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  summaryLabel: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  summaryValue: {
    display: 'block',
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  summaryMeta: { display: 'block', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, marginTop: 'auto' },
  mainGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
  },
  panelTitle: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  teamList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, margin: 0, padding: 0, listStyle: 'none' },
  teamRow: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Renders one project's Dashboard. */
export function DashboardPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const project = useProjectSummary(projectId);
  const configuration = useProjectConfiguration(projectId);
  // The newest runs for the list, and the open runs for the counts.
  const runs = useProjectRunList(projectId, { pageSize: 5 });
  const openRuns = useProjectRunList(projectId, { states: ['open'], pageSize: 100 });
  const base = `/projects/${encodeURIComponent(projectId)}`;
  // Refresh re-reads the summary, the saved configuration, and both run lists.
  const refresh = () => { project.reload(); configuration.reload(); runs.reload(); openRuns.reload(); };
  const loading = project.loading || configuration.loading;
  // Show one error at a time. A summary error is shown before a configuration error.
  const error = project.error ?? configuration.error;
  const config = configuration.data?.configuration;
  // Readiness needs the configuration. An archived project gets the archived
  // status card no matter how complete its setup is.
  const readiness = config ? projectReadiness(config, project.data?.state === 'archived') : null;
  // Counts come from the loaded open runs. A "+" marks counts that may be
  // higher because the service has more open runs than one page.
  const more = openRuns.hasMore ? '+' : '';
  const needsInput = openRuns.items.filter((run) => run.executionState === 'blocked').length;
  const working = openRuns.items.filter((run) => run.executionState === 'active').length;
  const runError = runs.error ?? openRuns.error;
  const enabledSkills = config?.skills.filter((skill) => skill.enabled).length ?? 0;

  return (
    <div className={styles.root}>
      <PageHeading
        title="Dashboard"
        description="Project setup, recent runs, and the team for this project."
        actions={(
          <div className={styles.headerActions}>
            {configuration.loadedAt && <Text className={styles.generated}>Updated {configuration.loadedAt.toLocaleTimeString()}</Text>}
            <Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={refresh}>Refresh</Button>
          </div>
        )}
      />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {/* Show the spinner only until the first configuration arrives. A refresh keeps the old cards visible. */}
      {loading && !readiness && <Loading label="Loading dashboard" />}
      {readiness && config && configuration.data && (
        <>
          <section className={styles.commandStrip} aria-labelledby="project-status-title">
            <div className={styles.healthBlock}>
              <div><Badge appearance="tint" color={readiness.badge.color}>{readiness.badge.label}</Badge></div>
              <div>
                <Text as="h2" id="project-status-title" className={styles.healthTitle}>{readiness.title}</Text>
                <Text className={styles.healthCopy}>{readiness.body}</Text>
              </div>
              <ul className={styles.checks} aria-label="Setup checks">
                {readiness.checks.map((check) => (
                  <li key={check.key} className={styles.check}>
                    <Badge appearance="filled" size="extra-small" color={check.done ? 'success' : 'warning'} aria-label={check.done ? 'Done' : 'Not done'} />
                    {check.label}
                  </li>
                ))}
              </ul>
              <div className={styles.quickActions} aria-label="Project actions">
                <Link to={`${base}/${readiness.primaryAction.path}`} className={mergeClasses(styles.actionLink, styles.primaryActionLink)}>
                  {readiness.primaryAction.label}
                </Link>
                {/* Skip the extra Orchestrations link when the primary action already goes there. */}
                {readiness.primaryAction.path !== 'orchestrations' && <Link to={`${base}/orchestrations`} className={styles.actionLink}>Open Orchestrations</Link>}
                <Link to={`${base}/memories`} className={styles.actionLink}>Open Memories</Link>
              </div>
            </div>
            <div className={styles.summaryGrid} aria-label="Project summary">
              <Link to={`${base}/orchestrations`} className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Active runs</Text>
                <Text className={styles.summaryValue}>{openRuns.items.length}{more}</Text>
                <Text className={styles.summaryMeta}>Working, open, or waiting</Text>
              </Link>
              <Link to={`${base}/orchestrations`} className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Needs input</Text>
                <Text className={styles.summaryValue}>{needsInput}{more}</Text>
                <Text className={styles.summaryMeta}>{working}{more} working now</Text>
              </Link>
              <Link to={`${base}/team`} className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Agents</Text>
                <Text className={styles.summaryValue}>{config.agentCharters.length}</Text>
                <Text className={styles.summaryMeta}>{config.casting.length} in casting order</Text>
              </Link>
              <Link to={`${base}/skills`} className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Skills</Text>
                <Text className={styles.summaryValue}>{enabledSkills}</Text>
                <Text className={styles.summaryMeta}>Enabled in this project</Text>
              </Link>
            </div>
          </section>
          <div className={styles.mainGrid}>
            <div>
              {runError != null && <ErrorNotice code={errorCode(runError)}>{runListErrorMessage(runError)}</ErrorNotice>}
              <RunList
                title="Recent runs"
                description="The newest runs in this project."
                data={runs}
                emptyText={runs.loading ? 'Loading runs…' : 'No runs yet.'}
                limit={5}
              />
            </div>
            <aside className={styles.panel} aria-labelledby="dashboard-team-title">
              <Text as="h2" id="dashboard-team-title" className={styles.panelTitle}>Team</Text>
              {config.agentCharters.length === 0 ? <Text className={styles.muted}>No agents yet. Add agents in Agents.</Text> : (
                <ul className={styles.teamList}>
                  {/* Show at most eight agents here; the Agents page lists them all. */}
                  {config.agentCharters.slice(0, 8).map((agent) => (
                    <li key={agent.agentId} className={styles.teamRow}>
                      <Text weight="semibold">{agent.name}</Text>
                      <Text className={styles.muted}>{agent.role}</Text>
                    </li>
                  ))}
                </ul>
              )}
              <Link to={`${base}/team`} className={styles.actionLink}>Open Agents</Link>
            </aside>
          </div>
        </>
      )}
    </div>
  );
}
