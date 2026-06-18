import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';

// ResizeObserver is required by @xyflow/react and absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunGraph: vi.fn(),
    getWorkPlan: vi.fn(),
    getCoordinatorChildren: vi.fn(),
    steerCoordinator: vi.fn(),
    // OutcomeSpecPanel uses these — return empty/null to avoid noise.
    getOutcomeSpec: vi.fn(),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: [], status: 'done', error: null, reconnect: vi.fn() }),
}));

// OutcomeSpecPanel performs its own fetch; stub it so it renders nothing.
vi.mock('../components/OutcomeSpecPanel', () => ({
  OutcomeSpecPanel: () => null,
}));

import { apiClient } from '../api/apiClient';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { COORDINATOR_GRAPH_DESCRIPTOR } from './fixtures/graphDescriptor';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/orchestrations/coord-run-1']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations/:runId" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(COORDINATOR_GRAPH_DESCRIPTOR);
  vi.mocked(apiClient.getWorkPlan).mockRejectedValue(new Error('not found'));
  vi.mocked(apiClient.getCoordinatorChildren).mockRejectedValue(new Error('not found'));
});

afterEach(() => cleanup());

describe('CoordinatorRunPage — unified coordinator graph view', () => {
  it('renders coordinator node, subtask nodes, and planned assembly nodes', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    // Coordinator orchestrator node
    expect(text).toContain('Coordinator');
    // Subtask nodes from fixture
    expect(text).toContain('Subtask 1');
    expect(text).toContain('Subtask 2');
    // Planned assembly nodes
    expect(text).toContain('RAI Review');
    expect(text).toContain('Human Review');
  });

  it('renders planned assembly nodes with "Planned" badge (visually distinct)', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('RAI Review'),
      { timeout: 4000 },
    );

    // Planned nodes show a "Planned" status badge (from StatusBadge with isPlanned=true).
    const text = document.body.textContent ?? '';
    expect(text).toContain('Planned');

    // Planned nodes carry data-node-type attributes in the rendered HTML.
    const html = document.body.innerHTML;
    expect(html).toContain('data-node-type="gate"');    // planned RAI Review + Human Review
    expect(html).toContain('data-node-type="action"');  // planned Merge + Scribe
  });

  it('renders subtask nodes as data-node-type=subtask', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // SubtaskNode renders data-node-type="subtask" on its card div.
    const html = document.body.innerHTML;
    expect(html).toContain('data-node-type="subtask"');
  });

  it('subtask nodes with child_graph_ref render an expand button', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Subtask 1'),
      { timeout: 4000 },
    );

    // Both subtasks in the fixture have child_graph_ref → both should have expand buttons.
    const text = document.body.textContent ?? '';
    expect(text).toContain('Expand pipeline');
  });

  it('shows steering bar with Stop/Redirect/Amend buttons', async () => {
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Steer coordinator'),
      { timeout: 4000 },
    );

    const text = document.body.textContent ?? '';
    expect(text).toContain('Stop');
    expect(text).toContain('Redirect');
    expect(text).toContain('Amend');
  });

  it('renders from REST descriptor even when SSE stream is done (finished coordinator runs)', async () => {
    // Stream is already 'done' in the mock (simulates a finished coordinator run with closed SSE).
    render(<Wrapper><CoordinatorRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Coordinator'),
      { timeout: 4000 },
    );

    // Graph must render from REST seed even though SSE stream is done.
    const text = document.body.textContent ?? '';
    expect(text).toContain('Subtask 1');
    expect(text).toContain('RAI Review');
  });
});
