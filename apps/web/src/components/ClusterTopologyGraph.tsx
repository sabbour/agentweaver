import { makeStyles, tokens } from '@fluentui/react-components';
import '@xyflow/react/dist/style.css';
import {
  Handle,
  MarkerType,
  Position,
  ReactFlow,
} from '@xyflow/react';
import { useMemo } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import type { Edge, Node, NodeProps } from '@xyflow/react';
import type { ClusterDiagnosticsDto } from '../api/types';

const NODE_WIDTH = 280;
const NODE_HEIGHT = 96;
const COLUMN_GAP = 100;
const ROW_GAP = 28;

const useStyles = makeStyles({
  container: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
  },
  graphViewport: {
    height: '440px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  graphCanvas: {
    height: '100%',
  },
  instancePanel: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
  },
  instanceGroup: {
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  instanceGroupTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  instanceList: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'grid',
    gap: tokens.spacingVerticalXS,
  },
  instanceListItem: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  instanceListMeta: {
    minWidth: 0,
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
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
    wordBreak: 'break-word',
  },
  detail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  instanceName: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  link: {
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'underline',
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
  linkLabel?: string;
  linkTo?: string;
}

function ClusterNode({ data }: NodeProps) {
  const styles = useStyles();
  const node = data as ClusterNodeData;
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };

  return (
    <div
      className={`${styles.node} ${styles[node.status]}`}
      aria-label={`${node.title}: ${node.detail}${node.linkLabel ? ` · ${node.linkLabel}` : ''}`}
      data-testid="cluster-topology-node"
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <span className={styles.title} title={node.title}>{node.title}</span>
      <span className={styles.detail}>{node.detail}</span>
      {node.linkLabel && node.linkTo ? (
        <RouterLink to={node.linkTo} className={styles.link}>
          {node.linkLabel}
        </RouterLink>
      ) : null}
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

function instanceStatus(status: string): NodeStatus {
  if (status === 'available') return 'healthy';
  if (status === 'claimed') return 'warning';
  return 'unknown';
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
    const instanceIdsByClaimName = new Map<string, string>();
    const claimIds = new Map<string, string>();
    const columns = [0, NODE_WIDTH + COLUMN_GAP, (NODE_WIDTH + COLUMN_GAP) * 2, (NODE_WIDTH + COLUMN_GAP) * 3, (NODE_WIDTH + COLUMN_GAP) * 4];
    const y = [0, 0, 0, 0, 0];
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

    (data.warm_pools ?? []).forEach((pool, poolIndex) => {
      (pool.instances ?? []).forEach((instance, instanceIndex) => {
        const id = `instance-${poolIndex}-${instanceIndex}`;
        if (instance.claim_name) instanceIdsByClaimName.set(instance.claim_name, id);
        addNode(id, 2, {
          title: instance.name,
          detail: instance.status === 'claimed'
            ? 'Warm instance · claimed'
            : instance.status === 'available'
              ? 'Warm instance · available'
              : 'Warm instance · warming',
          status: instanceStatus(instance.status),
          linkLabel: instance.run_id && instance.project_id ? instance.run_id : undefined,
          linkTo: instance.run_id && instance.project_id
            ? `/projects/${instance.project_id}/orchestrations/${instance.run_id}`
            : undefined,
        });
        edges.push(topologyEdge(poolIds.get(pool.name) ?? 'cluster', id));
      });
    });

    (data.sandbox_claims ?? []).forEach((claim, index) => {
      const id = `claim-${index}`;
      claimIds.set(claim.name, id);
      addNode(id, 3, {
        title: claim.name,
        detail: `Sandbox claim · ${claim.phase}`,
        status: claimStatus(claim.phase, claim.ready),
      });
      edges.push(topologyEdge(instanceIdsByClaimName.get(claim.name) ?? poolIds.get(claim.warm_pool ?? '') ?? 'cluster', id));
    });

    [...data.active_agent_pods, ...data.orphaned_agent_pods].forEach((pod, index) => {
      const id = `pod-${index}`;
      addNode(id, 4, {
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
      <div className={styles.graphViewport} data-testid="cluster-topology-viewport">
        <ReactFlow
          className={styles.graphCanvas}
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
      <div className={styles.instancePanel}>
        {(data.warm_pools ?? []).map((pool) => (
          <section key={`${pool.name}-instances`} className={styles.instanceGroup}>
            <span className={styles.instanceGroupTitle}>{pool.name} instances</span>
            <ul className={styles.instanceList}>
              {(pool.instances ?? []).map((instance) => (
                <li
                  key={`${pool.name}-${instance.name}`}
                  className={styles.instanceListItem}
                  aria-label={`${instance.name}: Warm instance · ${instance.status}`}
                >
                  <div className={styles.instanceListMeta}>
                    <div className={styles.instanceName} title={instance.name}>{instance.name}</div>
                    <div className={styles.detail}>
                      {instance.status === 'claimed'
                        ? `Claimed${instance.claim_name ? ` by ${instance.claim_name}` : ''}`
                        : instance.status === 'available'
                          ? 'Unclaimed warm instance'
                          : 'Warming up'}
                    </div>
                  </div>
                  {instance.run_id && instance.project_id ? (
                    <RouterLink
                      to={`/projects/${instance.project_id}/orchestrations/${instance.run_id}`}
                      className={styles.link}
                    >
                      {instance.run_id}
                    </RouterLink>
                  ) : null}
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </div>
  );
}
