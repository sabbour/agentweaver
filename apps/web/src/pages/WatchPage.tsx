import { Link, useLocation, useParams } from 'react-router-dom';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { RunWatcher } from '../components/RunWatcher';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
});

export function WatchPage() {
  const styles = useStyles();
  const { runId } = useParams<{ runId: string }>();
  const location = useLocation();
  const projectId = (location.state as { projectId?: string } | null)?.projectId;

  if (!runId) {
    return <Text>No run id provided.</Text>;
  }

  return (
    <div className={styles.root}>
      <Title2>Watch run</Title2>
      {projectId
        ? <Link to={`/projects/${projectId}`}>Back to project</Link>
        : <Link to="/">Back to submit</Link>
      }
      <RunWatcher key={runId} runId={runId} />
    </div>
  );
}
