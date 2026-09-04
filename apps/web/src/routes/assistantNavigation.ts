export interface NewAssistantSessionState {
  assistantSessionKey: string;
}

export function createNewAssistantSessionState(): NewAssistantSessionState {
  return { assistantSessionKey: crypto.randomUUID() };
}

export function readAssistantSessionKey(state: unknown): string {
  if (!state || typeof state !== 'object') return '';
  const value = (state as { assistantSessionKey?: unknown }).assistantSessionKey;
  return typeof value === 'string' ? value : '';
}
