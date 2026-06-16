import { useEffect, useMemo, useState } from 'react';
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
import {
  ReactFlow,
  MarkerType,
  Position,
  Handle,
  type Node,
  type Edge,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H } from '../utils/dagLayout';

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
// Node data shape passed into React Flow custom nodes
// ---------------------------------------------------------------------------

interface WorkflowNodeData extends Record<string, unknown> {
  def: ExecutorDef;
  state: ExecutorState;
  agentName?: string;
  runId: string;
  projectId: string;
}

// ---------------------------------------------------------------------------
// Styles — page-level
// ---------------------------------------------------------------------------

const usePageStyles = makeStyles({
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
  dagContainer: {
    height: '340px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    // Override React Flow's default background so it matches the page.
    '& .react-flow__renderer': {
      borderRadius: '8px',
    },
  },
});

// ---------------------------------------------------------------------------
// Styles — custom workflow node
// ---------------------------------------------------------------------------

const useNodeStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '14px',
    width: `${NODE_W}px`,
    minHeight: `${NODE_H}px`,
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
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
  statusBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '2px 7px',
    borderRadius: '999px',
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  badgePending:   { backgroundColor: tokens.colorNeutralBackground4,        color: tokens.colorNeutralForeground3  },
  badgeStarted:   { backgroundColor: tokens.colorBrandBackground2,           color: tokens.colorBrandForeground1    },
  badgeCompleted: { backgroundColor: tokens.colorPaletteGreenBackground2,    color: tokens.colorPaletteGreenForeground1 },
  badgeSkipped:   { backgroundColor: tokens.colorNeutralBackground3,         color: tokens.colorNeutralForeground4  },
  badgeFailed:    { backgroundColor: tokens.colorPaletteRedBackground2,      color: tokens.colorPaletteRedForeground1 },
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
// Status badge component
// ---------------------------------------------------------------------------

function statusLabel(s: StepStatus) {
  if (s === 'pending')   return 'Pending';
  if (s === 'started')   return 'In Progress';
  if (s === 'completed') return 'Complete';
  if (s === 'skipped')   return 'Skipped';
  if (s === 'failed')    return 'Failed';
  return s;
}

