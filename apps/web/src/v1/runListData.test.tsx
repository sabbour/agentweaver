import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRunPage, ProjectRunSummary, ProjectSummary } from './contracts';
import {
  mergeRunItems,
  runDisplayLabel,
  runListErrorMessage,
  toRunListItem,
  useCrossProjectRunList,
  useProjectRunList,
} from './runListData';

const mocks = vi.hoisted(() => ({ listProjectRuns: vi.fn(), apiCall: vi.fn() }));

vi.mock('./AuthContext', () => ({ useAuth: () => ({ apiCall: mocks.apiCall }) }));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: { listProjectRuns: mocks.listProjectRuns },
}));

import { GatewayError } from './api';

function summary(runId: string, overrides: Partial<ProjectRunSummary> = {}): ProjectRunSummary {
  return {
    runId,
    rootSessionId: null,
    title: null,
    goal: null,
    state: 'active',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    startedBy: { issuer: 'https://issuer.test', subject: 'user' },
    ...overrides,
  };
}

function page(projectId: string, items: ProjectRunSummary[], nextCursor: string | null = null): ProjectRunPage {
  return { projectId, items, nextCursor };
}

function project(projectId: string, name: string): ProjectSummary {
  return { projectId, name, state: 'active', revision: 1, configurationRevision: 1, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', effectiveRoles: [] };
}

beforeEach(() => {
  mocks.listProjectRuns.mockReset();
  mocks.apiCall.mockReset().mockImplementation(<T,>(operation: (token: string, tenant: string | null) => Promise<T>) => operation('token', 'tenant'));
});

describe('run display helpers', () => {
  it('uses title, then goal, then "Run" with the short run ID', () => {
    expect(runDisplayLabel(summary('r1', { title: 'Title', goal: 'Goal', rootSessionId: 's1' }))).toBe('Title');
    expect(runDisplayLabel(summary('r1', { title: '  ', goal: 'Goal', rootSessionId: 's1' }))).toBe('Goal');
    // The root session ID is skipped: it is the same agent name for many runs.
    expect(runDisplayLabel(summary('0123456789abcdef', { rootSessionId: 'coordinator' }))).toBe('Run 01234567');
    expect(runDisplayLabel(summary('r1'))).toBe('Run r1');
    expect(runDisplayLabel({ runId: 'r2' } as ProjectRunSummary)).toBe('Run r2');
  });

  it('maps a run summary to a row', () => {
    expect(toRunListItem('p1', summary('r1', { goal: 'Fix it', state: 'cancelled', createdAt: '2026-02-01T00:00:00Z' }))).toEqual({
      projectId: 'p1', runId: 'r1', label: 'Fix it', executionState: 'cancelled', time: { label: 'Started', value: '2026-02-01T00:00:00Z' },
    });
  });

  it('merges rows without duplicates and sorts newest first on request', () => {
    const older = toRunListItem('p1', summary('r1', { createdAt: '2026-01-01T00:00:00Z' }));
    const newer = toRunListItem('p2', summary('r2', { createdAt: '2026-03-01T00:00:00Z' }));
    const updated = { ...older, label: 'Updated' };
    expect(mergeRunItems([older], [newer, updated], true)).toEqual([newer, updated]);
    expect(mergeRunItems([older], [newer])).toEqual([older, newer]);
  });

  it('explains a missing read permission', () => {
    expect(runListErrorMessage(new GatewayError(403, { code: 'forbidden' }, 'Forbidden')))
      .toBe('You do not have permission to read runs in this project.');
    expect(runListErrorMessage(new Error('Network down'))).toBe('Network down');
  });
});

describe('useProjectRunList', () => {
  it('reads the first page with the unbound token and loads more with the cursor', async () => {
    mocks.listProjectRuns
      .mockResolvedValueOnce(page('p1', [summary('r2', { title: 'Second' })], 'cursor-1'))
      .mockResolvedValueOnce(page('p1', [summary('r1', { state: 'completed' })]));
    const { result } = renderHook(() => useProjectRunList('p1', { states: ['open'], pageSize: 1 }));
    expect(result.current.loading).toBe(true);
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(mocks.apiCall).toHaveBeenCalledWith(expect.any(Function), null);
    expect(mocks.listProjectRuns).toHaveBeenLastCalledWith('token', 'p1', { states: ['open'], cursor: null, pageSize: 1 }, 'tenant');
    expect(result.current.items.map((item) => item.label)).toEqual(['Second']);
    expect(result.current.hasMore).toBe(true);

    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.items).toHaveLength(2));
    expect(mocks.listProjectRuns).toHaveBeenLastCalledWith('token', 'p1', { states: ['open'], cursor: 'cursor-1', pageSize: 1 }, 'tenant');
    expect(result.current.items.map((item) => item.runId)).toEqual(['r2', 'r1']);
    expect(result.current.hasMore).toBe(false);
    expect(result.current.loadingMore).toBe(false);
  });

  it('treats a missing next cursor as the last page', async () => {
    mocks.listProjectRuns.mockResolvedValueOnce({ projectId: 'p1', items: [summary('r1')] });
    const { result } = renderHook(() => useProjectRunList('p1'));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.hasMore).toBe(false);
  });

  it('reports a failure and reloads on request', async () => {
    const forbidden = new GatewayError(403, { code: 'forbidden' }, 'Forbidden');
    mocks.listProjectRuns.mockRejectedValueOnce(forbidden).mockResolvedValueOnce(page('p1', [summary('r1')]));
    const { result } = renderHook(() => useProjectRunList('p1'));
    await waitFor(() => expect(result.current.error).toBe(forbidden));
    expect(result.current.items).toEqual([]);
    act(() => result.current.reload());
    await waitFor(() => expect(result.current.items).toHaveLength(1));
    expect(result.current.error).toBeNull();
  });

  it('drops a response for a project that is no longer shown', async () => {
    let finishFirst!: (value: ProjectRunPage) => void;
    mocks.listProjectRuns
      .mockImplementationOnce(() => new Promise((resolve) => { finishFirst = resolve; }))
      .mockResolvedValueOnce(page('p2', [summary('r2')]));
    const { result, rerender } = renderHook(({ id }) => useProjectRunList(id), { initialProps: { id: 'p1' } });
    await waitFor(() => expect(mocks.listProjectRuns).toHaveBeenCalledTimes(1));
    rerender({ id: 'p2' });
    await waitFor(() => expect(result.current.items.map((item) => item.runId)).toEqual(['r2']));
    await act(async () => { finishFirst(page('p1', [summary('r1')])); });
    expect(result.current.items.map((item) => item.projectId)).toEqual(['p2']);
  });

  it('reads nothing without a project ID', async () => {
    const { result } = renderHook(() => useProjectRunList(''));
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(mocks.listProjectRuns).not.toHaveBeenCalled();
    expect(result.current.loading).toBe(false);
  });
});

