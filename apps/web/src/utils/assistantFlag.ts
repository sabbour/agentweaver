// Shared feature-flag gate for the #346 MCP-driven operator assistant rollout,
// used by both the /assistant route and the Sessions list (#4/#5) so they stay
// gated together during the gradual rollout (see routes/AssistantRoute.tsx).
export const ASSISTANT_FLAG_KEY = 'agentweaver.assistant.enabled';

/** Read the persisted (localStorage) assistant flag, defaulting to disabled when
 *  localStorage is unavailable (privacy mode) or unset. */
export function isAssistantFlagEnabled(): boolean {
  try {
    return window.localStorage.getItem(ASSISTANT_FLAG_KEY) === '1';
  } catch {
    return false;
  }
}

/**
 * Resolve the assistant flag from a `?assistant=` query param, persisting any
 * explicit `1`/`0` value to localStorage so later navigations (e.g. clicking the
 * Sessions nav item, which carries no query param of its own) keep the choice.
 */
export function resolveAssistantFlag(queryFlag: string | null): boolean {
  try {
    if (queryFlag === '1') {
      window.localStorage.setItem(ASSISTANT_FLAG_KEY, '1');
      return true;
    }
    if (queryFlag === '0') {
      window.localStorage.removeItem(ASSISTANT_FLAG_KEY);
      return false;
    }
    return isAssistantFlagEnabled();
  } catch {
    return queryFlag === '1';
  }
}
