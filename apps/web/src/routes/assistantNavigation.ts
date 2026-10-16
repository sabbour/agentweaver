/** Navigation-state helpers that let a newly opened assistant session remain distinct from route reuse. */
export interface NewAssistantSessionState {
  assistantSessionKey: string;
}

/** Creates a unique client-side session key for a new assistant navigation. */
export function createNewAssistantSessionState(): NewAssistantSessionState {
  return { assistantSessionKey: crypto.randomUUID() };
}

/** Reads a valid session key from router state without trusting an unknown state shape. */
export function readAssistantSessionKey(state: unknown): string {
  if (!state || typeof state !== 'object') return '';
  const value = (state as { assistantSessionKey?: unknown }).assistantSessionKey;
  return typeof value === 'string' ? value : '';
}