function StatusBadge({ status }: { status: StepStatus }) {
  const s = useNodeStyles();
  const badgeClass = {
    pending:   s.badgePending,
    started:   s.badgeStarted,
    completed: s.badgeCompleted,
    skipped:   s.badgeSkipped,
    failed:    s.badgeFailed,
  }[status];
  const Icon = {
    pending:   CircleRegular,
    started:   ArrowSyncRegular,
    completed: CheckmarkCircleRegular,
    skipped:   SubtractCircleRegular,
    failed:    DismissCircleRegular,
  }[status];
  return (
    <span className={`${s.statusBadge} ${badgeClass}`}>
      <Icon fontSize={10} aria-hidden="true" />
      {statusLabel(status)}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Custom React Flow node — matches existing ExecutorCard design
// Handles:
//   Left  = standard LR target (for forward edges coming in)
//   Right = standard LR source (for forward edges going out)
//   Top   = retrigger source (rai, review) / target (agent) for loop-back arcs
// ---------------------------------------------------------------------------

const LOOP_SOURCES = new Set<ExecutorKey>(['rai', 'review']);
const LOOP_TARGET: ExecutorKey = 'agent';

function WorkflowNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const { def, state, agentName, runId, projectId } = data as WorkflowNodeData;
  const { key, label, Icon, type } = def;
  const { status } = state;

  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  return (
    <div className={s.card} role="article" aria-label={`${label}: ${statusLabel(status)}`}>
      {/* React Flow handles — invisible, only for edge routing */}
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />
      {LOOP_SOURCES.has(key as ExecutorKey) && (
        <Handle type="source" id="retrigger" position={Position.Top} style={handleStyle} />
      )}
      {key === LOOP_TARGET && (
        <Handle type="target" id="retrigger" position={Position.Top} style={handleStyle} />
      )}

      {/* Card header: type label + status badge */}
      <div className={s.cardHeader}>
        <span className={s.cardTypeLabel}>{type}</span>
        <StatusBadge status={status} />
      </div>

      {/* Icon + title */}
      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          <Icon fontSize={22} />
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{label}</span>
          {agentName && <span className={s.cardSubText}>{agentName}</span>}
        </div>
      </div>

      {/* Action buttons */}
      {key === 'agent' && (
        <div className={s.cardActions}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View run</Button>
          </Link>
        </div>
      )}
      {(key === 'rai' || key === 'scribe') && (status === 'started' || status === 'completed' || status === 'failed') && (
        <div className={s.cardActions}>
          <Link to={`/watch/${runId}-${key}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View execution</Button>
          </Link>
        </div>
      )}
      {key === 'review' && status === 'started' && (
        <div className={s.cardActions}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="primary" size="small">Awaiting review</Button>
          </Link>
        </div>
      )}
    </div>
  );
}

const nodeTypes = { workflow: WorkflowNode };

// ---------------------------------------------------------------------------
// Edge helpers
// ---------------------------------------------------------------------------

const STROKE = `var(--colorNeutralStroke1)`;
const STROKE_MUTED = `var(--colorNeutralStroke2)`;

function forwardEdge(id: string, source: string, target: string, animated = false): Edge {
  return {
    id,
    source,
    target,
    type: 'smoothstep',
    animated,
    style: { stroke: STROKE_MUTED, strokeWidth: 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: STROKE_MUTED, width: 12, height: 12 },
  };
}

function loopbackEdge(id: string, source: string, target: string, label: string): Edge {
  return {
    id,
    source,
    target,
    sourceHandle: 'retrigger',
    targetHandle: 'retrigger',
    type: 'smoothstep',
    label,
    labelStyle: { fill: STROKE, fontSize: 10, fontWeight: 600 },
    labelBgStyle: { fill: `var(--colorNeutralBackground1)`, fillOpacity: 0.9 },
    labelBgPadding: [4, 6] as [number, number],
    labelBgBorderRadius: 4,
    style: { stroke: STROKE, strokeWidth: 1, strokeDasharray: '5 3' },
    markerEnd: { type: MarkerType.ArrowClosed, color: STROKE, width: 10, height: 10 },
  };
}

// Forward edges only — fed to dagre for layout. Loop-backs excluded so dagre
// doesn't try to invert cycles and corrupt the LR rank assignment.
const FORWARD_EDGES: Edge[] = [
  forwardEdge('agent-rai',     'agent',  'rai'),
  forwardEdge('rai-review',    'rai',    'review'),
  forwardEdge('review-merge',  'review', 'merge'),
  forwardEdge('merge-scribe',  'merge',  'scribe'),
];

// Loop-back edges rendered by React Flow but excluded from dagre.
const LOOPBACK_EDGES: Edge[] = [
  loopbackEdge('rai-agent-revise',    'rai',    'agent', 'Revise'),
  loopbackEdge('review-agent-change', 'review', 'agent', 'Request changes'),
];

const ALL_EDGES: Edge[] = [...FORWARD_EDGES, ...LOOPBACK_EDGES];

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function WorkflowRunPage() {
  const styles = usePageStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const [agentName, setAgentName] = useState<string | undefined>(undefined);
  const [runStatus, setRunStatus] = useState<string | undefined>(undefined);
  const [loading, setLoading] = useState(true);

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

  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // Derive executor states from SSE workflow.step events.
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

    // Fallback: stream done but no step events — infer from terminal run status.
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
          map['review'] = { status: 'skipped' };
          map['merge']  = { status: 'skipped' };
        }
      }
    }

    return map;
  }, [events, streamStatus, runStatus, agentName]);

  // Build React Flow nodes from executor definitions + live states.
  // Run dagre on the forward edges only to compute stable LR positions.
  const rfNodes: Node[] = useMemo(() => {
    const raw: Node[] = EXECUTORS.map((def) => ({
      id: def.key,
      type: 'workflow',
      data: {
        def,
        state: executorStates[def.key] ?? { status: 'pending' },
        agentName: def.key === 'agent'
          ? (executorStates['agent']?.agentName ?? agentName)
          : undefined,
        runId: runId ?? '',
        projectId: projectId ?? '',
      } as WorkflowNodeData,
      position: { x: 0, y: 0 },
    }));
    return layoutDag(raw, FORWARD_EDGES, { rankdir: 'LR', rankSep: 72, nodeSep: 32 });
  }, [executorStates, agentName, runId, projectId]);

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

      {/* React Flow diagram */}
      <div className={styles.dagContainer}>
        <ReactFlow
          nodes={rfNodes}
          edges={ALL_EDGES}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.25 }}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          panOnScroll={false}
          zoomOnScroll={false}
          zoomOnPinch={false}
          zoomOnDoubleClick={false}
          panOnDrag={false}
          proOptions={{ hideAttribution: true }}
        />
      </div>
    </div>
  );
}
