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
  useEdges,
  useNodes,
  type Node,
  type Edge,
  type NodeProps,
  type EdgeProps,
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

interface ExecutorState {
  status: StepStatus;
  agentName?: string;
}

interface ExecutorDef {
  key: ExecutorKey;
  label: string;
  Icon: FluentIcon;
}

const EXECUTORS: ExecutorDef[] = [
  { key: 'agent',  label: 'Agent',  Icon: BotRegular      },
  { key: 'rai',    label: 'Rai',    Icon: ShieldRegular   },
  { key: 'review', label: 'Review', Icon: PersonRegular   },
  { key: 'merge',  label: 'Merge',  Icon: MergeRegular    },
  { key: 'scribe', label: 'Scribe', Icon: NotebookRegular },
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
  reviewedBy?: string;
}

// ---------------------------------------------------------------------------
// Styles — page-level
// ---------------------------------------------------------------------------

const usePageStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
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
    height: '300px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
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
    height: `${NODE_H}px`,        // fixed height — all nodes same size → handles at same Y
    boxSizing: 'border-box',
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
    cursor: 'default',
  },
  cardActive: {
    borderLeft: `3px solid ${tokens.colorBrandForeground1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  cardActionRequired: {
    border: `2px solid ${tokens.colorBrandForeground1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  cardHeader: {
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
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
  },
  cardIcon: {
    display: 'flex',
    color: tokens.colorNeutralForeground2,
    flexShrink: 0,
  },
  cardTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
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
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardActions: {
    marginTop: tokens.spacingVerticalXS,
  },
  reviewerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    marginTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Status badge + description helpers
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

function statusDescription(key: ExecutorKey, status: StepStatus): string | null {
  if (status === 'pending') return null;
  if (key === 'agent') {
    if (status === 'started')   return 'Working on task...';
    if (status === 'completed') return 'Finished';
    if (status === 'failed')    return 'Failed';
  }
  if (key === 'rai') {
    if (status === 'started')   return 'Reviewing safety...';
    if (status === 'completed') return 'Passed';
    if (status === 'failed')    return 'Flagged';
  }
  if (key === 'review') {
    if (status === 'started')   return 'Awaiting your review';
    if (status === 'completed') return 'Reviewed';
    if (status === 'skipped')   return 'Skipped';
  }
  if (key === 'merge') {
    if (status === 'started')   return 'Merging...';
    if (status === 'completed') return 'Merged';
    if (status === 'failed')    return 'Merge failed';
    if (status === 'skipped')   return 'Skipped';
  }
  if (key === 'scribe') {
    if (status === 'started')   return 'Logging session...';
    if (status === 'completed') return 'Done';
  }
  return null;
}

// ---------------------------------------------------------------------------
// Custom React Flow node
// Only left (target) and right (source) handles — no hardcoded top/bottom.
// Loop-back arcs are drawn by the LoopbackEdge custom edge component.
// Interactive elements carry the "nopan nodrag" classes so React Flow does
// not swallow click events on buttons / links inside the card.
// ---------------------------------------------------------------------------

function WorkflowNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const { def, state, agentName, runId, projectId, reviewedBy } = data as WorkflowNodeData;
  const { key, label, Icon } = def;
  const { status } = state;

  const isActive         = status === 'started' && key !== 'review';
  const isActionRequired = key === 'review' && status === 'started';
  const cardClass = [
    s.card,
    isActive         ? s.cardActive         : '',
    isActionRequired ? s.cardActionRequired : '',
  ].filter(Boolean).join(' ');

  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  const subText = statusDescription(key as ExecutorKey, status);

  return (
    <div className={cardClass} role="article" aria-label={`${label}: ${statusLabel(status)}`}>
      {/* Standard LR handles — no Top/Bottom, positions are generic */}
      <Handle type="target" position={Position.Left}  style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />

      {/* Status badge */}
      <div className={s.cardHeader}>
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
          {subText && <span className={s.cardSubText}>{subText}</span>}
        </div>
      </div>

      {/* Action buttons — nopan/nodrag prevents React Flow from swallowing clicks */}
      {key === 'agent' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View run</Button>
          </Link>
        </div>
      )}
      {(key === 'rai' || key === 'scribe') && (status === 'started' || status === 'completed' || status === 'failed') && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/watch/${runId}-${key}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small">View execution</Button>
          </Link>
        </div>
      )}
      {key === 'review' && status === 'started' && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Link to={`/watch/${runId}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
            <Button appearance="primary" size="medium">Review now</Button>
          </Link>
        </div>
      )}
      {key === 'review' && status === 'completed' && reviewedBy && (
        <div className={`${s.reviewerRow} nopan nodrag`}>
          <img
            src={`https://github.com/${reviewedBy}.png?size=28`}
            style={{ width: 28, height: 28, borderRadius: '50%', border: `2px solid ${tokens.colorBrandForeground1}` }}
            alt={reviewedBy}
          />
          <Text size={200} style={{ color: tokens.colorNeutralForeground2 }}>{reviewedBy}</Text>
        </div>
      )}
    </div>
  );
}

