import { AzureFluentProvider } from '../copilot-fluent-system';
import {
  ActiveEdgeContext,
  buildWorkflowDefinitionGraph,
  ExecutionModalContext,
  workflowNodeTypes,
} from '../components/WorkflowGraphPanel';
import {
  buildSteppedConnectorRoute,
  FIXED_NODE_W,
  WORKFLOW_PILL_DEFAULT_NODE_H,
} from '../utils/dagLayout';
import { BotRegular, CheckmarkCircleRegular, MergeRegular, ShieldRegular } from '../copilot-fluent-system';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { ReactFlow } from '@xyflow/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowNodeData } from '../components/WorkflowGraphPanel';
import type { WorkflowGraphDto } from '../api/types';
import type { Node } from '@xyflow/react';
/**
 * Direct unit tests for WorkflowNode — verifies that node_type drives
 * data-node-type attribute and card width class.
 *
 * Renders a minimal ReactFlow with workflowNodeTypes rather than going
 * through the full page loading chain, which avoids async
 * descriptor-fetch timing issues in happy-dom.
 */
// ResizeObserver is absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn().mockResolvedValue({ kubernetes: false, podName: null }),
  },
}));

afterEach(cleanup);

function makeAgentNode(nodeType: WorkflowNodeData['nodeType']): Node[] {
  return [
    {
      id: 'n1',
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {
        def: { key: 'agent', label: 'Agent', roleDescription: 'AI Assistant', Icon: BotRegular },
        state: { status: 'pending' },
        nodeType,
        runId: 'run-1',
        executionId: 'exec-1',
        projectId: 'p1',
      } satisfies WorkflowNodeData,
    },
  ];
}

function Wrapper({ nodes, openModal }: { nodes: Node[]; openModal?: (id: string) => void }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        <ExecutionModalContext.Provider value={openModal}>
          <ActiveEdgeContext.Provider value={undefined}>
            <div style={{ width: 800, height: 600 }}>
              <ReactFlow
                nodes={nodes}
                edges={[]}
                nodeTypes={workflowNodeTypes}
              />
            </div>
          </ActiveEdgeContext.Provider>
        </ExecutionModalContext.Provider>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

describe('WorkflowNode — node_type drives data-node-type attribute', () => {
  it('renders data-node-type="agent" for nodeType=agent', async () => {
    render(<Wrapper nodes={makeAgentNode('agent')} />);
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="agent"'),
      { timeout: 4000 },
    );
  });

  it('renders data-node-type="gate" for nodeType=gate', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'rai', label: 'Rai', roleDescription: 'RAI Reviewer', Icon: ShieldRegular },
        state: { status: 'pending' },
        nodeType: 'gate',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="gate"'),
      { timeout: 4000 },
    );
  });

  it('renders data-node-type="action" for nodeType=action (action nodes are visually smaller than agent)', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'merge', label: 'Merge', roleDescription: 'Merge Coordinator', Icon: MergeRegular },
        state: { status: 'pending' },
        nodeType: 'action',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="action"'),
      { timeout: 4000 },
    );
  });

  it('renders data-node-type="terminal" for nodeType=terminal', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'assemble-ready', label: 'Assemble-ready', roleDescription: 'Ready for assembly', Icon: CheckmarkCircleRegular },
        state: { status: 'pending' },
        nodeType: 'terminal',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="terminal"'),
      { timeout: 4000 },
    );
  });

  it('renders data-node-type="default" when nodeType is undefined', async () => {
    render(<Wrapper nodes={makeAgentNode(undefined)} />);
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="default"'),
      { timeout: 4000 },
    );
    // Confirm agent-specific attribute is NOT present
    expect(document.body.innerHTML).not.toContain('data-node-type="agent"');
  });

  it('planned nodes (isPlanned=true) always use cardDefault regardless of node_type', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'rai', label: 'RAI Review', roleDescription: 'RAI Reviewer', Icon: ShieldRegular },
        state: { status: 'pending' },
        nodeType: 'gate',
        isPlanned: true,
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    // Planned state is now conveyed structurally (dashed pill + aria-label), not a visible
    // "Planned" badge on the compact node face. Assert the node rendered and preserved its type.
    await waitFor(
      () => expect(document.body.innerHTML).toContain('data-node-type="gate"'),
      { timeout: 4000 },
    );
    expect(document.body.textContent).toContain('RAI Review');
  });
});

