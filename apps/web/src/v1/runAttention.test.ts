import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UserNotification } from './contracts';
import {
  ATTENTION_PAGE_SIZE,
  ATTENTION_POLL_MS,
  attentionKey,
  attentionPath,
  fromUserNotification,
  subtasksCreatedLabel,
  useRunAttention,
} from './runAttention';
import type { AttentionItem } from './runAttention';

const mocks = vi.hoisted(() => {
  const apiCall = vi.fn((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
  return {
    session: null as null | { accessToken: string },
    apiCall,
    listNotifications: vi.fn(),
    dismissNotification: vi.fn(),
  };
});

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ session: mocks.session, apiCall: mocks.apiCall }),
}));

vi.mock('./api', () => ({
  gatewayClient: {
    listNotifications: mocks.listNotifications,
    dismissNotification: mocks.dismissNotification,
  },
}));

const notification = (overrides: Partial<UserNotification> = {}): UserNotification => ({
  notificationId: 'n1', source: 'pendingGate', projectId: 'p1', runId: 'r1', sessionId: 's1',
  requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?', createdAt: '2026-10-20T10:00:00Z',
  gateKind: 'question', stateVersion: 3, updatedAt: '2026-10-20T10:00:00Z', promotionId: null, taskCount: null,
  ...overrides,
});

const promoted = notification({
  notificationId: 'n2', source: 'backlogPromoted', requestId: null, kind: null, gateKind: null, stateVersion: null,
  prompt: null, promotionId: 'm1', taskCount: 4,
});

const gateItem: AttentionItem = {
  notificationId: 'n1', source: 'pendingGate', projectId: 'p1', runId: 'r1', sessionId: 's1',
  createdAt: '2026-10-20T10:00:00Z', requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?',
};

describe('fromUserNotification', () => {
  it('maps a pending gate and requires its gate fields', () => {
    expect(fromUserNotification(notification())).toEqual(gateItem);
    expect(fromUserNotification(notification({ requestId: null }))).toBeNull();
    expect(fromUserNotification(notification({ kind: null }))).toBeNull();
  });

  it('maps a backlog promotion without reading the null gate fields', () => {
    expect(fromUserNotification(promoted)).toEqual({
      notificationId: 'n2', source: 'backlogPromoted', projectId: 'p1', runId: 'r1', sessionId: 's1',
      createdAt: '2026-10-20T10:00:00Z', promotionId: 'm1', taskCount: 4,
    });
    expect(fromUserNotification({ ...promoted, promotionId: null })).toBeNull();
    expect(fromUserNotification({ ...promoted, taskCount: 0 })).toBeNull();
  });

  it('ignores an unknown source', () => {
    expect(fromUserNotification(notification({ source: 'other' as UserNotification['source'] }))).toBeNull();
  });
});

describe('attention helpers', () => {
  it('uses singular and plural subtask text', () => {
    expect(subtasksCreatedLabel(1)).toBe('1 subtask created');
    expect(subtasksCreatedLabel(5)).toBe('5 subtasks created');
  });

  it('keys items by notification ID', () => {
    expect(attentionKey(gateItem)).toBe('n1');
  });

  it('opens the run Approvals view for a gate and the Board for a promotion', () => {
    expect(attentionPath({ ...gateItem, projectId: 'p 1', runId: 'r/1' })).toBe('/projects/p%201/runs/r%2F1?view=approvals');
    expect(attentionPath(fromUserNotification({ ...promoted, projectId: 'p 1' })!)).toBe('/projects/p%201/board');
  });
});

describe('useRunAttention', () => {
  beforeEach(() => {
    mocks.session = null;
    mocks.apiCall.mockClear();
    mocks.listNotifications.mockReset();
    mocks.dismissNotification.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
  });

  it('does not read while signed out', async () => {
    const { result } = renderHook(() => useRunAttention());
    expect(result.current.loading).toBe(false);
    expect(result.current.items).toEqual([]);
    expect(result.current.count).toBe(0);
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(mocks.listNotifications).not.toHaveBeenCalled();
  });

  it('reads all projects with the unbound token and reports the service count', async () => {
    mocks.session = { accessToken: 't' };
    mocks.listNotifications.mockResolvedValue({ items: [notification(), promoted, notification({ notificationId: 'bad', kind: null })], count: 7, nextCursor: 'c' });

    const { result } = renderHook(() => useRunAttention());
    expect(result.current.loading).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).toBeNull();
    expect(result.current.items.map((item) => item.notificationId)).toEqual(['n1', 'n2']);
    expect(result.current.count).toBe(7);
    // No projectId: the list covers every project.
    expect(mocks.listNotifications).toHaveBeenCalledWith('token', { pageSize: ATTENTION_PAGE_SIZE }, 'tenant-1');
    // A null binding selects the unbound token.
    expect(mocks.apiCall).toHaveBeenCalledWith(expect.any(Function), null);
  });

  it('reports an error with no items and a zero count', async () => {
    mocks.session = { accessToken: 't' };
    mocks.listNotifications.mockRejectedValue(new Error('Boom'));
    const { result } = renderHook(() => useRunAttention());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect((result.current.error as Error).message).toBe('Boom');
    expect(result.current.items).toEqual([]);
    expect(result.current.count).toBe(0);
  });

  it('polls every 30 seconds and skips reads while the tab is hidden', async () => {
    vi.useFakeTimers();
    mocks.session = { accessToken: 't' };
    mocks.listNotifications.mockResolvedValue({ items: [], count: 0, nextCursor: null });
    renderHook(() => useRunAttention());
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(mocks.listNotifications).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(ATTENTION_POLL_MS); });
    expect(mocks.listNotifications).toHaveBeenCalledTimes(2);

    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    await act(async () => { await vi.advanceTimersByTimeAsync(ATTENTION_POLL_MS * 3); });
    expect(mocks.listNotifications).toHaveBeenCalledTimes(2);

    // Showing the tab reads again right away.
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(mocks.listNotifications).toHaveBeenCalledTimes(3);
  });

  it('dismisses with the unbound token, hides the item, and reads again', async () => {
    mocks.session = { accessToken: 't' };
    mocks.listNotifications.mockResolvedValue({ items: [notification(), promoted], count: 2, nextCursor: null });
    mocks.dismissNotification.mockResolvedValue(undefined);
    const { result } = renderHook(() => useRunAttention());
    await waitFor(() => expect(result.current.items).toHaveLength(2));

    await act(async () => { await result.current.dismiss('n1'); });
    expect(mocks.dismissNotification).toHaveBeenCalledWith('token', 'n1', 'tenant-1');
    expect(mocks.apiCall).toHaveBeenLastCalledWith(expect.any(Function), null);
    // Hidden at once, and the count drops even before the next read.
    expect(result.current.items.map((item) => item.notificationId)).toEqual(['n2']);
    expect(result.current.count).toBe(1);
    await waitFor(() => expect(mocks.listNotifications).toHaveBeenCalledTimes(2));
  });

  it('restores the item and reports the error when a dismiss fails', async () => {
    mocks.session = { accessToken: 't' };
    mocks.listNotifications.mockResolvedValue({ items: [notification()], count: 1, nextCursor: null });
    mocks.dismissNotification.mockRejectedValue(new Error('Denied'));
    const { result } = renderHook(() => useRunAttention());
    await waitFor(() => expect(result.current.items).toHaveLength(1));

    await act(async () => { await result.current.dismiss('n1'); });
    expect((result.current.dismissError as Error).message).toBe('Denied');
    expect(result.current.items).toHaveLength(1);
    expect(result.current.count).toBe(1);
  });
});