const nodeTypes = { workflow: WorkflowNode };

// ---------------------------------------------------------------------------
// Custom loopback edge — topology-agnostic, heuristic orthogonal routing.
//
// Shape: angled orthogonal path exiting/entering the TOP or BOTTOM CENTER of
// each card (not the sides).  Three right-angle segments:
//   M sx,sy  → L sx,apexY  → L tx,apexY  → L tx,ty
// where sx/sy and tx/ty are the top/bottom centers of source and target nodes,
// computed from node positions (props sourceX/Y are ignored — they come from
// the L/R side handles which we do not want here).
//
// Heuristics:
// 1. SIDE — Sort siblings by source X, even=above, odd=below.
// 2. CLEARANCE — apexY clears all intermediate node tops/bottoms + ARC_GAP.
// 3. STAGGER — Each same-side sibling adds STAGGER px so rails don't overlap.
// ---------------------------------------------------------------------------

const LOOPBACK_STROKE = 'var(--colorNeutralStroke1)';
const ARC_GAP = 12; // clearance above/below card edge
const STAGGER = 28; // extra rail separation per same-side sibling

function LoopbackEdge({ id, label, data }: EdgeProps) {
  const allEdges = useEdges();
  const allNodes = useNodes();

  // --- Look up own edge to get source/target node ids safely ---
  const myEdge   = allEdges.find(e => e.id === id);
  const sourceId = myEdge?.source ?? '';
  const targetId = myEdge?.target ?? '';

  const sourceNode = allNodes.find(n => n.id === sourceId);
  const targetNode = allNodes.find(n => n.id === targetId);

  // --- Heuristic 1: side ---
  const siblings = allEdges
    .filter(e => e.type === 'loopback' && e.target === targetId)
    .sort((a, b) => {
      const ax = allNodes.find(n => n.id === a.source)?.position.x ?? 0;
      const bx = allNodes.find(n => n.id === b.source)?.position.x ?? 0;
      return ax - bx; // nearest source first
    });

  const myIndex   = siblings.findIndex(e => e.id === id);
  const autoAbove = myIndex % 2 === 0;
  const above     = data?.above !== undefined ? Boolean(data.above) : autoAbove;

  // --- Compute connection points at top/bottom CENTER of each card ---
  // (ignoring the L/R handle positions React Flow provides, which would give side connections)
  const sx = (sourceNode?.position.x ?? 0) + NODE_W / 2;
  const tx = (targetNode?.position.x ?? 0) + NODE_W / 2;
  const sy = above
    ? (sourceNode?.position.y ?? 0)            // top-center of source
    : (sourceNode?.position.y ?? 0) + NODE_H;  // bottom-center of source
  const ty = above
    ? (targetNode?.position.y ?? 0)            // top-center of target
    : (targetNode?.position.y ?? 0) + NODE_H;  // bottom-center of target

  // --- Heuristic 2: clearance against intermediate nodes ---
  const minX = Math.min(sx, tx);
  const maxX = Math.max(sx, tx);

  const overlapping = allNodes.filter(n => {
    const nl = n.position.x ?? 0;
    const nr = nl + NODE_W;
    return nr > minX && nl < maxX;
  });

  let apexY: number;
  if (above) {
    const minTop = overlapping.length > 0
      ? Math.min(...overlapping.map(n => n.position.y ?? 0))
      : sy - NODE_H / 2;
    apexY = minTop - ARC_GAP;
  } else {
    const maxBottom = overlapping.length > 0
      ? Math.max(...overlapping.map(n => (n.position.y ?? 0) + NODE_H))
      : sy + NODE_H / 2;
    apexY = maxBottom + ARC_GAP;
  }

  // --- Heuristic 3: stagger same-side siblings ---
  const sameSideBefore = siblings.slice(0, myIndex).filter((e, i) => {
    const sAbove = e.data?.above !== undefined ? Boolean(e.data.above) : i % 2 === 0;
    return sAbove === above;
  }).length;

  apexY += (above ? -1 : 1) * sameSideBefore * STAGGER;

  // --- Orthogonal path: top/bottom center → straight up/down → horizontal rail → back down/up ---
  const d = `M ${sx},${sy} L ${sx},${apexY} L ${tx},${apexY} L ${tx},${ty}`;

  const midX    = (sx + tx) / 2;
  const labelY  = above ? apexY - 5 : apexY + 12;
  const markerId = `lb-arrow-${id}`;

  return (
    <>
      <defs>
        <marker
          id={markerId}
          markerWidth="8"
          markerHeight="6"
          refX="6"
          refY="3"
          orient="auto"
        >
          <path d="M 0 0 L 6 3 L 0 6 Z" fill={LOOPBACK_STROKE} />
        </marker>
      </defs>
      <path
        d={d}
        fill="none"
        stroke={LOOPBACK_STROKE}
        strokeWidth={1.5}
        strokeDasharray="5 3"
        markerEnd={`url(#${markerId})`}
      />
      {label != null && (
        <text
          x={midX}
          y={labelY}
          textAnchor="middle"
          fontSize={10}
          fill={LOOPBACK_STROKE}
          fontWeight={600}
          style={{ userSelect: 'none', pointerEvents: 'none' }}
        >
          {label as string}
        </text>
      )}
    </>
  );
}

