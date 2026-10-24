/**
 * Chief of Staff data for the web app: the caller's private conversations and
 * Priorities, and the new-chat start flow. Every Chief of Staff route needs the
 * unbound session; only run start uses a token bound to the new run.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import type { Dispatch, SetStateAction } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type {
  ChiefOfStaffConversation,
  ChiefOfStaffConversationPage,
  ChiefOfStaffPriorityList,
  CreateProjectRunResponse,
  ProjectSummary,
} from './contracts';

/** A created run whose start has not finished. Holds identities only, never message text. */
export interface PendingChat {
  projectId: string;
  runId: string;
  rootSessionId: string;
}

/** sessionStorage key for created chats that still need start after a reload or sign-in redirect. */
export const PENDING_CHAT_STORAGE_KEY = 'agentweaver.cos.pending-chats';

/** Type guard for one stored pending chat. Storage is user-editable, so every field is checked. */
function isPendingChat(value: unknown): value is PendingChat {
  // Reject null, numbers, strings and other non-object entries first.
  if (!value || typeof value !== 'object') return false;
  const entry = value as Record<string, unknown>;
  return typeof entry.projectId === 'string' && typeof entry.runId === 'string' && typeof entry.rootSessionId === 'string';
}

/** Reads pending chats. Malformed storage reads as empty. */
export function readPendingChats(): PendingChat[] {
  try {
    // A missing key reads as an empty list.
    const parsed: unknown = JSON.parse(window.sessionStorage.getItem(PENDING_CHAT_STORAGE_KEY) ?? '[]');
    // Keep only well-formed entries; drop anything else instead of failing.
    return Array.isArray(parsed) ? parsed.filter(isPendingChat) : [];
  } catch {
    // Invalid JSON or blocked storage: behave as if nothing is pending.
    return [];
  }
}

/** Writes the pending chat list. An empty list removes the key so no stale entry stays behind. */
function writePendingChats(entries: PendingChat[]) {
  try {
    if (entries.length === 0) window.sessionStorage.removeItem(PENDING_CHAT_STORAGE_KEY);
    else window.sessionStorage.setItem(PENDING_CHAT_STORAGE_KEY, JSON.stringify(entries));
  } catch {
    // Storage can be full or blocked; the conversation list still offers Start.
  }
}

/** Saves a created run before start, so a reload or sign-in redirect resumes it. */
export function savePendingChat(created: CreateProjectRunResponse) {
  const entry: PendingChat = {
    projectId: created.binding.projectId,
    runId: created.runId,
    rootSessionId: created.rootSessionId,
  };
  // Replace any older entry for the same run so a retry never stores duplicates.
  writePendingChats([...readPendingChats().filter((item) => item.runId !== entry.runId), entry]);
}

/** Removes a pending chat after start succeeds. */
export function clearPendingChat(runId: string) {
  writePendingChats(readPendingChats().filter((item) => item.runId !== runId));
}

/** Path of the run page that shows a chat's history. */
export function runPath(projectId: string, runId: string): string {
  return `/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}`;
}

/** `started` when the first message was accepted; `sign_in_required` when the run token needs interactive sign-in. */
export type StartChatResult = 'started' | 'sign_in_required';

/**
 * Binds a created run to My conversations, gets a token bound to that run, and
 * starts it. Bind and start are idempotent, so calling this again after a
 * reload, timeout, or sign-in resumes the same chat without a second message.
 */
export function useStartChat() {
  const { apiCall, ensureSession } = useAuth();
  return useCallback(async (chat: PendingChat): Promise<StartChatResult> => {
    const binding = { projectId: chat.projectId, runId: chat.runId };
    // Step 1: link the run to the caller's conversation list. This uses the
    // unbound session (null binding) because no run token exists yet.
    await apiCall((token, tenant) => gatewayClient.bindChiefOfStaffConversation(token, binding, tenant), null);
    // Step 2: get a token bound to this run without a popup (interactive = false).
    // If that needs sign-in, stop here; the pending entry stays so start resumes later.
    if (!await ensureSession(binding, false)) return 'sign_in_required';
    // Step 3: start the run with the run-bound token. This sends the first message.
    await apiCall((token, tenant) => gatewayClient.startProjectRun(token, chat.projectId, chat.runId, tenant), binding);
    // Only forget the pending entry after start succeeded. Any thrown error above
    // leaves it in storage so the user can retry.
    clearPendingChat(chat.runId);
    return 'started';
  }, [apiCall, ensureSession]);
}

/** Result of reading My conversations. */
export interface ConversationListState {
  items: ChiefOfStaffConversation[];
  loading: boolean;
  error: unknown;
  reload: () => Promise<void>;
  /** Reads the next page when one exists. */
  loadMore?: () => void;
}

/** Page size for conversation reads. */
export const CONVERSATION_PAGE_SIZE = 50;

