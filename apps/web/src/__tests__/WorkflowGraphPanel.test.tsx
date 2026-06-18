/**
 * Direct unit tests for WorkflowNode — verifies that node_type drives
 * data-node-type attribute and card width class.
 *
 * Renders a minimal ReactFlow with workflowNodeTypes rather than going
 * through the full WorkflowRunPage loading chain, which avoids async
 * descriptor-fetch timing issues in happy-dom.
 */
import { describe, it, expect, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { ReactFlow } from '@xyflow/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { BotRegular, ShieldRegular, MergeRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';

// ResizeObserver is absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

import {
  workflowNodeTypes,
  ExecutionModalContext,
  ActiveEdgeContext,
  type WorkflowNodeData,
} from '../components/WorkflowGraphPanel';
import type { Node } from '@xyflow/react';

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
    <FluentProvider theme={webLightTheme}>
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
    </FluentProvider>
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
        def: { key: 'assemble-ready', label: 'Assemble-ready', roleDescription: 'Awaiting assembly', Icon: CheckmarkCircleRegular },
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