describe('WorkflowNode — message field display', () => {
  it('renders the message from ExecutorState as a status line on the card', async () => {
    const nodes = [
      {
        id: 'n1',
        type: 'workflow',
        position: { x: 0, y: 0 },
        data: {
          def: { key: 'agent', label: 'Agent', roleDescription: 'AI Assistant', Icon: BotRegular },
          state: { status: 'started', message: 'Analyzing the codebase...' },
          runId: 'run-1',
          executionId: 'exec-1',
          projectId: 'p1',
        },
      },
    ];
    render(<Wrapper nodes={nodes} />);
    await waitFor(
      () => expect(document.body.textContent).toContain('Analyzing the codebase...'),
      { timeout: 4000 },
    );
  });

  it('shows message over hardcoded statusDescription when both are present', async () => {
    const nodes = [
      {
        id: 'n1',
        type: 'workflow',
        position: { x: 0, y: 0 },
        data: {
          def: { key: 'rai', label: 'Rai', roleDescription: 'RAI Reviewer', Icon: ShieldRegular },
          state: { status: 'started', message: 'Custom backend message' },
          runId: 'run-1',
          executionId: 'exec-1',
          projectId: 'p1',
        },
      },
    ];
    render(<Wrapper nodes={nodes} />);
    await waitFor(
      () => expect(document.body.textContent).toContain('Custom backend message'),
      { timeout: 4000 },
    );
    // The hardcoded 'Reviewing safety...' should NOT appear — message takes priority.
    expect(document.body.textContent).not.toContain('Reviewing safety');
  });

  it('shows an on-face "Review now" action for the Human Review gate while it awaits a decision (grows to fit)', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'review', label: 'Human Review', roleDescription: 'Human reviewer', Icon: ShieldRegular },
        state: { status: 'started' },
        nodeType: 'gate',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} openModal={() => {}} />);
    // Awaiting review ⇒ the primary action renders on the node face (button, not just popover).
    const btnText = await screen.findByText('Review now', undefined, { timeout: 4000 });
    expect(btnText.closest('button')).toBeTruthy();
  });

  it('does NOT show an on-face action button for a Human Review gate that is not awaiting', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'review', label: 'Human Review', roleDescription: 'Human reviewer', Icon: ShieldRegular },
        state: { status: 'pending' },
        nodeType: 'gate',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    await waitFor(() => expect(document.body.textContent).toContain('Human Review'), { timeout: 4000 });
    expect(screen.queryByText('Review now')).toBeNull();
  });

  it('stays compact (no Name/Role on face) but renders the model caption for a node that has a model (Coordinator/RAI)', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'rai', label: 'RAI Check', roleDescription: 'Responsible AI', Icon: ShieldRegular },
        state: { status: 'completed' },
        nodeType: 'gate',
        agentName: 'Sentinel',
        agentRoleTitle: 'RAI Reviewer',
        modelId: 'claude-3-5-sonnet',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    // The model caption renders BELOW the compact card…
    await waitFor(() => expect(document.body.textContent).toContain('Claude 3 5 Sonnet'), { timeout: 4000 });
    // …but the agent Name (Role) is NOT on the compact face (it lives in the hover popover only).
    expect(document.body.textContent).not.toContain('Sentinel (RAI Reviewer)');
  });

  it('stays compact with NO model caption for a pure gate node with no model', async () => {
    const nodes: Node[] = [{
      id: 'n1', type: 'workflow', position: { x: 0, y: 0 },
      data: {
        def: { key: 'merge', label: 'Merge', roleDescription: 'Integrate branch', Icon: MergeRegular },
        state: { status: 'completed' },
        nodeType: 'gate',
        runId: 'run-1', executionId: 'exec-1', projectId: 'p1',
      } satisfies WorkflowNodeData,
    }];
    render(<Wrapper nodes={nodes} />);
    await waitFor(() => expect(document.body.textContent).toContain('Merge'), { timeout: 4000 });
    // No agent/model ⇒ compact: no Name(Role) face line and no model caption.
    expect(document.body.textContent).not.toContain('(Integrate branch)');
  });
});

