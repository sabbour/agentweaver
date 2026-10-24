import { describe, expect, it } from 'vitest';
import { GatewayError } from './api';
import {
  boardColumnFor,
  boardColumns,
  boardErrorMessage,
  isStaleRevision,
  newBacklogTaskId,
  prerequisiteTitles,
  queueInsertionPosition,
} from './boardData';
import type { BacklogGraph, BacklogTaskSnapshot } from './contracts';

function task(taskId: string, extra: Partial<BacklogTaskSnapshot> = {}): BacklogTaskSnapshot {
  return {
    reference: { projectId: 'p1', taskId }, revision: 1, state: 'backlog', isArchived: false,
    isAutomationInvocationPending: false, title: `Task ${taskId}`, description: null, lifecycle: 'open', repositoryIssue: null,
    position: 0,
    ...extra,
  };
}

const conflict = (...codes: string[]) =>
  new GatewayError(409, { issues: codes.map((code) => ({ code, path: '', message: code })) }, 'Conflict');

describe('board data', () => {
  it('maps queue state and lifecycle to columns and hides archived tasks', () => {
    expect(boardColumnFor(task('a'))).toBe('backlog');
    expect(boardColumnFor(task('a', { state: 'ready' }))).toBe('ready');
    expect(boardColumnFor(task('a', { state: 'claimed' }))).toBe('active');
    expect(boardColumnFor(task('a', { state: 'claimed', lifecycle: 'closed' }))).toBe('done');
    expect(boardColumnFor(task('a', { isArchived: true }))).toBeNull();
  });

  it('keeps owner queue order inside each column', () => {
    const graph: BacklogGraph = {
      projectId: 'p1', revision: 1, dependencies: [],
      tasks: [task('a', { position: 1 }), task('b', { position: 0 }), task('c', { state: 'ready' })],
    };
    const columns = boardColumns(graph);
    expect(columns.map((column) => column.id)).toEqual(['backlog', 'ready', 'active', 'done']);
    expect(columns[0].tasks.map((item) => item.reference.taskId)).toEqual(['b', 'a']);
    expect(columns[1].tasks).toHaveLength(1);
  });

  it('finds insertion indexes after excluding the moved task from a sparse queue', () => {
    const first = task('first', { position: 0 });
    const movedToReady = task('middle', { state: 'ready', position: 0 });
    const last = task('last', { position: 2 });
    const graph: BacklogGraph = {
      projectId: 'p1', revision: 1, dependencies: [], tasks: [first, movedToReady, last],
    };

    expect(queueInsertionPosition(graph, first, last, 'after')).toBe(1);
    expect(queueInsertionPosition(graph, last, first, 'before')).toBe(0);
  });

  it('finds visible neighbors across archived tasks in the state queue', () => {
    const first = task('first', { position: 0 });
    const hidden = task('hidden', { isArchived: true, position: 1 });
    const last = task('last', { position: 2 });
    const graph: BacklogGraph = {
      projectId: 'p1', revision: 1, dependencies: [], tasks: [first, hidden, last],
    };

    expect(queueInsertionPosition(graph, first, last, 'after')).toBe(2);
  });

  it('names prerequisites by title and counts the ones not in the graph', () => {
    const graph: BacklogGraph = {
      projectId: 'p1', revision: 1, tasks: [task('a'), task('b')],
      dependencies: [
        { task: { projectId: 'p1', taskId: 'b' }, prerequisite: { projectId: 'p1', taskId: 'a' } },
        { task: { projectId: 'p1', taskId: 'b' }, prerequisite: { projectId: 'p1', taskId: 'gone' } },
      ],
    };
    expect(prerequisiteTitles(graph, 'b')).toEqual({ titles: ['Task a'], unknown: 1 });
    expect(prerequisiteTitles(graph, 'a')).toEqual({ titles: [], unknown: 0 });
  });

  it('creates task IDs the service accepts', () => {
    expect(newBacklogTaskId()).toMatch(/^[A-Za-z0-9][A-Za-z0-9._:-]*$/);
  });

  it('treats only revision conflicts as stale', () => {
    expect(isStaleRevision(conflict('staleGraphRevision'))).toBe(true);
    expect(isStaleRevision(conflict('StaleTaskRevision', 'stale_graph_revision'))).toBe(true);
    expect(isStaleRevision(conflict('staleGraphRevision', 'taskNotEditable'))).toBe(false);
    expect(isStaleRevision(new GatewayError(409, {}, 'Conflict'))).toBe(false);
    expect(isStaleRevision(new GatewayError(400, { issues: [{ code: 'staleGraphRevision' }] }, 'Bad'))).toBe(false);
  });

  it('gives plain messages for access, conflicts and missing tasks', () => {
    const forbidden = new GatewayError(403, {}, 'Forbidden');
    expect(boardErrorMessage(forbidden, 'read')).toBe("You don't have permission to view this project's board.");
    expect(boardErrorMessage(forbidden, 'edit')).toBe("You don't have permission to change tasks in this project.");
    expect(boardErrorMessage(conflict('taskNotEditable'), 'edit')).toMatch(/can no longer be edited/);
    expect(boardErrorMessage(conflict('duplicateRepositoryIssue'), 'capture')).toBe('Another task already tracks that repository issue.');
    expect(boardErrorMessage(conflict('staleGraphRevision'), 'capture')).toMatch(/board changed/);
    expect(boardErrorMessage(new GatewayError(404, {}, 'Missing'), 'edit')).toBe('This task no longer exists. The board has been reloaded.');
  });
});
