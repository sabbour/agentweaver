import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AssistantRunPage } from '../pages/AssistantRunPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

// ResizeObserver is required by some Fluent surfaces and absent in happy-dom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

// Mutable run-stream state so a test can inject a permission-required event.
const mockRunStreamState = vi.hoisted(() => ({
  current: {
    events: [] as Array<{ sequence: number; type: string; payload: Record<string, unknown> }>,
    droppedEventCount: 0,
    status: 'streaming',
    error: null as string | null,
    reconnect: vi.fn(),
  },
}));

vi.mock('../api/apiClient', () => ({
  apiClient: {
    createAssistantRun: vi.fn(),
    sendAssistantMessage: vi.fn(),
    getRunEvents: vi.fn().mockResolvedValue([]),
    approveTool: vi.fn().mockResolvedValue(undefined),
    denyTool: vi.fn().mockResolvedValue(undefined),
    approveShell: vi.fn().mockResolvedValue(undefined),
    denyShell: vi.fn().mockResolvedValue(undefined),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: () => mockRunStreamState.current,
}));

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/assistant']}>{children}</MemoryRouter>
    </AzureFluentProvider>
  );
}

function typeAndSend(message: string) {
  const textarea = screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement;
  fireEvent.change(textarea, { target: { value: message } });
  // Enter submits the Composer.
  fireEvent.keyDown(textarea, { key: 'Enter', code: 'Enter' });
}

/** Real response shape from POST /api/assistant/runs (201). */
const REAL_CREATE_RESPONSE = {
  run_id: 'assistant-run-1',
  status: 'in_progress',
  message: 'Hello, how can I help?',
  tools_invoked: [],
};

/** Real response shape from POST /api/assistant/runs/{id}/messages (200). */
const REAL_MESSAGE_RESPONSE = {
  run_id: 'assistant-run-1',
  role: 'assistant' as const,
  message: 'I checked the projects. Here is what I found.',
  status: 'in_progress',
  tools_invoked: ['coordinator_list_projects'],
};

beforeEach(() => {
  vi.clearAllMocks();
  mockRunStreamState.current = {
    events: [],
    droppedEventCount: 0,
    status: 'streaming',
    error: null,
    reconnect: vi.fn(),
  };
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
  vi.mocked(apiClient.createAssistantRun).mockResolvedValue(REAL_CREATE_RESPONSE);
  vi.mocked(apiClient.sendAssistantMessage).mockResolvedValue(REAL_MESSAGE_RESPONSE);
});

afterEach(() => {
  cleanup();
});

describe('AssistantRunPage', () => {
  it('renders the page with the empty invitation state before a run exists', () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    expect(screen.getByTestId('assistant-run-page')).toBeTruthy();
    expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    expect(screen.getByPlaceholderText('Message the assistant...')).toBeTruthy();
  });

  it('creates a run on the first composer submit using real backend shape', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('what projects exist?');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
    });
    expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
      expect.objectContaining({ message: 'what projects exist?' }),
    );
    // A normal first-ever conversation never had a prior run to resume from.
    const [firstCallArgs] = vi.mocked(apiClient.createAssistantRun).mock.calls[0];
    expect(firstCallArgs.resume_from_run_id).toBeUndefined();
    expect(apiClient.sendAssistantMessage).not.toHaveBeenCalled();
    // Once the run exists the empty state is replaced by the transcript.
    await waitFor(() => {
      expect(screen.queryByTestId('assistant-empty-state')).toBeNull();
    });
  });

  it('sends subsequent messages via real send-message endpoint once a run exists', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    typeAndSend('second message');
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledTimes(1);
    });
    expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
      'assistant-run-1',
      expect.objectContaining({ message: 'second message' }),
    );
    // The create call is not made again for follow-ups.
    expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
  });

  it('renders the tool-approval UI when a permission-required event is streamed', async () => {
    // Seed a pending tool approval on the live stream before the run is created.
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        {
          sequence: 1,
          type: 'tool.approval_required',
          payload: { request_id: 'req-1', tool_name: 'coordinator_start', intention: 'start a run' },
        },
      ],
    };

    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // Create the run so the stream (and thus approvals) bind.
    typeAndSend('please start a run');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    const gate = await screen.findByTestId('assistant-approval-gate');
    expect(gate).toBeTruthy();
    expect(screen.getByText('Tool Approval Required')).toBeTruthy();

    // Approving wires through to apiClient.approveTool against the run id.
    fireEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    await waitFor(() => {
      expect(apiClient.approveTool).toHaveBeenCalledWith('assistant-run-1', 'req-1', 'once');
    });
  });

  it('shows operator_run_limit message on 429 from createAssistantRun', async () => {
    const err = new ApiError(429, JSON.stringify({ error: 'operator_run_limit', message: 'Limit reached.' }));
    vi.mocked(apiClient.createAssistantRun).mockRejectedValue(err);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('hello');

    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toContain(
      'You have too many active assistant conversations',
    );
    // Run state is not set — empty state remains.
    expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
  });

  it('shows conversation-timeout message and resets on 404 run_not_found from sendAssistantMessage', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    // Now the run gets idle-closed on the next message.
    const err = new ApiError(404, JSON.stringify({ error: 'run_not_found', message: 'Run not found.' }));
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toContain('timed out');
    // Page resets: empty state reappears so the user can start a new run.
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });
  });

  it('passes resume_from_run_id on the next new-run request after a 404 idle-closed reset', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run — this is the run that will later be idle-closed.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    const err = new ApiError(404, JSON.stringify({ error: 'run_not_found', message: 'Run not found.' }));
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });

    // The very next submit starts a brand-new run and should auto-seed it with the
    // just-closed run's history via resume_from_run_id.
    typeAndSend('continuing message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(2));
    expect(apiClient.createAssistantRun).toHaveBeenLastCalledWith(
      expect.objectContaining({
        message: 'continuing message',
        resume_from_run_id: 'assistant-run-1',
      }),
    );
  });

  it('shows conversation-closed message and resets on 409 operator_run_closed from sendAssistantMessage', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    // The run's durable event stream is already sealed (idle-closed) — the server now
    // refuses to revive it with a 409 instead of the legacy 404 run_not_found.
    const err = new ApiError(
      409,
      JSON.stringify({ error: 'operator_run_closed', message: 'Run is closed.' }),
    );
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toContain('closed after being idle');
    // Page resets: empty state reappears so the user can start a new run.
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });
  });

  it('passes resume_from_run_id on the next new-run request after a 409 idle-closed reset', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run — this is the run that will later be idle-closed.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    const err = new ApiError(
      409,
      JSON.stringify({ error: 'operator_run_closed', message: 'Run is closed.' }),
    );
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });

    // The very next submit starts a brand-new run and should auto-seed it with the
    // just-closed run's history via resume_from_run_id.
    typeAndSend('continuing message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(2));
    expect(apiClient.createAssistantRun).toHaveBeenLastCalledWith(
      expect.objectContaining({
        message: 'continuing message',
        resume_from_run_id: 'assistant-run-1',
      }),
    );
  });

  it('shows idle-timeout notice in the transcript when run.completed reason is idle_timeout', async () => {
    // Seed an idle-timeout completion event on the stream.
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        { sequence: 1, type: 'run.completed', payload: { reason: 'idle_timeout' } },
      ],
    };

    render(<Wrapper><AssistantRunPage /></Wrapper>);

    typeAndSend('hello');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    // The idle-timeout notice renders in the transcript area.
    const notice = await screen.findByTestId('assistant-idle-timeout');
    expect(notice).toBeTruthy();
    expect(notice.textContent).toContain('inactivity');
  });
});
