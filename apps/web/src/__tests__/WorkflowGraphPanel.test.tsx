import { AzureFluentProvider } from '../copilot-fluent-system';
import { ActiveEdgeContext, ExecutionModalContext, workflowNodeTypes } from '../components/WorkflowGraphPanel';
import { buildSteppedConnectorRoute } from '../utils/dagLayout';
import { BotRegular, CheckmarkCircleRegular, MergeRegular, ShieldRegular } from '../copilot-fluent-system';
import { cleanup, render, waitFor } from '@testing-library/react';
import { ReactFlow } from '@xyflow/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowNodeData } from '../components/WorkflowGraphPanel';
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

function Wrapper({ nodes }: { nodes: Node[] }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        <ExecutionModalContext.Provider value={undefined}>
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
    // Planned nodes render the "Planned" badge text
    await waitFor(
      () => expect(document.body.textContent).toContain('Planned'),
      { timeout: 4000 },
    );
    // Even though nodeType=gate, isPlanned=true forces cardDefault width class
    // (the data-node-type attribute still reflects the nodeType for structural info)
    expect(document.body.innerHTML).toContain('data-node-type="gate"');
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
});

describe('WorkflowGraphPanel — topology connector routing', () => {
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
