import type { RunStreamEvent } from '../api/sse';
import type { PortForwardSessionDto } from '../api/types';
import { readStr } from '../utils/readStr';

export type RunPreviewState =
  | { status: 'none' }
  | { status: 'ready'; previewUrl: string; targetPort?: string; eventSequence: number }
  | { status: 'pending'; targetPort?: string }
  | {
    status: 'failed';
    reason: string;
    message?: string;
    retryAvailable: boolean;
    approvalRequestId?: string;
  };

export function latestPreviewStateFromEvents(
  events: RunStreamEvent[],
  sessions: PortForwardSessionDto[],
): RunPreviewState {
  const latestAssembly = [...events].reverse().find((evt) =>
    evt.type === 'coordinator.assembly_started'
    || evt.type === 'coordinator.assembly_review_requested',
  );
  const currentTree = latestAssembly?.type === 'coordinator.assembly_review_requested'
    ? readStr(latestAssembly.payload, ['treeHash', 'tree_hash'])
    : null;
  for (let i = events.length - 1; i >= 0; i -= 1) {
    const evt = events[i];
    if (evt.type === 'sandbox.preview_ready' || evt.type === 'coordinator.preview_ready') {
      const preview = evt.payload['preview_url'] ?? evt.payload['previewUrl'];
      const tree = readStr(evt.payload, ['tree_hash', 'treeHash']);
      const token = readStr(evt.payload, ['session_id', 'sessionId']);
      const pod = readStr(evt.payload, ['pod_name', 'podName']);
      const port = evt.payload['target_port'] ?? evt.payload['targetPort'];
      if (preview != null && String(preview).trim() !== ''
        && currentTree && tree === currentTree
        && sessions.some((session) => session.session_id === token
          && session.pod_name === pod && session.target_port === Number(port)
          && (session.preview_url ?? session.previewUrl) === String(preview))) {
        return {
          status: 'ready',
          previewUrl: String(preview),
          targetPort: port == null ? undefined : String(port),
          eventSequence: evt.sequence,
        };
      }
      continue;
    }
    if (evt.type === 'sandbox.preview_pending') {
      if (!currentTree || readStr(evt.payload, ['tree_hash', 'treeHash']) !== currentTree) continue;
      const targetPort = evt.payload['target_port'] ?? evt.payload['targetPort'];
      return {
        status: 'pending',
        targetPort: targetPort == null ? undefined : String(targetPort),
      };
    }
    if (evt.type === 'sandbox.preview_failed') {
      if (!currentTree || readStr(evt.payload, ['tree_hash', 'treeHash']) !== currentTree) continue;
      return {
        status: 'failed',
        reason: readStr(evt.payload, ['reason']) ?? 'unknown',
        message: readStr(evt.payload, ['message']),
        retryAvailable: evt.payload['retry_available'] === true,
        approvalRequestId: readStr(evt.payload, ['approval_request_id', 'approvalRequestId']),
      };
    }
  }
  return { status: 'none' };
}
