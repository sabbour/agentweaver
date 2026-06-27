/**
 * Tests for PodIndicator component and useRuntimeInfo hook integration.
 *
 * Asserts: (a) indicator shows podName when kubernetes=true;
 *          (b) indicator NOT rendered when kubernetes=false or podName=null.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { ReactFlow } from '@xyflow/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { BotRegular } from '@fluentui/react-icons';

// ResizeObserver stub required by @xyflow/react.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getSystemRuntime: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import {
  workflowNodeTypes,
  ExecutionModalContext,
  ActiveEdgeContext,
  type WorkflowNodeData,
} from '../components/WorkflowGraphPanel';
import { PodIndicator } from '../components/PodIndicator';
import { _resetRuntimeInfoCache } from '../hooks/useRuntimeInfo';
import type { Node } from '@xyflow/react';

afterEach(() => {
  cleanup();
  _resetRuntimeInfoCache();
  vi.clearAllMocks();
});

// ---------------------------------------------------------------------------
// PodIndicator unit tests (pure presentational)
// ---------------------------------------------------------------------------

describe('PodIndicator', () => {
  it('renders nothing when podName is null', () => {
    const { container } = render(
      <FluentProvider theme={webLightTheme}>
        <PodIndicator podName={null} />
      </FluentProvider>,
    );
    // FluentProvider renders a wrapper div; check no pill content is present
    expect(container.querySelector('[role="status"]')).toBeNull();
    expect(container.textContent).toBe('');
  });

  it('renders nothing when podName is undefined', () => {
    const { container } = render(
      <FluentProvider theme={webLightTheme}>
        <PodIndicator podName={undefined} />
      </FluentProvider>,
    );
    expect(container.querySelector('[role="status"]')).toBeNull();
    expect(container.textContent).toBe('');
  });

  it('renders the pod name when podName is provided', () => {
    const { container } = render(
      <FluentProvider theme={webLightTheme}>
        <PodIndicator podName="agentweaver-api-abc123" />
      </FluentProvider>,
    );
    expect(container.textContent).toContain('agentweaver-api-abc123');
  });

  it('has correct aria-label with pod name', () => {
    const { container } = render(
      <FluentProvider theme={webLightTheme}>
        <PodIndicator podName="pod-xyz" />
      </FluentProvider>,
    );
    const pill = container.querySelector('[aria-label]');
    expect(pill?.getAttribute('aria-label')).toBe('Executing in pod pod-xyz');
  });
});

// ---------------------------------------------------------------------------
// WorkflowNode + useRuntimeInfo integration tests
// ---------------------------------------------------------------------------

function makeNode(overrides?: Partial<WorkflowNodeData>): Node[] {
  return [
    {
      id: 'n1',
      type: 'workflow',
      position: { x: 0, y: 0 },
      data: {
        def: { key: 'agent', label: 'Agent', roleDescription: 'AI Assistant', Icon: BotRegular },
        state: { status: 'pending' },
        ...overrides,
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
              <ReactFlow nodes={nodes} edges={[]} nodeTypes={workflowNodeTypes} />
            </div>
          </ActiveEdgeContext.Provider>
        </ExecutionModalContext.Provider>
      </MemoryRouter>
    </FluentProvider>
  );
}

describe('WorkflowNode — pod indicator via useRuntimeInfo', () => {
  it('shows global pod name when kubernetes=true and no per-node override', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({
      kubernetes: true,
      podName: 'api-pod-abc123',
    });

    render(<Wrapper nodes={makeNode()} />);

    await waitFor(
      () => expect(document.body.textContent).toContain('api-pod-abc123'),
      { timeout: 4000 },
    );
  });

  it('does NOT show pod indicator when kubernetes=false', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({
      kubernetes: false,
      podName: null,
    });

    render(<Wrapper nodes={makeNode()} />);

    // Wait for the agent card to be rendered
    await waitFor(
      () => expect(document.body.textContent).toContain('Agent'),
      { timeout: 4000 },
    );
    expect(document.body.querySelector('[aria-label^="Executing in pod"]')).toBeNull();
  });

  it('does NOT show pod indicator when kubernetes=true but podName is null', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({
      kubernetes: true,
      podName: null,
    });

    render(<Wrapper nodes={makeNode()} />);

    await waitFor(
      () => expect(document.body.textContent).toContain('Agent'),
      { timeout: 4000 },
    );
    expect(document.body.querySelector('[aria-label^="Executing in pod"]')).toBeNull();
  });

  it('does NOT show pod indicator when getSystemRuntime fails (network error)', async () => {
    vi.mocked(apiClient.getSystemRuntime).mockRejectedValue(new Error('network error'));

    render(<Wrapper nodes={makeNode()} />);

    await waitFor(
      () => expect(document.body.textContent).toContain('Agent'),
      { timeout: 4000 },
    );
    expect(document.body.querySelector('[aria-label^="Executing in pod"]')).toBeNull();
  });

  it('per-node executionPodName overrides the global fallback pod name', async () => {
    // Global fallback is the shared API pod; node has its own per-agent pod (spec-018 world)
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({
      kubernetes: true,
      podName: 'api-pod-global',
    });

    render(<Wrapper nodes={makeNode({ executionPodName: 'agent-pod-xyz-worker' })} />);

    await waitFor(
      () => expect(document.body.textContent).toContain('agent-pod-xyz-worker'),
      { timeout: 4000 },
    );
    // Global fallback must NOT appear — per-node value wins
    expect(document.body.textContent).not.toContain('api-pod-global');
  });

  it('per-node executionPodName=null falls back to global pod name', async () => {
    // Node explicitly has null (today's default) — should fall through to global
    vi.mocked(apiClient.getSystemRuntime).mockResolvedValue({
      kubernetes: true,
      podName: 'api-pod-global',
    });

    render(<Wrapper nodes={makeNode({ executionPodName: null })} />);

    await waitFor(
      () => expect(document.body.textContent).toContain('api-pod-global'),
      { timeout: 4000 },
    );
  });
});