const edgeTypes = { loopback: LoopbackEdge };

// ---------------------------------------------------------------------------
// Edge helpers
// ---------------------------------------------------------------------------

const STROKE_MUTED = 'var(--colorNeutralStroke2)';

function forwardEdge(id: string, source: string, target: string, animated = false): Edge {
  return {
    id,
    source,
    target,
    type: 'default',
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
    type: 'loopback',
    label,
    // No data.above/offset — heuristics in LoopbackEdge compute everything automatically.
  };
}

// Forward edges only — fed to dagre. Loop-backs are excluded so dagre doesn't
// invert cycles and corrupt LR rank assignment.
const FORWARD_EDGES: Edge[] = [
  forwardEdge('agent-rai',    'agent',  'rai'),
  forwardEdge('rai-review',   'rai',    'review'),
  forwardEdge('review-merge', 'review', 'merge'),
  forwardEdge('merge-scribe', 'merge',  'scribe'),
];

// Loop-back edges — sides and heights computed automatically by LoopbackEdge.
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

  const [agentName,   setAgentName]   = useState<string | undefined>(undefined);
  const [runStatus,   setRunStatus]   = useState<string | undefined>(undefined);
  const [reviewedBy,  setReviewedBy]  = useState<string | undefined>(undefined);
  const [loading,     setLoading]     = useState(true);

  useEffect(() => {
    if (!projectId || !runId) return;
    let cancelled = false;
    apiClient.getProjectRuns(projectId)
      .then((runs) => {
        if (cancelled) return;
        const run = runs.find((r) => r.run_id === runId);
        setAgentName(run?.agent_name   ?? undefined);
        setRunStatus(run?.status       ?? undefined);
        setReviewedBy(run?.reviewed_by ?? undefined);
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
      const step     = String(evt.payload['step'] ?? '');
      const evtStatus = String(evt.payload['status'] ?? 'started') as StepStatus;
      const evtAgent  = evt.payload['agent_name'] != null ? String(evt.payload['agent_name']) : undefined;
      const prev = map[step];
      map[step] = { status: evtStatus, agentName: evtAgent ?? prev?.agentName };
    }

    // Fallback: stream done but no step events — infer from terminal run status.
    const hasStepEvents = Object.keys(map).length > 0;
    const streamDone    = streamStatus === 'done' || streamStatus === 'error';
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

  // Build React Flow nodes; run dagre on forward edges only for LR layout.
  const rfNodes: Node[] = useMemo(() => {
    const raw: Node[] = EXECUTORS.map((def) => ({
      id: def.key,
      type: 'workflow',
      data: {
        def,
        state:      executorStates[def.key] ?? { status: 'pending' },
        agentName:  def.key === 'agent' ? (executorStates['agent']?.agentName ?? agentName) : undefined,
        runId:      runId      ?? '',
        projectId:  projectId  ?? '',
        reviewedBy: def.key === 'review' ? reviewedBy : undefined,
      } as WorkflowNodeData,
      position: { x: 0, y: 0 },
    }));
    return layoutDag(raw, FORWARD_EDGES, { rankdir: 'LR', rankSep: 60, nodeSep: 30 });
  }, [executorStates, agentName, reviewedBy, runId, projectId]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId      = runId.length > 8 ? runId.slice(0, 8) : runId;
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

      {/* React Flow diagram
          - fitView only fits to nodes (loop-back SVG arcs don't affect bounds)
          - elementsSelectable omitted (default true) so pointer events flow to buttons
          - nodesDraggable=false / nodesConnectable=false for read-only mode
      */}
      <div className={styles.dagContainer}>
        <ReactFlow
          nodes={rfNodes}
          edges={ALL_EDGES}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          fitView
          fitViewOptions={{ padding: 0.15, maxZoom: 1.1 }}
          minZoom={0.5}
          nodesDraggable={false}
          nodesConnectable={false}
          nodesFocusable={false}
          edgesFocusable={false}
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