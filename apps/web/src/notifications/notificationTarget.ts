import type { NotificationDto } from '../api/types';

export function notificationTargetPath(notification: NotificationDto): string | null {
  if (notification.type === 'human_review' || notification.type === 'tool_approval') {
    const projectId = notification.project_id?.trim();
    const runId = notification.run_id.trim();
    if (!projectId || !runId) return null;

    return `/projects/${encodeURIComponent(projectId)}/orchestrations/${encodeURIComponent(runId)}`;
  }

  return notification.cta_path?.trim() || null;
}

export function unavailableNotificationTargetMessage(notification: NotificationDto): string {
  if (notification.type === 'tool_approval')
    return 'This approval no longer has a run to review.';
  if (notification.type === 'human_review')
    return 'This review no longer has a run to open.';
  return 'This notification can no longer be opened.';
}
