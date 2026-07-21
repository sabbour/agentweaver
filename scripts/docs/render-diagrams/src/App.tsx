import { useEffect, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  type Edge,
  type Node,
  type NodeProps,
  ReactFlowProvider,
  useReactFlow,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { layoutDiagram, type DiagramLayout } from './layout';
import { buildSvg } from './svgExport';
import { NODE_VARIANT_STYLES, type DiagramSource, type NodeVariant } from './diagramTypes';

/** Simple rounded-rect card node — mirrors WorkflowGraphPanel's card shape/shadow, coloured per the
 *  variant palette carried over from the original Mermaid classDefs. */
function ArchNode({ data }: NodeProps) {
  const variant = data.variant as NodeVariant;
  const style = NODE_VARIANT_STYLES[variant];
  return (
    <div
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        background: style.fill,
        border: `${style.strokeWidth}px solid ${style.stroke}`,
        borderRadius: 8,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        textAlign: 'center',
        whiteSpace: 'pre-line',
        fontSize: 14,
        color: '#242424',
        padding: '4px 8px',
      }}
    >
      {String(data.label)}
    </div>
  );
}

function ArchGroup({ data }: NodeProps) {
  return (
    <div
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        background: '#FAF9F8',
        border: '1.5px solid #D2D0CE',
        borderRadius: 10,
      }}
    >
      <div style={{ fontSize: 14, fontWeight: 600, color: '#242424', padding: '6px 0 0 12px' }}>{String(data.label)}</div>
    </div>
  );
}

const nodeTypes = { archNode: ArchNode, archGroup: ArchGroup };

function toFlowElements(layout: DiagramLayout): { nodes: Node[]; edges: Edge[] } {
  const nodes: Node[] = [
    ...layout.groups.map((group) => ({
      id: group.id,
      type: 'archGroup',
      position: { x: group.x, y: group.y },
      style: { width: group.width, height: group.height },
      data: { label: group.label },
      draggable: false,
      selectable: false,
    })),
    ...layout.nodes.map((node) => ({
      id: node.id,
      type: 'archNode',
      position: { x: node.x, y: node.y },
      style: { width: node.width, height: node.height },
      data: { label: node.label, variant: node.variant },
      draggable: false,
      selectable: false,
    })),
  ];
  const edges: Edge[] = layout.edges.map((edge, i) => ({
    id: `e${i}-${edge.source}-${edge.target}`,
    source: edge.source,
    target: edge.target,
    label: edge.label,
    style: { stroke: '#C8C6C4', strokeDasharray: edge.style === 'dashed' ? '6 4' : undefined },
    type: 'straight',
  }));
  return { nodes, edges };
}

function DiagramCanvas({ source }: { source: DiagramSource }) {
  const { fitView } = useReactFlow();
  const layout = useMemo(() => layoutDiagram(source), [source]);
  const { nodes, edges } = useMemo(() => toFlowElements(layout), [layout]);

  useEffect(() => {
    // Two rAFs let React Flow finish its own internal measure/paint pass
    // before we fit the view and hand the pre-computed SVG off to Playwright.
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        fitView({ padding: 0.05, duration: 0 });
        (window as any).__DIAGRAM_SVG__ = buildSvg(layout, source.title);
        (window as any).__DIAGRAM_READY__ = true;
      });
    });
  }, [fitView, layout, source.title]);

  return (
    <div style={{ width: layout.width, height: layout.height }}>
      <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} fitView proOptions={{ hideAttribution: true }}>
        <Background />
      </ReactFlow>
    </div>
  );
}

export function App() {
  const [source, setSource] = useState<DiagramSource | null>(null);

  useEffect(() => {
    fetch(`/diagram-data.json?t=${Date.now()}`, { cache: 'no-store' })
      .then((res) => res.json())
      .then(setSource);
  }, []);

  if (!source) return <div>Loading diagram…</div>;

  return (
    <ReactFlowProvider>
      <DiagramCanvas source={source} />
    </ReactFlowProvider>
  );
}
