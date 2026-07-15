import { apiClient } from '../api/apiClient';
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
  vi.mocked(apiClient.createAssistantRun).mockResolvedValue({ run_id: 'assistant-local-1', status: 'created' });
  vi.mocked(apiClient.sendAssistantMessage).mockResolvedValue({ status: 'queued' });
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

  it('creates a run on the first composer submit (stubbed create-run call)', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);
    typeAndSend('what projects exist?');

    await waitFor(() => {
      expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1);
    });
    expect(apiClient.createAssistantRun).toHaveBeenCalledWith(
      expect.objectContaining({ message: 'what projects exist?' }),
    );
    expect(apiClient.sendAssistantMessage).not.toHaveBeenCalled();
    // Once the run exists the empty state is replaced by the transcript.
    await waitFor(() => {
      expect(screen.queryByTestId('assistant-empty-state')).toBeNull();
    });
  });

  it('sends subsequent messages via the send-message stub once a run exists', async () => {
    render(<Wrapper><AssistantRunPage /></Wrapper>);

    typeAndSend('first message');
    await waitFor(() => expect(apiClient.createAssistantRun).toHaveBeenCalledTimes(1));

    typeAndSend('second message');
    await waitFor(() => {
      expect(apiClient.sendAssistantMessage).toHaveBeenCalledTimes(1);
    });
    expect(apiClient.sendAssistantMessage).toHaveBeenCalledWith(
      'assistant-local-1',
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
      expect(apiClient.approveTool).toHaveBeenCalledWith('assistant-local-1', 'req-1', 'once');
    });
  });
});
