import { createContext, useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowRoutingRegular,
  BotRegular,
  EditRegular,
  StopRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  Handle,
  Position,
  type Node,
  type Edge,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useRunStream } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { GraphDescriptor, SteerKind } from '../api/types';
import { API_KEY, API_URL } from '../config';
import { layoutDag, NODE_W, NODE_H, NODE_TYPE_W, NODE_TYPE_H } from '../utils/dagLayout';
import type { NodeSizeHint } from '../utils/dagLayout';
import { OutcomeSpecPanel } from '../components/OutcomeSpecPanel';
import { AgentAvatar } from '../components/AgentAvatar';
import {
  workflowNodeTypes,
  forwardEdge,
  loopbackEdge,
  roleDescForRole,
  iconForRole,
  useNodeStyles,
  StatusBadge,
  type ExecutorDef,
  type ExecutorState,
  type StepStatus,
  type WorkflowNodeData,
} from '../components/WorkflowGraphPanel';
import {
  buildTopologyState,
  initialTopologyState,
  seedTopologyFromWorkPlan,
  type CoordinatorTopologyState,
  type TopologyNodeState,
} from '../state/topologyReducer';

// ---------------------------------------------------------------------------
// Steering context — page-level; lets the steer bar trigger the dialog
// ---------------------------------------------------------------------------

interface SteerRequest {
  kind: SteerKind;
}

