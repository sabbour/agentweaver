import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/apiClient';
import { useBlueprintGeneration } from '../components/BlueprintPicker.helpers';
import type { AiExecutionContext, Blueprint, BlueprintGenerationJob } from '../api/types';

const setPhase = vi.fn();
const applyCompletedContext = vi.fn();
const handleInvocationError = vi.fn(() => false);
const restorePreparedContext = vi.fn();

vi.mock('../api/apiClient', () => ({
  apiClient: {
    generateBlueprint: vi.fn(),
    getBlueprintGenerationJob: vi.fn(),
    getBlueprintGenerationResult: vi.fn(),
    cancelBlueprintGeneration: vi.fn(),
    retryBlueprintGeneration: vi.fn(),
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

const completedJob: BlueprintGenerationJob = {
  job_id: 'job-1',
  status: 'completed',
  attempt: 1,
  provider_snapshot: {
    provider_kind: 'platform_github_copilot',
    provider_key: 'completed-provider',
    provider_scope: 'platform',
    resolution_scope: 'platform',
  },
  artifact: { artifact_id: 'artifact-1', logical_id: 'generated', version: 1 },
  created_at: '2026-09-23T00:00:00Z',
  updated_at: '2026-09-23T00:00:01Z',
  status_url: '/status',
  result_url: '/result',
  cancel_url: '/cancel',
  retry_url: '/retry',
  ai_execution_context: completedContext,
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getBlueprintGenerationResult).mockResolvedValue({
    job_id: 'job-1',
    artifact_id: 'artifact-1',
    logical_id: 'generated',
    version: 1,
    blueprint,
    warnings: [],
  });
});

describe('useBlueprintGeneration provider provenance', () => {
  it('shows Using during dispatch and applies Used provenance on success', async () => {
    let resolve!: (value: BlueprintGenerationJob) => void;
    const response = new Promise<BlueprintGenerationJob>((next) => {
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
      resolve(completedJob);
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

  it('applies a completed result when cancellation loses the completion race', async () => {
    const runningJob = { ...completedJob, status: 'running' as const, artifact: null };
    vi.mocked(apiClient.generateBlueprint).mockResolvedValue(runningJob);
    vi.mocked(apiClient.getBlueprintGenerationJob).mockImplementation(
      () => new Promise(() => undefined));
    vi.mocked(apiClient.cancelBlueprintGeneration).mockResolvedValue(completedJob);
    const onChange = vi.fn();
    const { result } = renderHook(() => useBlueprintGeneration(onChange));

    act(() => {
      void result.current.generate('build a service');
    });
    await act(async () => {
      await Promise.resolve();
    });
    await act(async () => {
      await result.current.cancel();
    });

    expect(apiClient.getBlueprintGenerationResult).toHaveBeenCalledWith('job-1');
    expect(onChange).toHaveBeenCalledWith({
      kind: 'generated',
      blueprint,
      generatedWorkflowYaml: undefined,
    });
    expect(result.current.error).toBeNull();
  });
});
