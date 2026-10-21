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
  // No pin to lose (nothing was pinned, no model chosen, or hosted Copilot needs no key).
  if (!savedSelection?.credentialReference || !selection?.reference || selection.sourceMode === 'hostedCopilot') return false;
  // Otherwise the draft needs a complete pin of its own: both id and version.
  return !selection.credentialReference?.id.trim() || !selection.credentialReference.version.trim();
}

