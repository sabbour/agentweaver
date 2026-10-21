import { beforeEach, describe, expect, it } from 'vitest';
import { clearRecentRuns, forgetRun, readRecentRuns, RECENT_RUN_LIMIT, rememberRun } from './recentRuns';

describe('recent runs in this browser', () => {
  beforeEach(() => localStorage.clear());

  it('keeps runs per tenant, newest first, and filters by project', () => {
    rememberRun('tenant-a', { projectId: 'p1', runId: 'r1' }, new Date('2026-01-01T00:00:00Z'));
    rememberRun('tenant-a', { projectId: 'p2', runId: 'r2' }, new Date('2026-01-02T00:00:00Z'));
    rememberRun('tenant-b', { projectId: 'p1', runId: 'other' }, new Date('2026-01-03T00:00:00Z'));

    expect(readRecentRuns('tenant-a').map((run) => run.runId)).toEqual(['r2', 'r1']);
    expect(readRecentRuns('tenant-a', 'p1').map((run) => run.runId)).toEqual(['r1']);
    expect(readRecentRuns('tenant-b').map((run) => run.runId)).toEqual(['other']);
  });

  it('moves a reopened run to the top and keeps its last known state', () => {
    rememberRun('t', { projectId: 'p', runId: 'r1', executionState: 'running' }, new Date('2026-01-01T00:00:00Z'));
    rememberRun('t', { projectId: 'p', runId: 'r2' }, new Date('2026-01-02T00:00:00Z'));
    rememberRun('t', { projectId: 'p', runId: 'r1' }, new Date('2026-01-03T00:00:00Z'));

    const runs = readRecentRuns('t');
    expect(runs.map((run) => run.runId)).toEqual(['r1', 'r2']);
    expect(runs[0].executionState).toBe('running');
  });

  it('caps the list, forgets one run, and clears everything on sign-out', () => {
    for (let index = 0; index < RECENT_RUN_LIMIT + 3; index += 1)
      rememberRun('t', { projectId: 'p', runId: `r${index}` }, new Date(Date.UTC(2026, 0, 1, 0, index)));
    expect(readRecentRuns('t')).toHaveLength(RECENT_RUN_LIMIT);

    forgetRun('t', 'p', `r${RECENT_RUN_LIMIT + 2}`);
    expect(readRecentRuns('t').some((run) => run.runId === `r${RECENT_RUN_LIMIT + 2}`)).toBe(false);

    localStorage.setItem('unrelated', 'kept');
    clearRecentRuns();
    expect(readRecentRuns('t')).toEqual([]);
    expect(localStorage.getItem('unrelated')).toBe('kept');
  });

  it('ignores malformed stored entries', () => {
    localStorage.setItem('agentweaver.v1.recentRuns:t', JSON.stringify([{ runId: 'x' }, 'bad']));
    expect(readRecentRuns('t')).toEqual([]);
  });
});
