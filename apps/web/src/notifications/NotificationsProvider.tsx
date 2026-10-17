import { apiClient } from '../api/apiClient';
import {
  Link as FluentLink,
  Toast,
  ToastBody,
  ToastFooter,
  ToastTitle,
  Toaster,
  useId,
  useToastController,
} from '@fluentui/react-components';
import { NotificationsContext } from './notificationsContext';
import { armAudioUnlock, getNotificationsMuted, playNotificationChime, setNotificationsMuted } from './sound';
import { notificationTargetPath, unavailableNotificationTargetMessage } from './notificationTarget';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { NotificationDto } from '../api/types';
import type { ReactNode } from 'react';

/** Polls the signed-in user's notifications and coordinates unread counts, actionable toasts, and sound. */
// #247 — global notification center. DELIVERY CHOICE: polling GET /api/notifications rather than a
// new SSE stream (see apps/Agentweaver.Api/Notifications/NotificationsService.cs for the backend
// rationale). 20s balances "feels live" against load; the endpoint is a couple of indexed DB reads.
export const NOTIFICATIONS_POLL_INTERVAL_MS = 20_000;

// Per-type toast copy. The title + CTA label differ by notification kind so a "subtasks created"
// promotion notice doesn't read "Awaiting your review". Unknown/future types fall back to a
// generic prompt so the toast never renders blank (mirrors NotificationTypeBadge's fallback).
const TOAST_COPY: Record<string, { title: string; cta: string }> = {
  human_review: { title: 'Awaiting your review', cta: 'Review now' },
  tool_approval: { title: 'Approval needed', cta: 'Review now' },
  backlog_promoted: { title: 'Subtasks created', cta: 'View board' },
};
const FALLBACK_TOAST_COPY = { title: 'Action needed', cta: 'Open' };

/** Compares notification identity and its owning run so reused IDs cannot preserve stale approval toasts. */
function hasSameNotificationSource(current: NotificationDto, announced: NotificationDto): boolean {
  return current.id === announced.id
    && current.type === announced.type
    && current.project_id === announced.project_id
    && current.run_id === announced.run_id;
}

/** Snapshot accepted for one notification so older overlapping requests cannot replace newer evidence. */
interface AcceptedNotificationSource {
  requestVersion: number;
  source: NotificationDto | undefined;
}

/** Provider children and optional polling interval override for isolated tests. */
export interface NotificationsProviderProps {
  children: ReactNode;
  /** Test seam — override the poll interval so tests don't wait out the real 20s default. */
  pollIntervalMs?: number;
}

/**
 * Owns notification polling and toast delivery. Initial items update the badge without replaying
 * old notifications; tool approvals are revalidated before navigation.
 */
