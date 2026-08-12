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
  const timerRef = useRef<ReturnType<typeof setInterval> | undefined>(undefined);

  const handleCta = useCallback((notification: NotificationDto, toastId: string) => {
    dismissToast(toastId);
    navigate(notification.cta_path);
  }, [dismissToast, navigate]);

  const announce = useCallback((notification: NotificationDto) => {
    const toastId = `notif-${notification.id}`;
    const copy = TOAST_COPY[notification.type] ?? FALLBACK_TOAST_COPY;
    dispatchToast(
      <Toast>
        <ToastTitle>{copy.title}</ToastTitle>
        <ToastBody subtitle={notification.project_name ?? undefined}>{notification.title}</ToastBody>
        <ToastFooter>
          <FluentLink onClick={() => handleCta(notification, toastId)}>{copy.cta}</FluentLink>
        </ToastFooter>
      </Toast>,
      {
        toastId,
        intent: notification.type === 'tool_approval' ? 'warning' : 'info',
        // Approval requests are safety gates, not transient FYIs. Keep their toast visible until
        // the operator follows the CTA or dismisses it; the global bell remains the durable backup.
        timeout: notification.type === 'tool_approval' ? -1 : 12000,
      },
    );
    playNotificationChime();
  }, [dispatchToast, handleCta]);

  const poll = useCallback(async () => {
    try {
      const response = await apiClient.getNotifications();
      const items = response.notifications;
      setNotifications(items);
      setLoading(false);

      const currentIds = new Set(items.map((n) => n.id));
      const previousIds = knownIdsRef.current;

      if (previousIds === null) {
        // First successful load: surface the badge count for the existing backlog, but don't
        // spam a toast+chime per item — those are reserved for genuinely new arrivals.
        setUnreadCount(items.length);
      } else {
        const freshlyArrived = items.filter((n) => !previousIds.has(n.id));
        if (freshlyArrived.length > 0) {
          setUnreadCount((count) => count + freshlyArrived.length);
          freshlyArrived.forEach(announce);
        }
      }
      knownIdsRef.current = currentIds;
    } catch {
      // Silent — same fallback posture as useAppVersion/StatusDot: a transient failure just means
      // the badge doesn't update this tick; it retries on the next interval, no state is lost.
      setLoading(false);
    }
  }, [announce]);

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
    setNotifications((current) => {
      const next = current.filter((notification) => notification.id !== id);
      setUnreadCount((count) => Math.min(count, next.length));
      return next;
    });
    void apiClient.dismissNotification(id);
  }, []);
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
