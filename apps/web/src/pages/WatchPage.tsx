import { Link, useLocation, useParams } from 'react-router-dom';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { RunWatcher } from '../components/RunWatcher';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  navRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
});

export function WatchPage() {
  const styles = useStyles();
  const { runId } = useParams<{ runId: string }>();
  const location = useLocation();
  const state = location.state as { projectId?: string; workflowRunId?: string } | null;
  const projectId      = state?.projectId;
  const workflowRunId  = state?.workflowRunId;

  if (!runId) {
    return <Text>No run id provided.</Text>;
  }

  return (
    <div className={styles.root}>
      <Title2>Watch run</Title2>
      <div className={styles.navRow}>
        {workflowRunId && projectId && (
          <Link to={`/projects/${projectId}/runs/${workflowRunId}/workflow`}>
            ← Back to workflow
          </Link>
        )}
        {projectId
          ? <Link to={`/projects/${projectId}`}>Back to project</Link>
          : <Link to="/">Back to home</Link>
        }
      </div>
      <RunWatcher key={runId} runId={runId} />
    </div>
  );
}
