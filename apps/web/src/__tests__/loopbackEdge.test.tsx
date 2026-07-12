import { render, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { EdgeProps } from '@xyflow/react';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ActiveEdgeContext, LoopbackEdge } from '../components/WorkflowGraphPanel';

/**
 * Direct render test for LoopbackEdge — confirms the loopback (gate -> coordinator) return
 * arc and its warm label render when such an edge IS present in the graph data. The full
 * <ReactFlow> renderer does not lay out edges under happy-dom (nodes measure 0), so we mock
 * the ReactFlow store hooks (useEdges/useNodes) and render the edge component inside an <svg>.
 */
const fixtures = vi.hoisted(() => ({
  nodes: [
    { id: 'coordinator', type: 'workflow', position: { x: 0, y: 0 }, data: {}, measured: { width: 250, height: 58 } },
    { id: 'rai', type: 'workflow', position: { x: 360, y: 0 }, data: {}, measured: { width: 250, height: 58 } },
  ],
  edges: [
    { id: 'rai->coordinator', source: 'rai', target: 'coordinator', type: 'loopback', label: 'RAI flags' },
  ],
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
  it('renders the warm loopback label when a loopback edge is present in the data', () => {
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

    // The dashed return path is drawn...
    expect(container.querySelector('path[stroke-dasharray]')).toBeTruthy();
    // ...and its warm label is rendered as SVG text.
    expect(container.textContent).toContain('RAI flags');
  });
});
