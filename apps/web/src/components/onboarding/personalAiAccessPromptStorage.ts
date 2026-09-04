const PROMPT_VERSION = 'v1';

export function personalAiAccessPromptStorageKey(userKey: string): string {
  const normalized = userKey.trim().toLowerCase();
  return `agentweaver.personalAiAccessPrompt.${PROMPT_VERSION}.${encodeURIComponent(normalized)}`;
}

export function hasDismissedPersonalAiAccessPrompt(storageKey: string): boolean {
  try {
    return localStorage.getItem(storageKey) === 'dismissed';
  } catch {
    return false;
  }
}

export function markPersonalAiAccessPromptDismissed(storageKey: string): void {
  try {
    localStorage.setItem(storageKey, 'dismissed');
  } catch {
    // The prompt remains dismissible for the current page when storage is unavailable.
  }
}
