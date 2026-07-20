import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AssistantRunPage } from '../pages/AssistantRunPage';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

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

  it('clears the textarea before the first-run create request settles', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('  what projects exist?  ');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
        expect.objectContaining({ message: 'what projects exist?' }),
      );
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    });
    expect(screen.getByTestId('assistant-pending-message').textContent).toContain('what projects exist?');

    createRequest.resolve(REAL_CREATE_RESPONSE);
    await waitFor(() => expect(screen.queryByTestId('assistant-empty-state')).toBeNull());
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

  it('clears the textarea before a follow-up request settles', async () => {
    const messageRequest = deferred<typeof REAL_MESSAGE_RESPONSE>();
    vi.mocked(apiClient.sendAssistantMessage).mockReturnValueOnce(messageRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    typeAndSend('  second message  ');
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        expect.objectContaining({ message: 'second message' }),
      );
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    });
    expect(screen.getByTestId('assistant-pending-message').textContent).toContain('second message');

    messageRequest.resolve(REAL_MESSAGE_RESPONSE);
    await waitFor(() => {
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).disabled).toBe(false);
    });
  });

  it('keeps the textarea clear if a dispatched request fails', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('hello');
    await waitFor(() => {
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    });

    createRequest.reject(new Error('Request failed'));
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error').textContent).toContain('Request failed');
    });
    expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
  });

  it('leaves empty or busy submissions unchanged and does not dispatch the old message twice', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    const textarea = screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement;

    fireEvent.change(textarea, { target: { value: '   ' } });
    fireEvent.keyDown(textarea, { key: 'Enter', code: 'Enter' });
    expect(textarea.value).toBe('   ');
    expect(apiClient.createAssistantRun).not.toHaveBeenCalled();

    fireEvent.change(textarea, { target: { value: 'first message' } });
    act(() => {
      fireEvent.keyDown(textarea, { key: 'Enter', code: 'Enter' });
      fireEvent.keyDown(textarea, { key: 'Enter', code: 'Enter' });
    });
    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
      expect(textarea.value).toBe('');
    });

    createRequest.resolve(REAL_CREATE_RESPONSE);
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

  it('shows a conversation-gone message and resets on 404 run_not_found from sendAssistantMessage', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    // The run turns out to be genuinely gone (foreign/nonexistent id, or a legacy pre-fix
    // zombie row) — NOT plain idle timeout, which now wakes the same run transparently.
    const err = new ApiError(404, JSON.stringify({ error: 'run_not_found', message: 'Run not found.' }));
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toContain('could not be found');
    // Page resets: empty state reappears so the user can start a new run.
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });
  });

  it('passes resume_from_run_id on the next new-run request after a 404 run_not_found reset', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run — this is the run that will later turn out to be gone.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    const err = new ApiError(404, JSON.stringify({ error: 'run_not_found', message: 'Run not found.' }));
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });

    // The very next submit starts a brand-new run and should auto-seed it with the
    // just-lost run's history via resume_from_run_id.
    typeAndSend('continuing message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(2));
    expect(apiClient.createAssistantRun).toHaveBeenLastCalledWith(
      expect.objectContaining({
        message: 'continuing message',
        resume_from_run_id: 'assistant-run-1',
      }),
    );
  });

  it('shows a conversation-ended message and resets on 409 operator_run_closed from sendAssistantMessage', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run.
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    // The run's durable event stream is already sealed with a genuinely terminal
    // run.completed event — a real end-of-conversation, not plain inactivity (idle runs are
    // dormant, not sealed, and wake transparently as the same run).
    const err = new ApiError(
      409,
      JSON.stringify({ error: 'operator_run_closed', message: 'Run is closed.' }),
    );
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValue(err);

    typeAndSend('second message');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toContain('has ended');
    // Page resets: empty state reappears so the user can start a new run.
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });
  });

  it('passes resume_from_run_id on the next new-run request after a 409 operator_run_closed reset', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    // First message creates the run — this is the run that will later be sealed as closed.
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
});
