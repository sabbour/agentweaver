// Shell project-context persistence (overview-keeps-project).
//
// Global destinations (Overview "/overview", landing "/") carry no :projectId in
// the route. To avoid ejecting the user from the project they had loaded, the
// shell remembers the last project route they visited and falls back to it for
// the switcher display and the project-scoped nav targets on global pages.

const LAST_ACTIVE_KEY = 'agentweaver:last-active-project-id';

export function getLastActiveProjectId(): string | undefined {
  try {
    return localStorage.getItem(LAST_ACTIVE_KEY) ?? undefined;
  } catch {
    return undefined;
  }
}

export function setLastActiveProjectId(id: string): void {
  try {
    localStorage.setItem(LAST_ACTIVE_KEY, id);
  } catch {
    /* localStorage unavailable — fall back to in-memory state only */
  }
}

export function clearLastActiveProjectId(): void {
  try {
    localStorage.removeItem(LAST_ACTIVE_KEY);
  } catch {
    /* localStorage unavailable — nothing to clear */
  }
}
