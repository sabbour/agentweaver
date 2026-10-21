/**
 * Browser-local list of runs the signed-in user opened, used until the Gateway
 * exposes a run list (gap G2). Entries hold identifiers and the last state the
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

function storageKey(tenantId: string): string {
  return `${STORAGE_PREFIX}${tenantId}`;
}

function isRecentRun(value: unknown): value is RecentRun {
  if (!value || typeof value !== 'object') return false;
  const run = value as Record<string, unknown>;
  return typeof run.projectId === 'string' && run.projectId.length > 0
    && typeof run.runId === 'string' && run.runId.length > 0
    && typeof run.lastOpenedAt === 'string' && Number.isFinite(Date.parse(run.lastOpenedAt))
    && (run.executionState === null || typeof run.executionState === 'string');
}

function readAll(tenantId: string): RecentRun[] {
  try {
    const raw = localStorage.getItem(storageKey(tenantId));
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter(isRecentRun) : [];
  } catch {
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
  const previous = existing.find((item) => item.projectId === run.projectId && item.runId === run.runId);
  const next: RecentRun = {
    projectId: run.projectId,
    runId: run.runId,
    lastOpenedAt: now.toISOString(),
    executionState: run.executionState ?? previous?.executionState ?? null,
  };
  const rest = existing.filter((item) => item !== previous)
    .sort((left, right) => Date.parse(right.lastOpenedAt) - Date.parse(left.lastOpenedAt));
  try {
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
    for (let index = localStorage.length - 1; index >= 0; index -= 1) {
      const key = localStorage.key(index);
      if (key?.startsWith(STORAGE_PREFIX)) localStorage.removeItem(key);
    }
  } catch {
    // Ignore blocked storage.
  }
}
