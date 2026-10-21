/**
 * Reads pending questions and approvals. `useRunAttention` reads the run the Broker
 * session is bound to; run reads need a token bound to that exact run.
 * `useNotificationAttention` reads the caller's notifications across runs with an
 * unbound token, for Overview and Flow.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { CoordinationBlockerKind, SessionStatusBlocker } from './contracts';
import { notificationsClient } from './notificationsClient';
import type { UserNotification, UserNotificationPage } from './notificationsClient';

/** One pending request in the bound run, or one caller notification. */
export interface AttentionItem {
  projectId: string;
  runId: string;
  sessionId: string;
  requestId: string;
  kind: CoordinationBlockerKind;
  prompt: string | null;
  /** Set for items read from `GET /api/v1/notifications`. */
  notificationId?: string;
  /** Decision-state version the notification was derived from; DecisionView rejects a different version. */
  stateVersion?: number;
}

/** Result of reading pending requests. */
export interface RunAttention {
  /** Project and run that were read, or null when the session is not bound to a run. */
  binding: { projectId: string; runId: string } | null;
  items: AttentionItem[];
  loading: boolean;
  error: unknown;
  reload: () => void;
  /** `notifications` reads across runs with an unbound token and needs no bound run. Defaults to `run`. */
  source?: 'run' | 'notifications';
  /** Dismisses a notification for the caller. The gate stays pending and still shows in Flow. */
  dismiss?: (item: AttentionItem) => Promise<void>;
}

/** How often the bound run is read again while the app is open. */
export const ATTENTION_POLL_MS = 30_000;

/** Flattens session blockers into attention items for one run. */
export function toAttentionItems(
  projectId: string,
  runId: string,
  sessions: { sessionId: string; blockers: SessionStatusBlocker[] }[],
): AttentionItem[] {
  // One session can have several blockers. Each blocker becomes its own item so
  // the user can answer them one by one. A missing prompt is stored as null.
  return sessions.flatMap(({ sessionId, blockers }) => blockers.map((blocker) => ({
    projectId,
    runId,
    sessionId,
    requestId: blocker.requestId,
    kind: blocker.kind,
    prompt: blocker.prompt ?? null,
  })));
}

