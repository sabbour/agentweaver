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
      <span data-testid="provider-key">{execution.providerKey}</span>
      <span data-testid="phase">{execution.context?.phase}</span>
      <span data-testid="loading">{String(execution.loading)}</span>
      <button onClick={() => void execution.refresh()}>Refresh</button>
      <button onClick={() => execution.setPhase('active')}>Use</button>
      <button onClick={() => execution.applyCompletedContext({
        ...prepared('platform_github_copilot', ''),
        phase: 'completed',
        execution_key: null,
        expires_at: null,
      })}>Complete</button>
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

function ScopedHarness({ operation, projectId }: { operation: string; projectId: string }) {
  const execution = useAiExecutionContext(operation, projectId);
  return (
    <>
      <span data-testid="scoped-provider-key">{execution.providerKey}</span>
      <span data-testid="scoped-provider">
        {execution.context?.effective_model_provider?.provider_kind}
      </span>
      <span data-testid="scoped-error">{execution.error}</span>
      <span data-testid="scoped-loading">{String(execution.loading)}</span>
    </>
  );
}

function DeferredActionHarness({
  operation,
  projectId,
  completion,
  failure,
}: {
  operation: string;
  projectId: string;
  completion: Promise<AiExecutionContext>;
  failure: Promise<never>;
}) {
  const execution = useAiExecutionContext(operation, projectId);
  return (
    <>
      <span data-testid="action-provider-key">{execution.providerKey}</span>
      <span data-testid="action-provider">
        {execution.context?.effective_model_provider?.provider_kind}
      </span>
      <span data-testid="action-phase">{execution.context?.phase}</span>
      <span data-testid="action-error">{execution.error}</span>
      <span data-testid="action-announcement">{execution.announcement}</span>
      <button onClick={() => {
        void completion.then(execution.applyCompletedContext);
        void failure.catch(execution.handleInvocationError);
      }}>Start action</button>
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
  it('ignores stale preparation results, errors, and loading from a previous scope', async () => {
    let rejectA!: (reason: Error) => void;
    let resolveB!: (value: AiExecutionContext) => void;
    const requestA = new Promise<AiExecutionContext>((_, reject) => {
      rejectA = reject;
    });
    const requestB = new Promise<AiExecutionContext>((resolve) => {
      resolveB = resolve;
    });
    vi.mocked(apiClient.prepareAiExecutionContext).mockImplementation(
      (operation) => operation === 'operation-a' ? requestA : requestB,
    );

    const view = render(<ScopedHarness operation="operation-a" projectId="project-a" />);
    await waitFor(() => expect(apiClient.prepareAiExecutionContext).toHaveBeenCalledTimes(1));
    view.rerender(<ScopedHarness operation="operation-b" projectId="project-b" />);
    await waitFor(() => expect(apiClient.prepareAiExecutionContext).toHaveBeenCalledTimes(2));

    await act(async () => {
      resolveB(prepared('byok', 'key-b'));
      await requestB;
    });
    expect(screen.getByTestId('scoped-provider-key').textContent).toBe('key-b');
    expect(screen.getByTestId('scoped-provider').textContent).toBe('byok');
    expect(screen.getByTestId('scoped-loading').textContent).toBe('false');

    await act(async () => {
      rejectA(new Error('stale A failure'));
      await requestA.catch(() => undefined);
    });
    expect(screen.getByTestId('scoped-provider-key').textContent).toBe('key-b');
    expect(screen.getByTestId('scoped-provider').textContent).toBe('byok');
    expect(screen.getByTestId('scoped-error').textContent).toBe('');
    expect(screen.getByTestId('scoped-loading').textContent).toBe('false');
  });

  it('ignores completed contexts and invocation errors from actions that settle after navigation', async () => {
    let completeOldAction!: (value: AiExecutionContext) => void;
    let failOldAction!: (reason: ApiError) => void;
    const completion = new Promise<AiExecutionContext>((resolve) => {
      completeOldAction = resolve;
    });
    const failure = new Promise<never>((_, reject) => {
      failOldAction = reject;
    });
    vi.mocked(apiClient.prepareAiExecutionContext).mockImplementation(
      (operation) => Promise.resolve(operation === 'operation-a'
        ? prepared('platform_github_copilot', 'key-a')
        : prepared('byok', 'key-b')),
    );

    const view = render(
      <DeferredActionHarness
        operation="operation-a"
        projectId="project-a"
        completion={completion}
        failure={failure}
      />,
    );
    await waitFor(() => expect(screen.getByTestId('action-provider-key').textContent).toBe('key-a'));
    fireEvent.click(screen.getByRole('button', { name: 'Start action' }));

    view.rerender(
      <DeferredActionHarness
        operation="operation-b"
        projectId="project-b"
        completion={completion}
        failure={failure}
      />,
    );
    await waitFor(() => expect(screen.getByTestId('action-provider-key').textContent).toBe('key-b'));

    await act(async () => {
      completeOldAction({
        ...prepared('platform_github_copilot', ''),
        phase: 'completed',
        execution_key: null,
        expires_at: null,
      });
      failOldAction(new ApiError(
        409,
        JSON.stringify({
          error: 'model_provider_changed',
          context: prepared('platform_github_copilot', 'stale-key'),
        }),
      ));
      await Promise.allSettled([completion, failure]);
    });

    expect(screen.getByTestId('action-provider-key').textContent).toBe('key-b');
    expect(screen.getByTestId('action-provider').textContent).toBe('byok');
    expect(screen.getByTestId('action-phase').textContent).toBe('prepared');
    expect(screen.getByTestId('action-error').textContent).toBe('');
    expect(screen.getByTestId('action-announcement').textContent).toBe('');
  });

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

  it.each([
    ['active', 'Use'],
    ['completed', 'Complete'],
  ] as const)('renews the prepared key without replacing %s provider provenance', async (
    phase,
    action,
  ) => {
    vi.mocked(apiClient.prepareAiExecutionContext)
      .mockResolvedValueOnce(prepared('platform_github_copilot', 'key-1'))
      .mockResolvedValueOnce(prepared('byok', 'key-2'));

    render(<Harness />);
    await waitFor(() => expect(screen.getByTestId('provider-key').textContent).toBe('key-1'));
    fireEvent.click(screen.getByRole('button', { name: action }));
    expect(screen.getByTestId('phase').textContent).toBe(phase);

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(screen.getByTestId('provider-key').textContent).toBe('key-2'));

    expect(screen.getByTestId('phase').textContent).toBe(phase);
    expect(screen.getByTestId('label').textContent).toBe('platform_github_copilot');
  });
});
