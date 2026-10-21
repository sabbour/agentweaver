/**
 * Reads the signed-in user's notifications across all projects for the
 * notification bell and the Overview attention list. The notifications API
 * accepts only an unbound token: a token bound to a project or run is rejected.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { CoordinationBlockerKind, UserNotification } from './contracts';

/** Fields every attention item has, whatever produced it. */
interface AttentionItemBase {
  notificationId: string;
  projectId: string;
  runId: string;
  sessionId: string;
  createdAt: string;
}

/** A pending question or approval that the shared decision view answers. */
export interface PendingGateItem extends AttentionItemBase {
  source: 'pendingGate';
  requestId: string;
  kind: CoordinationBlockerKind;
  prompt: string | null;
  stateVersion?: number;
}

/** Subtasks a run added to the project backlog. They open the project Board. */
export interface BacklogPromotedItem extends AttentionItemBase {
  source: 'backlogPromoted';
  promotionId: string;
  taskCount: number;
}

/** One item in the bell and attention list. Only `pendingGate` items carry gate fields. */
export type AttentionItem = PendingGateItem | BacklogPromotedItem;

/** Result of reading the user's notifications. */
export interface RunAttention {
  /** Newest first, at most one page, without items dismissed in this tab. */
  items: AttentionItem[];
  /** Total pending notifications on the service, for the badge. 0 on error. */
  count: number;
  loading: boolean;
  error: unknown;
  /** Reads the list again right away. */
  reload: () => void;
  /** Hides one notification for this user. The list is read again afterwards. */
  dismiss: (notificationId: string) => Promise<void>;
  /** Error from the last failed dismiss, or null. */
  dismissError: unknown;
}

/** How often the list is read again while the tab is visible. */
export const ATTENTION_POLL_MS = 30_000;

/** Items read per request. The badge still shows the full `count`. */
export const ATTENTION_PAGE_SIZE = 50;

/**
 * Maps a caller notification to an attention item. Gate fields are read only when
 * `source === 'pendingGate'`. Returns null for an unknown source or an item that
 * lacks the fields its source requires, so the list never shows a broken entry.
 */
export function fromUserNotification(notification: UserNotification): AttentionItem | null {
  const { notificationId, projectId, runId, sessionId, createdAt } = notification;
  const base = { notificationId, projectId, runId, sessionId, createdAt };
  if (notification.source === 'pendingGate') {
    if (notification.requestId === null || notification.kind === null) return null;
    return { ...base, source: 'pendingGate', requestId: notification.requestId, kind: notification.kind,
      prompt: notification.prompt, stateVersion: notification.stateVersion ?? undefined };
  }
  if (notification.source === 'backlogPromoted') {
    const { promotionId, taskCount } = notification;
    // A promotion without an ID or with no tasks has nothing to show.
    if (promotionId === null || taskCount === null || !Number.isInteger(taskCount) || taskCount < 1) return null;
    return { ...base, source: 'backlogPromoted', promotionId, taskCount };
  }
  return null;
}

/** Text for a backlog promotion: "1 subtask created" or "{n} subtasks created". */
export function subtasksCreatedLabel(taskCount: number): string {
  return `${taskCount} subtask${taskCount === 1 ? '' : 's'} created`;
}

/** Stable list key. The notification ID is unique per user. */
export function attentionKey(item: AttentionItem): string {
  return item.notificationId;
}

/** Path for one item: a gate opens the run's Approvals view; a promotion opens the project Board. */
export function attentionPath(item: AttentionItem): string {
  const project = `/projects/${encodeURIComponent(item.projectId)}`;
  if (item.source === 'backlogPromoted') return `${project}/board`;
  const query = new URLSearchParams({ view: 'approvals' });
  // Carry the exact gate version the user opened; another version requires a fresh review.
  if (item.stateVersion !== undefined) {
    query.set('requestId', item.requestId);
    query.set('stateVersion', String(item.stateVersion));
  }
  return `${project}/runs/${encodeURIComponent(item.runId)}?${query}`;
}

/** Accepts only an exact, safely representable gate version from the user-editable URL. */
export function pinnedDecisionFromSearch(params: URLSearchParams): { requestId: string; stateVersion: number } | null {
  const requestId = params.get('requestId');
  const raw = params.get('stateVersion');
  if (!requestId || raw === null || !/^\d+$/.test(raw)) return null;
  const stateVersion = Number(raw);
  return Number.isSafeInteger(stateVersion) ? { requestId, stateVersion } : null;
}

