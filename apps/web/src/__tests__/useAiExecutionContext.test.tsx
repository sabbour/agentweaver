import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { useAiExecutionContext } from '../hooks/useAiExecutionContext';
import type { AiExecutionContext } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    prepareAiExecutionContext: vi.fn(),
  },
}));

const prepared = (
  providerKind: 'platform_github_copilot' | 'byok',
  providerKey: string,
  expiresAt = '2099-01-01T00:00:00Z',
): AiExecutionContext => ({
  ai_required: true,
  operation: 'orchestration',
  phase: 'prepared',
  execution_key: providerKey,
  expires_at: expiresAt,
  effective_model_provider: {
    state: 'resolved',
    provider_kind: providerKind,
    resolution_scope: 'project',
    provider_scope: 'platform',
    provider_type: providerKind === 'byok' ? 'azure' : null,
    model_id: 'gpt-5',
    provider_key: `fingerprint-${providerKind}`,
    unavailable_reason: null,
  },
});

function Harness() {
  const execution = useAiExecutionContext('orchestration', 'project-1');
  return (
    <>
      <span data-testid="label">{execution.context?.effective_model_provider?.provider_kind}</span>
      <span data-testid="announcement">{execution.announcement}</span>
      <span data-testid="error">{execution.error}</span>
      <button onClick={() => void execution.refresh()}>Refresh</button>
      <button onClick={() => execution.handleInvocationError(new ApiError(
        409,
        JSON.stringify({ error: 'model_provider_changed', context: prepared('platform_github_copilot', 'key-2') }),
      ))}>Replace</button>
      <button onClick={() => execution.handleInvocationError(new ApiError(
        409,
        JSON.stringify({ error: 'ai_execution_context_expired', context: prepared('platform_github_copilot', 'key-2') }),
      ))}>Expire</button>
    </>
  );
}

function OnDemandHarness() {
  const execution = useAiExecutionContext(
    'marketplace_catalog_classification',
    'project-1',
    undefined,
    false,
  );
  return (
    <>
      <span data-testid="on-demand-label">{execution.context?.effective_model_provider?.provider_kind}</span>
      <span data-testid="on-demand-announcement">{execution.announcement}</span>
      <span data-testid="on-demand-error">{execution.error}</span>
      <button onClick={() => execution.handleInvocationError(new ApiError(
        409,
        JSON.stringify({
          error: 'ai_execution_context_required',
          context: prepared('platform_github_copilot', 'key-2'),
        }),
      ))}>Require</button>
    </>
  );
}

beforeEach(() => vi.clearAllMocks());
afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe('useAiExecutionContext', () => {
  it('announces a same-kind provider replacement detected during refresh', async () => {
    const replacement = prepared('byok', 'new-execution-key');
    replacement.effective_model_provider!.provider_key = 'replacement-fingerprint';
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared('byok', 'old-execution-key'))
      .mockResolvedValueOnce(replacement);

    render(<Harness />);
    await waitFor(() => expect(screen.getByTestId('label').textContent).toBe('byok'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(screen.getByTestId('announcement').textContent)
      .toBe('AI provider changed. Expected provider: Azure BYOK. Model: gpt-5.'));
    expect(screen.queryByText('replacement-fingerprint')).toBeNull();
  });

  it('announces only a normalized provider identity change', async () => {
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared('platform_github_copilot', 'key-1'))
      .mockResolvedValueOnce(prepared('platform_github_copilot', 'key-2'))
      .mockResolvedValueOnce(prepared('byok', 'key-3'));

    render(<Harness />);
    await waitFor(() =>
      expect(screen.getByTestId('label').textContent).toBe('platform_github_copilot'),
    );
    expect(screen.getByTestId('announcement').textContent).toBe('');

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(apiClient.prepareAiExecutionContext).toHaveBeenCalledTimes(2));
    expect(screen.getByTestId('announcement').textContent).toBe('');

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(screen.getByTestId('label').textContent).toBe('byok'));
    expect(screen.getByTestId('announcement').textContent)
      .toBe('AI provider changed. Expected provider: Azure BYOK. Model: gpt-5.');
  });

  it('announces a server-reported provider replacement even when its public label is unchanged', async () => {
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared('platform_github_copilot', 'key-1'));

    render(<Harness />);
    await waitFor(() =>
      expect(screen.getByTestId('label').textContent).toBe('platform_github_copilot'),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Replace' }));

    expect(screen.getByTestId('announcement').textContent)
      .toBe('AI provider changed. Expected provider: GitHub Copilot. Model: gpt-5.');
  });

  it('reports an expired confirmation without claiming the provider changed', async () => {
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared('platform_github_copilot', 'key-1'));

    render(<Harness />);
    await waitFor(() =>
      expect(screen.getByTestId('label').textContent).toBe('platform_github_copilot'),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Expire' }));

    expect(screen.getByTestId('announcement').textContent)
      .toBe('AI provider confirmation expired. Expected provider: GitHub Copilot. Model: gpt-5.');
    expect(screen.getByTestId('error').textContent)
      .toBe('The AI provider confirmation expired. Review the provider and retry the action.');
  });

  it('supports on-demand provider preparation without an eager preflight', async () => {
    render(<OnDemandHarness />);

    await waitFor(() => expect(apiClient.prepareAiExecutionContext).not.toHaveBeenCalled());
    expect(screen.getByTestId('on-demand-label').textContent).toBe('');

    fireEvent.click(screen.getByRole('button', { name: 'Require' }));

    expect(screen.getByTestId('on-demand-label').textContent).toBe('platform_github_copilot');
    expect(screen.getByTestId('on-demand-announcement').textContent)
      .toBe('AI provider confirmation required. Expected provider: GitHub Copilot. Model: gpt-5.');
    expect(screen.getByTestId('on-demand-error').textContent)
      .toBe('Review the AI provider and retry the action.');
  });

  it('refreshes a prepared execution key before it expires', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared(
        'platform_github_copilot',
        'key-1',
        '2026-01-01T00:01:00Z',
      ))
      .mockResolvedValueOnce(prepared(
        'platform_github_copilot',
        'key-2',
        '2026-01-01T00:06:00Z',
      ));

    render(<Harness />);
    await act(async () => {
      await Promise.resolve();
    });
    expect(apiClient.prepareAiExecutionContext).toHaveBeenCalledTimes(1);

    await act(async () => {
      vi.advanceTimersByTime(30_001);
      await Promise.resolve();
    });

    expect(apiClient.prepareAiExecutionContext).toHaveBeenCalledTimes(2);
  });
});
