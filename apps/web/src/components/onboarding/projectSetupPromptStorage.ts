const PROMPT_VERSION = 'v1';

export interface ProjectSetupPromptState {
  projectId: string;
  userKey: string;
  origin: string;
  sourceRepository: string | null;
  repositoryAccessRequired: boolean;
  repositoryAccessStatus: string;
  repositoryAccessReasonCode: string;
}

function normalizeKeyPart(value: string): string {
  return encodeURIComponent(value.trim().toLowerCase() || 'anonymous');
}

/** Builds the project-specific browser-storage key used to remember setup-prompt dismissal. */
export function projectSetupPromptStorageKey(
  state: Pick<ProjectSetupPromptState, 'projectId' | 'userKey'>,
): string {
  return `agentweaver.projectSetupPrompt.${PROMPT_VERSION}.${normalizeKeyPart(state.userKey)}.${normalizeKeyPart(state.projectId)}`;
}

/** Creates the setup-state fingerprint used to expire dismissal after requirements change. */
export function projectSetupPromptFingerprint(state: ProjectSetupPromptState): string {
  return JSON.stringify([
    state.origin,
    state.sourceRepository,
    state.repositoryAccessRequired,
    state.repositoryAccessStatus,
    state.repositoryAccessReasonCode,
  ]);
}

/** Checks whether a dismissal matches the current project setup fingerprint. */
export function hasDismissedProjectSetupPrompt(
  storageKey: string,
  fingerprint: string,
): boolean {
  try {
    return localStorage.getItem(storageKey) === fingerprint;
  } catch {
    return false;
  }
}

/** Persists a dismissal with the current setup fingerprint when local storage is available. */
export function markProjectSetupPromptDismissed(
  storageKey: string,
  fingerprint: string,
): void {
  try {
    localStorage.setItem(storageKey, fingerprint);
  } catch {
    // The prompt remains dismissible for the current page when storage is unavailable.
  }
}
