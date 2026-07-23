import { apiClient } from '../api/apiClient';
import { useState } from 'react';
import type { Blueprint } from '../api/types';

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

  const generate = async (description: string) => {
    if (!description.trim()) return;
    setGenerating(true);
    setError(null);
    try {
      const res = targetRepository
        ? await apiClient.generateBlueprint(description.trim(), targetRepository)
        : await apiClient.generateBlueprint(description.trim());
      const next = { blueprint: res.blueprint, generatedWorkflowYaml: res.generated_workflow_yaml };
      setGenerated(next);
      onChange({ kind: 'generated', blueprint: next.blueprint, generatedWorkflowYaml: next.generatedWorkflowYaml });
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setGenerating(false);
    }
  };

  return { generated, generating, error, generate, setGenerated };
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
