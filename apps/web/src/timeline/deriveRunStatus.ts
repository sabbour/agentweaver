import type { RunStreamEvent } from '../api/sse';

/**
 * Derive the current run status from the ordered list of SSE lifecycle events.
 * Scans events in arrival order; the last recognized lifecycle event wins.
 * Falls back to the isLiveStream hint when no lifecycle events have arrived yet.
 *
 * Correctness property: a revision cycle emits revision.started (=> in_progress)
 * followed later by a second review.requested (=> awaiting_review). Because we
 * return the status of the LAST matching event, each new gate correctly
 * overrides the previous one rather than being masked by history.
 */
export function deriveRunStatusFromEvents(
  events: RunStreamEvent[],
  isLiveStream: boolean,
): string {
  let status: string | null = null;

  for (const evt of events) {
    switch (evt.type) {
      case 'revision.started':
      case 'review.changes_requested':
        status = 'in_progress';
        break;
      case 'review.requested':
        status = 'awaiting_review';
        break;
      case 'review.approved':
        status = 'approved';
        break;
      case 'review.declined':
        status = 'declined';
        break;
      case 'merge.started':
        status = 'merging';
        break;
      case 'merge.completed':
        status = 'merged';
        break;
      case 'merge.failed':
        status = 'merge_failed';
        break;
      case 'run.completed':
        status = 'completed';
        break;
      case 'run.failed':
        status = 'failed';
        break;
    }
  }

  if (status !== null) return status;
  return isLiveStream ? 'in_progress' : 'completed';
}
