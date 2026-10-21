/** Flow page grouping rules, kept apart from the component for reuse and tests. */

/** Pull request state for runs. No source reports it, so it is never Open or Merged. */
export const PULL_REQUEST_NOTE = 'Pull request: Unavailable';

/** Flow group for one run state. */
export type FlowGroup = 'open' | 'working' | 'done';

/** Maps a run state to its Flow group, or null for a state this app does not know. */
export function flowGroup(state: string | null): FlowGroup | null {
  switch (state) {
    case 'idle': return 'open';
    case 'active':
    case 'blocked': return 'working';
    case 'completed':
    case 'failed':
    case 'cancelled':
    case 'indeterminate': return 'done';
    default: return null;
  }
}
