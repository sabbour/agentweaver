import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Card,
  Spinner,
  Text,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
  ArrowRightRegular,
  ArrowSyncRegular,
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  MergeRegular,
  NotebookRegular,
  PersonRegular,
  ShieldRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import { useRunStream } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { API_KEY, API_URL } from '../config';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type StepStatus = 'pending' | 'started' | 'completed' | 'skipped' | 'failed';
type ExecutorKey = 'agent' | 'rai' | 'review' | 'merge' | 'scribe';

interface ExecutorState {
  status: StepStatus;
  agentName?: string;
}

// ---------------------------------------------------------------------------
// Executor definitions (MAF pipeline order)
// ---------------------------------------------------------------------------

interface ExecutorDef {
  key: ExecutorKey;
  label: string;
  Icon: FluentIcon;
}

const EXECUTORS: ExecutorDef[] = [
  { key: 'agent', label: 'Agent', Icon: BotRegular },
  { key: 'rai', label: 'Rai', Icon: ShieldRegular },
  { key: 'review', label: 'Review', Icon: PersonRegular },
  { key: 'merge', label: 'Merge', Icon: MergeRegular },
  { key: 'scribe', label: 'Scribe', Icon: NotebookRegular },
];

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '1020px',
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
  pipeline: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'stretch',
    gap: tokens.spacingHorizontalS,
  },
  connector: {
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground4,
    flexShrink: 0,
  },
  executorCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    minWidth: '148px',
    maxWidth: '190px',
    flex: '0 0 auto',
  },
  cardTop: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  cardIconBase: {
    display: 'flex',
    color: tokens.colorNeutralForeground2,
  },
  cardLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  cardSubLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginTop: '1px',
  },
  cardStatus: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: tokens.fontSizeBase200,
  },
  iconPending: { display: 'flex', color: tokens.colorNeutralForeground4 },
  iconStarted: { display: 'flex', color: tokens.colorBrandForeground1 },
  iconCompleted: { display: 'flex', color: tokens.colorPaletteGreenForeground1 },
  iconSkipped: { display: 'flex', color: tokens.colorNeutralForeground4 },
  iconFailed: { display: 'flex', color: tokens.colorPaletteRedForeground1 },
  cardActions: {
    marginTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatusIcon({ status }: { status: StepStatus }) {
  switch (status) {
    case 'started': return <ArrowSyncRegular fontSize={14} />;
    case 'completed': return <CheckmarkCircleRegular fontSize={14} />;
    case 'skipped': return <SubtractCircleRegular fontSize={14} />;
    case 'failed': return <DismissCircleRegular fontSize={14} />;
    default: return <CircleRegular fontSize={14} />;
  }
}

function statusLabel(s: StepStatus): string {
  if (s === 'pending') return 'Pending';
  if (s === 'started') return 'In progress';
  if (s === 'completed') return 'Completed';
  if (s === 'skipped') return 'Skipped';
  if (s === 'failed') return 'Failed';
  return s;
}

interface ExecutorCardProps {
  def: ExecutorDef;
  state: ExecutorState;
  agentName: string | undefined;
  runId: string;
  projectId: string;
}

function ExecutorCard({ def, state, agentName, runId, projectId }: ExecutorCardProps) {
  const styles = useStyles();
  const { key, label, Icon } = def;
  const { status } = state;

  const iconClass = {
    pending: styles.iconPending,
    started: styles.iconStarted,
    completed: styles.iconCompleted,
    skipped: styles.iconSkipped,
    failed: styles.iconFailed,
  }[status];

  return (
    <Card className={styles.executorCard}>
      <div className={styles.cardTop}>
        <span className={styles.cardIconBase} aria-hidden="true">
          <Icon fontSize={22} />
        </span>
        <div>
          <div className={styles.cardLabel}>{label}</div>
          {agentName && <div className={styles.cardSubLabel}>{agentName}</div>}
        </div>
      </div>

      <div className={styles.cardStatus} aria-label={`${label}: ${statusLabel(status)}`}>
        <span className={iconClass} aria-hidden="true">
          <StatusIcon status={status} />
        </span>
        <Text size={100}>{statusLabel(status)}</Text>
      </div>

      {key === 'agent' && (
        <div className={styles.cardActions}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View run</Button>
          </Link>
        </div>
      )}

      {(key === 'rai' || key === 'scribe') && (status === 'started' || status === 'completed' || status === 'failed') && (
        <div className={styles.cardActions}>
          <Link to={`/watch/${runId}-${key}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View execution</Button>
          </Link>
        </div>
      )}

      {key === 'review' && status === 'started' && (
        <div className={styles.cardActions}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="primary" size="small">Awaiting review</Button>
          </Link>
        </div>
      )}
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function WorkflowRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const [agentName, setAgentName] = useState<string | undefined>(undefined);
  const [loading, setLoading] = useState(true);

  // Fetch the run list to get agent_name
  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;
    apiClient.getProjectRuns(projectId)
      .then((runs) => {
        if (cancelled) return;
        const run = runs.find((r) => r.run_id === runId);
        setAgentName(run?.agent_name ?? undefined);
      })
      .catch(() => { /* non-fatal — agent name is optional */ })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, runId]);

  // Subscribe to SSE stream for live workflow.step events
  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // Derive executor states from received workflow.step events
  const executorStates = useMemo<Record<string, ExecutorState>>(() => {
    const map: Record<string, ExecutorState> = {};
    for (const evt of events) {
      if (evt.type !== 'workflow.step') continue;
      const step = String(evt.payload['step'] ?? '');
      const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
      const evtAgent = evt.payload['agent_name'] != null ? String(evt.payload['agent_name']) : undefined;
      const prev = map[step];
      map[step] = { status: evtStatus, agentName: evtAgent ?? prev?.agentName };
    }
    return map;
  }, [events]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting = streamStatus === 'connecting';

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/watch/${runId}`} state={{ projectId }} className={styles.breadcrumbLink}>
          Run {shortId}
        </Link>
        <span aria-hidden="true">/</span>
        <span>Workflow</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Workflow Run</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {(loading || isConnecting) && <Spinner size="extra-tiny" aria-label="Loading" />}
      </div>

      {/* Executor pipeline */}
      <div className={styles.pipeline} role="list" aria-label="Workflow executor pipeline">
        {EXECUTORS.flatMap((exec, idx) => {
          const state: ExecutorState = executorStates[exec.key] ?? { status: 'pending' };
          const resolvedAgentName =
            exec.key === 'agent' ? (state.agentName ?? agentName) : undefined;
          const isLast = idx === EXECUTORS.length - 1;

          const elements = [
            <div key={exec.key} role="listitem">
              <ExecutorCard
                def={exec}
                state={state}
                agentName={resolvedAgentName}
                runId={runId}
                projectId={projectId}
              />
            </div>,
          ];

          if (!isLast) {
            elements.push(
              <div key={`arrow-${idx}`} className={styles.connector} aria-hidden="true">
                <ArrowRightRegular fontSize={20} />
              </div>,
            );
          }

          return elements;
        })}
      </div>
    </div>
  );
}
