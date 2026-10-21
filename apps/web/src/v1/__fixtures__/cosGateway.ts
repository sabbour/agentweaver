/**
 * In-memory Gateway for Chief of Staff, run list, and notification tests. It
 * implements the documented status codes and `{ error }` bodies, records every
 * request, and replays creates by Idempotency-Key like the owners do.
 */
import type {
  ChiefOfStaffConversation,
  ChiefOfStaffPriority,
  ChiefOfStaffPriorityLink,
  ProjectRunState,
  ProjectRunSummary,
  ProjectSummary,
  UserNotification,
} from '../contracts';

export interface RecordedRequest {
  method: string;
  url: URL;
  headers: Headers;
  body: unknown;
}

export interface FakeGatewayState {
  projects: ProjectSummary[];
  runs: Record<string, ProjectRunSummary[]>;
  conversations: ChiefOfStaffConversation[];
  priorities: ChiefOfStaffPriority[];
  orderRevision: number;
  notifications: Array<UserNotification & { dismissed?: boolean }>;
}

/**
 * A queued failure: an HTTP error, a request that never reached the service (
etwork),
 * or one the service processed whose response was lost (lost).
 */
export type Failure = { status: number; body: unknown } | 'network' | 'lost';

export const NOW = '2026-10-20T12:00:00.000Z';

export function project(projectId: string, name = projectId): ProjectSummary {
  return { projectId, name, state: 'active', revision: 1, configurationRevision: 3, createdAt: NOW, updatedAt: NOW };
}

export function run(runId: string, state: ProjectRunState, title: string | null = null): ProjectRunSummary {
  return {
    runId,
    rootSessionId: `${runId}-root`,
    title,
    goal: null,
    state,
    createdAt: NOW,
    updatedAt: NOW,
    completedAt: ['completed', 'failed', 'cancelled', 'indeterminate'].includes(state) ? NOW : null,
    startedBy: { issuer: 'https://broker.test/', subject: 'me' },
  };
}

export function priority(priorityId: string, outcome: string, overrides: Partial<ChiefOfStaffPriority> = {}): ChiefOfStaffPriority {
  return {
    priorityId, outcome, notes: null, state: 'active', checklist: [], position: 0, revision: 1, links: [], createdAt: NOW, updatedAt: NOW,
    ...overrides,
  };
}

export function notification(notificationId: string, overrides: Partial<UserNotification & { dismissed?: boolean }> = {}): UserNotification & { dismissed?: boolean } {
  return {
    notificationId, projectId: 'p1', runId: 'r-blocked', sessionId: 's1', requestId: `req-${notificationId}`,
    source: 'pendingGate' as const, promotionId: null, taskCount: null,
    kind: 'awaitingInput' as const, prompt: 'Which branch?', gateKind: 'question' as const, stateVersion: 4,
    createdAt: NOW, updatedAt: NOW, ...overrides,
  };
}

function json(status: number, body: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
  });
}