/** Saved result of the last read. */
interface ReadState {
  items: AttentionItem[];
  count: number;
  error: unknown;
  loaded: boolean;
}

const EMPTY_STATE: ReadState = { items: [], count: 0, error: null, loaded: false };

/**
 * Polls the user's notifications every 30 seconds while signed in and while the
 * tab is visible, and dismisses single items.
 */
export function useRunAttention({ projectId, includeDismissed = false }: { projectId?: string; includeDismissed?: boolean } = {}): RunAttention {
  const { session, apiCall } = useAuth();
  const signedIn = Boolean(session);
  const [state, setState] = useState<ReadState>(EMPTY_STATE);
  // IDs dismissed in this tab. They stay hidden even if a read that started
  // before the dismiss still returns them. Dismissal is permanent for the user.
  const [hidden, setHidden] = useState<ReadonlySet<string>>(() => new Set());
  const [dismissError, setDismissError] = useState<unknown>(null);
  // Bumped by `reload` to restart the poll right away.
  const [generation, setGeneration] = useState(0);
  // Signed-in state readable from async code, so a result that lands after
  // sign-out is dropped.
  const signedInRef = useRef(signedIn);
  useEffect(() => { signedInRef.current = signedIn; }, [signedIn]);

  useEffect(() => {
    // Without a session there is nothing to read. Clear on the next tick so the
    // state update stays out of the effect body.
    if (!signedIn) {
      const timer = window.setTimeout(() => {
        setState(EMPTY_STATE);
        setHidden(new Set());
        setDismissError(null);
      }, 0);
      return () => window.clearTimeout(timer);
    }
    // Cleared on unmount, reload, or sign-out so a pending read does not save its
    // result or schedule another read.
    let active = true;
    let timer = 0;
    let reading = false;
    const schedule = (delay: number) => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => { void read(); }, delay);
    };
    const read = async () => {
      // A hidden tab does not poll. The visibility listener reads again when the
      // tab is shown.
      if (!active || document.visibilityState === 'hidden') return;
      reading = true;
      try {
        // The binding is null so the call uses the unbound token. A token bound
        // to a project or run is rejected by the notifications API.
        const page = await apiCall(
          (token, tenant) => gatewayClient.listNotifications(token, {
            pageSize: ATTENTION_PAGE_SIZE, ...(projectId ? { projectId } : {}),
            ...(includeDismissed ? { includeDismissed: true } : {}),
          }, tenant),
          null,
        );
        if (active && signedInRef.current) {
          const items = page.items.map(fromUserNotification).filter((item): item is AttentionItem => item !== null);
          setState({ items, count: page.count, error: null, loaded: true });
        }
      } catch (reason) {
        // Drop the old list and count so the badge does not show stale numbers.
        if (active && signedInRef.current) setState({ items: [], count: 0, error: reason, loaded: true });
      }
      reading = false;
      // Schedule the next read only after this one finishes, so reads never overlap.
      if (active) schedule(ATTENTION_POLL_MS);
    };
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible' && !reading) schedule(0);
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    // First read on the next tick, outside the effect body.
    schedule(0);
    return () => {
      active = false;
      window.clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [apiCall, signedIn, generation, projectId, includeDismissed]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);

  const dismiss = useCallback(async (notificationId: string) => {
    // Hide the item at once; restore it if the request fails.
    setHidden((current) => new Set(current).add(notificationId));
    try {
      // Unbound token, as for the list. Repeating the call is safe.
      await apiCall((token, tenant) => gatewayClient.dismissNotification(token, notificationId, tenant), null);
      setDismissError(null);
    } catch (reason) {
      setHidden((current) => {
        const next = new Set(current);
        next.delete(notificationId);
        return next;
      });
      setDismissError(reason);
    }
    // Read the list again so the count matches the service.
    reload();
  }, [apiCall, reload]);

  // A dismissed notification is still a pending gate in Flow until its owner resolves it.
  const items = state.items.filter((item) => includeDismissed || !hidden.has(item.notificationId));
  return {
    items,
    // Subtract items hidden in this tab that the last read still counted.
    count: Math.max(0, state.count - (state.items.length - items.length)),
    loading: signedIn && !state.loaded,
    error: state.error,
    reload,
    dismiss,
    dismissError,
  };
}
