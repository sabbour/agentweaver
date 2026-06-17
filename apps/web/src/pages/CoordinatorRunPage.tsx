import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Spinner, Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { API_KEY, API_URL } from '../config';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '860px',
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
    flexWrap: 'wrap',
  },
  runIdLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  goal: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
});

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // Goal is carried by the coordinator.started event (Principle III — read it from the stream).
  const goal = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'coordinator.started' && typeof evt.payload['goal'] === 'string') {
        return evt.payload['goal'] as string;
      }
    }
    return undefined;
  }, [events]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId = runId.length > 8 ? runId.slice(0, 8) : runId;

  return (
    <div className={styles.root}>
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span aria-hidden="true">/</span>
        <span>Orchestration {shortId}</span>
      </nav>

      <div className={styles.headerRow}>
        <Title2>Orchestration</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {streamStatus === 'connecting' && <Spinner size="extra-tiny" aria-label="Connecting" />}
      </div>

      {goal && <Text className={styles.goal}>Goal: {goal}</Text>}

      <OutcomeSpecPanel runId={runId} events={events} streamStatus={streamStatus} />
    </div>
  );
}