describe('useCrossProjectRunList', () => {
  it('merges each project newest first and lists unreadable projects', async () => {
    const projects = [project('p1', 'Alpha'), project('p2', 'Beta'), project('p3', 'Gamma')];
    mocks.listProjectRuns.mockImplementation(async (_token: string, projectId: string, options: { cursor: string | null }) => {
      if (projectId === 'p3') throw new GatewayError(403, {}, 'Forbidden');
      if (projectId === 'p1' && !options.cursor) return page('p1', [summary('a2', { createdAt: '2026-01-03T00:00:00Z' })], 'next');
      if (projectId === 'p1') return page('p1', [summary('a1', { createdAt: '2026-01-01T00:00:00Z' })]);
      return page('p2', [summary('b1', { createdAt: '2026-01-02T00:00:00Z' })]);
    });
    const { result } = renderHook(() => useCrossProjectRunList(projects, 10));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.items.map((item) => item.runId)).toEqual(['a2', 'b1']);
    expect(result.current.unreadableProjects).toEqual(['Gamma']);
    expect(result.current.error).toBeNull();
    expect(result.current.projectNames.get('p2')).toBe('Beta');
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { cursor: null, pageSize: 10 }, 'tenant');

    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.items).toHaveLength(3));
    expect(result.current.items.map((item) => item.runId)).toEqual(['a2', 'b1', 'a1']);
    expect(result.current.hasMore).toBe(false);
  });

  it('reports an error when no project can be read', async () => {
    const failure = new GatewayError(403, {}, 'Forbidden');
    mocks.listProjectRuns.mockRejectedValue(failure);
    const { result } = renderHook(() => useCrossProjectRunList([project('p1', 'Alpha')]));
    await waitFor(() => expect(result.current.error).toBe(failure));
  });

  it('waits for the project list', async () => {
    const { result } = renderHook(() => useCrossProjectRunList(null));
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(mocks.listProjectRuns).not.toHaveBeenCalled();
    expect(result.current.items).toEqual([]);
  });
});