/** Reads the caller's conversations, newest first, with the unbound session. */
export function useChiefOfStaffConversations(): ConversationListState {
  const { apiCall } = useAuth();
  const [state, setState] = useState<{ items: ChiefOfStaffConversation[]; nextCursor: string | null; error: unknown; loaded: boolean }>(
    { items: [], nextCursor: null, error: null, loaded: false },
  );
  // Each reload invalidates older reads, including a pending "load more" page.
  const requestRef = useRef(0);
  const mountedRef = useRef(false);

  // Reads one page. A null cursor means the first (newest) page.
  const readPage = useCallback((cursor: string | null): Promise<ChiefOfStaffConversationPage> => apiCall(
    (token, tenant) => gatewayClient.listChiefOfStaffConversations(token, { cursor, pageSize: CONVERSATION_PAGE_SIZE }, tenant),
    null,
  ), [apiCall]);

  // A reload returns its read promise so page-level refreshes can avoid overlap.
  const reload = useCallback(async () => {
    // Drop a reply if a newer refresh or unmount made this request stale.
    const request = ++requestRef.current;
    try {
      const page = await readPage(null);
      if (mountedRef.current && request === requestRef.current) {
        setState({ items: page.items, nextCursor: page.nextCursor, error: null, loaded: true });
      }
    } catch (reason) {
      if (mountedRef.current && request === requestRef.current) {
        setState({ items: [], nextCursor: null, error: reason, loaded: true });
      }
    }
  }, [readPage]);

  // Defer the first read so the effect does not update state synchronously.
  useEffect(() => {
    mountedRef.current = true;
    const timer = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => {
      mountedRef.current = false;
      window.clearTimeout(timer);
      requestRef.current += 1;
    };
  }, [reload]);
  const nextCursor = state.nextCursor;
  // Appends the next page to the current list.
  const loadMore = useCallback(() => {
    // No cursor means the last page is already loaded.
    if (!nextCursor) return;
    // Remember which reload this request belongs to.
    const started = requestRef.current;
    void readPage(nextCursor).then((page) => {
      // A reload happened while this page was loading; its result is stale.
      if (requestRef.current !== started) return;
      // If the cursor already moved (another load more finished first), keep the newer state.
      // Otherwise append, skipping conversations that are already listed so a list
      // that shifted between pages never shows the same item twice.
      setState((previous) => previous.nextCursor !== nextCursor ? previous : {
        ...previous,
        items: [...previous.items, ...page.items.filter((item) => !previous.items.some((known) => known.conversationId === item.conversationId))],
        nextCursor: page.nextCursor,
      });
    // A failed "load more" keeps the items already shown and only records the error.
    }, (reason: unknown) => setState((previous) => ({ ...previous, error: reason })));
  }, [nextCursor, readPage]);

  // Expose loadMore only when another page exists, so the UI can hide the button.
  return { items: state.items, loading: !state.loaded, error: state.error, reload, loadMore: nextCursor ? loadMore : undefined };
}

/** Result of reading Priorities. `replace` stores a list returned by a write. */
export interface PriorityListState {
  list: ChiefOfStaffPriorityList | null;
  loading: boolean;
  error: unknown;
  reload: () => Promise<ChiefOfStaffPriorityList | null>;
  replace: Dispatch<SetStateAction<ChiefOfStaffPriorityList | null>>;
}

/** Reads the caller's ordered Priorities with the unbound session. */
export function useChiefOfStaffPriorities(): PriorityListState {
  const { apiCall } = useAuth();
  const [list, setList] = useState<ChiefOfStaffPriorityList | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loaded, setLoaded] = useState(false);
  // Tracks whether the hook is still mounted, so late responses do not set state.
  const mounted = useRef(true);

  // Reads the list again. It also returns the list (or null on failure) so callers
  // can act on fresh data, for example after a write conflict.
  const reload = useCallback(async () => {
    try {
      const next = await apiCall((token, tenant) => gatewayClient.listChiefOfStaffPriorities(token, tenant), null);
      if (mounted.current) {
        // A successful read clears any earlier error.
        setList(next);
        setError(null);
      }
      return next;
    } catch (reason) {
      // Keep the last good list on screen and record the error. The caller gets null.
      if (mounted.current) setError(reason);
      return null;
    } finally {
      // Loading ends after the first attempt, whether it worked or not.
      if (mounted.current) setLoaded(true);
    }
  }, [apiCall]);

  // Initial read. Set `mounted` again here because React may run cleanup and
  // then this effect again (for example in Strict Mode).
  useEffect(() => {
    mounted.current = true;
    // Defer by one tick so state is not set synchronously inside the effect.
    const timer = window.setTimeout(() => { void reload(); }, 0);
    return () => {
      mounted.current = false;
      window.clearTimeout(timer);
    };
  }, [reload]);

  return { list, loading: !loaded, error, reload, replace: setList };
}

/** Reads the projects the caller can open, for pickers. */
export function useProjectChoices(): { projects: ProjectSummary[] | null; error: unknown } {
  const { apiCall } = useAuth();
  const [state, setState] = useState<{ projects: ProjectSummary[] | null; error: unknown }>({ projects: null, error: null });
  useEffect(() => {
    // Ignore the response if the component unmounted while it was loading.
    let active = true;
    // Defer by one tick so state is not set synchronously inside the effect.
    const timer = window.setTimeout(() => {
      // On failure use an empty list (not null) so pickers stop showing "loading"
      // and can show the error instead.
      void apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null).then(
        (projects) => { if (active) setState({ projects, error: null }); },
        (reason: unknown) => { if (active) setState({ projects: [], error: reason }); },
      );
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall]);
  return state;
}
