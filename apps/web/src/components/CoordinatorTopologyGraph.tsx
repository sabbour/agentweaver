import { createContext, useCallback, useContext, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
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
  Tooltip,
  makeStyles,
  shorthands,
  tokens,
} from '@fluentui/react-components';
import type { FluentIcon } from '@fluentui/react-icons';
import {
  AlertRegular,
  ArrowMaximizeRegular,
  ArrowRoutingRegular,
  ArrowSyncRegular,
  BotRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  ClockRegular,
  DismissCircleRegular,
  EditRegular,
  FlowRegular,
  HourglassRegular,
  OpenRegular,
  SendRegular,
  StopRegular,
  ZoomInRegular,
  ZoomOutRegular,
} from '@fluentui/react-icons';
import {
  ReactFlow,
  MarkerType,
  Panel,
  Position,
  Handle,
  useReactFlow,
  useStore,
  type Node,
  type Edge,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { SteerKind, TopologyEdge } from '../api/types';
import type { TopologyNodeState } from '../state/topologyReducer';
import { DAG_NODE_SEP, layoutDag, NODE_W, RENDERED_TOPOLOGY_NODE_H } from '../utils/dagLayout';
import { AgentAvatar } from './AgentAvatar';
import { PodIndicator } from './PodIndicator';
import { useRuntimeInfo } from '../hooks/useRuntimeInfo';
import { STEERING_HELP } from './steeringHelp';

// ---------------------------------------------------------------------------
// Steering context — lets a custom node trigger a steering action without
// threading callbacks through React Flow node data.
// ---------------------------------------------------------------------------

interface SteerRequest {
  node: TopologyNodeState;
  kind: SteerKind;
}

const SteerContext = createContext<((req: SteerRequest) => void) | undefined>(undefined);

// ---------------------------------------------------------------------------
// Status presentation — render server status as-is (Principle III).
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '14px',
    width: `${NODE_W}px`,
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: '8px',
    cursor: 'default',
  },
  cardCoordinator: {
    borderLeft: `3px solid ${tokens.colorBrandForeground1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  cardActive: {
    borderLeft: `3px solid ${tokens.colorBrandForeground1}`,
  },
  cardFlagged: {
    border: `2px solid ${tokens.colorPaletteMarigoldBorderActive}`,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
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
  badgePending: { backgroundColor: tokens.colorNeutralBackground4, color: tokens.colorNeutralForeground3 },
  badgeDispatched: { backgroundColor: tokens.colorPaletteLightTealBackground2, color: tokens.colorPaletteLightTealForeground2 },
  badgeRunning: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1 },
  badgeAssemble: { backgroundColor: tokens.colorPaletteLavenderBackground2, color: tokens.colorPaletteLavenderForeground2 },
  badgeFlagged: { backgroundColor: tokens.colorPaletteMarigoldBorderActive, color: tokens.colorNeutralForegroundInverted },
  badgePendingCapacity: { backgroundColor: tokens.colorPaletteMarigoldBackground2, color: tokens.colorPaletteMarigoldForeground2 },
  badgeCompleted: { backgroundColor: tokens.colorPaletteGreenBackground2, color: tokens.colorPaletteGreenForeground1 },
  badgeFailed: { backgroundColor: tokens.colorPaletteRedBackground2, color: tokens.colorPaletteRedForeground1 },
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
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardSubText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginTop: '2px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardModel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    fontFamily: tokens.fontFamilyMonospace,
    marginTop: '2px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  steeringNote: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground2,
  },
  assembleNote: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorPaletteLavenderForeground2,
  },
  actions: {
    marginTop: tokens.spacingVerticalXS,
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  container: {
    position: 'relative',
    height: '560px',
    borderRadius: '8px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    '& .react-flow__renderer': { borderRadius: '8px' },
  },
  zoomCluster: {
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.gap('2px'),
    padding: '4px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow8,
  },
  zoomLevel: {
    minWidth: '52px',
    fontVariantNumeric: 'tabular-nums',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

interface StatusMeta {
  label: string;
  badgeClass: keyof ReturnType<typeof useStyles>;
  Icon: FluentIcon;
}

function statusMeta(status: string, styles: ReturnType<typeof useStyles>): { label: string; className: string; Icon: FluentIcon } {
  const table: Record<string, StatusMeta> = {
    pending: { label: 'Pending', badgeClass: 'badgePending', Icon: CircleRegular },
    dispatched: { label: 'Dispatched', badgeClass: 'badgeDispatched', Icon: SendRegular },
    running: { label: 'Running', badgeClass: 'badgeRunning', Icon: ArrowSyncRegular },
    assemble_ready: { label: 'Awaiting assembly', badgeClass: 'badgeAssemble', Icon: ClockRegular },
    rai_flagged: { label: 'RAI flagged', badgeClass: 'badgeFlagged', Icon: AlertRegular },
    pending_capacity: { label: 'Waiting for capacity', badgeClass: 'badgePendingCapacity', Icon: HourglassRegular },
    completed: { label: 'Completed', badgeClass: 'badgeCompleted', Icon: CheckmarkCircleRegular },
    failed: { label: 'Failed', badgeClass: 'badgeFailed', Icon: DismissCircleRegular },
  };
  const meta = table[status] ?? { label: status, badgeClass: 'badgePending' as const, Icon: CircleRegular };
  return { label: meta.label, className: styles[meta.badgeClass] as string, Icon: meta.Icon };
}

const ACTIVE_STATUSES = new Set(['dispatched', 'running', 'assemble_ready', 'rai_flagged', 'pending_capacity']);

// ---------------------------------------------------------------------------
// Node data passed into the React Flow custom node
// ---------------------------------------------------------------------------

interface TopologyNodeData extends Record<string, unknown> {
  node: TopologyNodeState;
  projectId: string;
}

function TopologyNodeCard({ data }: NodeProps) {
  const styles = useStyles();
  const { node, projectId } = data as TopologyNodeData;
  const { podName: globalPodName } = useRuntimeInfo();
  // Per-node pod name takes priority; fall back to the global API pod when on k8s.
  const resolvedPodName = node.executionPodName ?? globalPodName;
  const onSteer = useContext(SteerContext);

  const isCoordinator = node.kind === 'coordinator';
  const sm = statusMeta(node.status, styles);
  const isActive = ACTIVE_STATUSES.has(node.status);
  const isFlagged = node.status === 'rai_flagged';

  const cardClass = [
    styles.card,
    isCoordinator ? styles.cardCoordinator : '',
    isFlagged ? styles.cardFlagged : isActive ? styles.cardActive : '',
  ].filter(Boolean).join(' ');

  // Steering is offered on the coordinator (whole orchestration) and on active
  // subtasks that already have a child run to target.
  const canSteer = isCoordinator || (node.kind === 'subtask' && !!node.childRunId && ACTIVE_STATUSES.has(node.status));
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  return (
    <>
      <PodIndicator podName={resolvedPodName} />
      <div className={cardClass} role="article" aria-label={`${node.title}: ${sm.label}`}>
        <Handle type="target" position={Position.Left} style={handleStyle} />
        <Handle type="source" position={Position.Right} style={handleStyle} />

      <div className={styles.cardHeader}>
        {node.status === 'pending_capacity' ? (
          <Tooltip
            content="The agent pod could not start — the cluster is at capacity. The system will retry automatically."
            relationship="description"
            positioning="above"
            withArrow
          >
            <span className={`${styles.statusBadge} ${sm.className}`}>
              <sm.Icon fontSize={10} aria-hidden="true" />
              {sm.label}
            </span>
          </Tooltip>
        ) : (
          <span className={`${styles.statusBadge} ${sm.className}`}>
            <sm.Icon fontSize={10} aria-hidden="true" />
            {sm.label}
          </span>
        )}
      </div>

      <div className={styles.cardMain}>
        <span className={styles.cardIcon} aria-hidden="true">
          {isCoordinator ? (
            <FlowRegular fontSize={24} />
          ) : node.assignedAgent ? (
            <AgentAvatar name={node.assignedAgent} size={28} circle />
          ) : (
            <BotRegular fontSize={22} />
          )}
        </span>
        <div className={styles.cardTitleGroup}>
          <span className={styles.cardTitle}>{node.title}</span>
          {isCoordinator && <span className={styles.cardSubText}>Coordinator</span>}
          {node.assignedAgent && <span className={styles.cardSubText}>{node.assignedAgent}</span>}
          {node.selectedModelId && <span className={styles.cardModel}>{node.selectedModelId}</span>}
        </div>
      </div>

      {node.steering && (
        <span className={styles.steeringNote}>
          <ArrowRoutingRegular fontSize={12} aria-hidden="true" />
          {steerKindLabel(node.steering.kind)} · {node.steering.status}
        </span>
      )}

      {node.kind === 'subtask' && node.status === 'assemble_ready' && (
        <span className={styles.assembleNote}>
          <ClockRegular fontSize={12} aria-hidden="true" />
          Finished its part — waiting for collective assembly
        </span>
      )}

      <div className={`${styles.actions} nopan nodrag`}>
        {node.kind === 'subtask' && node.childRunId && (
          <Link to={`/projects/${projectId}/runs/${node.childRunId}/workflow`} style={{ textDecoration: 'none' }}>
            <Button appearance="outline" size="small" icon={<OpenRegular />}>View run</Button>
          </Link>
        )}
        {canSteer && (
          <>
            <Button appearance="subtle" size="small" icon={<StopRegular />} onClick={() => onSteer?.({ node, kind: 'stop' })}>
              Stop
            </Button>
            <Button appearance="subtle" size="small" icon={<ArrowRoutingRegular />} onClick={() => onSteer?.({ node, kind: 'redirect' })}>
              Redirect
            </Button>
            <Button appearance="subtle" size="small" icon={<EditRegular />} onClick={() => onSteer?.({ node, kind: 'amend' })}>
              Amend
            </Button>
          </>
        )}
      </div>
      </div>
    </>
  );
}

const nodeTypes = { topology: TopologyNodeCard };

function steerKindLabel(kind: SteerKind): string {
  if (kind === 'stop') return 'Stop';
  if (kind === 'send') return 'Send';
  if (kind === 'redirect') return 'Redirect';
  return 'Amend';
}

// ---------------------------------------------------------------------------
// Edge helper
// ---------------------------------------------------------------------------

const STROKE_MUTED = 'var(--colorNeutralStroke2)';

function topoEdge(source: string, target: string): Edge {
  return {
    id: `${source}->${target}`,
    source,
    target,
    type: 'default',
    style: { stroke: STROKE_MUTED, strokeWidth: 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: STROKE_MUTED, width: 12, height: 12 },
  };
}

// ---------------------------------------------------------------------------
// Zoom / pan controls
// ---------------------------------------------------------------------------

const MIN_ZOOM = 0.2;
const MAX_ZOOM = 2;
const ZOOM_DURATION = 200;
const FIT_VIEW_OPTIONS = { padding: 0.15, maxZoom: 1.1, duration: ZOOM_DURATION };

// Always-visible zoom/pan control cluster. Rendered inside <ReactFlow> as a Panel so
// it shares the flow store context (useReactFlow / useStore). Buttons are the reliable,
// discoverable path to zoom; wheel-zoom is gated behind Ctrl/Cmd so the graph never
// hijacks normal page scroll. Exported for isolated testing.
export function GraphControls() {
  const styles = useStyles();
  const { zoomIn, zoomOut, zoomTo, fitView } = useReactFlow();
  const zoom = useStore((s) => s.transform[2]);
  const zoomPercent = `${Math.round((zoom ?? 1) * 100)}%`;

  return (
    <div className={styles.zoomCluster} role="group" aria-label="Graph zoom controls">
      <Tooltip content="Zoom in" relationship="label" withArrow>
        <Button
          appearance="subtle"
          size="small"
          icon={<ZoomInRegular />}
          aria-label="Zoom in"
          onClick={() => void zoomIn({ duration: ZOOM_DURATION })}
        />
      </Tooltip>
      <Tooltip content="Reset to 100%" relationship="label" withArrow>
        <Button
          appearance="subtle"
          size="small"
          className={styles.zoomLevel}
          aria-label={`Reset zoom to 100% (currently ${zoomPercent})`}
          onClick={() => void zoomTo(1, { duration: ZOOM_DURATION })}
        >
          {zoomPercent}
        </Button>
      </Tooltip>
      <Tooltip content="Zoom out" relationship="label" withArrow>
        <Button
          appearance="subtle"
          size="small"
          icon={<ZoomOutRegular />}
          aria-label="Zoom out"
          onClick={() => void zoomOut({ duration: ZOOM_DURATION })}
        />
      </Tooltip>
      <Tooltip content="Fit to view" relationship="label" withArrow>
        <Button
          appearance="subtle"
          size="small"
          icon={<ArrowMaximizeRegular />}
          aria-label="Fit to view"
          onClick={() => void fitView(FIT_VIEW_OPTIONS)}
        />
      </Tooltip>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Graph
// ---------------------------------------------------------------------------

interface CoordinatorTopologyGraphProps {
  projectId: string;
  coordinatorRunId: string;
  nodes: TopologyNodeState[];
  edges: TopologyEdge[];
}

export function CoordinatorTopologyGraph({ projectId, coordinatorRunId, nodes, edges }: CoordinatorTopologyGraphProps) {
  const styles = useStyles();

  const [steerReq, setSteerReq] = useState<SteerRequest | null>(null);
  const [instruction, setInstruction] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const openSteer = useCallback((req: SteerRequest) => {
    setSteerReq(req);
    setInstruction('');
    setError(null);
  }, []);

  const closeSteer = useCallback(() => {
    setSteerReq(null);
    setInstruction('');
    setError(null);
    setBusy(false);
  }, []);

  const submitSteer = useCallback(async () => {
    if (!steerReq) return;
    setBusy(true);
    setError(null);
    try {
      const targetChildRunId = steerReq.node.kind === 'subtask' ? steerReq.node.childRunId : undefined;
      await apiClient.steerCoordinator(coordinatorRunId, {
        kind: steerReq.kind,
        target_child_run_id: targetChildRunId,
        instruction: steerReq.kind === 'stop' ? undefined : instruction.trim() || undefined,
      });
      closeSteer();
    } catch (err) {
      setError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
      setBusy(false);
    }
  }, [steerReq, instruction, coordinatorRunId, closeSteer]);

  // Build forward edges from server dependency edges. As pure layout glue (not
  // topology computation), connect the coordinator node to root subtasks ONLY
  // when the server did not already wire the coordinator into the graph.
  const displayEdges = useMemo<TopologyEdge[]>(() => {
    const coordinator = nodes.find((n) => n.kind === 'coordinator');
    const coordinatorWired = coordinator
      ? edges.some((e) => e.from === coordinator.id || e.to === coordinator.id)
      : true;
    if (coordinator && !coordinatorWired) {
      const hasIncoming = new Set(edges.map((e) => e.to));
      const roots = nodes.filter((n) => n.kind === 'subtask' && !hasIncoming.has(n.id));
      return [...edges, ...roots.map((r) => ({ from: coordinator.id, to: r.id }))];
    }
    return edges;
  }, [nodes, edges]);

  const rfEdges = useMemo<Edge[]>(() => displayEdges.map((e) => topoEdge(e.from, e.to)), [displayEdges]);

  const rfNodes = useMemo<Node[]>(() => {
    const raw: Node[] = nodes.map((node) => ({
      id: node.id,
      type: 'topology',
      data: { node, projectId } as TopologyNodeData,
      position: { x: 0, y: 0 },
    }));
    const nodeSizeHints = Object.fromEntries(
      nodes.map((node) => [node.id, { width: NODE_W, height: node.kind === 'coordinator' ? 220 : RENDERED_TOPOLOGY_NODE_H }]),
    );
    return layoutDag(raw, rfEdges, { rankdir: 'LR', rankSep: 80, nodeSep: DAG_NODE_SEP }, nodeSizeHints);
  }, [nodes, projectId, rfEdges]);

  const needsInstruction = steerReq?.kind === 'redirect' || steerReq?.kind === 'amend';

  return (
    <>
      <SteerContext.Provider value={openSteer}>
        <div className={styles.container}>
          <ReactFlow
            nodes={rfNodes}
            edges={rfEdges}
            nodeTypes={nodeTypes}
            fitView
            fitViewOptions={FIT_VIEW_OPTIONS}
            minZoom={MIN_ZOOM}
            maxZoom={MAX_ZOOM}
            nodesDraggable={false}
            nodesConnectable={false}
            nodesFocusable={false}
            edgesFocusable={false}
            panOnScroll={false}
            zoomOnScroll
            zoomActivationKeyCode={['Meta', 'Control']}
            zoomOnPinch
            zoomOnDoubleClick={false}
            panOnDrag
            proOptions={{ hideAttribution: true }}
          >
            <Panel position="top-right">
              <GraphControls />
            </Panel>
          </ReactFlow>
        </div>
      </SteerContext.Provider>

      <Dialog open={!!steerReq} onOpenChange={(_, d) => { if (!d.open) closeSteer(); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {steerReq ? steerKindLabel(steerReq.kind) : ''}{steerReq ? ` — ${steerReq.node.title}` : ''}
            </DialogTitle>
            <DialogContent>
              <div className={styles.dialogFields}>
                {steerReq?.kind === 'stop' ? (
                  <Text>
                    Stop {steerReq.node.kind === 'coordinator' ? 'this orchestration' : 'this subtask'}? No
                    further work will be dispatched for it.
                  </Text>
                ) : (
                  <>
                    <Text>
                      {steerReq?.kind === 'redirect'
                        ? STEERING_HELP.redirect
                        : STEERING_HELP.amend}
                    </Text>
                    <Field label="Instruction" required>
                      <Textarea
                        value={instruction}
                        onChange={(_, v) => setInstruction(v.value)}
                        placeholder={steerReq?.kind === 'redirect'
                          ? 'e.g. Target the v2 API instead and skip the legacy adapter.'
                          : 'e.g. Also add integration tests for the new endpoint.'}
                        rows={4}
                      />
                    </Field>
                  </>
                )}
                {error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{error}</MessageBarBody>
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

      {/* Steering dialog above is the only overlay this graph owns. */}
    </>
  );
}
