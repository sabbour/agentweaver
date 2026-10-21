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

function isPendingChat(value: unknown): value is PendingChat {
  if (!value || typeof value !== 'object') return false;
  const entry = value as Record<string, unknown>;
  return typeof entry.projectId === 'string' && typeof entry.runId === 'string' && typeof entry.rootSessionId === 'string';
}

/** Reads pending chats. Malformed storage reads as empty. */
export function readPendingChats(): PendingChat[] {
  try {
    const parsed: unknown = JSON.parse(window.sessionStorage.getItem(PENDING_CHAT_STORAGE_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter(isPendingChat) : [];
  } catch {
    return [];
  }
}

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
    await apiCall((token, tenant) => gatewayClient.bindChiefOfStaffConversation(token, binding, tenant), null);
    if (!await ensureSession(binding, false)) return 'sign_in_required';
    await apiCall((token, tenant) => gatewayClient.startProjectRun(token, chat.projectId, chat.runId, tenant), binding);
    clearPendingChat(chat.runId);
    return 'started';
  }, [apiCall, ensureSession]);
}

/** Result of reading My conversations. */
export interface ConversationListState {
  items: ChiefOfStaffConversation[];
  loading: boolean;
  error: unknown;
  reload: () => void;
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
  const [generation, setGeneration] = useState(0);
  const generationRef = useRef(generation);
  useEffect(() => { generationRef.current = generation; }, [generation]);

  const readPage = useCallback((cursor: string | null): Promise<ChiefOfStaffConversationPage> => apiCall(
    (token, tenant) => gatewayClient.listChiefOfStaffConversations(token, { cursor, pageSize: CONVERSATION_PAGE_SIZE }, tenant),
    null,
  ), [apiCall]);

  useEffect(() => {
    let active = true;
    const timer = window.setTimeout(() => {
      void readPage(null).then(
        (page) => { if (active) setState({ items: page.items, nextCursor: page.nextCursor, error: null, loaded: true }); },
        (reason: unknown) => { if (active) setState({ items: [], nextCursor: null, error: reason, loaded: true }); },
      );
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [readPage, generation]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const nextCursor = state.nextCursor;
  const loadMore = useCallback(() => {
    if (!nextCursor) return;
    const started = generationRef.current;
    void readPage(nextCursor).then((page) => {
      if (generationRef.current !== started) return;
      setState((previous) => previous.nextCursor !== nextCursor ? previous : {
        ...previous,
        items: [...previous.items, ...page.items.filter((item) => !previous.items.some((known) => known.conversationId === item.conversationId))],
        nextCursor: page.nextCursor,
      });
    }, (reason: unknown) => setState((previous) => ({ ...previous, error: reason })));
  }, [nextCursor, readPage]);

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
  const mounted = useRef(true);

  const reload = useCallback(async () => {
    try {
      const next = await apiCall((token, tenant) => gatewayClient.listChiefOfStaffPriorities(token, tenant), null);
      if (mounted.current) {
        setList(next);
        setError(null);
      }
      return next;
    } catch (reason) {
      if (mounted.current) setError(reason);
      return null;
    } finally {
      if (mounted.current) setLoaded(true);
    }
  }, [apiCall]);

  useEffect(() => {
    mounted.current = true;
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
    let active = true;
    const timer = window.setTimeout(() => {
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
