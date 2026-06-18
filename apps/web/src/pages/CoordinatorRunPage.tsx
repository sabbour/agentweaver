import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Spinner, Text, Title2, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { API_KEY, API_URL } from '../config';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';
import { CoordinatorTopologyGraph } from '../components/CoordinatorTopologyGraph';
import { buildTopologyState, type TopologyNodeState } from '../state/topologyReducer';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '1100px',
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
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionTitleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  specMax: {
    maxWidth: '860px',
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

  // Fold the SSE stream into topology state (snapshot + deltas + subtask/steering).
  const topology = useMemo(() => buildTopologyState(events), [events]);
  const topologyNodes = useMemo<TopologyNodeState[]>(
    () => topology.nodeOrder.map((id) => topology.nodes[id]).filter(Boolean),
    [topology],
  );
  const hasTopology = topologyNodes.length > 0;

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

      {/* Live coordinator topology — replaces the generic pipeline for coordinator runs. */}
      {hasTopology && (
        <div className={styles.section}>
          <div className={styles.sectionTitleRow}>
            <Title3>Topology</Title3>
            {streamStatus === 'streaming' && <Spinner size="extra-tiny" aria-label="Live" />}
          </div>
          <Text className={styles.hint}>
            Live view of the coordinator and its subtasks. Open a subtask to drill into its run, or use the
            inline controls to stop, redirect, or amend.
          </Text>
          <CoordinatorTopologyGraph
            projectId={projectId}
            coordinatorRunId={runId}
            nodes={topologyNodes}
            edges={topology.edges}
          />
        </div>
      )}

      {/* Outcome spec — the coordinator node's detail (confirmation gate before dispatch). */}
      <div className={styles.specMax}>
        <OutcomeSpecPanel runId={runId} events={events} streamStatus={streamStatus} />
      </div>
    </div>
  );
}
