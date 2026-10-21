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
  // No pin is needed when nothing was pinned before, no model is chosen yet,
  // or the model is hosted Copilot, which uses the user's Copilot sign-in instead of a stored key.
  if (!savedSelection?.credentialReference || !selection?.reference || selection.sourceMode === 'hostedCopilot') return false;
  // A pin is complete only when both the secret id and its version are filled in.
  // Blank or whitespace values count as missing.
  return !selection.credentialReference?.id.trim() || !selection.credentialReference.version.trim();
}

