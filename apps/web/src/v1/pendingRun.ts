/**
 * Helpers for starting a new chat. A new chat is created first, then needs a
 * sign-in for that exact run before it can start. That sign-in can leave the
 * page, so the created run is saved in session storage and resumed on return.
 * Storage holds only identifiers, never tokens or message text.
 */
import { GatewayError } from './api';

/** A created chat run that has not started yet. */
export interface PendingRun {
  projectId: string;
  runId: string;
  /** ISO time when the run was created. */
  createdAt: string;
}

/** Session-storage key for the one pending run of this tab. */
export const PENDING_RUN_STORAGE_KEY = 'agentweaver.v1.pendingRun';
/** A pending run older than this is dropped. The run itself still exists in Sessions. */
export const PENDING_RUN_MAX_AGE_MS = 24 * 60 * 60 * 1000;
/** How many times start is tried when the service reports a start timeout. */
export const START_ATTEMPTS = 3;

/** Plain copy for a create request denied for lack of the Orchestrator role. */
export const ORCHESTRATOR_ROLE_REQUIRED =
  'You need the Orchestrator role in this project to start a chat. Ask a project admin to assign it.';
/** Plain copy for a stop request denied for lack of a write role. */
export const STOP_ROLE_REQUIRED = 'You need the Contributor or Owner role to stop runs.';

/** Checks one stored value. Storage can be edited by hand, so trust nothing. */
function isPendingRun(value: unknown): value is PendingRun {
  if (!value || typeof value !== 'object') return false;
  const run = value as Record<string, unknown>;
  return typeof run.projectId === 'string' && run.projectId.length > 0
    && typeof run.runId === 'string' && run.runId.length > 0
    && typeof run.createdAt === 'string' && Number.isFinite(Date.parse(run.createdAt));
}

/** Saves the pending run before any step that can leave the page. */
export function savePendingRun(run: PendingRun): void {
  try {
    sessionStorage.setItem(PENDING_RUN_STORAGE_KEY, JSON.stringify(run));
  } catch {
    // Blocked storage only loses the resume after a full-page sign-in; the
    // in-page flow still works.
  }
}

/**
 * Reads the pending run. Bad, unreadable, or expired entries count as none;
 * bad and expired entries are also removed.
 */
export function readPendingRun(now: number = Date.now()): PendingRun | null {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(PENDING_RUN_STORAGE_KEY);
  } catch {
    return null;
  }
  if (!raw) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    parsed = null;
  }
  if (!isPendingRun(parsed) || now - Date.parse(parsed.createdAt) > PENDING_RUN_MAX_AGE_MS) {
    clearPendingRun();
    return null;
  }
  return { projectId: parsed.projectId, runId: parsed.runId, createdAt: parsed.createdAt };
}

/** Removes the pending run after it started or ended, or when the user dismisses it. */
export function clearPendingRun(): void {
  try {
    sessionStorage.removeItem(PENDING_RUN_STORAGE_KEY);
  } catch {
    // Nothing to clear when storage is blocked.
  }
}

/** Run page address for a chat; it opens on the Chat tab. */
export function runChatPath(projectId: string, runId: string): string {
  return `/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}?view=chat`;
}

/** True for a Gateway error with this status and, when given, this code. */
function isGatewayError(error: unknown, status: number, code?: string): error is GatewayError {
  return error instanceof GatewayError && error.status === status && (code === undefined || error.code === code);
}

/**
 * True when a create request must be sent again with a new key. A 409 means
 * the project settings changed or the key was already used for other content;
 * a retry with the same key would fail the same way.
 */
export function needsNewCreateKey(error: unknown): boolean {
  return isGatewayError(error, 409);
}

/** Maps a create failure to plain copy. Unknown failures keep their own message. */
export function createRunError(error: unknown): unknown {
  if (isGatewayError(error, 403))
    return new Error(ORCHESTRATOR_ROLE_REQUIRED);
  if (isGatewayError(error, 409))
    return new Error('The project settings changed. The latest settings are loaded. Send your message again.');
  return error;
}

/** Maps a stop failure to plain copy. Unknown failures keep their own message. */
export function cancelRunError(error: unknown): unknown {
  if (isGatewayError(error, 403)) return new Error(STOP_ROLE_REQUIRED);
  return error;
}

/** Waits `ms` milliseconds. Tests pass their own wait. */
function wait(ms: number): Promise<void> {
  return new Promise((resolve) => { window.setTimeout(resolve, ms); });
}

/** Steps the resume flow needs from the page, so the flow can run without React. */
export interface ResumeSteps {
  /** Gets a session for the run without a prompt. False means the user must sign in. */
  ensureRunSession: () => Promise<boolean>;
  /** Sends start with the run-bound session. */
  start: () => Promise<unknown>;
  /** Optional wait between start attempts. */
  sleep?: (ms: number) => Promise<void>;
}

/** Result of one resume attempt. */
export type ResumeOutcome = 'started' | 'ended' | 'needs-sign-in';

/**
 * Binds and starts a pending run. Start is safe to repeat: the service sends
 * the saved first message only once. Returns:
 * - `started`: the run started; the pending record is cleared.
 * - `ended`: the run already ended; the pending record is cleared.
 * - `needs-sign-in`: no session could be made without the user; the record stays.
 * Other failures are thrown and the record stays, so the user can try again.
 */
export async function resumePendingRun(steps: ResumeSteps): Promise<ResumeOutcome> {
  const sleep = steps.sleep ?? wait;
  // One silent attempt only. When it fails the caller shows a sign-in button;
  // it must not start more silent attempts on its own.
  if (!await steps.ensureRunSession()) return 'needs-sign-in';
  for (let attempt = 1; ; attempt += 1) {
    try {
      await steps.start();
      clearPendingRun();
      return 'started';
    } catch (error) {
      if (isGatewayError(error, 409, 'run_terminal')) {
        // The run was stopped before it started. Nothing is left to resume.
        clearPendingRun();
        return 'ended';
      }
      if (isGatewayError(error, 404)) {
        // The run is gone or belongs to someone else; keeping it would loop forever.
        clearPendingRun();
        throw new Error('This chat could not be found. Start a new chat.', { cause: error });
      }
      if (isGatewayError(error, 504, 'run_start_timeout')) {
        // Start timed out on the service. Retry the same run a few times with a
        // growing pause, then let the user retry.
        if (attempt < START_ATTEMPTS) {
          await sleep(1000 * attempt);
          continue;
        }
        throw new Error('The chat is saved but did not start in time. Try again.', { cause: error });
      }
      throw error;
    }
  }
}
