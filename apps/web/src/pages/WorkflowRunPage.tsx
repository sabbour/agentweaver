import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Spinner,
  Text,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
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
type ExecutorType = 'STAGE' | 'ACTION';

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
  type: ExecutorType;
}

const EXECUTORS: ExecutorDef[] = [
  { key: 'agent',  label: 'Agent',  Icon: BotRegular,      type: 'STAGE'  },
  { key: 'rai',    label: 'Rai',    Icon: ShieldRegular,   type: 'ACTION' },
  { key: 'review', label: 'Review', Icon: PersonRegular,   type: 'ACTION' },
  { key: 'merge',  label: 'Merge',  Icon: MergeRegular,    type: 'STAGE'  },
  { key: 'scribe', label: 'Scribe', Icon: NotebookRegular, type: 'ACTION' },
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
  // Pipeline layout
  pipeline: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  parallelGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  parallelRow: {
    display: 'flex',
    alignItems: 'center',
  },
  // Connector: 40px wide, hollow circles at each end with a 1px line
  connector: {
    display: 'flex',
    alignItems: 'center',
    width: '40px',
    flexShrink: 0,
  },
  connectorDot: {
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: 'transparent',
    flexShrink: 0,
  },
  connectorLine: {
    flex: 1,
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  // Arc above the pipeline — wraps pipeline in relative container with top padding
  pipelineArcWrap: {
    position: 'relative',
    paddingTop: '48px',
  },
  arcSvg: {
    position: 'absolute',
    top: 0,
    left: 0,
    width: '100%',
    height: '48px',
    pointerEvents: 'none',
    overflow: 'visible',
  },
  // Executor card
  executorCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '16px',
    minWidth: '160px',
    maxWidth: '220px',
    flex: '0 0 auto',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
    boxSizing: 'border-box',
  },
  cardHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  cardTypeLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    letterSpacing: '0.5px',
    fontWeight: tokens.fontWeightSemibold,
  },
  // Status badge (pill)
  statusBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '2px 8px',
    borderRadius: '999px',
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  badgePending: {
    backgroundColor: tokens.colorNeutralBackground4,
    color: tokens.colorNeutralForeground3,
  },
  badgeStarted: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
  },
  badgeCompleted: {
    backgroundColor: tokens.colorPaletteGreenBackground2,
    color: tokens.colorPaletteGreenForeground1,
  },
  badgeSkipped: {
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground4,
  },
  badgeFailed: {
    backgroundColor: tokens.colorPaletteRedBackground2,
    color: tokens.colorPaletteRedForeground1,
  },
  // Card main content
  cardMain: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
  },
  cardIcon: {
    display: 'flex',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  cardTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
  },
  cardTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  cardSubText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginTop: '2px',
  },
  cardActions: {
    marginTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function statusLabel(s: StepStatus): string {
  if (s === 'pending') return 'Pending';
  if (s === 'started') return 'In Progress';
  if (s === 'completed') return 'Complete';
  if (s === 'skipped') return 'Skipped';
  if (s === 'failed') return 'Failed';
  return s;
}

function StatusBadge({ status }: { status: StepStatus }) {
  const styles = useStyles();
  const badgeClass = {
    pending:   styles.badgePending,
    started:   styles.badgeStarted,
    completed: styles.badgeCompleted,
    skipped:   styles.badgeSkipped,
    failed:    styles.badgeFailed,
  }[status];

  const Icon = {
    pending:   CircleRegular,
    started:   ArrowSyncRegular,
    completed: CheckmarkCircleRegular,
    skipped:   SubtractCircleRegular,
    failed:    DismissCircleRegular,
  }[status];

  return (
    <span className={`${styles.statusBadge} ${badgeClass}`}>
      <Icon fontSize={10} aria-hidden="true" />
      {statusLabel(status)}
    </span>
  );
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
  const { key, label, Icon, type } = def;
  const { status } = state;

  return (
    <div className={styles.executorCard} role="article" aria-label={`${label}: ${statusLabel(status)}`}>
      {/* Type label (top-left) + status badge (top-right) */}
      <div className={styles.cardHeader}>
        <span className={styles.cardTypeLabel}>{type}</span>
        <StatusBadge status={status} />
      </div>

      {/* Icon + title + optional sub-text */}
      <div className={styles.cardMain}>
        <span className={styles.cardIcon} aria-hidden="true">
          <Icon fontSize={22} />
        </span>
        <div className={styles.cardTitleGroup}>
          <span className={styles.cardTitle}>{label}</span>
          {agentName && <span className={styles.cardSubText}>{agentName}</span>}
        </div>
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
    </div>
  );
}

// ---------------------------------------------------------------------------
// Connectors
// ---------------------------------------------------------------------------

function PipeConnector() {
  const styles = useStyles();
  return (
    <div className={styles.connector} aria-hidden="true">
      <div className={styles.connectorDot} />
      <div className={styles.connectorLine} />
      <div className={styles.connectorDot} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function WorkflowRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const [agentName, setAgentName] = useState<string | undefined>(undefined);
  const [runStatus, setRunStatus] = useState<string | undefined>(undefined);
  const [loading, setLoading] = useState(true);

  // Fetch the run list to get agent_name and status
  // Refs for measuring the Agent and Rai card positions to draw the feedback arc above.
  const pipelineWrapRef = useRef<HTMLDivElement>(null);
  const agentCardRef    = useRef<HTMLDivElement>(null);
  const raiCardRef      = useRef<HTMLDivElement>(null);
  const [arcGeom, setArcGeom] = useState<{ fromX: number; toX: number } | null>(null);

  useLayoutEffect(() => {
    function measure() {
      if (!pipelineWrapRef.current || !agentCardRef.current || !raiCardRef.current) return;
      const wrapRect  = pipelineWrapRef.current.getBoundingClientRect();
      const agentRect = agentCardRef.current.getBoundingClientRect();
      const raiRect   = raiCardRef.current.getBoundingClientRect();
      setArcGeom({
        fromX: raiRect.left   + raiRect.width   / 2 - wrapRect.left,
        toX:   agentRect.left + agentRect.width  / 2 - wrapRect.left,
      });
    }
    measure();
    const ro = new ResizeObserver(measure);
    if (pipelineWrapRef.current) ro.observe(pipelineWrapRef.current);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;
    apiClient.getProjectRuns(projectId)
      .then((runs) => {
        if (cancelled) return;
        const run = runs.find((r) => r.run_id === runId);
        setAgentName(run?.agent_name ?? undefined);
        setRunStatus(run?.status ?? undefined);
      })
      .catch(() => { /* non-fatal */ })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, runId]);

  // Subscribe to SSE stream for live workflow.step events
  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // Derive executor states from received workflow.step events.
  // When the stream closes with no step events (historical run predating RunEvents persistence),
  // infer completed/skipped states from the run's terminal status.
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

    // Fallback: stream finished but no step events — infer from terminal run status
    const hasStepEvents = Object.keys(map).length > 0;
    const streamDone = streamStatus === 'done' || streamStatus === 'error';
    if (!hasStepEvents && streamDone && runStatus) {
      const isTerminal = ['merged', 'declined', 'merge_failed', 'failed', 'completed'].includes(runStatus);
      if (isTerminal) {
        const agentState: StepStatus = runStatus === 'failed' ? 'failed' : 'completed';
        map['agent'] = { status: agentState, agentName };
        if (runStatus !== 'failed') {
          map['rai']    = { status: 'completed' };
          map['scribe'] = { status: 'completed' };
        }
        if (runStatus === 'merged') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'completed' };
        } else if (runStatus === 'declined') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'skipped' };
        } else if (runStatus === 'merge_failed') {
          map['review'] = { status: 'completed' };
          map['merge']  = { status: 'failed' };
        } else if (runStatus === 'completed') {
          // Legacy "completed" = no-changes path; review/merge skipped
          map['review'] = { status: 'skipped' };
          map['merge']  = { status: 'skipped' };
        }
      }
    }

    return map;
  }, [events, streamStatus, runStatus, agentName]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting = streamStatus === 'connecting';

  const [agentDef, raiDef, reviewDef, mergeDef, scribeDef] = EXECUTORS;

  function getState(key: ExecutorKey): ExecutorState {
    return executorStates[key] ?? { status: 'pending' };
  }

  // SVG arc color — use the Fluent red token so it respects theming.
  const arcColor = tokens.colorPaletteRedForeground1;
  const arcId    = 'rai-arc-arrow';

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

      {/* Executor pipeline — wrapped in relative container so the arc SVG can sit above.
          Layout: [Agent] ──○ ○── [Rai]    ──○ ○── [Merge] ──○ ○── [Scribe]
                                  [Review] ──○ /
          Red arc above: Rai center ──┐ top ──┐ ↓ Agent center
      */}
      <div ref={pipelineWrapRef} className={styles.pipelineArcWrap}>
        {/* Feedback arc SVG — rendered above the pipeline row */}
        {arcGeom && (
          <svg aria-hidden="true" className={styles.arcSvg}>
            <defs>
              <marker id={arcId} markerWidth="8" markerHeight="8" refX="4" refY="4" orient="auto-start-reverse">
                <path d="M 0 0 L 8 4 L 0 8 z" fill={arcColor} />
              </marker>
            </defs>
            {/* L-shaped path: up from Rai, across top, down to Agent with arrowhead */}
            <path
              d={`M ${arcGeom.fromX},48 L ${arcGeom.fromX},8 L ${arcGeom.toX},8 L ${arcGeom.toX},48`}
              stroke={arcColor}
              strokeWidth="1.5"
              fill="none"
              markerEnd={`url(#${arcId})`}
            />
          </svg>
        )}

        <div className={styles.pipeline} role="list" aria-label="Workflow executor pipeline">
          {/* Agent */}
          <div ref={agentCardRef} role="listitem">
            <ExecutorCard
              def={agentDef}
              state={getState('agent')}
              agentName={getState('agent').agentName ?? agentName}
              runId={runId}
              projectId={projectId}
            />
          </div>

          {/* Parallel group: Rai (top) + Review (bottom) */}
          <div className={styles.parallelGroup} role="listitem" aria-label="Parallel actions">
            <div className={styles.parallelRow}>
              <PipeConnector />
              <div ref={raiCardRef}>
                <ExecutorCard
                  def={raiDef}
                  state={getState('rai')}
                  agentName={undefined}
                  runId={runId}
                  projectId={projectId}
                />
              </div>
              <PipeConnector />
            </div>
            <div className={styles.parallelRow}>
              <PipeConnector />
              <ExecutorCard
                def={reviewDef}
                state={getState('review')}
                agentName={undefined}
                runId={runId}
                projectId={projectId}
              />
              <PipeConnector />
            </div>
          </div>

          {/* Merge */}
          <div role="listitem">
            <ExecutorCard
              def={mergeDef}
              state={getState('merge')}
              agentName={undefined}
              runId={runId}
              projectId={projectId}
            />
          </div>

          {/* Connector to Scribe */}
          <PipeConnector />

          {/* Scribe */}
          <div role="listitem">
            <ExecutorCard
              def={scribeDef}
              state={getState('scribe')}
              agentName={undefined}
              runId={runId}
              projectId={projectId}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
