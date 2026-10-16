const PROMPT_VERSION = 'v1';

/** Builds the per-user local-storage key for the personal AI-access prompt. */
export function personalAiAccessPromptStorageKey(userKey: string): string {
  const normalized = userKey.trim().toLowerCase();
  return `agentweaver.personalAiAccessPrompt.${PROMPT_VERSION}.${encodeURIComponent(normalized)}`;
}

/** Checks whether the user dismissed the prompt; unavailable storage is treated as not dismissed. */
export function hasDismissedPersonalAiAccessPrompt(storageKey: string): boolean {
  try {
    return localStorage.getItem(storageKey) === 'dismissed';
  } catch {
    return false;
  }
}

/** Persists prompt dismissal when local storage is available; storage errors are ignored. */
export function markPersonalAiAccessPromptDismissed(storageKey: string): void {
  try {
    localStorage.setItem(storageKey, 'dismissed');
  } catch {
    // The prompt remains dismissible for the current page when storage is unavailable.
  }
}
