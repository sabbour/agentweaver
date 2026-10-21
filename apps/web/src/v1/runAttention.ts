/**
 * Reads the pending questions and approvals of the run the Broker session is
 * bound to. Run reads need a token bound to that exact run, so only the bound
 * run can be checked. Checking every run at once needs a notifications API
 * that does not exist yet.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { CoordinationBlockerKind, SessionStatusBlocker } from './contracts';

/** One pending request in the bound run. */
export interface AttentionItem {
  projectId: string;
  runId: string;
  sessionId: string;
  requestId: string;
  kind: CoordinationBlockerKind;
  prompt: string | null;
}

/** Result of reading the bound run's pending requests. */
export interface RunAttention {
  /** Project and run that were read, or null when the session is not bound to a run. */
  binding: { projectId: string; runId: string } | null;
  items: AttentionItem[];
  loading: boolean;
  error: unknown;
  reload: () => void;
}

/** How often the bound run is read again while the app is open. */
export const ATTENTION_POLL_MS = 30_000;

/** Flattens session blockers into attention items for one run. */
export function toAttentionItems(
  projectId: string,
  runId: string,
  sessions: { sessionId: string; blockers: SessionStatusBlocker[] }[],
): AttentionItem[] {
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
  // The scope names the token and run being read. A result is kept only if the scope
  // is unchanged when it arrives, so a late answer for an old token or run is dropped.
  const scope = JSON.stringify([session?.accessToken ?? null, projectId, runId]);
  const [state, setState] = useState<{ scope: string; items: AttentionItem[]; error: unknown; loaded: boolean }>(
    { scope: '', items: [], error: null, loaded: false },
  );
  // Bumped by `reload` to restart the poll right away.
  const [generation, setGeneration] = useState(0);
  // Latest scope, readable from async code that captured an older one.
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  useEffect(() => {
    // An unbound session cannot read any run, so there is nothing to poll.
    if (!projectId || !runId) return undefined;
    // Cleared on unmount or scope change so a pending read does not schedule another.
    let active = true;
    let timer = 0;
    // Every call passes this binding so it uses the token bound to this exact run.
    const runBinding = { projectId, runId };
    const read = async () => {
      try {
        // Read the run, then its session tree, then each session's status (where blockers live).
        const run = await apiCall((token, tenant) => gatewayClient.getRunStatus(token, projectId, runId, tenant), runBinding);
        if (run.projectId !== projectId || run.runId !== runId)
          throw new Error('The service returned a different run.');
        const tree = await apiCall((token, tenant) => gatewayClient.getSessionTree(
          token, projectId, runId, run.rootSessionId, tenant,
        ), runBinding);
        const statuses = await Promise.all(tree.nodes.map((node) => apiCall((token, tenant) => gatewayClient.getSessionStatus(
          token, projectId, runId, node.identity.sessionId, tenant,
        ), runBinding)));
        const sessions = statuses
          // Drop any status that does not belong to this run.
          .filter((status) => status.identity.projectId === projectId && status.identity.runId === runId)
          .map((status) => ({ sessionId: status.identity.sessionId, blockers: status.blockers }));
        if (active && scopeRef.current === scope)
          setState({ scope, items: toAttentionItems(projectId, runId, sessions), error: null, loaded: true });
      } catch (reason) {
        if (active && scopeRef.current === scope)
          setState({ scope, items: [], error: reason, loaded: true });
      }
      // Schedule the next read only after this one finishes, so reads never overlap.
      if (active) timer = window.setTimeout(() => { void read(); }, ATTENTION_POLL_MS);
    };
    // First read on the next tick, outside the effect body.
    timer = window.setTimeout(() => { void read(); }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, runId, scope, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  // Hide state saved for an older scope until the first read for the new scope lands.
  const current = state.scope === scope;
  return {
    binding: binding ? { projectId, runId } : null,
    items: current ? state.items : [],
    loading: Boolean(binding) && !(current && state.loaded),
    error: current ? state.error : null,
    reload,
  };
}

/** Path that opens one item's decision on the run page. */
export function attentionPath(item: AttentionItem): string {
  return `/projects/${encodeURIComponent(item.projectId)}/runs/${encodeURIComponent(item.runId)}?view=approvals`;
}