export function NotificationsProvider({ children, pollIntervalMs = NOTIFICATIONS_POLL_INTERVAL_MS }: NotificationsProviderProps) {
  const navigate = useNavigate();
  const toasterId = useId('notifications-toaster');
  const { dispatchToast, dismissToast } = useToastController(toasterId);

  const [notifications, setNotifications] = useState<NotificationDto[]>([]);
  const [unreadCount, setUnreadCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [muted, setMuted] = useState(() => getNotificationsMuted());

  // null until the first successful poll completes — distinguishes "nothing arrived yet" (initial
  // load, don't toast the whole backlog) from "this really is new since last poll" (toast it).
  const knownIdsRef = useRef<Set<string> | null>(null);
  const activeApprovalToastsRef = useRef<Map<string, NotificationDto>>(new Map());
  const timerRef = useRef<ReturnType<typeof setInterval> | undefined>(undefined);
  const notificationRequestVersionRef = useRef(0);
  const latestPollRequestVersionRef = useRef(0);
  const acceptedSourcesRef = useRef<Map<string, AcceptedNotificationSource>>(new Map());
  const pendingApprovalValidationsRef = useRef<Map<number, NotificationDto>>(new Map());

  // A poll may complete after a CTA validation; only a newer request can replace the source snapshot.
  const reconcileApprovalToasts = useCallback((items: NotificationDto[], requestVersion: number) => {
    const currentById = new Map(items.map((notification) => [notification.id, notification]));
    for (const [id, announced] of activeApprovalToastsRef.current) {
      const current = currentById.get(id);
      const accepted = acceptedSourcesRef.current.get(id);
      if (!accepted || requestVersion > accepted.requestVersion) {
        acceptedSourcesRef.current.set(id, { requestVersion, source: current });
      }
      if (!current || !hasSameNotificationSource(current, announced)) {
        dismissToast(`notif-${id}`);
        activeApprovalToastsRef.current.delete(id);
      }
    }
    for (const notification of pendingApprovalValidationsRef.current.values()) {
      const current = currentById.get(notification.id);
      const accepted = acceptedSourcesRef.current.get(notification.id);
      if (!accepted || requestVersion > accepted.requestVersion) {
        acceptedSourcesRef.current.set(notification.id, { requestVersion, source: current });
      }
    }
  }, [dismissToast]);

  // Keep the newest accepted source when concurrent polls and CTA checks finish out of order.
  const acceptValidationSource = useCallback((
    notification: NotificationDto,
    items: NotificationDto[],
    requestVersion: number,
  ): NotificationDto | undefined => {
    const current = items.find((item) => item.id === notification.id);
    const accepted = acceptedSourcesRef.current.get(notification.id);
    if (!accepted || requestVersion > accepted.requestVersion) {
      acceptedSourcesRef.current.set(notification.id, { requestVersion, source: current });
      return current;
    }
    return accepted.source;
  }, []);

  // Replace a stale actionable toast only when it still refers to the notification being checked.
  const showUnavailableTarget = useCallback((notification: NotificationDto, toastId: string) => {
    const active = activeApprovalToastsRef.current.get(notification.id);
    if (!active || hasSameNotificationSource(active, notification)) {
      dismissToast(toastId);
      activeApprovalToastsRef.current.delete(notification.id);
    }
    const copy = TOAST_COPY[notification.type] ?? FALLBACK_TOAST_COPY;
    dispatchToast(
      <Toast>
        <ToastTitle>{copy.title}</ToastTitle>
        <ToastBody subtitle={notification.project_name ?? undefined}>{notification.title}</ToastBody>
        <ToastFooter><span>{unavailableNotificationTargetMessage(notification)}</span></ToastFooter>
      </Toast>,
      {
        toastId: `${toastId}-unavailable`,
        intent: notification.type === 'tool_approval' ? 'warning' : 'info',
        timeout: 12000,
      },
    );
  }, [dismissToast, dispatchToast]);

  // Approval navigation is gated on a fresh server read; other notification links navigate directly.
  const handleCta = useCallback(async (notification: NotificationDto, toastId: string) => {
    if (notification.type !== 'tool_approval') {
      dismissToast(toastId);
      const targetPath = notificationTargetPath(notification);
      if (targetPath) navigate(targetPath);
      return;
    }

    const requestVersion = ++notificationRequestVersionRef.current;
    pendingApprovalValidationsRef.current.set(requestVersion, notification);
    try {
      // Confirm the source is still available immediately before navigating. Polling can otherwise
      // leave a short window where a permanent approval toast points at a deleted or inaccessible run.
      const response = await apiClient.getNotifications();
      const current = acceptValidationSource(notification, response.notifications, requestVersion);
      if (!current || !hasSameNotificationSource(current, notification)) {
        showUnavailableTarget(notification, toastId);
        return;
      }

      const targetPath = notificationTargetPath(current);
      if (!targetPath) {
        showUnavailableTarget(current, toastId);
        return;
      }

      dismissToast(toastId);
      activeApprovalToastsRef.current.delete(notification.id);
      navigate(targetPath);
    } catch {
      // Do not navigate without proof that this exact source is still present. Keep the CTA so
      // a transient request failure does not turn into a false "unavailable" result.
    } finally {
      pendingApprovalValidationsRef.current.delete(requestVersion);
    }
  }, [acceptValidationSource, dismissToast, navigate, showUnavailableTarget]);

  // Adds a toast for a newly arrived item and retains active approval records for later reconciliation.
  const announce = useCallback((notification: NotificationDto) => {
    const toastId = `notif-${notification.id}`;
    const copy = TOAST_COPY[notification.type] ?? FALLBACK_TOAST_COPY;
    const targetPath = notificationTargetPath(notification);
    dispatchToast(
      <Toast>
        <ToastTitle>{copy.title}</ToastTitle>
        <ToastBody subtitle={notification.project_name ?? undefined}>{notification.title}</ToastBody>
        <ToastFooter>
          {targetPath
            ? <FluentLink onClick={() => handleCta(notification, toastId)}>{copy.cta}</FluentLink>
            : <span>{unavailableNotificationTargetMessage(notification)}</span>}
        </ToastFooter>
      </Toast>,
      {
        toastId,
        intent: notification.type === 'tool_approval' ? 'warning' : 'info',
        // Approval requests are safety gates, not transient FYIs. Keep their toast visible until
        // the operator follows the CTA or dismisses it; the global bell remains the durable backup.
        timeout: notification.type === 'tool_approval' && targetPath ? -1 : 12000,
      },
    );
    if (notification.type === 'tool_approval') {
      activeApprovalToastsRef.current.set(notification.id, notification);
    }
    playNotificationChime();
  }, [dispatchToast, handleCta]);

  // Version each request so an older poll cannot overwrite newer state after overlapping responses.
  const poll = useCallback(async () => {
    const requestVersion = ++notificationRequestVersionRef.current;
    latestPollRequestVersionRef.current = requestVersion;
    try {
      const response = await apiClient.getNotifications();
      if (requestVersion !== latestPollRequestVersionRef.current) return;

      const items = response.notifications;
      setNotifications(items);
      setLoading(false);
      reconcileApprovalToasts(items, requestVersion);

      const currentIds = new Set(items.map((n) => n.id));
      const previousIds = knownIdsRef.current;

      if (previousIds === null) {
        // First successful load: surface the badge count for the existing backlog, but don't
        // spam a toast+chime per item — those are reserved for genuinely new arrivals.
        setUnreadCount(items.length);
      } else {
        const freshlyArrived = items.filter((n) => !previousIds.has(n.id));
        setUnreadCount((count) => Math.min(items.length, count + freshlyArrived.length));
        if (freshlyArrived.length > 0) {
          freshlyArrived.forEach(announce);
        }
      }
      knownIdsRef.current = currentIds;
    } catch {
      // Silent — same fallback posture as useAppVersion/StatusDot: a transient failure just means
      // the badge doesn't update this tick; it retries on the next interval, no state is lost.
      setLoading(false);
    }
  }, [announce, reconcileApprovalToasts]);

  useEffect(() => armAudioUnlock(), []);

  useEffect(() => {
    const startPolling = async () => {
      await poll();
    };
    void startPolling();
    timerRef.current = setInterval(() => void poll(), pollIntervalMs);
    return () => {
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [poll, pollIntervalMs]);

  // Keep the in-memory control in sync with the best-effort local preference.
  const toggleMuted = useCallback(() => {
    setMuted((current) => {
      const next = !current;
      setNotificationsMuted(next);
      return next;
    });
  }, []);

  // Clear the badge only; pending server items remain in the notification list.
  const markAllSeen = useCallback(() => setUnreadCount(0), []);
  // Remove from the current view immediately, then request the server-side dismissal.
  const dismissNotification = useCallback((id: string) => {
    dismissToast(`notif-${id}`);
    activeApprovalToastsRef.current.delete(id);
    setNotifications((current) => {
      const next = current.filter((notification) => notification.id !== id);
      setUnreadCount((count) => Math.min(count, next.length));
      return next;
    });
    void apiClient.dismissNotification(id);
  }, [dismissToast]);
  // Expose an immediate poll for callers that need to refresh outside the normal timer.
  const refresh = useCallback(() => { void poll(); }, [poll]);

  return (
    <NotificationsContext.Provider
      value={{ notifications, unreadCount, loading, muted, toggleMuted, markAllSeen, dismissNotification, refresh }}
    >
      <Toaster toasterId={toasterId} position="top-end" />
      {children}
    </NotificationsContext.Provider>
  );
}
