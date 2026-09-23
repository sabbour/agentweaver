import { apiClient } from '../api/apiClient';
import { useEffect, useRef, useState } from 'react';
import type { Blueprint, BlueprintGenerationJob } from '../api/types';
import { useAiExecutionContext } from '../hooks/useAiExecutionContext';

export type BlueprintSelection =
  | { kind: 'none' }
  | { kind: 'predefined'; blueprint: Blueprint }
  | { kind: 'generated'; blueprint: Blueprint; generatedWorkflowYaml?: string | null };

export const NO_BLUEPRINT: BlueprintSelection = { kind: 'none' };

export type BlueprintPanelTab = 'suggested' | 'templates' | 'generate';

export function useBlueprintGeneration(onChange: (selection: BlueprintSelection) => void, targetRepository?: string | null) {
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [generated, setGenerated] = useState<{ blueprint: Blueprint; generatedWorkflowYaml?: string | null } | null>(null);
  const [job, setJob] = useState<BlueprintGenerationJob | null>(null);
  const sequence = useRef(0);
  const providerContext = useAiExecutionContext('blueprint_generation');

  useEffect(() => () => {
    sequence.current += 1;
  }, []);

  const waitForResult = async (accepted: BlueprintGenerationJob, requestSequence: number) => {
    let current = accepted;
    while (current.status === 'queued' || current.status === 'running') {
      await new Promise(resolve => window.setTimeout(resolve, 500));
      if (requestSequence !== sequence.current) return;
      current = await apiClient.getBlueprintGenerationJob(current.job_id);
      if (requestSequence !== sequence.current) return;
      setJob(current);
    }
    if (current.status === 'failed')
      throw new Error(current.failure?.message ?? 'Blueprint generation failed.');
    if (current.status === 'cancelled')
      throw new Error('Blueprint generation was cancelled.');
    const res = await apiClient.getBlueprintGenerationResult(current.job_id);
    if (requestSequence !== sequence.current) return;
    providerContext.setPhase('completed');
    const next = { blueprint: res.blueprint, generatedWorkflowYaml: res.generated_workflow_yaml };
    setGenerated(next);
    onChange({ kind: 'generated', blueprint: next.blueprint, generatedWorkflowYaml: next.generatedWorkflowYaml });
  };

  const generate = async (description: string) => {
    if (!description.trim()) return;
    const requestSequence = ++sequence.current;
    setGenerating(true);
    setError(null);
    setJob(null);
    providerContext.setPhase('active');
    try {
      const accepted = targetRepository
        ? await apiClient.generateBlueprint(
            description.trim(),
            targetRepository,
            providerContext.providerKey)
        : await apiClient.generateBlueprint(
            description.trim(),
            undefined,
            providerContext.providerKey);
      if (requestSequence !== sequence.current) return;
      setJob(accepted);
      providerContext.applyCompletedContext(accepted.ai_execution_context);
      await waitForResult(accepted, requestSequence);
    } catch (err) {
      if (requestSequence !== sequence.current) return;
      const providerChanged = providerContext.handleInvocationError(err);
      if (!providerChanged) providerContext.restorePreparedContext();
      setError(providerChanged
        ? 'The AI provider changed. Review the updated provider and generate again.'
        : err instanceof Error ? err.message : String(err));
    } finally {
      if (requestSequence === sequence.current) setGenerating(false);
    }
  };

  const cancel = async () => {
    if (!job || (job.status !== 'queued' && job.status !== 'running')) return;
    const requestSequence = ++sequence.current;
    try {
      const cancelled = await apiClient.cancelBlueprintGeneration(job.job_id);
      if (requestSequence !== sequence.current) return;
      setJob(cancelled);
      if (cancelled.status === 'completed') {
        setError(null);
        await waitForResult(cancelled, requestSequence);
      } else if (cancelled.status === 'failed') {
        setError(cancelled.failure?.message ?? 'Blueprint generation failed.');
      } else if (cancelled.status === 'cancelled') {
        setError('Blueprint generation was cancelled.');
        providerContext.restorePreparedContext();
      } else {
        await waitForResult(cancelled, requestSequence);
      }
    } catch (err) {
      if (requestSequence === sequence.current)
        setError(err instanceof Error ? err.message : String(err));
    } finally {
      if (requestSequence === sequence.current) setGenerating(false);
    }
  };

  const retry = async () => {
    if (!job || (job.status !== 'failed' && job.status !== 'cancelled')) return;
    const requestSequence = ++sequence.current;
    setGenerating(true);
    setError(null);
    providerContext.setPhase('active');
    try {
      const retried = await apiClient.retryBlueprintGeneration(job.job_id);
      if (requestSequence !== sequence.current) return;
      setJob(retried);
      await waitForResult(retried, requestSequence);
    } catch (err) {
      if (requestSequence === sequence.current)
        setError(err instanceof Error ? err.message : String(err));
    } finally {
      if (requestSequence === sequence.current) setGenerating(false);
    }
  };

  return {
    generated,
    generating,
    error,
    job,
    generate,
    cancel,
    retry,
    setGenerated,
    providerContext,
  };
}

export function applyBlueprintToRequest<T extends {
  blueprint_id?: string;
  blueprint?: Blueprint;
  generated_workflow_yaml?: string | null;
}>(req: T, selection: BlueprintSelection): T {
  if (selection.kind === 'predefined') {
    req.blueprint_id = selection.blueprint.id;
  } else if (selection.kind === 'generated') {
    req.blueprint = selection.blueprint;
    req.generated_workflow_yaml = selection.generatedWorkflowYaml ?? null;
  }
  return req;
}
