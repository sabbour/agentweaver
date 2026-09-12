import { apiClient } from '../api/apiClient';
import { TransactionTracePanel } from '../components/runs/TransactionTracePanel';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunTraces: vi.fn(),
    getRunEvents: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
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
  vi.clearAllMocks();
});

describe('TransactionTracePanel trace detail', () => {
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

    fireEvent.click(screen.getByRole('tab', { name: 'Events' }));
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('tool.call');
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

    expect(screen.getByText('Input')).toBeTruthy();
    expect(screen.getByText(/"pattern": "trace"/)).toBeTruthy();
    expect(screen.getByText(/"matches": \[/)).toBeTruthy();
    expect(screen.getByText(/"src\/trace.ts"/)).toBeTruthy();
  });

  it('labels an absent persisted tool output with correctly spaced text', async () => {
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

    expect(screen.getByText('No output')).toBeTruthy();
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
    expect(screen.getByLabelText('Trace summary').textContent).toContain('Completed run');
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
    expect(screen.getByLabelText('Trace summary').textContent).toContain('Terminal failed run');
    expect(screen.getByLabelText('Span inspector').textContent).toContain('Run failed — terminal outcome');
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

    expect(screen.queryByText(secret)).toBeNull();
    expect(screen.getAllByText('Redacted')).toHaveLength(2);
    expect(screen.getAllByText(/\*\*\*REDACTED\*\*\*/)).toHaveLength(2);
  });
});
