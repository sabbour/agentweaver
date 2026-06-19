import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import type { RunStreamEvent } from '../api/sse';

// ResizeObserver is required by @xyflow/react and is absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

let currentEvents: RunStreamEvent[] = [];

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectRuns: vi.fn(),
    getTeam: vi.fn(),
    getRun: vi.fn(),
    getRunEvents: vi.fn(),
    getRunGraph: vi.fn(),
    answerQuestion: vi.fn(),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => ({ events: currentEvents, status: 'streaming', error: null, reconnect: vi.fn() }),
}));

import { apiClient } from '../api/apiClient';
import { WorkflowRunPage } from '../pages/WorkflowRunPage';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1/runs/run-1/workflow']}>
        <Routes>
          <Route path="/projects/:projectId/runs/:runId/workflow" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  currentEvents = [];
  vi.mocked(apiClient.getProjectRuns).mockResolvedValue([
    {
      workflow_run_id: 'run-1',
      execution_id: 'run-1',
      task: 'Do work',
      status: 'running',
      agent_name: 'Neo',
      started_at: '2026-06-18T00:00:00Z',
    },
  ]);
  vi.mocked(apiClient.getTeam).mockResolvedValue({
    project_id: 'p1', project_name: 'Demo', members: [],
  } as unknown as Awaited<ReturnType<typeof apiClient.getTeam>>);
  vi.mocked(apiClient.getRun).mockResolvedValue({
    run_id: 'run-1', status: 'running', parent_run_id: null,
  } as unknown as Awaited<ReturnType<typeof apiClient.getRun>>);
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
  vi.mocked(apiClient.getRunGraph).mockResolvedValue(null);
  vi.mocked(apiClient.answerQuestion).mockResolvedValue({ run_id: 'run-1', request_id: 'q-1', answered: true });
});

afterEach(() => cleanup());

describe('WorkflowRunPage — bubbled questions', () => {
  it('renders an answer box for an unanswered question and submits to the run id', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.question_asked', payload: { requestId: 'q-1', question: 'What port should I use?' } },
    ];

    const { container } = render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('What port should I use?'),
      { timeout: 4000 },
    );

    const textarea = container.querySelector('textarea[placeholder="Type your answer…"]') as HTMLTextAreaElement;
    expect(textarea).toBeTruthy();
    fireEvent.change(textarea, { target: { value: '8080' } });

    const submit = Array.from(container.querySelectorAll('button')).find((b) => b.textContent?.includes('Submit answer'));
    submit!.click();

    await waitFor(
      () => expect(vi.mocked(apiClient.answerQuestion)).toHaveBeenCalledWith('run-1', 'q-1', '8080'),
      { timeout: 4000 },
    );
  });

  it('collapses to a muted answered state when the matching answer arrives', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.question_asked', payload: { requestId: 'q-1', question: 'What port?' } },
      { sequence: 2, type: 'agent.question_answered', payload: { requestId: 'q-1', answer: '8080', timedOut: false } },
    ];

    const { container } = render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Question answered'),
      { timeout: 4000 },
    );
    expect(container.querySelector('textarea[placeholder="Type your answer…"]')).toBeNull();
    expect(document.body.textContent).toContain('8080');
  });

  it('shows a timed-out hint when the question timed out', async () => {
    currentEvents = [
      { sequence: 1, type: 'agent.question_asked', payload: { requestId: 'q-1', question: 'What port?' } },
      { sequence: 2, type: 'agent.question_answered', payload: { requestId: 'q-1', answer: '', timedOut: true } },
    ];

    render(<Wrapper><WorkflowRunPage /></Wrapper>);

    await waitFor(
      () => expect(document.body.textContent).toContain('Question timed out'),
      { timeout: 4000 },
    );
  });
});
