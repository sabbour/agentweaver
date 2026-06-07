import { Link, useParams } from 'react-router-dom';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { RunReview } from '../components/RunReview';
import { RunDetail } from '../components/RunDetail';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
});

export function ReviewPage() {
  const styles = useStyles();
  const { runId } = useParams<{ runId: string }>();

  if (!runId) {
    return <Text>No run id provided.</Text>;
  }

  return (
    <div className={styles.root}>
      <Title2>Review run</Title2>
      <Link to={`/watch/${runId}`}>Back to watch</Link>
      <RunDetail key={runId} runId={runId} />
      <RunReview key={runId} runId={runId} />
    </div>
  );
}