/** Polls the bound run's session statuses and returns their pending requests. */
export function useRunAttention(): RunAttention {
  const { session, apiCall } = useAuth();
  const binding = session?.binding ?? null;
  const projectId = binding?.projectId ?? '';
  const runId = binding?.runId ?? '';
  // The scope string identifies "who is reading which run". If the token, project,
  // or run changes, the scope changes and older results no longer apply.
  const scope = JSON.stringify([session?.accessToken ?? null, projectId, runId]);
  const [state, setState] = useState<{ scope: string; items: AttentionItem[]; error: unknown; loaded: boolean }>(
    { scope: '', items: [], error: null, loaded: false },
  );
  // Bumping the generation re-runs the polling effect. reload() uses it.
  const [generation, setGeneration] = useState(0);
  // A ref holds the latest scope so an in-flight read can tell if it is stale.
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  useEffect(() => {
    // An unbound session cannot read run data, so there is nothing to poll.
    if (!projectId || !runId) return undefined;
    // `active` turns false on cleanup. Reads that finish later must not set state.
    let active = true;
    let timer = 0;
    // Every call passes the binding so the auth layer uses a token bound to this run.
    const runBinding = { projectId, runId };
    const read = async () => {
      try {
        // Step 1: read the run to find its root session.
        const run = await apiCall((token, tenant) => gatewayClient.getRunStatus(token, projectId, runId, tenant), runBinding);
        // Safety check: never show data from a run other than the one we asked for.
        if (run.projectId !== projectId || run.runId !== runId)
          throw new Error('The service returned a different run.');
        // Step 2: read the session tree under the root session.
        const tree = await apiCall((token, tenant) => gatewayClient.getSessionTree(
          token, projectId, runId, run.rootSessionId, tenant,
        ), runBinding);
        // Step 3: read every session's status in parallel. Blockers live on the status.
        const statuses = await Promise.all(tree.nodes.map((node) => apiCall((token, tenant) => gatewayClient.getSessionStatus(
          token, projectId, runId, node.identity.sessionId, tenant,
        ), runBinding)));
        // Drop any status that belongs to another project or run, for the same safety reason.
        const sessions = statuses
          .filter((status) => status.identity.projectId === projectId && status.identity.runId === runId)
          .map((status) => ({ sessionId: status.identity.sessionId, blockers: status.blockers }));
        // Only store the result if this effect is still mounted and the scope did not change.
        if (active && scopeRef.current === scope)
          setState({ scope, items: toAttentionItems(projectId, runId, sessions), error: null, loaded: true });
      } catch (reason) {
        // On failure, clear the items and keep the error so the UI can show it.
        if (active && scopeRef.current === scope)
          setState({ scope, items: [], error: reason, loaded: true });
      }
      // Schedule the next poll only after this read finishes, so reads never overlap.
      if (active) timer = window.setTimeout(() => { void read(); }, ATTENTION_POLL_MS);
    };
    // Start the first read on the next tick instead of inside the effect body.
    timer = window.setTimeout(() => { void read(); }, 0);
    return () => {
      // Stop polling and ignore any read that is still running.
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, runId, scope, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  // State from an older scope is hidden. The caller sees empty items and a loading
  // flag until the read for the new scope finishes.
  const current = state.scope === scope;
  return {
    binding: binding ? { projectId, runId } : null,
    items: current ? state.items : [],
    loading: Boolean(binding) && !(current && state.loaded),
    error: current ? state.error : null,
    reload,
  };
}

/**
 * Path that opens one item's decision on the run page. Notification items also carry
 * the request and state version, so the decision view can reject a changed gate.
 */
export function attentionPath(item: AttentionItem): string {
  const base = `/projects/${encodeURIComponent(item.projectId)}/runs/${encodeURIComponent(item.runId)}?view=approvals`;
  // Items read from the bound run have no state version. They open the plain approvals view.
  if (item.stateVersion === undefined) return base;
  const query = new URLSearchParams({ requestId: item.requestId, stateVersion: String(item.stateVersion) });
  return `${base}&${query.toString()}`;
}

/** Reads the request and state version pinned by {@link attentionPath}, or null when absent or malformed. */
export function pinnedDecisionFromSearch(params: URLSearchParams): { requestId: string; stateVersion: number } | null {
  const requestId = params.get('requestId');
  const raw = params.get('stateVersion');
  // The URL is user-editable. Accept only plain digits, so values like "1e3",
  // "-1", or " 2" are rejected instead of being coerced by Number().
  if (!requestId || raw === null || !/^\d+$/.test(raw)) return null;
  const stateVersion = Number(raw);
  // Very long digit strings lose precision as numbers, so treat them as invalid too.
  return Number.isSafeInteger(stateVersion) ? { requestId, stateVersion } : null;
}

/**
 * The canonical owner returns pending gates without a source field. Ignore unrelated
 * sources defensively if a mixed inbox payload reaches this UI adapter.
 */
export function isPendingGateNotification(item: UserNotification): item is UserNotification & {
  requestId: string;
  kind: CoordinationBlockerKind;
  stateVersion: number;
} {
  // `source` is not in the typed shape, so read it as unknown.
  const source: unknown = (item as { source?: unknown }).source;
  // A gate item needs a request id, a kind, and a state version. Without them the
  // decision view cannot open or verify the gate, so the item is skipped.
  return (source === undefined || source === 'pendingGate')
    && typeof item.requestId === 'string' && item.requestId.length > 0
    && item.kind !== null
    && typeof item.stateVersion === 'number';
}

/** Converts pending-gate notifications to attention items and drops every other source. */
export function notificationAttentionItems(page: UserNotificationPage): AttentionItem[] {
  return page.items.filter(isPendingGateNotification).map((item) => ({
    projectId: item.projectId,
    runId: item.runId,
    sessionId: item.sessionId,
    requestId: item.requestId,
    kind: item.kind,
    prompt: item.prompt,
    notificationId: item.notificationId,
    stateVersion: item.stateVersion,
  }));
}

/**
 * Polls the caller's notifications with the unbound session. Overview uses the default
 * (dismissed items hidden); Flow passes `projectId` and `includeDismissed: true`, so a
 * dismissed bell item still shows as Needs input while its gate is pending.
 */
export function useNotificationAttention(
  options: { projectId?: string; includeDismissed?: boolean } = {},
): RunAttention {
  const { session, apiCall } = useAuth();
  const { projectId, includeDismissed = false } = options;
  // The scope changes when the token or the filter options change. Results read
  // under an older scope are ignored, the same way as in useRunAttention.
  const scope = JSON.stringify([session?.accessToken ?? null, projectId ?? null, includeDismissed]);
  const [state, setState] = useState<{ scope: string; items: AttentionItem[]; error: unknown; loaded: boolean }>(
    { scope: '', items: [], error: null, loaded: false },
  );
  // Bumping the generation restarts polling; reload() and dismiss() use it.
  const [generation, setGeneration] = useState(0);
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  useEffect(() => {
    // `active` turns false on cleanup so late reads do not set state.
    let active = true;
    let timer = 0;
    const read = async () => {
      try {
        const items: AttentionItem[] = [];
        let cursor: string | null = null;
        // Read every page so the Needs input group is complete; the owner caps pages at 100.
        do {
          // `null` binding: notifications span many runs, so this call uses the unbound token.
          const page: UserNotificationPage = await apiCall((token, tenant) => notificationsClient.list(
            token, { projectId, includeDismissed, cursor, pageSize: 100 }, tenant,
          ), null);
          // Filter by project again on the client in case the service returns other projects.
          items.push(...notificationAttentionItems(page)
            .filter((item) => projectId === undefined || item.projectId === projectId));
          cursor = page.nextCursor;
          // Stop paging early if the effect was cleaned up mid-read.
        } while (cursor && active);
        if (active && scopeRef.current === scope) setState({ scope, items, error: null, loaded: true });
      } catch (reason) {
        // Any page failure drops the whole result; a partial list could hide pending gates.
        if (active && scopeRef.current === scope) setState({ scope, items: [], error: reason, loaded: true });
      }
      // Poll again only after this read finishes, so reads never overlap.
      if (active) timer = window.setTimeout(() => { void read(); }, ATTENTION_POLL_MS);
    };
    timer = window.setTimeout(() => { void read(); }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, includeDismissed, scope, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  // Dismiss hides one notification for this user, then reloads the list.
  // Errors are not caught here; the caller decides how to show them.
  const dismiss = useCallback(async (item: AttentionItem) => {
    // Only notification items can be dismissed. Run-bound items have no notification id.
    if (!item.notificationId) return;
    // Copy to a local const so the closure below keeps a narrowed string type.
    const notificationId = item.notificationId;
    await apiCall((token, tenant) => notificationsClient.dismiss(token, notificationId, tenant), null);
    setGeneration((value) => value + 1);
  }, [apiCall]);
  // Hide results from an older scope until the new read finishes.
  const current = state.scope === scope;
  return {
    binding: null,
    source: 'notifications',
    items: current ? state.items : [],
    loading: !(current && state.loaded),
    error: current ? state.error : null,
    reload,
    // When dismissed items are included (Flow), dismissing again makes no sense,
    // so the action is not offered.
    dismiss: includeDismissed ? undefined : dismiss,
  };
}
