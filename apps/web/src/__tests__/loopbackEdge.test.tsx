import { render, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Edge, EdgeProps, Node } from '@xyflow/react';
import { AzureFluentProvider } from '../copilot-fluent-system';
import {
  ActiveEdgeContext,
  LoopbackEdge,
  REVISION_EDGE_STROKE,
  SpineEdge,
} from '../components/WorkflowGraphPanel';

/**
 * Direct render test for LoopbackEdge — confirms the loopback (gate -> coordinator) return
 * arc and its distinct revision label render when such an edge IS present in the graph data. The full
 * <ReactFlow> renderer does not lay out edges under happy-dom (nodes measure 0), so we mock
 * the ReactFlow store hooks (useEdges/useNodes) and render the edge component inside an <svg>.
 */
const fixtures = vi.hoisted(() => ({
  nodes: [
    { id: 'coordinator', type: 'workflow', position: { x: 0, y: 0 }, data: {}, measured: { width: 250, height: 58 } },
    { id: 'rai', type: 'workflow', position: { x: 360, y: 0 }, data: {}, measured: { width: 250, height: 58 } },
  ] as Node[],
  edges: [
    { id: 'rai->coordinator', source: 'rai', target: 'coordinator', type: 'loopback', label: 'RAI flags' },
  ] as Edge[],
}));

vi.mock('@xyflow/react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@xyflow/react')>();
  return {
    ...actual,
    useEdges: () => fixtures.edges,
    useNodes: () => fixtures.nodes,
  };
});

afterEach(cleanup);

describe('LoopbackEdge — return arc rendering', () => {
  it('renders the distinct revision loopback label when a loopback edge is present in the data', () => {
    const props = {
      id: 'rai->coordinator',
      source: 'rai',
      target: 'coordinator',
      sourceX: 610, sourceY: 29,
      targetX: 0, targetY: 29,
      label: 'RAI flags',
      data: {},
    } as unknown as EdgeProps;

    const { container } = render(
      <AzureFluentProvider density="compact">
        <ActiveEdgeContext.Provider value={undefined}>
          <svg width={800} height={200}>
            <LoopbackEdge {...props} />
          </svg>
        </ActiveEdgeContext.Provider>
      </AzureFluentProvider>,
    );

    // The dashed return path starts and ends on its supplied anchors, then
    // uses the outer rail rather than a detached horizontal span.
    const loopbackPath = container.querySelector(`path[stroke="${REVISION_EDGE_STROKE}"][stroke-dasharray]`);
    expect(loopbackPath).toBeTruthy();
    expect(loopbackPath?.getAttribute('d')).toContain('M 610,29');
    expect(loopbackPath?.getAttribute('d')).toContain('L 0,29');
    const loopbackJunctions = container.querySelectorAll('[data-testid="workflow-loopback-junction"]');
    expect(loopbackJunctions).toHaveLength(2);
    for (const junction of loopbackJunctions) {
      expect(junction.getAttribute('r')).toBe('2.5');
      expect(junction.getAttribute('fill')).toBe(REVISION_EDGE_STROKE);
      expect(junction.getAttribute('stroke')).toBeNull();
    }
    // ...and its revision label is rendered as SVG text.
    expect(container.textContent).toContain('RAI flags');
  });

  it('draws a visible bridge when a later forward connector crosses an earlier one', () => {
    fixtures.nodes = [
      { id: 'left', type: 'workflow', position: { x: 0, y: 0 }, data: {}, measured: { width: 100, height: 100 } },
      { id: 'right', type: 'workflow', position: { x: 200, y: 0 }, data: {}, measured: { width: 100, height: 100 } },
      { id: 'top', type: 'workflow', position: { x: 80, y: -100 }, data: {}, measured: { width: 100, height: 100 } },
      { id: 'bottom', type: 'workflow', position: { x: 80, y: 200 }, data: {}, measured: { width: 100, height: 100 } },
    ];
    fixtures.edges = [
      {
        id: 'edge-a',
        source: 'left',
        target: 'right',
        type: 'spine',
        sourceHandle: 'source-right',
        targetHandle: 'target-left',
        data: { flowDirection: 'horizontal' },
      },
      {
        id: 'edge-b',
        source: 'top',
        target: 'bottom',
        type: 'spine',
        sourceHandle: 'source-bottom',
        targetHandle: 'target-top',
        data: { flowDirection: 'vertical' },
      },
    ];
    const props = {
      id: 'edge-b',
      source: 'top',
      target: 'bottom',
      sourceX: 130,
      sourceY: 0,
      targetX: 130,
      targetY: 200,
      data: { flowDirection: 'vertical' },
    } as unknown as EdgeProps;

    const { container } = render(
      <AzureFluentProvider density="compact">
        <svg width={400} height={400}>
          <SpineEdge {...props} />
        </svg>
      </AzureFluentProvider>,
    );

    expect(container.querySelector('[data-testid="workflow-spine-edge"]')?.getAttribute('d'))
      .toContain('A 7,7');
    expect(container.querySelector('path[stroke="var(--colorNeutralBackground1)"]')).toBeNull();
  });

  it('marks a shared connector origin with an explicit junction circle', () => {
    fixtures.nodes = [
      { id: 'origin', type: 'workflow', position: { x: 0, y: 0 }, data: {}, measured: { width: 100, height: 100 } },
      { id: 'upper', type: 'workflow', position: { x: 200, y: -100 }, data: {}, measured: { width: 100, height: 100 } },
      { id: 'lower', type: 'workflow', position: { x: 200, y: 100 }, data: {}, measured: { width: 100, height: 100 } },
    ];
    fixtures.edges = [
      {
        id: 'edge-a',
        source: 'origin',
        target: 'upper',
        type: 'spine',
        sourceHandle: 'source-right',
        targetHandle: 'target-left',
        data: { flowDirection: 'horizontal' },
      },
      {
        id: 'edge-b',
        source: 'origin',
        target: 'lower',
        type: 'spine',
        sourceHandle: 'source-right',
        targetHandle: 'target-left',
        data: { flowDirection: 'horizontal' },
      },
    ];
    const props = {
      id: 'edge-a',
      source: 'origin',
      target: 'upper',
      sourceX: 100,
      sourceY: 50,
      targetX: 200,
      targetY: -50,
      data: { flowDirection: 'horizontal' },
    } as unknown as EdgeProps;

    const { container } = render(
      <AzureFluentProvider density="compact">
        <svg width={400} height={300}>
          <SpineEdge {...props} />
        </svg>
      </AzureFluentProvider>,
    );

    const junction = container.querySelector('[data-testid="workflow-connector-junction"]');
    expect(junction).toBeTruthy();
    expect(junction?.getAttribute('r')).toBe('2.5');
    expect(junction?.getAttribute('stroke')).toBeNull();
  });
});