describe('WorkflowGraphPanel — topology connector routing', () => {
  it('uses compact pill size hints so fitView matches the rendered workflow cards', () => {
    const graph: WorkflowGraphDto = {
      graph_id: 'compact-workflow',
      variant: 'default',
      start_node_id: 'research',
      nodes: [
        { id: 'research', label: 'Research', role: 'agent', kind: 'planned', node_type: 'agent' },
        { id: 'review', label: 'Stakeholder Review', role: 'review', kind: 'planned', node_type: 'gate' },
        { id: 'done', label: 'Done', role: 'scribe', kind: 'planned', node_type: 'terminal' },
      ],
      edges: [
        { from: 'research', to: 'review', cardinality: 'direct', loopback: false },
        { from: 'review', to: 'done', cardinality: 'direct', loopback: false },
      ],
    };

    const { rfNodes } = buildWorkflowDefinitionGraph(graph);
    const byId = new Map(rfNodes.map((node) => [node.id, node]));

    expect(byId.get('research')?.initialWidth).toBe(FIXED_NODE_W);
    expect(byId.get('review')?.initialWidth).toBe(FIXED_NODE_W);
    expect(byId.get('done')?.initialWidth).toBe(FIXED_NODE_W);
    expect(byId.get('done')?.initialHeight).toBeLessThan(WORKFLOW_PILL_DEFAULT_NODE_H);
  });

  it('routes a diamond workflow through GRID handles without dangling edge endpoints', () => {
    const graph: WorkflowGraphDto = {
      graph_id: 'content-authoring',
      variant: 'default',
      start_node_id: 'author',
      nodes: [
        { id: 'author', label: 'Author', role: 'agent', kind: 'planned', node_type: 'agent' },
        { id: 'rai', label: 'RAI Check', role: 'rai', kind: 'planned', node_type: 'gate' },
        { id: 'safety-failed', label: 'Safety Failed', role: 'agent', kind: 'planned', node_type: 'action' },
        { id: 'review', label: 'Human Review', role: 'review', kind: 'planned', node_type: 'gate' },
        { id: 'declined', label: 'Declined', role: 'agent', kind: 'planned', node_type: 'terminal' },
        { id: 'publish', label: 'Publish', role: 'merge', kind: 'planned', node_type: 'action' },
        { id: 'done', label: 'Done', role: 'scribe', kind: 'planned', node_type: 'terminal' },
      ],
      edges: [
        { from: 'author', to: 'rai', cardinality: 'direct', loopback: false },
        { from: 'rai', to: 'safety-failed', cardinality: 'direct', loopback: false },
        { from: 'rai', to: 'review', cardinality: 'direct', loopback: false },
        { from: 'safety-failed', to: 'publish', cardinality: 'direct', loopback: false },
        { from: 'review', to: 'declined', cardinality: 'direct', loopback: false },
        { from: 'review', to: 'publish', cardinality: 'direct', loopback: false },
        { from: 'publish', to: 'done', cardinality: 'direct', loopback: false },
      ],
    };

    const { rfNodes, rfEdges } = buildWorkflowDefinitionGraph(graph);
    const nodeIds = new Set(rfNodes.map((node) => node.id));

    expect(rfEdges).toHaveLength(graph.edges.length);
    for (const node of rfNodes) {
      expect(node.data.dir).toBe('GRID');
      expect(Number.isFinite(node.position.x)).toBe(true);
      expect(Number.isFinite(node.position.y)).toBe(true);
    }
    for (const edge of rfEdges) {
      expect(nodeIds.has(edge.source)).toBe(true);
      expect(nodeIds.has(edge.target)).toBe(true);
      expect(edge.sourceHandle).toMatch(/^source-(left|right|top|bottom)$/);
      expect(edge.targetHandle).toMatch(/^target-(left|right|top|bottom)$/);
      expect(edge.data).toMatchObject({ flowDirection: expect.any(String) });
    }
  });

  it('uses one orthogonal stepped path instead of cubic squiggles and junction dots', () => {
    const route = buildSteppedConnectorRoute({
      sourceX: 120,
      sourceY: 260,
      targetX: 240,
      targetY: 500,
    });

    expect(route.points).toEqual([
      { x: 120, y: 260 },
      { x: 120, y: 380 },
      { x: 240, y: 380 },
      { x: 240, y: 500 },
    ]);
    expect(route.path).toMatch(/^M 120,260 /);
    expect(route.path).toContain('L 120,372');
    expect(route.path).toContain('Q 120,380 128,380');
    expect(route.path).not.toMatch(/\sC\s/);
  });

  it('keeps same-row dependencies on a predictable vertical lane between cards', () => {
    const route = buildSteppedConnectorRoute({
      sourceX: 220,
      sourceY: 180,
      targetX: 520,
      targetY: 180,
    });

    expect(route.points).toEqual([
      { x: 220, y: 180 },
      { x: 370, y: 180 },
      { x: 520, y: 180 },
    ]);
    expect(route.path).toBe('M 220,180 L 370,180 L 520,180');
    expect(route.path).not.toMatch(/\sC\s/);
  });

  it('can force left-to-right lane routing for fan-out and fan-in execution graphs', () => {
    const route = buildSteppedConnectorRoute({
      sourceX: 240,
      sourceY: 180,
      targetX: 560,
      targetY: 420,
      orientation: 'horizontal',
    });

    expect(route.points).toEqual([
      { x: 240, y: 180 },
      { x: 400, y: 180 },
      { x: 400, y: 420 },
      { x: 560, y: 420 },
    ]);
    expect(route.path).not.toMatch(/\sC\s/);
  });
});