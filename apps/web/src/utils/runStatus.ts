const RUN_TERMINAL_STATUS_VALUES = [
  'completed',
  'merged',
  'assemble_ready',
  'declined',
  'failed',
  'merge_failed',
] as const;

/** Run statuses for which the live execution has reached a terminal state. */
export const RUN_TERMINAL_STATUSES: ReadonlySet<string> = new Set(RUN_TERMINAL_STATUS_VALUES);

/** Normalizes status text for comparisons without changing underscore separators. */
export function normalizeRunStatus(status: string | null | undefined): string {
  return status?.toLowerCase().replace(/[^a-z_]/g, '') ?? '';
}

/** True when the normalized status denotes a run that no longer executes. */
export function isTerminalRunStatus(status: string | null | undefined): boolean {
  return RUN_TERMINAL_STATUSES.has(normalizeRunStatus(status));
}
