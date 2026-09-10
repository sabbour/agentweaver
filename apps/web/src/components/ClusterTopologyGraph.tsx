import {
  Badge,
  Card,
  CardHeader,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import '@xyflow/react/dist/style.css';
import {
  Handle,
  MarkerType,
  Position,
  ReactFlow,
} from '@xyflow/react';
import { useMemo, useState } from 'react';
import type { Edge, Node, NodeProps } from '@xyflow/react';
import type {
  KubernetesTopologyDto,
  KubernetesTopologyNodeDto,
} from '../api/types';

const NODE_WIDTH = 250;
const NODE_HEIGHT = 90;
const COLUMN_GAP = 90;
const ROW_GAP = 24;

const useStyles = makeStyles({
  container: { display: 'grid', gap: tokens.spacingVerticalM },
  graphViewport: {
    height: '520px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  graphCanvas: { height: '100%' },
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
    cursor: 'pointer',
  },
  healthy: {
    borderTopColor: tokens.colorPaletteGreenBorder2,
    borderRightColor: tokens.colorPaletteGreenBorder2,
    borderBottomColor: tokens.colorPaletteGreenBorder2,
    borderLeftColor: tokens.colorPaletteGreenBorder2,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  attention: {
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
    overflowWrap: 'anywhere',
  },
  detail: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  drilldown: { maxWidth: '720px' },
  details: {
    display: 'grid',
    gridTemplateColumns: 'minmax(120px, auto) 1fr',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    margin: 0,
  },
  detailKey: { fontWeight: tokens.fontWeightSemibold },
  layerStatus: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
});

interface GraphNodeData extends Record<string, unknown> {
  resource: KubernetesTopologyNodeDto;
}

function ResourceNode({ data }: NodeProps) {
  const styles = useStyles();
  const resource = (data as GraphNodeData).resource;
  const handleStyle: React.CSSProperties = { opacity: 0, pointerEvents: 'none' };
  return (
    <div
      className={`${styles.node} ${styles[resource.health]}`}
      aria-label={`${resource.name}: ${resource.summary}`}
      title={`${resource.type} ${resource.namespace ? `${resource.namespace}/` : ''}${resource.name}`}
      data-testid="cluster-topology-node"
    >
      <Handle type="target" position={Position.Left} style={handleStyle} />
      <span className={styles.title}>{resource.name}</span>
      <span className={styles.detail}>{resource.summary}</span>
      <span className={styles.detail}>{resource.type} · {resource.layer}</span>
      <Handle type="source" position={Position.Right} style={handleStyle} />
    </div>
  );
}

const nodeTypes = { resource: ResourceNode };

export function ClusterTopologyGraph({ topology }: { topology: KubernetesTopologyDto }) {
  const styles = useStyles();
  const [selected, setSelected] = useState<KubernetesTopologyNodeDto | null>(null);
  const { nodes, edges } = useMemo(() => {
    const types = [...new Set(topology.nodes.map(node => node.type))].sort();
    const typeColumns = new Map(types.map((type, index) => [type, index]));
    const rows = new Map<number, number>();
    const nodes: Node[] = topology.nodes.map(resource => {
      const column = typeColumns.get(resource.type) ?? 0;
      const row = rows.get(column) ?? 0;
      rows.set(column, row + 1);
      return {
        id: resource.id,
        type: 'resource',
        data: { resource },
        position: {
          x: column * (NODE_WIDTH + COLUMN_GAP),
          y: row * (NODE_HEIGHT + ROW_GAP),
        },
      };
    });
    const edges: Edge[] = topology.edges.map(edge => {
      const stroke = edge.inferred
        ? 'var(--colorNeutralStroke3)'
        : 'var(--colorBrandStroke1)';
      return {
        id: edge.id,
        source: edge.source,
        target: edge.target,
        label: edge.type,
        title: edge.summary,
        animated: false,
        style: {
          stroke,
          strokeWidth: edge.inferred ? 1 : 1.6,
          strokeDasharray: edge.inferred ? '5 4' : undefined,
        },
        markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 12, height: 12 },
      };
    });
    return { nodes, edges };
  }, [topology]);

  return (
    <div className={styles.container} data-testid="cluster-topology-graph">
      <div className={styles.layerStatus}>
        {topology.layers.filter(layer => topology.requested_layers.includes(layer.name)).map(layer => (
          <Badge
            key={layer.name}
            appearance="tint"
            color={layer.status === 'available' ? 'success' : layer.status === 'partial' ? 'warning' : 'danger'}
            title={layer.message}
          >
            {layer.name}: {layer.resource_count}
          </Badge>
        ))}
        {topology.truncated && <Badge color="warning">Result truncated</Badge>}
      </div>
      <div className={styles.graphViewport} data-testid="cluster-topology-viewport">
        <ReactFlow
          className={styles.graphCanvas}
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          onNodeClick={(_, node) => setSelected((node.data as GraphNodeData).resource)}
          fitView
          fitViewOptions={{ padding: 0.12, maxZoom: 1 }}
          minZoom={0.15}
          maxZoom={2}
          nodesDraggable={false}
          nodesConnectable={false}
          panOnScroll
          zoomOnScroll
          zoomActivationKeyCode={['Meta', 'Control']}
          zoomOnDoubleClick={false}
          panOnDrag
          proOptions={{ hideAttribution: true }}
        />
      </div>
      {selected && (
        <Card className={styles.drilldown} aria-label={`${selected.name} resource details`}>
          <CardHeader
            header={<strong>{selected.name}</strong>}
            description={`${selected.type} · ${selected.namespace ?? 'cluster scoped'} · ${selected.health}`}
          />
          <p>{selected.summary}</p>
          <dl className={styles.details}>
            <dt className={styles.detailKey}>API version</dt><dd>{selected.api_version}</dd>
            {Object.entries(selected.details).map(([key, value]) => (
              <span key={key} style={{ display: 'contents' }}>
                <dt className={styles.detailKey}>{key}</dt><dd>{value}</dd>
              </span>
            ))}
          </dl>
        </Card>
      )}
    </div>
  );
}
