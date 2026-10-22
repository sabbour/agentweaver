/** Flow page grouping rules, kept apart from the component for reuse and tests. */

/** Stored project records do not establish a run's current provider-side PR state. */
export const PULL_REQUEST_NOTE = 'Pull request: Unavailable';

/** Flow group for one run state. */
export type FlowGroup = 'open' | 'working' | 'done';

/** Display order for the run groups; each row retains its exact terminal state. */
export const FLOW_GROUPS: ReadonlyArray<{ key: FlowGroup; title: string }> = [
  { key: 'open', title: 'Open' }, { key: 'working', title: 'Working' }, { key: 'done', title: 'Done' },
];

/** Maps a run state to its Flow group, or null for a state this app does not know. */
export function flowGroup(state: string | null): FlowGroup | null {
  switch (state) {
    case 'idle': return 'open';
    // A blocked run is still in progress, so it is grouped with Working.
    case 'active':
    case 'blocked': return 'working';
    // Every finished state goes to Done, including failure and unknown outcome.
    // The row's own state badge still shows which one it was.
    case 'completed':
    case 'failed':
    case 'cancelled':
    case 'indeterminate': return 'done';
    default: return null;
  }
}
