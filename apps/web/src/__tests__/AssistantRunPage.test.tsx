import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AssistantRunPage } from '../pages/AssistantRunPage';
import { AssistantRoute } from '../routes/AssistantRoute';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
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

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location-probe">{`${location.pathname}${location.search}`}</div>;
}

function NewSessionRouteHarness() {
  const navigate = useNavigate();
  return (
    <>
      <button
        type="button"
        onClick={() => navigate(
          '/assistant?project=proj-7',
          { state: { assistantSessionKey: 'explicit-new-session' } },
        )}
        data-testid="new-session-nav"
      >
        New session
      </button>
      <button
        type="button"
        onClick={() => navigate('/assistant?project=proj-7&runId=assistant-run-2')}
        data-testid="established-run-nav"
      >
        Open another run
      </button>
      <AssistantRoute />
      <LocationProbe />
    </>
  );
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
  localStorage.removeItem('agentweaver:last-active-project-id');
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

  it('shows suggested prompt buttons on the empty state and hides them once a run exists', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    const suggestions = screen.getAllByTestId('assistant-suggested-prompt');
    // A small, curated set — not a huge list.
    expect(suggestions.length).toBeGreaterThanOrEqual(3);
    expect(suggestions.length).toBeLessThanOrEqual(5);

    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));
    await waitFor(() => {
      expect(screen.queryByTestId('assistant-suggested-prompts')).toBeNull();
    });
  });

  it('clicking a suggested prompt populates and focuses the composer without submitting it', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    const [firstSuggestion] = screen.getAllByTestId('assistant-suggested-prompt');
    const expectedText = firstSuggestion.textContent ?? '';
    expect(expectedText.length).toBeGreaterThan(0);

    fireEvent.click(firstSuggestion);

    const textarea = screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement;
    await waitFor(() => {
      expect(textarea.value).toBe(expectedText);
      expect(document.activeElement).toBe(textarea);
      expect(textarea.selectionStart).toBe(expectedText.length);
      expect(textarea.selectionEnd).toBe(expectedText.length);
    });
    // Populating is not the same as sending — no request should have gone out yet.
    expect(apiClient.createAssistantRun).not.toHaveBeenCalled();
  });

  it('resets the active run when navigation clears runId from the assistant route', async () => {
    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?project=proj-7&runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<NewSessionRouteHarness />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-empty-state')).toBeNull();
      expect(screen.getByTestId('location-probe').textContent).toBe('/assistant?project=proj-7&runId=assistant-run-1');
    });

    fireEvent.click(screen.getByTestId('new-session-nav'));

    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
      expect(screen.getByTestId('location-probe').textContent).toBe('/assistant?project=proj-7');
    });
  });

  it('does not remount an explicit new session when the first send adds runId', async () => {
    const openingTurn = deferred<typeof REAL_MESSAGE_RESPONSE>();
    vi.mocked(apiClient.sendAssistantMessage).mockReturnValueOnce(openingTurn.promise);
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      status: 'connecting',
    };

    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?project=proj-7&runId=assistant-run-old']}>
          <Routes>
            <Route path="/assistant" element={<NewSessionRouteHarness />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    fireEvent.click(screen.getByTestId('new-session-nav'));
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });

    typeAndSend('keep the explicit session mounted');

    await waitFor(() => {
      expect(screen.getByTestId('location-probe').textContent).toBe(
        '/assistant?project=proj-7&runId=assistant-run-1',
      );
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'keep the explicit session mounted',
      );
    });

    openingTurn.resolve(REAL_MESSAGE_RESPONSE);
  });

  it('remounts and resets local state when navigating between established run IDs', async () => {
    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?project=proj-7&runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<NewSessionRouteHarness />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    const textarea = screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: 'draft for the first run' } });
    expect(textarea.value).toBe('draft for the first run');

    fireEvent.click(screen.getByTestId('established-run-nav'));

    await waitFor(() => {
      expect(screen.getByTestId('location-probe').textContent).toBe(
        '/assistant?project=proj-7&runId=assistant-run-2',
      );
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
      expect(apiClient.getRunEvents).toHaveBeenCalledWith('assistant-run-2');
    });
  });

  it('starts a platform-wide run when /assistant has no explicit project query', async () => {
    localStorage.setItem('agentweaver:last-active-project-id', 'proj-remembered');

    render(<Wrapper><AssistantRoute /></Wrapper>);
    typeAndSend('what projects exist?');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
    });
    const [firstCallArgs] = vi.mocked(apiClient.createAssistantRun).mock.calls[0];
    expect(firstCallArgs.message).toBe('what projects exist?');
    expect(firstCallArgs.defer_first_turn).toBe(true);
    expect(firstCallArgs.project_id).toBeUndefined();
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        { message: 'what projects exist?' },
      );
    });
  });

  it('still uses an explicit project query when one is present', async () => {
    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?project=proj-7']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    typeAndSend('project-scoped request');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
    });
    expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
      expect.objectContaining({
        message: 'project-scoped request',
        defer_first_turn: true,
        project_id: 'proj-7',
      }),
    );
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        { message: 'project-scoped request' },
      );
    });
  });

  it('creates the run before sending the opening message so its stream can attach', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('what projects exist?');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
    });
    const [firstCallArgs] = vi.mocked(apiClient.createAssistantRun).mock.calls[0];
    expect(firstCallArgs.message).toBe('what projects exist?');
    expect(firstCallArgs.defer_first_turn).toBe(true);
    // A normal first-ever conversation never had a prior run to resume from.
    expect(firstCallArgs.resume_from_run_id).toBeUndefined();
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        { message: 'what projects exist?' },
      );
    });
    // Once the run exists the empty state is replaced by the transcript.
    await waitFor(() => {
      expect(screen.queryByTestId('assistant-empty-state')).toBeNull();
    });
  });

  it('preserves a message sent before the new session stream connects', async () => {
    const openingTurn = deferred<typeof REAL_MESSAGE_RESPONSE>();
    vi.mocked(apiClient.sendAssistantMessage).mockReturnValueOnce(openingTurn.promise);
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      status: 'connecting',
    };

    render(<Wrapper><AssistantRoute /></Wrapper>);
    typeAndSend('stream the first reply');

    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        { message: 'stream the first reply' },
      );
      expect(screen.queryByTestId('assistant-empty-state')).toBeNull();
      expect(screen.getByText(/Connected to operator run assistant-run-1/)).toBeTruthy();
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'stream the first reply',
      );
    });

    openingTurn.resolve(REAL_MESSAGE_RESPONSE);
  });

  it('merges reconnect history with a pending message without rendering a duplicate', async () => {
    const persistedHistory = deferred<Array<{
      sequence: number;
      type: string;
      payload: Record<string, unknown>;
    }>>();
    vi.mocked(apiClient.getRunEvents).mockReturnValueOnce(persistedHistory.promise as never);
    const view = render(<Wrapper><AssistantRoute /></Wrapper>);

    typeAndSend('keep this visible');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'keep this visible',
      );
    });

    const userEvent = {
      sequence: 1,
      type: 'agent.message',
      payload: {
        messageId: 'user-1',
        role: 'user',
        content: 'keep this visible',
      },
    };
    persistedHistory.resolve([userEvent]);

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
      expect(screen.getAllByTestId('timeline-message').filter(
        (message) => message.getAttribute('data-role') === 'user',
      )).toHaveLength(1);
    });

    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [userEvent],
      status: 'streaming',
    };
    view.rerender(<Wrapper><AssistantRoute /></Wrapper>);

    expect(screen.getAllByTestId('timeline-message').filter(
      (message) => message.getAttribute('data-role') === 'user',
    )).toHaveLength(1);
  });

  it('does not reconcile a repeated message against an older identical history turn', async () => {
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [{
        sequence: 1,
        type: 'agent.message',
        payload: { messageId: 'user-1', role: 'user', content: 'repeat this' },
      }],
    };
    const repeatedTurn = deferred<typeof REAL_MESSAGE_RESPONSE>();
    vi.mocked(apiClient.sendAssistantMessage).mockReturnValueOnce(repeatedTurn.promise);
    const view = render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await screen.findByText('repeat this');
    typeAndSend('repeat this');

    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'repeat this',
      );
    });

    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        mockRunStreamState.current.events[0],
        {
          sequence: 2,
          type: 'agent.message',
          payload: { messageId: 'user-2', role: 'user', content: 'repeat this' },
        },
      ],
    };
    view.rerender(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
      expect(screen.getAllByTestId('timeline-message').filter(
        (message) => message.getAttribute('data-role') === 'user',
      )).toHaveLength(2);
    });
    repeatedTurn.resolve(REAL_MESSAGE_RESPONSE);
  });

  it('keeps a repeated optimistic message visible when delayed seed history contains an older identical turn', async () => {
    const persistedHistory = deferred<Array<{
      sequence: number;
      type: string;
      payload: Record<string, unknown>;
    }>>();
    vi.mocked(apiClient.getRunEvents).mockReturnValueOnce(persistedHistory.promise as never);
    const view = render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    typeAndSend('repeat this');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain('repeat this');
    });

    persistedHistory.resolve([{
      sequence: 1,
      type: 'agent.message',
      payload: { messageId: 'user-1', role: 'user', content: 'repeat this' },
    }]);

    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain('repeat this');
      expect(screen.getAllByTestId('timeline-message').filter(
        (message) => message.getAttribute('data-role') === 'user',
      )).toHaveLength(1);
    });

    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [{
        sequence: 2,
        type: 'agent.message',
        payload: { messageId: 'user-2', role: 'user', content: 'repeat this' },
      }],
    };
    view.rerender(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
      expect(screen.getAllByTestId('timeline-message').filter(
        (message) => message.getAttribute('data-role') === 'user',
      )).toHaveLength(2);
    });
  });

  it('does not reconcile repeated text against replayed history after initial hydration fails', async () => {
    const oldUserEvent = {
      sequence: 1,
      type: 'agent.message',
      payload: { messageId: 'user-1', role: 'user', content: 'repeat after failure' },
    };
    vi.mocked(apiClient.getRunEvents)
      .mockRejectedValueOnce(new Error('history unavailable'))
      .mockResolvedValueOnce([oldUserEvent] as never);

    const view = render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await screen.findByRole('button', { name: 'Retry sync' });
    typeAndSend('repeat after failure');
    fireEvent.click(screen.getByRole('button', { name: 'Retry sync' }));

    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'repeat after failure',
      );
      expect(screen.getAllByTestId('timeline-message').filter(
        (message) => message.getAttribute('data-role') === 'user',
      )).toHaveLength(1);
    });

    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        oldUserEvent,
        {
          sequence: 2,
          type: 'agent.message',
          payload: { messageId: 'user-2', role: 'user', content: 'repeat after failure' },
        },
      ],
    };
    view.rerender(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
    });
  });

  it('keeps a retry-established baseline when the original hydration resolves later', async () => {
    const originalHydration = deferred<Array<{
      sequence: number;
      type: string;
      payload: Record<string, unknown>;
    }>>();
    const retryHydration = deferred<Array<{
      sequence: number;
      type: string;
      payload: Record<string, unknown>;
    }>>();
    const oldUserEvent = {
      sequence: 1,
      type: 'agent.message',
      payload: { messageId: 'user-1', role: 'user', content: 'overlapping repeat' },
    };
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      status: 'error',
      error: 'connection lost',
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRunEvents)
      .mockReturnValueOnce(originalHydration.promise as never)
      .mockReturnValueOnce(retryHydration.promise as never);

    const view = render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => expect(apiClient.getRunEvents).toHaveBeenCalledTimes(1));
    typeAndSend('overlapping repeat');
    await waitFor(() => expect(apiClient.getRunEvents).toHaveBeenCalledTimes(2));

    retryHydration.resolve([oldUserEvent]);
    await waitFor(() => {
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'overlapping repeat',
      );
    });

    originalHydration.resolve([]);
    await act(async () => {
      await originalHydration.promise;
    });
    expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
      'overlapping repeat',
    );

    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [{
        sequence: 2,
        type: 'agent.message',
        payload: { messageId: 'user-2', role: 'user', content: 'overlapping repeat' },
      }],
    };
    view.rerender(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => {
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
    });
  });

  it('clears the textarea before the first-run create request settles', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('  what projects exist?  ');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
        expect.objectContaining({
          message: 'what projects exist?',
          defer_first_turn: true,
        }),
      );
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    });
    expect(screen.getByTestId('assistant-pending-message').textContent).toContain('what projects exist?');

    createRequest.resolve(REAL_CREATE_RESPONSE);
    await waitFor(() => expect(screen.queryByTestId('assistant-empty-state')).toBeNull());
  });

  it('scrolls a newly sent pending message into view immediately', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);
    const originalScrollIntoView = Element.prototype.scrollIntoView;
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;

    try {
      render(<Wrapper><AssistantRunPage /></Wrapper>);
      typeAndSend('what projects exist?');

      await waitFor(() => {
        expect(screen.getByTestId('assistant-pending-message')).toBeTruthy();
      });
      await waitFor(() => {
        expect(scrollIntoView).toHaveBeenCalledWith({ block: 'end', behavior: 'smooth' });
      });
    } finally {
      createRequest.resolve(REAL_CREATE_RESPONSE);
      Element.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('sends subsequent messages via real send-message endpoint once a run exists', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    typeAndSend('first message');
    await waitFor(() => expect(apiClient.sendAssistantMessage).toHaveBeenCalledTimes(1));

    typeAndSend('second message');
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledTimes(2);
    });
    expect(apiClient.sendAssistantMessage).toHaveBeenLastCalledWith(
      'assistant-run-1',
      expect.objectContaining({ message: 'second message' }),
    );
    // The create call is not made again for follow-ups.
    expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
  });

  it('clears the textarea before a follow-up request settles', async () => {
    const messageRequest = deferred<typeof REAL_MESSAGE_RESPONSE>();

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('first message');
    await waitFor(() => expect(apiClient.sendAssistantMessage).toHaveBeenCalledTimes(1));
    vi.mocked(apiClient.sendAssistantMessage).mockReturnValueOnce(messageRequest.promise);

    typeAndSend('  second message  ');
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
        'assistant-run-1',
        expect.objectContaining({ message: 'second message' }),
      );
      expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    });
    expect(screen.getAllByTestId('assistant-pending-message').some(
      (message) => message.textContent?.includes('second message'),
    )).toBe(true);

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
    expect(screen.getByTestId('assistant-pending-message').textContent).toContain('hello');

    createRequest.reject(new Error('Request failed'));
    await waitFor(() => {
      expect(screen.getByTestId('assistant-error').textContent).toContain('Request failed');
    });
    expect((screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement).value).toBe('');
    expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
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

  it('keeps the textarea enabled and focused while a send is in flight (#item-1, #item-2)', async () => {
    const createRequest = deferred<typeof REAL_CREATE_RESPONSE>();
    vi.mocked(apiClient.createAssistantRun).mockReturnValueOnce(createRequest.promise);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    const textarea = screen.getByPlaceholderText('Message the assistant...') as HTMLTextAreaElement;
    textarea.focus();
    expect(document.activeElement).toBe(textarea);

    typeAndSend('hello there');

    // While the request is still pending, the textarea must stay enabled (not
    // force-blurred by a `disabled` attribute) and keep focus so the user can
    // immediately start typing their next message.
    await waitFor(() => expect(textarea.value).toBe(''));
    expect(textarea.disabled).toBe(false);
    expect(document.activeElement).toBe(textarea);

    createRequest.resolve(REAL_CREATE_RESPONSE);
    await waitFor(() => expect(screen.queryByTestId('assistant-empty-state')).toBeNull());
    expect(textarea.disabled).toBe(false);
    expect(document.activeElement).toBe(textarea);
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

  it('renders the native approval gate for a durable tool.approval_context event', async () => {
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      events: [
        {
          sequence: 1,
          type: 'tool.approval_context',
          payload: { RequestId: 'req-ctx-1', ToolName: 'coordinator_start', Url: 'https://example.test/start' },
        },
      ],
    };

    render(<Wrapper><AssistantRunPage /></Wrapper>);

    typeAndSend('start a run');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    expect(await screen.findByTestId('assistant-approval-gate')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Allow once' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Allow tool' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Allow once' }));
    await waitFor(() => {
      expect(apiClient.approveTool).toHaveBeenCalledWith('assistant-run-1', 'req-ctx-1', 'once');
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

  it('renders a backend validation message only once when createAssistantRun fails with a direct message', async () => {
    const err = new ApiError(
      400,
      JSON.stringify({
        error: 'project_context_required',
        message: 'Choose a project before starting an AgentHost assistant session.',
      }),
    );
    vi.mocked(apiClient.createAssistantRun).mockRejectedValue(err);

    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('hello');

    await waitFor(() => {
      expect(screen.getByTestId('assistant-error')).toBeTruthy();
    });
    expect(screen.getByTestId('assistant-error').textContent).toBe(
      'Choose a project before starting an AgentHost assistant session.',
    );
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
        resume_from_run_id: 'assistant-run-1',
      }),
    );
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenLastCalledWith(
        'assistant-run-1',
        { message: 'continuing message' },
      );
    });
  });

  it('keeps the resumed optimistic message mounted when a replacement runId is assigned', async () => {
    const gone = new ApiError(404, JSON.stringify({
      error: 'run_not_found',
      message: 'Run not found.',
    }));
    const resumedTurn = deferred<typeof REAL_MESSAGE_RESPONSE>();
    vi.mocked(apiClient.sendAssistantMessage)
      .mockRejectedValueOnce(gone)
      .mockReturnValueOnce(resumedTurn.promise);
    vi.mocked(apiClient.createAssistantRun).mockResolvedValueOnce({
      ...REAL_CREATE_RESPONSE,
      run_id: 'assistant-run-2',
    });

    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    typeAndSend('message on the missing run');
    await waitFor(() => {
      expect(screen.getByTestId('assistant-empty-state')).toBeTruthy();
    });

    typeAndSend('continue in the replacement run');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenLastCalledWith(
        expect.objectContaining({ resume_from_run_id: 'assistant-run-1' }),
      );
      expect(apiClient.sendAssistantMessage).toHaveBeenLastCalledWith(
        'assistant-run-2',
        { message: 'continue in the replacement run' },
      );
      expect(screen.getByTestId('assistant-pending-message').textContent).toContain(
        'continue in the replacement run',
      );
    });

    resumedTurn.resolve({
      ...REAL_MESSAGE_RESPONSE,
      run_id: 'assistant-run-2',
    });
  });

  it('does not resume a failed conversation after explicitly starting a new session', async () => {
    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?project=proj-7&runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<NewSessionRouteHarness />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    const err = new ApiError(404, JSON.stringify({
      error: 'run_not_found',
      message: 'Run not found.',
    }));
    vi.mocked(apiClient.sendAssistantMessage).mockRejectedValueOnce(err);

    typeAndSend('failed message');
    await waitFor(() => expect(screen.getByTestId('assistant-empty-state')).toBeTruthy());

    fireEvent.click(screen.getByTestId('new-session-nav'));
    vi.mocked(apiClient.createAssistantRun).mockResolvedValueOnce(REAL_CREATE_RESPONSE);
    vi.mocked(apiClient.sendAssistantMessage).mockResolvedValueOnce(REAL_MESSAGE_RESPONSE);
    typeAndSend('brand new message');

    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));
    expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
      expect.objectContaining({ resume_from_run_id: undefined }),
    );
  });

  it('refreshes durable history and reconnects after a successful send on a terminally errored stream', async () => {
    mockRunStreamState.current = {
      ...mockRunStreamState.current,
      status: 'error',
      error: 'connection lost',
      reconnect: vi.fn(),
    };
    vi.mocked(apiClient.getRunEvents)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{
        sequence: 1,
        type: 'agent.message',
        payload: { messageId: 'user-1', role: 'user', content: 'reconcile me' },
      }] as never);

    render(
      <AzureFluentProvider density="compact">
        <MemoryRouter initialEntries={['/assistant?runId=assistant-run-1']}>
          <Routes>
            <Route path="/assistant" element={<AssistantRoute />} />
          </Routes>
        </MemoryRouter>
      </AzureFluentProvider>,
    );

    await waitFor(() => expect(apiClient.getRunEvents).toHaveBeenCalledTimes(1));
    typeAndSend('reconcile me');

    await waitFor(() => {
      expect(apiClient.getRunEvents).toHaveBeenCalledTimes(2);
      expect(mockRunStreamState.current.reconnect).toHaveBeenCalledTimes(1);
      expect(screen.queryByTestId('assistant-pending-message')).toBeNull();
    });
    expect(screen.getByRole('button', { name: 'Retry sync' })).toBeDefined();
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
        resume_from_run_id: 'assistant-run-1',
      }),
    );
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenLastCalledWith(
        'assistant-run-1',
        { message: 'continuing message' },
      );
    });
  });
});
