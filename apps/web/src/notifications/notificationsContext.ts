import { createContext, useContext } from 'react';
import type { NotificationDto } from '../api/types';

export interface NotificationsContextValue {
  /** All currently pending notifications for the signed-in user (server truth, not filtered by read state). */
  notifications: NotificationDto[];
  /** Count of notifications the user hasn't yet acknowledged by opening the bell. */
  unreadCount: number;
  loading: boolean;
  muted: boolean;
  toggleMuted: () => void;
  /** Resets the unread badge to 0. Does not remove items from `notifications` — those only
   *  disappear once the underlying run is actually reviewed/actioned server-side. */
  markAllSeen: () => void;
  /** Removes a notification from the current notification list. */
  dismissNotification: (id: string) => void;
  refresh: () => void;
}

export const NotificationsContext = createContext<NotificationsContextValue | null>(null);

export function useNotifications(): NotificationsContextValue {
  const value = useContext(NotificationsContext);
  if (!value) {
    throw new Error('useNotifications must be used inside NotificationsProvider');
  }
  return value;
}
