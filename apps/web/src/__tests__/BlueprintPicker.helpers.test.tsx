import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/apiClient';
import { useBlueprintGeneration } from '../components/BlueprintPicker.helpers';
import type { AiExecutionContext, Blueprint } from '../api/types';

const setPhase = vi.fn();
const applyCompletedContext = vi.fn();
const handleInvocationError = vi.fn(() => false);
const restorePreparedContext = vi.fn();

vi.mock('../api/apiClient', () => ({
  apiClient: {
    generateBlueprint: vi.fn(),
  },
}));

vi.mock('../hooks/useAiExecutionContext', () => ({
  useAiExecutionContext: () => ({
    context: null,
    providerKey: 'prepared-key',
    available: true,
    loading: false,
    error: null,
    announcement: '',
    refresh: vi.fn(),
    handleInvocationError,
    applyCompletedContext,
    applyProvider: vi.fn(),
    setPhase,
    restorePreparedContext,
  }),
}));

const blueprint: Blueprint = {
  id: 'generated',
  name: 'Generated',
  description: 'Generated blueprint',
  roster: ['builder'],
  workflow: 'default',
  workflows: [],
  review_policy: 'default',
  sandbox_profile: 'default',
};

const completedContext: AiExecutionContext = {
  ai_required: true,
  operation: 'blueprint_generation',
  phase: 'completed',
  execution_key: null,
  expires_at: null,
  effective_model_provider: {
    state: 'resolved',
    provider_kind: 'platform_github_copilot',
    resolution_scope: 'platform',
    provider_scope: 'platform',
    provider_type: null,
    model_id: 'gpt-5',
    provider_key: 'completed-provider',
    unavailable_reason: null,
  },
};

beforeEach(() => vi.clearAllMocks());

describe('useBlueprintGeneration provider provenance', () => {
  it('shows Using during dispatch and applies Used provenance on success', async () => {
    let resolve!: (value: {
      blueprint: Blueprint;
      ai_execution_context: AiExecutionContext;
    }) => void;
    const response = new Promise<{
      blueprint: Blueprint;
      ai_execution_context: AiExecutionContext;
    }>((next) => {
      resolve = next;
    });
    vi.mocked(apiClient.generateBlueprint).mockReturnValue(response);
    const onChange = vi.fn();
    const { result } = renderHook(() => useBlueprintGeneration(onChange));

    let generation!: Promise<void>;
    act(() => {
      generation = result.current.generate('build a service');
    });

    expect(setPhase).toHaveBeenCalledWith('active');
    expect(setPhase.mock.invocationCallOrder[0])
      .toBeLessThan(vi.mocked(apiClient.generateBlueprint).mock.invocationCallOrder[0]);
    expect(applyCompletedContext).not.toHaveBeenCalled();

    await act(async () => {
      resolve({ blueprint, ai_execution_context: completedContext });
      await generation;
    });

    expect(applyCompletedContext).toHaveBeenCalledWith(completedContext);
    expect(onChange).toHaveBeenCalledWith({
      kind: 'generated',
      blueprint,
      generatedWorkflowYaml: undefined,
    });
  });

  it('restores the prepared provider after a non-provider generation failure', async () => {
    vi.mocked(apiClient.generateBlueprint).mockRejectedValue(new Error('generation failed'));
    const { result } = renderHook(() => useBlueprintGeneration(vi.fn()));

    await act(async () => {
      await result.current.generate('build a service');
    });

    expect(setPhase).toHaveBeenCalledWith('active');
    expect(handleInvocationError).toHaveBeenCalled();
    expect(restorePreparedContext).toHaveBeenCalledTimes(1);
    expect(result.current.error).toBe('generation failed');
  });
});
