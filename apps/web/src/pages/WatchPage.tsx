import { Link, useLocation, useParams } from 'react-router-dom';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { RunWatcher } from '../components/RunWatcher';

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
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  idLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

const short = (id: string) => id.slice(0, 8);

export function WatchPage() {
  const styles = useStyles();

  // New canonical route: /projects/:projectId/runs/:runId/execution/:executionId
  // Legacy route:        /watch/:runId  (with optional location state)
  const params = useParams<{ projectId?: string; runId?: string; executionId?: string }>();
  const location = useLocation();
  const navState = location.state as { projectId?: string; workflowRunId?: string } | null;

  // Resolve context — prefer URL params, fall back to navigation state
  const projectId     = params.projectId   ?? navState?.projectId;
  const workflowRunId = params.runId       ?? navState?.workflowRunId;
  const executionId   = params.executionId ?? params.runId; // old route: executionId IS runId

  if (!executionId) {
    return <Text>No execution id provided.</Text>;
  }

  const isCanonicalRoute = Boolean(params.projectId && params.runId && params.executionId);

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      {isCanonicalRoute ? (
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
          <span>/</span>
          <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
          <span>/</span>
          <Link to={`/projects/${projectId}/runs/${workflowRunId}/workflow`} className={styles.breadcrumbLink}>
            Run {short(workflowRunId!)}
          </Link>
          <span>/</span>
          <span>Execution {short(executionId)}</span>
        </nav>
      ) : (
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          {workflowRunId && projectId && (
            <>
              <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
              <span>/</span>
              <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
              <span>/</span>
              <Link to={`/projects/${projectId}/runs/${workflowRunId}/workflow`} className={styles.breadcrumbLink}>
                Run {short(workflowRunId)}
              </Link>
              <span>/</span>
              <span>Execution {short(executionId)}</span>
            </>
          )}
          {!workflowRunId && (
            <Link to={projectId ? `/projects/${projectId}` : '/'} className={styles.breadcrumbLink}>
              ← Back
            </Link>
          )}
        </nav>
      )}

      <div className={styles.headerRow}>
        <Title2>Execution</Title2>
        <span className={styles.idLabel}>{short(executionId)}</span>
      </div>

      <RunWatcher key={executionId} runId={executionId} />
    </div>
  );
}
