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
      },
    ],
  });
  vi.mocked(apiClient.getRunEvents).mockResolvedValue([
    {
      sequence: 8,
      type: 'tool.call',
      payload: {
        callId: 'call-7',
        toolName: 'grep',
        timestamp_utc: '2026-09-11T16:00:01.000Z',
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
    expect(screen.getByLabelText('Trace summary').textContent).toContain('120 input');
    expect(screen.getByLabelText('Trace summary').textContent).toContain('45 output');
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

    fireEvent.click(screen.getByRole('tab', { name: 'Events' }));
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('tool.call');
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Sequence 8');
    expect(screen.getByLabelText('Persisted trace events').textContent).toContain('Call call-7');
  });
});