/** Creates a fake Gateway rooted at `https://gw.test/api/v1`. `fetch` is the fetcher to pass to clients. */
export function createFakeGateway(seed: Partial<FakeGatewayState> = {}) {
  const state: FakeGatewayState = {
    projects: [project('p1', 'Payments')],
    runs: {},
    conversations: [],
    priorities: [],
    orderRevision: 1,
    notifications: [],
    ...seed,
  };
  const requests: RecordedRequest[] = [];
  const createdRuns = new Map<string, { runId: string; projectId: string }>();
  const createdPriorities = new Map<string, string>();
  /** Per-route failures to return once, keyed by `METHOD path`. */
  const failures = new Map<string, Array<Failure>>();
  let sequence = 0;

  const findPriority = (id: string) => state.priorities.find((item) => item.priorityId === id);
  const ordered = () => ({ orderRevision: state.orderRevision, items: state.priorities.map((item, index) => ({ ...item, position: index })) });
  const savePriority = (next: ChiefOfStaffPriority) => {
    state.priorities = state.priorities.map((item) => item.priorityId === next.priorityId ? next : item);
    return next;
  };

  // Request bodies are shaped per route below; the fake trusts the typed client that sent them.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const handle = (method: string, url: URL, headers: Headers, body: any): Response => {
    const path = url.pathname.replace(/^\/api\/v1/, '');
    const parts = path.split('/').filter(Boolean).map(decodeURIComponent);
    if (method === 'GET' && path === '/projects') return json(200, state.projects);
    if (parts[0] === 'projects' && parts[2] === 'runs') {
      const projectId = parts[1];
      if (method === 'GET' && parts.length === 3) {
        const filter = url.searchParams.get('state')?.split(',') ?? null;
        const items = (state.runs[projectId] ?? []).filter((item) => !filter || filter.includes(item.state));
        return json(200, { projectId, items, nextCursor: null });
      }
      if (method === 'POST' && parts.length === 3) {
        const key = headers.get('Idempotency-Key') ?? '';
        const existing = createdRuns.get(key);
        const runId = existing?.runId ?? `run-${++sequence}`;
        if (!existing) {
          createdRuns.set(key, { runId, projectId });
          state.runs[projectId] = [{ ...run(runId, 'idle', null), title: body.firstMessage }, ...(state.runs[projectId] ?? [])];
        }
        return json(existing ? 200 : 201, { runId, rootSessionId: `${runId}-root`, state: 'awaiting_binding', binding: { projectId, runId } });
      }
      if (method === 'POST' && parts[4] === 'start') {
        const runId = parts[3];
        state.runs[projectId] = (state.runs[projectId] ?? []).map((item) => item.runId === runId ? { ...item, state: 'active' } : item);
        state.conversations = state.conversations.map((item) => item.runId === runId ? { ...item, requestState: 'started', runState: 'active' } : item);
        return json(200, { runId, rootSessionId: `${runId}-root`, state: 'started' });
      }
    }
    if (path === '/notifications' && method === 'GET') {
      const projectId = url.searchParams.get('projectId');
      const includeDismissed = url.searchParams.get('includeDismissed') === 'true';
      const items = state.notifications
        .filter((item) => (!projectId || item.projectId === projectId) && (includeDismissed || !item.dismissed))
        .map((item) => {
          const copy: UserNotification & { dismissed?: boolean } = { ...item };
          delete copy.dismissed;
          return copy as UserNotification;
        });
      return json(200, { items, count: items.length, nextCursor: null });
    }
    if (parts[0] === 'notifications' && parts[2] === 'dismiss' && method === 'POST') {
      state.notifications = state.notifications.map((item) => item.notificationId === parts[1] ? { ...item, dismissed: true } : item);
      return json(204, undefined);
    }
    if (parts[0] === 'chief-of-staff' && parts[1] === 'conversations') {
      if (method === 'GET' && parts.length === 2) return json(200, { items: state.conversations, nextCursor: null });
      if (method === 'POST' && parts.length === 2) {
        const existing = state.conversations.find((item) => item.projectId === body.projectId && item.runId === body.runId);
        if (existing) return json(200, existing);
        const created = (state.runs[body.projectId] ?? []).find((item) => item.runId === body.runId);
        const conversation: ChiefOfStaffConversation = {
          conversationId: `conv-${body.runId}`,
          access: 'available',
          projectId: body.projectId,
          runId: body.runId,
          rootSessionId: `${body.runId}-root`,
          title: created?.title ?? null,
          requestState: created?.state === 'idle' ? 'awaiting_binding' : 'started',
          runState: created?.state ?? null,
          createdAt: NOW,
        };
        state.conversations = [conversation, ...state.conversations];
        return json(200, conversation);
      }
    }
    if (parts[0] === 'chief-of-staff' && parts[1] === 'priorities') {
      if (method === 'GET' && parts.length === 2) return json(200, ordered());
      if (method === 'POST' && parts.length === 2) {
        const key = headers.get('Idempotency-Key') ?? '';
        const replay = createdPriorities.get(key);
        if (replay) return json(200, findPriority(replay));
        const created = priority(`pri-${++sequence}`, body.outcome, { notes: body.notes ?? null });
        createdPriorities.set(key, created.priorityId);
        state.priorities = [...state.priorities, created];
        state.orderRevision += 1;
        return json(201, created);
      }
      if (method === 'PUT' && parts[2] === 'order') {
        if (body.expectedOrderRevision !== state.orderRevision) return json(409, { error: 'priority_order_conflict' });
        state.priorities = body.priorityIds.map((id: string) => findPriority(id)!);
        state.orderRevision += 1;
        return json(200, ordered());
      }
      const current = findPriority(parts[2]);
      if (!current) return json(404, { error: 'priority_not_found' });
      if (method === 'GET' && parts.length === 3) return json(200, current);
      if (method === 'PATCH' && parts.length === 3) {
        if (body.expectedRevision !== current.revision) return json(409, { error: 'priority_revision_conflict' });
        const checklist = body.checklist
          ? body.checklist.map((item: { itemId?: string | null; text: string; done: boolean }) => ({ itemId: item.itemId ?? `item-${++sequence}`, text: item.text, done: item.done }))
          : current.checklist;
        return json(200, savePriority({
          ...current,
          outcome: body.outcome ?? current.outcome,
          notes: body.notes === undefined ? current.notes : body.notes,
          state: body.state ?? current.state,
          checklist,
          revision: current.revision + 1,
        }));
      }
      if (method === 'POST' && parts[3] === 'links') {
        if (body.expectedRevision !== current.revision) return json(409, { error: 'priority_revision_conflict' });
        const target = (state.runs[body.projectId] ?? []).find((item) => item.runId === body.runId);
        const conversation = state.conversations.find((item) => item.conversationId === body.conversationId);
        const link: ChiefOfStaffPriorityLink = {
          linkId: `link-${++sequence}`,
          kind: body.kind,
          access: 'available',
          projectId: body.projectId,
          runId: body.runId ?? conversation?.runId ?? undefined,
          conversationId: body.conversationId ?? undefined,
          artifactId: body.artifactId ?? undefined,
          state: body.kind === 'artifact' ? 'unavailable' : target?.state ?? conversation?.runState ?? undefined,
          createdAt: NOW,
        };
        return json(200, savePriority({ ...current, links: [...current.links, link], revision: current.revision + 1 }));
      }
      if (method === 'DELETE' && parts[3] === 'links') {
        if (Number(url.searchParams.get('expectedRevision')) !== current.revision) return json(409, { error: 'priority_revision_conflict' });
        return json(200, savePriority({ ...current, links: current.links.filter((item) => item.linkId !== parts[4]), revision: current.revision + 1 }));
      }
    }
    return json(404, { error: 'not_found' });
  };

  const fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const url = new URL(String(input));
    const method = (init.method ?? 'GET').toUpperCase();
    const headers = new Headers(init.headers);
    const body = typeof init.body === 'string' ? JSON.parse(init.body) : undefined;
    requests.push({ method, url, headers, body });
    const queued = failures.get(`${method} ${url.pathname}`);
    const failure = queued?.shift();
    if (failure === 'network') throw new TypeError('Failed to fetch');
    if (failure === 'lost') {
      handle(method, url, headers, body);
      throw new TypeError('Failed to fetch');
    }
    if (failure) return json(failure.status, failure.body);
    return handle(method, url, headers, body);
  };

  return {
    state,
    requests,
    fetch: fetch as typeof globalThis.fetch,
    /** Makes the next request to `METHOD /api/v1/...` fail once, after it is recorded. */
    failNext(method: string, path: string, failure: Failure) {
      const key = `${method} ${path}`;
      failures.set(key, [...(failures.get(key) ?? []), failure]);
    },
    /** Requests other than reads whose path starts with `prefix`. */
    writes(prefix = '') {
      return requests.filter((request) => request.method !== 'GET' && request.url.pathname.startsWith(prefix));
    },
  };
}

export type FakeGateway = ReturnType<typeof createFakeGateway>;