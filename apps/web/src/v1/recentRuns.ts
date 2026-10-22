/**
 * Browser-local list of runs the signed-in user opened. It exists because the
 * server has no run-list API yet. Entries hold identifiers and the last state the
 * run page saw, never tokens or run content. Storage is keyed by tenant so one
 * tenant's runs never appear in another, and sign-out removes every entry.
 */

/** One run the user opened in this browser. */
export interface RecentRun {
  projectId: string;
  runId: string;
  /** ISO time when the run page last loaded this run. */
  lastOpenedAt: string;
  /** Owner execution state from the last snapshot the run page read, if any. */
  executionState: string | null;
}

const STORAGE_PREFIX = 'agentweaver.v1.recentRuns:';
/** Upper bound per tenant so the list stays small and readable. */
export const RECENT_RUN_LIMIT = 25;

/** localStorage key for one tenant. A separate key per tenant keeps tenants apart. */
function storageKey(tenantId: string): string {
  return `${STORAGE_PREFIX}${tenantId}`;
}

/**
 * Checks one stored entry. localStorage can hold old, edited, or broken data,
 * so every field is validated before the entry is trusted.
 */
function isRecentRun(value: unknown): value is RecentRun {
  if (!value || typeof value !== 'object') return false;
  const run = value as Record<string, unknown>;
  // Ids must be non-empty, the time must parse as a date, and the state must be a string or null.
  return typeof run.projectId === 'string' && run.projectId.length > 0
    && typeof run.runId === 'string' && run.runId.length > 0
    && typeof run.lastOpenedAt === 'string' && Number.isFinite(Date.parse(run.lastOpenedAt))
    && (run.executionState === null || typeof run.executionState === 'string');
}

/** Reads the tenant's stored list in stored order. Returns an empty list on any problem. */
function readAll(tenantId: string): RecentRun[] {
  try {
    const raw = localStorage.getItem(storageKey(tenantId));
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    // Keep only valid entries; one bad entry should not hide the others.
    return Array.isArray(parsed) ? parsed.filter(isRecentRun) : [];
  } catch {
    // Blocked storage or invalid JSON: behave as if the list is empty.
    return [];
  }
}

/** Returns the tenant's recent runs, newest first, optionally limited to one project. */
export function readRecentRuns(tenantId: string, projectId?: string): RecentRun[] {
  const runs = readAll(tenantId)
    .sort((left, right) => Date.parse(right.lastOpenedAt) - Date.parse(left.lastOpenedAt));
  return projectId ? runs.filter((run) => run.projectId === projectId) : runs;
}

/**
 * Records that the user opened a run. Re-opening moves it to the top; a known
 * execution state replaces the stored one, and `null` keeps the previous value.
 */
export function rememberRun(
  tenantId: string,
  run: { projectId: string; runId: string; executionState?: string | null },
  now: Date = new Date(),
): void {
  const existing = readAll(tenantId);
  // A run is identified by project id plus run id.
  const previous = existing.find((item) => item.projectId === run.projectId && item.runId === run.runId);
  const next: RecentRun = {
    projectId: run.projectId,
    runId: run.runId,
    lastOpenedAt: now.toISOString(),
    // Prefer the new state. If it is missing, keep the last known state rather than erasing it.
    executionState: run.executionState ?? previous?.executionState ?? null,
  };
  // Remove the old entry for this run, then put the updated entry first.
  const rest = existing.filter((item) => item !== previous)
    .sort((left, right) => Date.parse(right.lastOpenedAt) - Date.parse(left.lastOpenedAt));
  try {
    // Trim to the limit so the oldest runs fall off the end.
    localStorage.setItem(storageKey(tenantId), JSON.stringify([next, ...rest].slice(0, RECENT_RUN_LIMIT)));
  } catch {
    // Storage can be full or blocked; the list is a convenience, so skip it.
  }
}

/** Removes one run from the tenant's list. */
export function forgetRun(tenantId: string, projectId: string, runId: string): void {
  const remaining = readAll(tenantId).filter((item) => item.projectId !== projectId || item.runId !== runId);
  try {
    localStorage.setItem(storageKey(tenantId), JSON.stringify(remaining));
  } catch {
    // Ignore blocked storage.
  }
}

/** Removes every tenant's recent-run list. Called on sign-out. */
export function clearRecentRuns(): void {
  try {
    // Walk backwards because removing a key shifts the indexes of the keys after it.
    for (let index = localStorage.length - 1; index >= 0; index -= 1) {
      const key = localStorage.key(index);
      // Only remove this feature's keys; other app data stays.
      if (key?.startsWith(STORAGE_PREFIX)) localStorage.removeItem(key);
    }
  } catch {
    // Ignore blocked storage.
  }
}
