/**
 * Maps owner execution states to the four work-state words used by Flow,
 * Orchestrations, Overview, and the run page: Needs input, Open, Working, and
 * Done. Failure and missing data stay separate so they are never shown as done.
 */

/** User-facing run state. */
export type RunStateLabel = 'Needs input' | 'Open' | 'Working' | 'Done' | 'Failed' | 'Cancelled' | 'Outcome unknown' | 'Unavailable';

/** Badge colors used with each state label. */
export type RunStateColor = 'warning' | 'informative' | 'brand' | 'success' | 'danger' | 'severe' | 'subtle';

/**
 * Converts an Orchestrator `executionState` (`idle`, `active`, `blocked`,
 * `completed`, `failed`, `cancelled`, `indeterminate`) to a label and color. Any other or
 * missing value is `Unavailable`.
 */
export function runState(executionState: string | null | undefined): { label: RunStateLabel; color: RunStateColor } {
  switch (executionState) {
    case 'blocked': return { label: 'Needs input', color: 'warning' };
    case 'idle': return { label: 'Open', color: 'informative' };
    case 'active': return { label: 'Working', color: 'brand' };
    case 'completed': return { label: 'Done', color: 'success' };
    case 'failed': return { label: 'Failed', color: 'danger' };
    case 'cancelled': return { label: 'Cancelled', color: 'subtle' };
    case 'indeterminate': return { label: 'Outcome unknown', color: 'severe' };
    default: return { label: 'Unavailable', color: 'subtle' };
  }
}

/** True when the owner reports the run finished, successfully or not. */
export function isRunFinished(executionState: string | null | undefined): boolean {
  return executionState === 'completed' || executionState === 'failed' || executionState === 'cancelled' ||
    executionState === 'indeterminate';
}
