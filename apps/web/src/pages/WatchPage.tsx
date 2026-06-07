import { Link, useParams } from 'react-router-dom';
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

  if (!runId) {
    return <Text>No run id provided.</Text>;
  }

  return (
    <div className={styles.root}>
      <Title2>Watch run</Title2>
      <Link to="/">Back to submit</Link>
      <RunWatcher key={runId} runId={runId} />
    </div>
  );
}