const CoordSteerContext = createContext<((req: SteerRequest) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Topology status helpers
// ---------------------------------------------------------------------------

function topoStatusToStepStatus(status: string): StepStatus {
  switch (status) {
    case 'dispatched':     return 'started';
    case 'running':        return 'started';
    case 'assemble_ready': return 'completed';
    case 'rai_flagged':    return 'revise';
    case 'completed':      return 'completed';
    case 'failed':         return 'failed';
    default:               return 'pending';
  }
}

function topoStatusToLabel(status: string): string {
  switch (status) {
    case 'dispatched':     return 'Dispatched';
    case 'running':        return 'Running';
    case 'assemble_ready': return 'Awaiting assembly';
    case 'rai_flagged':    return 'RAI flagged';
    case 'completed':      return 'Completed';
    case 'failed':         return 'Failed';
    default:               return 'Pending';
  }
}

/** Map a coordinator graph node id (e.g. 'plan:subtask-1') to its topology node. */
function resolveSubtaskTopoNode(
  graphNodeId: string,
  topology: CoordinatorTopologyState,
): TopologyNodeState | undefined {
  if (topology.nodes[graphNodeId]) return topology.nodes[graphNodeId];
  // Strip 'plan:' prefix: 'plan:subtask-1' → 'subtask-1'
  const stripped = graphNodeId.replace(/^plan:/, '');
  return topology.nodes[stripped];
}

// ---------------------------------------------------------------------------
// Subtask node data + custom React Flow node
// ---------------------------------------------------------------------------

interface SubtaskNodeData extends Record<string, unknown> {
  graphNodeId: string;
  label: string;
  topoStatus: string;
  topoNode: TopologyNodeState | undefined;
  childGraphRef: string | undefined;
  childRunId: string | undefined;
  agent: string | undefined;
  model: string | undefined;
  phase: string | undefined;
  projectId: string;
}

function SubtaskNode({ data }: NodeProps) {
  const s = useNodeStyles();
  const d = data as SubtaskNodeData;
  const [expanded, setExpanded] = useState(false);
  const [childLabels, setChildLabels] = useState<string[]>([]);
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  useEffect(() => {
    if (!expanded || !d.childRunId) return;
    let cancelled = false;
    apiClient.getRunGraph(d.childRunId as string)
      .then((desc) => {
        if (!cancelled && desc) setChildLabels(desc.nodes.map((n) => n.label));
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [expanded, d.childRunId]);

  const stepStatus = topoStatusToStepStatus(d.topoStatus as string);
  const statusLabel = topoStatusToLabel(d.topoStatus as string);

  return (
    <div
      className={`${s.card} ${s.cardSubtask}`}
      data-node-type="subtask"
      role="article"
      aria-label={`${d.label as string}: ${d.topoStatus as string}`}
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <Handle type="source" position={Position.Right} style={handleStyle} />

      <div className={s.cardHeader}>
        <StatusBadge status={stepStatus} label={statusLabel} />
      </div>

      <div className={s.cardMain}>
        <span className={s.cardIcon} aria-hidden="true">
          {d.agent
            ? <AgentAvatar name={d.agent as string} size={28} circle />
            : <BotRegular fontSize={22} />}
        </span>
        <div className={s.cardTitleGroup}>
          <span className={s.cardTitle}>{d.label as string}</span>
          <span className={s.cardRole}>Subtask Agent</span>
          {d.agent && <span className={s.cardSubText}>{d.agent as string}</span>}
          {d.model && <span className={s.cardModel}>{d.model as string}</span>}
          {d.phase && <span className={s.cardSubText}>{d.phase as string}</span>}
        </div>
      </div>

      {d.childGraphRef && (
        <div className={`${s.cardActions} nopan nodrag`}>
          <Button
            appearance="outline"
            size="small"
            aria-label={`${expanded ? 'Collapse' : 'Expand'} pipeline for ${d.label as string}`}
            onClick={() => setExpanded((prev) => !prev)}
          >
            {expanded ? 'Collapse pipeline' : 'Expand pipeline'}
          </Button>
          {d.childRunId && (
            <Link
              to={`/projects/${d.projectId as string}/runs/${d.childRunId as string}/workflow`}
              style={{ textDecoration: 'none' }}
            >
              <Button appearance="outline" size="small">View run</Button>
            </Link>
          )}
        </div>
      )}

      {expanded && childLabels.length > 0 && (
        <div style={{ marginTop: 8, fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3 }}>
          {childLabels.join(' → ')}
        </div>
      )}
    </div>
  );
}

/** Combined node types: generic workflow nodes + subtask expandable node. */
const coordinatorNodeTypes = { ...workflowNodeTypes, subtask: SubtaskNode };

// ---------------------------------------------------------------------------
// Page styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '1200px',
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
    flexWrap: 'wrap',
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  dagContainer: {
    height: '580px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '& .react-flow__renderer': { borderRadius: '8px' },
  },
  steerBar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  steerLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  specMax: {
    maxWidth: '860px',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

function steerKindLabel(kind: SteerKind): string {
  if (kind === 'stop') return 'Stop';
  if (kind === 'redirect') return 'Redirect';
  return 'Amend';
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function CoordinatorRunPage() {
  const styles = useStyles();
  const { projectId, runId } = useParams<{ projectId: string; runId: string }>();

  const { events, status: streamStatus } = useRunStream(runId ?? '', API_KEY, API_URL);

  // REST seed: coordinator GraphDescriptor (GET /api/runs/{id}/graph, coordinator variant).
  const [restDescriptor, setRestDescriptor] = useState<GraphDescriptor | null>(null);

  // Topology seed from work plan + children (for subtask status projection).
  const [topoSeed, setTopoSeed] = useState(initialTopologyState);

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    // Fetch graph descriptor for REST seed (so finished coordinator runs still render).
    apiClient.getRunGraph(runId)
      .then((desc) => { if (!cancelled) setRestDescriptor(desc); })
      .catch(() => {});

    // Fetch work plan + children for topology status seed.
    void (async () => {
      const [workPlan, children] = await Promise.all([
        apiClient.getWorkPlan(runId).catch(() => null),
        apiClient.getCoordinatorChildren(runId).catch(() => null),
      ]);
      if (cancelled) return;
      if (workPlan) setTopoSeed(seedTopologyFromWorkPlan(workPlan, children));
    })();

    return () => { cancelled = true; };
  }, [runId]);

  // Goal is carried by the coordinator.started event.
  const goal = useMemo<string | undefined>(() => {
    for (const evt of events) {
      if (evt.type === 'coordinator.started' && typeof evt.payload['goal'] === 'string') {
        return evt.payload['goal'] as string;
      }
    }
    return undefined;
  }, [events]);

  // coordinator.graph SSE: highest-seq-wins over REST seed (same pattern as run.workflow_graph).
  const sseDescriptor = useMemo<GraphDescriptor | undefined>(() => {
    let best: { seq: number; desc: GraphDescriptor } | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.graph') {
        const seq = typeof evt.payload['seq'] === 'number' ? evt.payload['seq'] : 0;
        if (!best || seq >= best.seq) {
          best = { seq, desc: evt.payload as unknown as GraphDescriptor };
        }
      }
    }
    return best?.desc;
  }, [events]);

  const effectiveDescriptor: GraphDescriptor | null = sseDescriptor ?? restDescriptor;

  // Topology state for subtask status projection.
  const topology = useMemo(
    () => buildTopologyState(events, topoSeed),
    [events, topoSeed],
  );

  // Build React Flow nodes + forward edges from the coordinator descriptor.
  const { rfNodes, displayEdges } = useMemo<{ rfNodes: Node[]; displayEdges: Edge[] }>(() => {
    if (!effectiveDescriptor) return { rfNodes: [], displayEdges: [] };

    const fwdEdges: Edge[] = [];
    const allEdges: Edge[] = [];
    for (const edge of effectiveDescriptor.edges) {
      const edgeId = `${edge.from}-${edge.to}`;
      if (edge.loopback) {
        allEdges.push(loopbackEdge(edgeId, edge.from, edge.to, ''));
      } else {
        const e = forwardEdge(edgeId, edge.from, edge.to);
        fwdEdges.push(e);
        allEdges.push(e);
      }
    }

    const nodeSizeHints: Record<string, NodeSizeHint> = {};
    const raw: Node[] = effectiveDescriptor.nodes.map((node) => {
      const nt = node.node_type;
      nodeSizeHints[node.id] = {
        width:  NODE_TYPE_W[nt ?? ''] ?? NODE_W,
        height: NODE_TYPE_H[nt ?? ''] ?? NODE_H,
      };

      const planned = node.kind === 'planned';

      if (nt === 'subtask') {
        // Subtask node — look up topology status by mapped id.
        const topoNode = resolveSubtaskTopoNode(node.id, topology);
        // Defensive: read display fields from flat props OR nested data map.
        const agentField  = node.agent  ?? (node.data?.['agent']  as string | undefined) ?? topoNode?.assignedAgent;
        const modelField  = node.model  ?? (node.data?.['model']  as string | undefined) ?? topoNode?.selectedModelId;
        const phaseField  = node.phase  ?? (node.data?.['phase']  as string | undefined);
        const childRunId  = node.child_run_id ?? (node.data?.['child_run_id'] as string | undefined) ?? topoNode?.childRunId;
        return {
          id:   node.id,
          type: 'subtask',
          data: {
            graphNodeId:   node.id,
            label:         node.label,
            topoStatus:    topoNode?.status ?? 'pending',
            topoNode,
            childGraphRef: node.child_graph_ref,
            childRunId,
            agent:         agentField,
            model:         modelField,
            phase:         phaseField,
            projectId:     projectId ?? '',
          } as SubtaskNodeData,
          position: { x: 0, y: 0 },
        };
      }

      // Coordinator or planned assembly node — use generic WorkflowNode.
      const coordTopoNode = topology.nodes['coordinator'];
      const coordStatus: StepStatus = node.id === 'coordinator'
        ? topoStatusToStepStatus(coordTopoNode?.status ?? 'running')
        : 'pending';

      const st: ExecutorState = planned
        ? { status: 'pending' }
        : { status: coordStatus };

      const def: ExecutorDef = {
        key:             node.id,
        label:           node.label,
        roleDescription: roleDescForRole(node.role),
        Icon:            iconForRole(node.role),
      };

      return {
        id:   node.id,
        type: 'workflow',
        data: {
          def,
          state:     st,
          isPlanned: planned,
          nodeType:  nt,
          runId:     runId      ?? '',
          executionId: '',
          projectId:   projectId ?? '',
        } as WorkflowNodeData,
        position: { x: 0, y: 0 },
      };
    });

    return {
      rfNodes:      layoutDag(raw, fwdEdges, { rankdir: 'LR', rankSep: 60, nodeSep: 30 }, nodeSizeHints),
      displayEdges: allEdges,
    };
  }, [effectiveDescriptor, topology, projectId, runId]);

  // ---------------------------------------------------------------------------
  // Steering dialog
  // ---------------------------------------------------------------------------

  const [steerReq, setSteerReq] = useState<SteerRequest | null>(null);
  const [instruction, setInstruction] = useState('');
  const [busy, setBusy] = useState(false);
  const [steerError, setSteerError] = useState<string | null>(null);

  const openSteer = useCallback((req: SteerRequest) => {
    setSteerReq(req);
    setInstruction('');
    setSteerError(null);
  }, []);

  const closeSteer = useCallback(() => {
    setSteerReq(null);
    setInstruction('');
    setSteerError(null);
    setBusy(false);
  }, []);

  const submitSteer = useCallback(async () => {
    if (!steerReq || !runId) return;
    setBusy(true);
    setSteerError(null);
    try {
      await apiClient.steerCoordinator(runId, {
        kind: steerReq.kind,
        instruction: steerReq.kind === 'stop' ? undefined : instruction.trim() || undefined,
      });
      closeSteer();
    } catch (err) {
      setSteerError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
      setBusy(false);
    }
  }, [steerReq, instruction, runId, closeSteer]);

  if (!projectId || !runId) {
    return <Text>Invalid route parameters.</Text>;
  }

  const shortId         = runId.length > 8 ? runId.slice(0, 8) : runId;
  const isConnecting    = streamStatus === 'connecting';
  const isStreaming     = streamStatus === 'streaming';
  const hasGraph        = rfNodes.length > 0;
  const needsInstruction = steerReq?.kind === 'redirect' || steerReq?.kind === 'amend';

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span aria-hidden="true">/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span aria-hidden="true">/</span>
        <span>Orchestration {shortId}</span>
      </nav>

      {/* Header */}
      <div className={styles.headerRow}>
        <Title2>Orchestration</Title2>
        <span className={styles.runIdLabel}>{shortId}</span>
        {(isConnecting || isStreaming) && <Spinner size="extra-tiny" aria-label="Connecting" />}
      </div>

      {goal && <Text className={styles.goal}>Goal: {goal}</Text>}

      {/* Unified coordinator graph — coordinator + subtasks + planned assembly. */}
      <div className={styles.section}>
        <div className={styles.sectionTitleRow}>
          <Title3>Coordinator Graph</Title3>
          {isStreaming && <Spinner size="extra-tiny" aria-label="Live" />}
        </div>
        <Text className={styles.hint}>
          Live view of the coordinator and its subtasks. Expand a subtask to see its pipeline, or use
          the steering controls to stop, redirect, or amend the orchestration.
        </Text>

        {/* Steering bar — always visible when coordinator run is mounted. */}
        <CoordSteerContext.Provider value={openSteer}>
          <div className={styles.steerBar}>
            <span className={styles.steerLabel}>Steer coordinator:</span>
            <Button appearance="subtle" size="small" icon={<StopRegular />}
              onClick={() => openSteer({ kind: 'stop' })}>
              Stop
            </Button>
            <Button appearance="subtle" size="small" icon={<ArrowRoutingRegular />}
              onClick={() => openSteer({ kind: 'redirect' })}>
              Redirect
            </Button>
            <Button appearance="subtle" size="small" icon={<EditRegular />}
              onClick={() => openSteer({ kind: 'amend' })}>
              Amend
            </Button>
          </div>

          {/* ReactFlow canvas */}
          {hasGraph ? (
            <div className={styles.dagContainer}>
              <ReactFlow
                nodes={rfNodes}
                edges={displayEdges}
                nodeTypes={coordinatorNodeTypes}
                fitView
                fitViewOptions={{ padding: 0.12, maxZoom: 1.1 }}
                minZoom={0.4}
                nodesDraggable={false}
                nodesConnectable={false}
                nodesFocusable={false}
                edgesFocusable={false}
                panOnScroll={false}
                zoomOnScroll={false}
                zoomOnPinch={false}
                zoomOnDoubleClick={false}
                panOnDrag
                proOptions={{ hideAttribution: true }}
              />
            </div>
          ) : (
            <Text className={styles.hint}>
              {isConnecting ? 'Connecting to coordinator stream...' : 'Waiting for coordinator graph...'}
            </Text>
          )}
        </CoordSteerContext.Provider>
      </div>

      {/* Outcome spec panel (confirmation gate + spec review). */}
      <div className={styles.specMax}>
        <OutcomeSpecPanel runId={runId} events={events} streamStatus={streamStatus} />
      </div>

      {/* Steering dialog */}
      <Dialog open={!!steerReq} onOpenChange={(_, d) => { if (!d.open) closeSteer(); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {steerReq ? steerKindLabel(steerReq.kind) : ''} — Coordinator
            </DialogTitle>
            <DialogContent>
              <div className={styles.dialogFields}>
                {steerReq?.kind === 'stop' ? (
                  <Text>Stop this orchestration? No further work will be dispatched.</Text>
                ) : (
                  <>
                    <Text>
                      {steerReq?.kind === 'redirect'
                        ? 'Describe the new direction for the orchestration.'
                        : 'Describe the amendment to apply to the orchestration.'}
                    </Text>
                    <Field label="Instruction" required>
                      <Textarea
                        value={instruction}
                        onChange={(_, v) => setInstruction(v.value)}
                        placeholder={steerReq?.kind === 'redirect'
                          ? 'e.g. Target the v2 API instead.'
                          : 'e.g. Also add integration tests.'}
                        rows={4}
                      />
                    </Field>
                  </>
                )}
                {steerError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{steerError}</MessageBarBody>
                  </MessageBar>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={busy} onClick={closeSteer}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={busy || (!!needsInstruction && !instruction.trim())}
                onClick={() => void submitSteer()}
              >
                {busy ? 'Sending' : steerReq?.kind === 'stop' ? 'Stop' : 'Send'}
              </Button>
              {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}

