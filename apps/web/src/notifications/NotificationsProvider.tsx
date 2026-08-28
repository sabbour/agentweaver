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

function hasSameNotificationSource(current: NotificationDto, announced: NotificationDto): boolean {
  return current.id === announced.id
    && current.type === announced.type
    && current.project_id === announced.project_id
    && current.run_id === announced.run_id;
}

export interface NotificationsProviderProps {
  children: ReactNode;
  /** Test seam — override the poll interval so tests don't wait out the real 20s default. */
  pollIntervalMs?: number;
}

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
  const notificationRequestGenerationRef = useRef(0);

  const reconcileApprovalToasts = useCallback((items: NotificationDto[]) => {
    const currentById = new Map(items.map((notification) => [notification.id, notification]));
    for (const [id, announced] of activeApprovalToastsRef.current) {
      const current = currentById.get(id);
      if (!current || !hasSameNotificationSource(current, announced)) {
        dismissToast(`notif-${id}`);
        activeApprovalToastsRef.current.delete(id);
      }
    }
  }, [dismissToast]);

  const showUnavailableTarget = useCallback((notification: NotificationDto, toastId: string) => {
    dismissToast(toastId);
    activeApprovalToastsRef.current.delete(notification.id);
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

  const handleCta = useCallback(async (notification: NotificationDto, toastId: string) => {
    if (notification.type !== 'tool_approval') {
      dismissToast(toastId);
      const targetPath = notificationTargetPath(notification);
      if (targetPath) navigate(targetPath);
      return;
    }

    let current: NotificationDto | undefined;
    const requestGeneration = ++notificationRequestGenerationRef.current;
    try {
      // Confirm the source is still available immediately before navigating. Polling can otherwise
      // leave a short window where a permanent approval toast points at a deleted or inaccessible run.
      const response = await apiClient.getNotifications();
      if (requestGeneration !== notificationRequestGenerationRef.current) {
        showUnavailableTarget(notification, toastId);
        return;
      }

      const items = response.notifications;
      setNotifications(items);
      setLoading(false);
      setUnreadCount((count) => Math.min(count, items.length));
      knownIdsRef.current = new Set(items.map((item) => item.id));
      reconcileApprovalToasts(items);
      current = items.find((item) => item.id === notification.id);
    } catch {
      // Without a current server response, navigating could turn an expired approval into a 404.
    }

    if (!current || !hasSameNotificationSource(current, notification)) {
      showUnavailableTarget(current ?? notification, toastId);
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
  }, [dismissToast, navigate, reconcileApprovalToasts, showUnavailableTarget]);

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

  const poll = useCallback(async () => {
    const requestGeneration = ++notificationRequestGenerationRef.current;
    try {
      const response = await apiClient.getNotifications();
      if (requestGeneration !== notificationRequestGenerationRef.current) return;

      const items = response.notifications;
      setNotifications(items);
      setLoading(false);
      reconcileApprovalToasts(items);

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

  const toggleMuted = useCallback(() => {
    setMuted((current) => {
      const next = !current;
      setNotificationsMuted(next);
      return next;
    });
  }, []);

  const markAllSeen = useCallback(() => setUnreadCount(0), []);
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
