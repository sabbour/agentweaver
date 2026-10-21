/**
 * Reads the pending questions and approvals of the run the Broker session is
 * bound to. Run reads need a token bound to that exact run, so only the bound
 * run can be checked. A cross-run inbox needs the notifications contract (gap G6).
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
  const scope = JSON.stringify([session?.accessToken ?? null, projectId, runId]);
  const [state, setState] = useState<{ scope: string; items: AttentionItem[]; error: unknown; loaded: boolean }>(
    { scope: '', items: [], error: null, loaded: false },
  );
  const [generation, setGeneration] = useState(0);
  const scopeRef = useRef(scope);
  useEffect(() => { scopeRef.current = scope; }, [scope]);

  useEffect(() => {
    if (!projectId || !runId) return undefined;
    let active = true;
    let timer = 0;
    const runBinding = { projectId, runId };
    const read = async () => {
      try {
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
          .filter((status) => status.identity.projectId === projectId && status.identity.runId === runId)
          .map((status) => ({ sessionId: status.identity.sessionId, blockers: status.blockers }));
        if (active && scopeRef.current === scope)
          setState({ scope, items: toAttentionItems(projectId, runId, sessions), error: null, loaded: true });
      } catch (reason) {
        if (active && scopeRef.current === scope)
          setState({ scope, items: [], error: reason, loaded: true });
      }
      if (active) timer = window.setTimeout(() => { void read(); }, ATTENTION_POLL_MS);
    };
    timer = window.setTimeout(() => { void read(); }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, runId, scope, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
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
