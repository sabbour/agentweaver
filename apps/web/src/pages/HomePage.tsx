import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { RunSubmitForm } from '../components/RunSubmitForm';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '720px',
  },
});

export function HomePage() {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <Title2>Submit a run</Title2>
      <Text>
        Describe a task and choose a model source. The run streams its steps as
        the agent works, then waits for your review before any change is merged.
      </Text>
      <RunSubmitForm />
    </div>
  );
}
