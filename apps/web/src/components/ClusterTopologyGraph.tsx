import { makeStyles, tokens } from '@fluentui/react-components';
import '@xyflow/react/dist/style.css';
import {
  Handle,
  MarkerType,
  Position,
  ReactFlow,
} from '@xyflow/react';
import { useMemo } from 'react';
import type { Edge, Node, NodeProps } from '@xyflow/react';
import type { ClusterDiagnosticsDto } from '../api/types';

const NODE_WIDTH = 190;
const NODE_HEIGHT = 72;
const COLUMN_GAP = 100;
const ROW_GAP = 28;

const useStyles = makeStyles({
  container: {
    height: '440px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  node: {
    width: `${NODE_WIDTH}px`,
    minHeight: `${NODE_HEIGHT}px`,
    boxSizing: 'border-box',
    padding: tokens.spacingHorizontalM,
    border: `2px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  healthy: {
    borderTopColor: tokens.colorPaletteGreenBorder2,
    borderRightColor: tokens.colorPaletteGreenBorder2,
    borderBottomColor: tokens.colorPaletteGreenBorder2,
    borderLeftColor: tokens.colorPaletteGreenBorder2,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  warning: {
    borderTopColor: tokens.colorPaletteMarigoldBorderActive,
    borderRightColor: tokens.colorPaletteMarigoldBorderActive,
    borderBottomColor: tokens.colorPaletteMarigoldBorderActive,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
    backgroundColor: tokens.colorPaletteMarigoldBackground1,
  },
  critical: {
    borderTopColor: tokens.colorPaletteRedBorder2,
    borderRightColor: tokens.colorPaletteRedBorder2,
    borderBottomColor: tokens.colorPaletteRedBorder2,
    borderLeftColor: tokens.colorPaletteRedBorder2,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  unknown: {},
  title: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  detail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
});

type NodeStatus = 'healthy' | 'warning' | 'critical' | 'unknown';

interface ClusterNodeData extends Record<string, unknown> {
  title: string;
  detail: string;
  status: NodeStatus;
}

function ClusterNode({ data }: NodeProps) {
  const styles = useStyles();
  const node = data as ClusterNodeData;
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  return (
    <div
      className={`${styles.node} ${styles[node.status]}`}
      aria-label={`${node.title}: ${node.detail}`}
      data-testid="cluster-topology-node"
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <span className={styles.title}>{node.title}</span>
      <span className={styles.detail}>{node.detail}</span>
      <Handle type="source" position={Position.Right} style={handleStyle} />
    </div>
  );
}

const nodeTypes = { cluster: ClusterNode };

function clusterStatus(data: ClusterDiagnosticsDto): NodeStatus {
  if (data.checks.some((check) => check.status === 'critical' || check.status === 'degraded')) return 'critical';
  if (data.checks.some((check) => check.status === 'warning')) return 'warning';
  return data.checks.length > 0 && data.checks.every((check) => check.status === 'healthy')
    ? 'healthy'
    : 'unknown';
}

function claimStatus(phase: string, ready: boolean): NodeStatus {
  if (phase === 'bound' && ready) return 'healthy';
  return phase === 'pending' ? 'warning' : 'unknown';
}

function podStatus(status: string): NodeStatus {
  if (status === 'ready') return 'healthy';
  return status === 'pending' ? 'warning' : 'unknown';
}

function topologyEdge(source: string, target: string): Edge {
  const stroke = 'var(--colorNeutralStroke2)';
  return {
    id: `${source}->${target}`,
    source,
    target,
    style: { stroke, strokeWidth: 1.5 },
    markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 12, height: 12 },
  };
}

export function ClusterTopologyGraph({ data }: { data: ClusterDiagnosticsDto }) {
  const { nodes, edges } = useMemo(() => {
    const nodes: Node[] = [];
    const edges: Edge[] = [];
    const poolIds = new Map<string, string>();
    const claimIds = new Map<string, string>();
    const columns = [0, NODE_WIDTH + COLUMN_GAP, (NODE_WIDTH + COLUMN_GAP) * 2, (NODE_WIDTH + COLUMN_GAP) * 3];
    const y = [0, 0, 0, 0];
    const addNode = (id: string, column: number, node: ClusterNodeData) => {
      nodes.push({
        id,
        type: 'cluster',
        data: node,
        position: { x: columns[column], y: y[column] },
      });
      y[column] += NODE_HEIGHT + ROW_GAP;
    };

    addNode('cluster', 0, {
      title: 'Cluster',
      detail: `${data.checks.filter((check) => check.status === 'healthy').length} / ${data.checks.length} checks healthy`,
      status: clusterStatus(data),
    });

    (data.warm_pools ?? []).forEach((pool, index) => {
      const id = `pool-${index}`;
      poolIds.set(pool.name, id);
      addNode(id, 1, {
        title: pool.name,
        detail: `Warm pool · ${pool.ready_replicas} / ${pool.desired_replicas} ready`,
        status: pool.status === 'healthy' ? 'healthy' : pool.status === 'warning' ? 'warning' : pool.status === 'critical' ? 'critical' : 'unknown',
      });
      edges.push(topologyEdge('cluster', id));
    });

    (data.sandbox_claims ?? []).forEach((claim, index) => {
      const id = `claim-${index}`;
      claimIds.set(claim.name, id);
      addNode(id, 2, {
        title: claim.name,
        detail: `Sandbox claim · ${claim.phase}`,
        status: claimStatus(claim.phase, claim.ready),
      });
      edges.push(topologyEdge(poolIds.get(claim.warm_pool ?? '') ?? 'cluster', id));
    });

    [...data.active_agent_pods, ...data.orphaned_agent_pods].forEach((pod, index) => {
      const id = `pod-${index}`;
      addNode(id, 3, {
        title: pod.pod_name ?? pod.claim_name,
        detail: `Agent pod · ${pod.status}`,
        status: podStatus(pod.status),
      });
      edges.push(topologyEdge(claimIds.get(pod.claim_name) ?? 'cluster', id));
    });

    return { nodes, edges };
  }, [data]);

  const styles = useStyles();
  return (
    <div className={styles.container} data-testid="cluster-topology-graph">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        fitView
        fitViewOptions={{ padding: 0.15, maxZoom: 1 }}
        minZoom={0.2}
        maxZoom={2}
        nodesDraggable={false}
        nodesConnectable={false}
        nodesFocusable={false}
        edgesFocusable={false}
        panOnScroll
        zoomOnScroll
        zoomActivationKeyCode={['Meta', 'Control']}
        zoomOnDoubleClick={false}
        panOnDrag
        proOptions={{ hideAttribution: true }}
      />
    </div>
  );
}
