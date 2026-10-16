const RUN_TERMINAL_STATUS_VALUES = [
  'completed',
  'merged',
  'assemble_ready',
  'declined',
  'failed',
  'merge_failed',
] as const;

export const RUN_TERMINAL_STATUSES: ReadonlySet<string> = new Set(RUN_TERMINAL_STATUS_VALUES);

export function normalizeRunStatus(status: string | null | undefined): string {
  return status?.toLowerCase().replace(/[^a-z_]/g, '') ?? '';
}

export function isTerminalRunStatus(status: string | null | undefined): boolean {
  return RUN_TERMINAL_STATUSES.has(normalizeRunStatus(status));
}
