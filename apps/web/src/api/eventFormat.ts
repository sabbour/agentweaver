import type { RunEvent } from './types';

function field(payload: Record<string, unknown>, name: string): string {
  const value = payload[name];
  if (value === undefined || value === null) {
    return '';
  }
  return typeof value === 'string' ? value : String(value);
}

/** Builds a human-readable summary for an event's payload. */
export function summarizeEvent(evt: RunEvent): string {
  const p = evt.payload ?? {};
  switch (evt.type) {
    case 'run.started':
      return `Run started (${field(p, 'model_source')})`;
    case 'run.completed':
      return `Run complete after ${field(p, 'step_count')} steps`;
    case 'run.failed':
      return `Run failed: ${field(p, 'reason')}`;
    case 'run.bounded':
      return `Run bounded: ${field(p, 'limit_type')} limit reached after ${field(p, 'step_count')} steps`;
    case 'agent.message':
      return field(p, 'text');
    case 'tool.call': {
      const op = field(p, 'operation').toLowerCase();
      const verb = op === 'write' ? 'write_file' : 'read_file';
      return `${verb}: ${field(p, 'path')}`;
    }
    case 'tool.result':
      return `OK ${field(p, 'path')} (${field(p, 'bytes_read_or_written')} bytes)`;
    case 'tool.rejected':
      return `REJECTED ${field(p, 'path')}: ${field(p, 'reason')}`;
    case 'tool.error':
      return `ERROR ${field(p, 'path')}: ${field(p, 'error_message')}`;
    case 'review.requested':
      return `Awaiting review (tree: ${field(p, 'tree_hash')})`;
    case 'review.approved':
      return `Review approved by ${field(p, 'approved_by')}`;
    case 'review.declined':
      return `Review declined by ${field(p, 'declined_by')}`;
    case 'merge.completed':
      return `Merge completed: ${field(p, 'merged_commit_hash')}`;
    case 'merge.failed':
      return `Merge failed: ${field(p, 'reason')}`;
    default:
      return JSON.stringify(p);
  }
}

/** Formats a timestamp as a short relative description. */
export function relativeTime(timestamp: string): string {
  const then = new Date(timestamp).getTime();
  if (Number.isNaN(then)) {
    return timestamp;
  }
  const seconds = Math.round((Date.now() - then) / 1000);
  if (seconds < 5) {
    return 'just now';
  }
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.round(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  const days = Math.round(hours / 24);
  return `${days}d ago`;
}
