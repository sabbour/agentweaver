import { apiClient } from '../api/apiClient';
import { TransactionTracePanel } from '../components/runs/TransactionTracePanel';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunTraces: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

afterEach(() => {
  vi.clearAllMocks();
});

describe('capture instrumentation markup', () => {
  it('exposes stable trace tree/span attributes and selected state', async () => {
    vi.mocked(apiClient.getRunTraces).mockResolvedValue({
      runId: 'run-1',
      spans: [{
        id: 'span-1',
        name: 'Coordinator',
        spanType: 'invoke-agent',
        timestamp: '2026-07-30T00:00:00Z',
        durationMs: 1200,
        success: true,
        agentName: 'Coordinator',
      }],
    });

    render(
      <Wrapper>
        <TransactionTracePanel runId="run-1" />
      </Wrapper>,
    );
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.getByTestId('transaction-trace-panel')).toBeTruthy();
    expect(screen.getByTestId('trace-tree')).toBeTruthy();
    const span = screen.getByTestId('trace-span');
    expect(span.getAttribute('data-span-key')).toBe('span-1');
    expect(span.getAttribute('data-span-type')).toBe('invoke-agent');
    expect(span.getAttribute('data-selected')).toBe('true');

    fireEvent.click(span);
    expect(span.getAttribute('data-selected')).toBe('true');
    expect(span.getAttribute('aria-pressed')).toBe('true');
  });
});
