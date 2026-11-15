import type { ModelSelectionSettings, ProjectConfiguration } from './contracts';

/**
 * True when a model other than hosted Copilot is selected, the saved selection
 * had a credential pin, and the current pin is incomplete. When a saved pin
 * exists, ConfigurationFields clears it for a different model and restores it
 * for the saved model.
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

