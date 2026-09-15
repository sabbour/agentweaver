import { apiClient } from '../api/apiClient';
import { TransactionTracePanel } from '../components/runs/TransactionTracePanel';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

const sseMocks = vi.hoisted(() => ({
  useRunStream: vi.fn(),
}));

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunTraces: vi.fn(),
    getRunEvents: vi.fn(),
  },
}));

vi.mock('../api/sse', () => ({
  useRunStream: sseMocks.useRunStream,
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
  sseMocks.useRunStream.mockReturnValue({
    events: [],
    droppedEventCount: 0,
    status: 'streaming',
    error: null,
    reconnect: vi.fn(),
  });
  vi.mocked(apiClient.getRunTraces).mockResolvedValue({
    runId: 'run-47',
    spans: [
      {
        id: 'agent',
        name: 'Coordinator turn',
        spanType: 'invoke-agent',
        timestamp: '2026-09-11T16:00:00.000Z',
        durationMs: 3_000,
        success: true,
        agentName: 'Coordinator',
        model: 'gpt-5',
        inputTokens: 120,
        outputTokens: 45,
        attributes: {
          sessionId: 'agentweaver-run-run-47',
          runId: 'run-47',
          projectId: 'project-1',
          agentName: 'Coordinator',
          operationName: 'chat',
          modelId: 'gpt-5',
          providerSource: 'github-copilot',
          providerKind: 'github_copilot',
          policyShellEnabled: true,
          policyNetworkEnabled: false,
          sandboxBackend: 'kubernetes-sandbox-claim',
          sandboxIsolated: true,
          runtimePurpose: 'default',
          inputTokens: 120,
          outputTokens: 45,
          totalTokens: 165,
          status: 'success',
          execution: {
            processStartedAt: '2026-09-11T16:00:00.000Z',
            processEndedAt: '2026-09-11T16:00:03.000Z',
            hostProcessCpuMs: 750,
            hostProcessWorkingSetBytes: 1048576,
            hostProcessPeakWorkingSetBytes: 2097152,
          },
        },
      },
      {
        id: 'tool',
        parentId: 'agent',
        name: 'workspace search',
        spanType: 'tool',
        timestamp: '2026-09-11T16:00:01.000Z',
        durationMs: 500,
        success: false,
        resultCode: 'timeout',
        toolName: 'grep',
        toolCallId: 'call-7',
        attributes: {
          sessionId: 'agentweaver-run-run-47',
          runId: 'run-47',
          toolName: 'grep',
          toolCallId: 'call-7',
          toolSuccess: false,
          policyDecision: 'denied',
          status: 'error',
          errorType: 'policy_denied',
          execution: {
            processStartedAt: '2026-09-11T16:00:01.000Z',
            processEndedAt: '2026-09-11T16:00:01.500Z',
            hostProcessCpuMs: 750,
            hostProcessWorkingSetBytes: 1048576,
            hostProcessPeakWorkingSetBytes: 2097152,
          },
        },
      },
    ],
  });
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([
    {
      sequence: 8,
      type: 'tool.call',
      timestamp_utc: '2026-09-11T16:00:01.000Z',
      duration_ms: 500,
      status: 'pending',
      payload: {
        callId: 'call-7',
        toolName: 'grep',
        arguments: { pattern: 'trace' },
      },
    },
    {
      sequence: 9,
      type: 'tool.error',
      payload: { callId: 'call-7', errorMessage: 'timeout' },
    },
  ]);
});

afterEach(() => {
  // vi.clearAllMocks() clears recorded calls but keeps queued mockResolvedValueOnce
  // implementations. Several tests queue more "once" values than they consume, and the
  // leftovers then shadow the defaults that beforeEach sets for later tests. Reset the
  // implementations so each test starts from the same state whatever the run order.
  vi.useRealTimers();
  vi.resetAllMocks();
});

describe('TransactionTracePanel trace detail', () => {
  it('does not imply an absent trace when the telemetry source is temporarily unavailable', async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(apiClient.getRunTraces).mockResolvedValue({
        runId: 'run-47',
        spans: [],
        queryError: 'Application Insights trace telemetry is temporarily unavailable. Retry shortly.',
      });
      render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

      await act(async () => {
        await Promise.resolve();
        await Promise.resolve();
      });
      await act(async () => {
        vi.advanceTimersByTime(500);
        await Promise.resolve();
        await Promise.resolve();
      });
      await act(async () => {
        vi.advanceTimersByTime(1_500);
        await Promise.resolve();
        await Promise.resolve();
      });

      expect(screen.getByText('Trace spans are temporarily unavailable.')).toBeTruthy();
      expect(screen.getByText(/This does not mean the run produced no trace data/)).toBeTruthy();
      expect(screen.queryByText('No trace data available for this run yet.')).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('automatically retries a first-load trace dependency failure without showing the failure banner', async () => {
    vi.useFakeTimers();
    try {
      const queryError = 'Application Insights trace telemetry is temporarily unavailable after a dependency failure. Retry shortly.';
      vi.mocked(apiClient.getRunTraces)
        .mockResolvedValueOnce({ runId: 'run-47', spans: [], queryError })
        .mockResolvedValueOnce({
          runId: 'run-47',
          spans: [{ id: 'agent', name: 'Coordinator turn', timestamp: '2026-09-11T16:00:00.000Z', durationMs: 1, success: true }],
        });

      render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

      await act(async () => {
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(1);
      expect(screen.getByText('Loading transaction trace')).toBeTruthy();
      expect(screen.queryByText(queryError)).toBeNull();
      expect(screen.queryByText('Trace spans are temporarily unavailable.')).toBeNull();

      await act(async () => {
        vi.advanceTimersByTime(500);
        await Promise.resolve();
        await Promise.resolve();
      });

      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(2);
      expect(screen.getByTestId('trace-tree').querySelector('[data-span-key="agent"]')).toBeTruthy();
      expect(screen.queryByText(queryError)).toBeNull();
      expect(screen.queryByText('Trace spans are temporarily unavailable.')).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('surfaces a persistent first-load trace dependency failure after retries and keeps manual Retry working', async () => {
    vi.useFakeTimers();
    try {
      const queryError = 'Application Insights trace telemetry is temporarily unavailable after a dependency failure. Retry shortly.';
      vi.mocked(apiClient.getRunTraces).mockResolvedValue({ runId: 'run-47', spans: [], queryError });

      render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

      await act(async () => {
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(1);
      expect(screen.getByText('Loading transaction trace')).toBeTruthy();
      expect(screen.queryByText(queryError)).toBeNull();

      await act(async () => {
        vi.advanceTimersByTime(500);
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(2);
      expect(screen.queryByText(queryError)).toBeNull();

      await act(async () => {
        vi.advanceTimersByTime(1_500);
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(3);
      expect(screen.getByText(queryError)).toBeTruthy();
      expect(screen.getByText('Trace spans are temporarily unavailable.')).toBeTruthy();
      expect(screen.getByText(/This does not mean the run produced no trace data/)).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
      await act(async () => {
        await Promise.resolve();
        await Promise.resolve();
      });
      expect(apiClient.getRunTraces).toHaveBeenCalledTimes(4);
      expect(screen.getByText('Loading transaction trace')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('loads trace pages incrementally without replacing already-loaded spans', async () => {
    vi.mocked(apiClient.getRunTraces)
      .mockResolvedValueOnce({
        runId: 'run-47',
        hasMore: true,
        nextCursor: 'next-page',
        spans: [{ id: 'initial', name: 'initial', timestamp: '2026-09-11T16:00:00.000Z', durationMs: 1, success: true }],
      })
      .mockResolvedValueOnce({
        runId: 'run-47',
        hasMore: false,
        spans: [{ id: 'later', name: 'later', timestamp: '2026-09-11T16:00:01.000Z', durationMs: 1, success: true }],
      });
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await waitFor(() => expect(screen.getByLabelText('More trace spans available')).toBeTruthy());
    expect(apiClient.getRunTraces).toHaveBeenLastCalledWith('run-47');
    fireEvent.click(screen.getByRole('button', { name: 'Load more spans' }));
    await waitFor(() => expect(apiClient.getRunTraces).toHaveBeenLastCalledWith('run-47', { cursor: 'next-page' }));
    expect(screen.getByTestId('trace-tree').querySelector('[data-span-key="initial"]')).toBeTruthy();
    expect(screen.getByTestId('trace-tree').querySelector('[data-span-key="later"]')).toBeTruthy();
  });

  it('keeps the continuation available after a page failure and retries the same cursor', async () => {
    vi.mocked(apiClient.getRunTraces)
      .mockResolvedValueOnce({
        runId: 'run-47', hasMore: true, nextCursor: 'retry-page',
        spans: [{ id: 'initial', name: 'initial', timestamp: '2026-09-11T16:00:00.000Z', durationMs: 1, success: true }],
      })
      .mockRejectedValueOnce(new Error('unavailable'))
      .mockResolvedValueOnce({
        runId: 'run-47', hasMore: false,
        spans: [{ id: 'later', name: 'later', timestamp: '2026-09-11T16:00:01.000Z', durationMs: 1, success: true }],
      });
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Load more spans' })).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Load more spans' }));
    await waitFor(() => expect(screen.getByLabelText('Trace page load failed')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(apiClient.getRunTraces).toHaveBeenLastCalledWith('run-47', { cursor: 'retry-page' }));
  });
  it('renders a data-backed summary, hierarchical timeline, and selected span inspector', async () => {
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByLabelText('Trace summary').textContent).toContain('Coordinator');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('run-47');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('agentweaver-run-run-47');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('120 input');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('45 output');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('Run active');
    expect(screen.getByTestId('trace-timeline')).toBeTruthy();
    expect(screen.getAllByTestId('trace-span')).toHaveLength(3);
    expect(screen.getAllByTestId('trace-span')[0].textContent).toContain('Coordinator');
    expect(screen.getAllByTestId('trace-span').some((span) => span.getAttribute('data-span-key') === 'agent::llm')).toBe(true);

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    expect(toolSpan?.textContent).toContain('timeout');
    expect(apiClient.getRunEvents).not.toHaveBeenCalled();
    fireEvent.click(toolSpan!);

    expect(toolSpan?.getAttribute('data-selected')).toBe('true');
    expect(screen.getByLabelText('Span inspector').textContent).toContain('timeout');
    expect(screen.getAllByTestId('trace-duration-bar')).toHaveLength(3);
  });

  it('shows normalized span attributes and persisted events in their respective tabs', async () => {
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);
    fireEvent.click(screen.getByRole('tab', { name: 'Attributes' }));
    expect(screen.getByText('tool.call.id')).toBeTruthy();
    expect(screen.getByText('call-7')).toBeTruthy();
    expect(screen.getByText('policy.decision')).toBeTruthy();
    expect(screen.getByText('denied')).toBeTruthy();
    expect(screen.getAllByText('Not recorded').length).toBeGreaterThan(0);
    expect(screen.getByText('Execution diagnostics')).toBeTruthy();
    expect(screen.getByText('Host-process evidence recorded; bottleneck is not determined from this trace alone.')).toBeTruthy();
    expect(screen.getByText('750 ms')).toBeTruthy();
    expect(screen.getByText('1.0 MB')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: 'Events' }));
    await waitFor(() => expect(screen.getByLabelText('Persisted trace events').textContent).toContain('tool.call'));
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Sequence 8');
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Call call-7');
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Duration 500 ms');
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Pending');
  });

  it('reports a successful trace when its successful root recovered from a failed tool attempt', async () => {
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByLabelText('Trace summary').textContent).toContain('Success');
  });

  it('keeps a long timeline in an accessible internal scroll region beside the inspector', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-47',
      spans: Array.from({ length: 40 }, (_, index) => ({
        id: `tool-${index}`,
        name: `tool ${index}`,
        spanType: 'tool' as const,
        timestamp: `2026-09-11T16:00:${String(index).padStart(2, '0')}.000Z`,
        durationMs: 100,
        success: true,
      })),
    });
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const timeline = screen.getByRole('region', { name: 'Trace timeline' });
    expect(timeline).toBe(screen.getByTestId('trace-timeline'));
    expect(timeline.getAttribute('tabindex')).toBe('0');
    expect(timeline.getAttribute('data-scrollable')).toBe('true');
    expect(screen.getAllByTestId('trace-span')).toHaveLength(40);
    expect(screen.getByLabelText('Span inspector')).toBeTruthy();
  });

  it('renders populated tool input and structured output from persisted events', async () => {
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 8,
        type: 'tool.call',
        payload: { callId: 'call-7', toolName: 'grep', arguments: { pattern: 'trace', path: 'src' } },
      },
      {
        sequence: 9,
        type: 'tool.result',
        payload: { callId: 'call-7', content: '{"matches":["src/trace.ts"],"count":1}' },
      },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);

    await waitFor(() => expect(screen.getByText('Input')).toBeTruthy());
    expect(screen.getByText(/"pattern": "trace"/)).toBeTruthy();
    expect(screen.getByText(/"matches": \[/)).toBeTruthy();
    expect(screen.getByText(/"src\/trace.ts"/)).toBeTruthy();
  });

  it('renders populated tool input and output from span attributes when persisted events are absent', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-47',
      spans: [
        {
          id: 'tool',
          name: 'run_command',
          spanType: 'tool',
          timestamp: '2026-09-11T16:00:01.000Z',
          durationMs: 500,
          success: true,
          toolName: 'run_command',
          toolCallId: 'call-7',
          attributes: {
            runId: 'run-47',
            toolName: 'run_command',
            toolCallId: 'call-7',
            toolInput: '{"command":"npm test"}',
            toolInputState: 'captured',
            toolOutput: 'stdout:\nok\nexit_code: 0',
            toolOutputState: 'captured',
          },
        },
      ],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    fireEvent.click(screen.getByTestId('trace-span'));
    fireEvent.click(screen.getByText('Command details'));

    expect(screen.getByText(/"command": "npm test"/)).toBeTruthy();
    expect(screen.getByLabelText('Span inspector').textContent).toContain('stdout:\nok');
  });

  it('renders distinct explanations for each missing or transformed span payload state', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-47',
      spans: [
        {
          id: 'not-captured',
          name: 'grep',
          spanType: 'tool',
          timestamp: '2026-09-11T16:00:01.000Z',
          durationMs: 100,
          success: true,
          toolName: 'grep',
          toolCallId: 'not-captured-call',
          attributes: {
            toolName: 'grep',
            toolCallId: 'not-captured-call',
            toolInputState: 'not_captured',
            toolOutputState: 'not_captured',
          },
        },
        {
          id: 'truncated',
          name: 'read_file',
          spanType: 'tool',
          timestamp: '2026-09-11T16:00:02.000Z',
          durationMs: 100,
          success: true,
          toolName: 'read_file',
          toolCallId: 'truncated-call',
          attributes: {
            toolName: 'read_file',
            toolCallId: 'truncated-call',
            toolInput: '{"path":"big.log"}',
            toolInputState: 'captured',
            toolOutput: 'line 1\n… [truncated because too large]',
            toolOutputState: 'truncated',
          },
        },
        {
          id: 'redacted',
          name: 'web_fetch',
          spanType: 'tool',
          timestamp: '2026-09-11T16:00:03.000Z',
          durationMs: 100,
          success: true,
          toolName: 'web_fetch',
          toolCallId: 'redacted-call',
          attributes: {
            toolName: 'web_fetch',
            toolCallId: 'redacted-call',
            toolInput: '{"url":"https://example.com","authorization":"***REDACTED***"}',
            toolInputState: 'redacted',
            toolOutput: '***REDACTED***',
            toolOutputState: 'redacted',
          },
        },
      ],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const spans = screen.getAllByTestId('trace-span');
    fireEvent.click(spans.find((span) => span.getAttribute('data-span-key') === 'not-captured')!);
    expect(screen.getByText('Input was not captured in telemetry.')).toBeTruthy();
    expect(screen.getByText('Output was not captured in telemetry.')).toBeTruthy();
    expect(screen.queryByText('No input')).toBeNull();

    fireEvent.click(spans.find((span) => span.getAttribute('data-span-key') === 'truncated')!);
    expect(screen.getByText('Truncated because too large')).toBeTruthy();
    expect(screen.getByText(/line 1/)).toBeTruthy();

    fireEvent.click(spans.find((span) => span.getAttribute('data-span-key') === 'redacted')!);
    expect(screen.getAllByText('Redacted by policy').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/\*\*\*REDACTED\*\*\*/).length).toBeGreaterThan(0);
  });

  it('renders live run_command heartbeat progress from the run stream', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-47',
      spans: [
        {
          id: 'tool',
          name: 'run_command',
          spanType: 'tool',
          timestamp: '2026-09-11T16:00:01.000Z',
          durationMs: 12_000,
          success: true,
          toolName: 'run_command',
          toolCallId: 'call-7',
          attributes: {
            runId: 'run-47',
            toolName: 'run_command',
            toolCallId: 'call-7',
            status: 'success',
          },
        },
      ],
    });
    sseMocks.useRunStream.mockReturnValue({
      events: [
        {
          sequence: 8,
          type: 'tool.execution_pending',
          payload: {
            runId: 'run-47',
            toolCallId: 'call-7',
            toolName: 'run_command',
            startedAtUtc: '2026-09-11T16:00:01.000Z',
            deadlineUtc: '2026-09-11T16:10:01.000Z',
            elapsedSeconds: 12,
          },
        },
      ],
      droppedEventCount: 0,
      status: 'streaming',
      error: null,
      reconnect: vi.fn(),
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([]);

    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    expect(toolSpan?.textContent).toContain('Running · 12 s');
    fireEvent.click(toolSpan!);
    expect(screen.getByLabelText('Span inspector').textContent).toContain('Live progress');
    expect(screen.getByLabelText('Span inspector').textContent).toContain('Running for 12 s');
  });

  it('labels an absent persisted tool output with a capture reason', async () => {
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 8,
        type: 'tool.call',
        payload: { callId: 'call-7', toolName: 'grep', arguments: { pattern: 'trace' } },
      },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);

    await waitFor(() => expect(screen.getByText('Output was not captured in telemetry.')).toBeTruthy());
  });

  it('distinguishes a recovered failed attempt from an active or terminally failed run', async () => {
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 8,
        type: 'tool.call',
        payload: { callId: 'call-7', toolName: 'grep', arguments: { pattern: 'trace' } },
      },
      {
        sequence: 9,
        type: 'tool.error',
        payload: { callId: 'call-7', errorMessage: 'tool timed out' },
      },
      { sequence: 10, type: 'run.completed', payload: {} },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);
    await waitFor(() => expect(screen.getByLabelText('Trace summary').textContent).toContain('Completed run'));
    expect(screen.getByLabelText('Trace summary').textContent).toContain('Failed tool attempts');
    expect(screen.getByLabelText('Span inspector').textContent)
      .toContain('Recovered — run completed after this failed attempt');
  });

  it('identifies a failed tool attempt within a terminally failed run', async () => {
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      { sequence: 8, type: 'tool.error', payload: { callId: 'call-7', errorMessage: 'tool timed out' } },
      { sequence: 9, type: 'run.failed', payload: {} },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);
    await waitFor(() => expect(screen.getByLabelText('Trace summary').textContent).toContain('Terminal failed run'));
    expect(screen.getByLabelText('Span inspector').textContent).toContain('Run failed — terminal outcome');
  });

  it('keeps run-command input and output collapsed until requested', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-47',
      spans: [{
        id: 'command',
        name: 'run_command',
        spanType: 'tool',
        timestamp: '2026-09-11T16:00:01.000Z',
        durationMs: 500,
        success: true,
        toolName: 'run_command',
        toolCallId: 'call-command',
      }],
    });
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 8,
        type: 'tool.call',
        payload: { callId: 'call-command', toolName: 'run_command', arguments: { command: 'dotnet test' } },
      },
      {
        sequence: 9,
        type: 'tool.result',
        payload: { callId: 'call-command', content: 'exit_code: 0' },
      },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    fireEvent.click(screen.getByTestId('trace-span'));

    const details = screen.getByTestId('run-command-details') as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(screen.getByText('Command details')).toBeTruthy();

    fireEvent.click(screen.getByText('Command details'));
    expect(details.open).toBe(true);
    expect(screen.getByText(/"command": "dotnet test"/)).toBeTruthy();
    expect(screen.getByText('exit_code: 0')).toBeTruthy();
  });

  it('redacts sensitive input and output again before rendering legacy event data', async () => {
    const secret = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789';
    vi.mocked(apiClient.getRunEvents).mockResolvedValue([
      {
        sequence: 8,
        type: 'tool.call',
        payload: { callId: 'call-7', toolName: 'grep', arguments: { authorization: `Bearer ${secret}` } },
      },
      {
        sequence: 9,
        type: 'tool.result',
        payload: { callId: 'call-7', content: secret },
      },
    ]);
    render(<Wrapper><TransactionTracePanel runId="run-47" /></Wrapper>);

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const toolSpan = screen.getAllByTestId('trace-span').find((span) => span.getAttribute('data-span-key') === 'tool');
    fireEvent.click(toolSpan!);

    await waitFor(() => expect(screen.queryByText(secret)).toBeNull());
    expect(screen.getAllByText('Redacted by policy')).toHaveLength(2);
    expect(screen.getAllByText(/\*\*\*REDACTED\*\*\*/)).toHaveLength(2);
  });
});
