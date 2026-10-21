import type { ModelSelectionSettings, ProjectConfiguration } from './contracts';

/**
 * True when the saved default model had a credential pin and the draft model needs a new one.
 * A credential pin belongs to one model, so a changed model must not reuse it.
 */
export function needsModelCredential(
  configuration: ProjectConfiguration,
  savedSelection: ModelSelectionSettings | null | undefined,
): boolean {
  const selection = configuration.modelSelection;
  if (!savedSelection?.credentialReference || !selection?.reference || selection.sourceMode === 'hostedCopilot') return false;
  return !selection.credentialReference?.id.trim() || !selection.credentialReference.version.trim();
}

