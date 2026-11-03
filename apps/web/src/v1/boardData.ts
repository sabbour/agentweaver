/**
 * Data for the project Board: the backlog graph, the synced repository issues
 * that enrich task cards, the column grouping, and the capture and edit writes.
 * Every call uses the unbound project session, because backlog reads and writes
 * are project-scoped and a run-bound session is rejected for issue reads.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { GatewayError, gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type {
  BacklogGraph,
  BacklogGraphRevisionRequest,
  BacklogTaskRevisionRequest,
  BacklogTaskSnapshot,
  BacklogTaskState,
  SetBacklogTaskPositionRequest,
  ProjectIssuePage,
  ProjectIssueSummary,
} from './contracts';
import { errorMessage } from './errors';

/** Authorization snapshot shared by one Board load or write. */
export interface BoardRequestScope {
  identity: object;
  accessToken: string | null;
  tenantId: string | null;
}

/** Checks whether a request still belongs to the currently rendered Board scope. */
export type IsBoardScopeCurrent = (scope: BoardRequestScope) => boolean;

/** A session refresh or context switch made the captured Board request stale. */
export class BoardScopeChangedError extends Error {
  constructor() {
    super('The Board authorization context changed. Review the task before continuing.');
    this.name = 'BoardScopeChangedError';
  }
}

function assertBoardScope(
  scope: BoardRequestScope,
  isScopeCurrent: IsBoardScopeCurrent,
  accessToken: string,
  tenantId: string | null,
): void {
  if (!isScopeCurrent(scope) || accessToken !== scope.accessToken || tenantId !== scope.tenantId)
    throw new BoardScopeChangedError();
}

/** Board column IDs. They match the 0.x column IDs so styles and anchors stay the same. */
export type BoardColumnId = 'backlog' | 'ready' | 'active' | 'done';

/** One rendered board column. */
export interface BoardColumn {
  id: BoardColumnId;
  label: string;
  description: string;
  tasks: BacklogTaskSnapshot[];
}

/** Column order and copy. Human Review and Problems are left out because no backlog data feeds them. */
export const BOARD_COLUMNS: ReadonlyArray<Omit<BoardColumn, 'tasks'>> = [
  { id: 'backlog', label: 'Backlog', description: 'Captured work not yet committed to an agent run.' },
  { id: 'ready', label: 'Ready', description: 'Committed tasks that wait for a run to claim them.' },
  { id: 'active', label: 'Active', description: 'Tasks that a run has claimed.' },
  { id: 'done', label: 'Done', description: 'Tasks whose work, or whose linked issue, is closed.' },
];

/** Board move guidance; card actions remain available to keyboard users. */
export const MOVE_GUIDANCE_COPY = 'Drag cards within or between Backlog and Ready, or use task actions to change queue and order.';

/** Issue pages read per board load. 100 issues per page keeps one load to at most 1000 issues. */
const ISSUE_PAGE_SIZE = 100;
const ISSUE_PAGE_LIMIT = 10;

/**
 * Picks the column for one task. Archived tasks are not shown. A closed task
 * goes to Done whatever its queue state, because closing ends the work. Open
 * tasks follow their queue state, and `claimed` shows as Active.
 */
export function boardColumnFor(task: BacklogTaskSnapshot): BoardColumnId | null {
  if (task.isArchived) return null;
  if (task.lifecycle === 'closed') return 'done';
  if (task.state === 'claimed') return 'active';
  return task.state === 'ready' ? 'ready' : 'backlog';
}

/** Groups the graph's tasks into the fixed board columns, keeping the graph's task order. */
export function boardColumns(graph: BacklogGraph): BoardColumn[] {
  const columns = BOARD_COLUMNS.map((column) => ({ ...column, tasks: [] as BacklogTaskSnapshot[] }));
  for (const task of graph.tasks) {
    const id = boardColumnFor(task);
    // A null column means the task is hidden, for example because it is archived.
    if (id) columns.find((column) => column.id === id)?.tasks.push(task);
  }
  for (const column of columns)
    column.tasks.sort((left, right) =>
      left.position - right.position || left.reference.taskId.localeCompare(right.reference.taskId));
  return columns;
}

/** Returns the insertion index before or after a visible neighbor in the full persisted state queue. */
export function queueInsertionPosition(
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  neighbor: BacklogTaskSnapshot | undefined,
  placement: 'before' | 'after',
): number | null {
  if (!neighbor || neighbor.state !== task.state) return null;
  const queue = graph.tasks
    .filter((candidate) => candidate.state === task.state &&
      candidate.reference.taskId !== task.reference.taskId)
    .sort((left, right) =>
      left.position - right.position || left.reference.taskId.localeCompare(right.reference.taskId));
  const neighborIndex = queue.findIndex((candidate) =>
    candidate.reference.taskId === neighbor.reference.taskId);
  return neighborIndex < 0 ? null : neighborIndex + (placement === 'after' ? 1 : 0);
}

/** Finds a drop position in the target queue after excluding the moved task. */
export function queueDropPosition(
  graph: BacklogGraph,
  taskId: string,
  targetState: Exclude<BacklogTaskState, 'claimed'>,
  neighborTaskId: string | null,
  placement: 'before' | 'after',
): number | null {
  const queue = graph.tasks
    .filter((task) => task.state === targetState && task.reference.taskId !== taskId)
    .sort((left, right) =>
      left.position - right.position || left.reference.taskId.localeCompare(right.reference.taskId));
  if (neighborTaskId === null) return queue.length;
  const neighborIndex = queue.findIndex((task) => task.reference.taskId === neighborTaskId);
  return neighborIndex < 0 ? null : neighborIndex + (placement === 'after' ? 1 : 0);
}

/**
 * Lists the titles of tasks that must finish before `taskId` can start. A
 * prerequisite missing from the graph is shown by its count only, so the user
 * never sees a raw task ID.
 */
export function prerequisiteTitles(graph: BacklogGraph, taskId: string): { titles: string[]; unknown: number } {
  const titles: string[] = [];
  let unknown = 0;
  for (const edge of graph.dependencies) {
    if (edge.task.taskId !== taskId) continue;
    const prerequisite = graph.tasks.find((task) => task.reference.taskId === edge.prerequisite.taskId);
    if (prerequisite) titles.push(prerequisite.title);
    else unknown += 1;
  }
  return { titles, unknown };
}

/**
 * Creates the client-chosen ID for a new task. The service accepts IDs that
 * start with a letter or digit and use only letters, digits, `-`, `_`, `.`
 * and `:`. A UUID matches that rule. A fallback covers browsers without
 * `crypto.randomUUID`, such as pages served over plain HTTP.
 */
export function newBacklogTaskId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return `task-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Codes of the reasons in a rejected backlog write, normalized to lowercase without separators. */
function backlogIssueCodes(error: unknown): string[] {
  if (!(error instanceof GatewayError) || !Array.isArray(error.problem?.issues)) return [];
  return error.problem.issues
    .map((issue: unknown) => (issue && typeof issue === 'object' ? (issue as { code?: unknown }).code : undefined))
    .filter((code): code is string => typeof code === 'string')
    .map((code) => code.toLowerCase().replace(/[^a-z]/g, ''));
}

/** True when the write failed only because another write changed the graph or task first. */
export function isStaleRevision(error: unknown): boolean {
  if (!(error instanceof GatewayError) || error.status !== 409) return false;
  const codes = backlogIssueCodes(error);
  return codes.length > 0 && codes.every((code) => code === 'stalegraphrevision' || code === 'staletaskrevision');
}

/**
 * Turns a Board failure into plain copy. 403 explains the missing access for
 * the action. 409 explains the common conflicts. Other failures keep the
 * status and message, which helps when someone reports a problem.
 */
export function boardErrorMessage(error: unknown, action: 'read' | 'capture' | 'edit' | 'mutate' | 'delete'): string {
  if (error instanceof BoardScopeChangedError && action === 'delete')
    return 'Your sign-in or authorization context changed. Review the board before deleting this task.';
  if (error instanceof GatewayError && error.status === 401 && action === 'delete')
    return 'Your sign-in may have expired. Sign in again and review the task before deleting it.';
  if (error instanceof GatewayError && error.status === 403) {
    return action === 'read'
      ? "You don't have permission to view this project's board."
      : action === 'delete'
        ? "You don't have permission to delete tasks in this project."
      : "You don't have permission to change tasks in this project.";
  }
  if (error instanceof GatewayError && error.status === 409) {
    const codes = backlogIssueCodes(error);
    if (action === 'delete' && codes.includes('taskhasdependents'))
      return 'Another task depends on this task. Remove its prerequisite link before deleting it.';
    if (action === 'delete' && codes.includes('taskdeletionunavailable'))
      return error.message || 'This task cannot be deleted while linked work is being preserved.';
    if (action === 'delete' && codes.includes('tasknoteditable'))
      return 'This task can no longer be deleted. It may be claimed, archived, or held by automation.';
    if (codes.includes('tasknoteditable'))
      return action === 'edit'
        ? 'This task can no longer be edited. It may be archived or waiting for an automation.'
        : 'This task can no longer be changed. It may be archived, claimed, closed, or waiting for an automation.';
    if (codes.includes('duplicaterepositoryissue'))
      return 'Another task already tracks that repository issue.';
    if (isStaleRevision(error))
      return 'The board changed while you were working. It has been reloaded; try again.';
  }
  if (error instanceof GatewayError && error.status === 404 && (action === 'edit' || action === 'mutate' || action === 'delete'))
    return action === 'delete'
      ? 'This task no longer exists. The board has been reloaded.'
      : 'This task no longer exists. The board has been reloaded.';
  if (error instanceof GatewayError && error.status === 503) {
    if (action === 'delete')
      return 'Task deletion is unavailable right now. The task has not been removed. Try again later.';
    if (action === 'mutate')
      return 'Queue ordering is unavailable right now. Try again after the service update.';
  }
  if (action === 'delete')
    return `Task deletion failed. The task has not been removed. ${errorMessage(error)}`;
  return errorMessage(error);
}

/** Board load state for one project. */
export interface ProjectBoardData {
  graph: BacklogGraph | null;
  /** Synced issues keyed by the task that tracks them. */
  issues: ReadonlyMap<string, ProjectIssueSummary>;
  loading: boolean;
  /** Backlog read failure. The board can't render without the graph. */
  error: unknown;
  /** Issue read failure. Cards still render, without issue details. */
  issuesError: unknown;
  /** True when the issue list had more pages than one load reads. */
  issuesTruncated: boolean;
  reload: () => void;
  /** Shows a graph returned by a successful write without another read. */
  replaceGraph: (graph: BacklogGraph) => void;
}

interface BoardState {
  /** The project this state belongs to. State for another project is ignored. */
  projectId: string;
  /** The authorization/session context this state belongs to. */
  scopeIdentity: object | null;
  graph: BacklogGraph | null;
  issues: ReadonlyMap<string, ProjectIssueSummary>;
  error: unknown;
  issuesError: unknown;
  issuesTruncated: boolean;
  settled: boolean;
}

const EMPTY_ISSUES: ReadonlyMap<string, ProjectIssueSummary> = new Map();

/**
 * Loads the backlog graph and every synced issue page for one project. The
 * graph and the issues load in parallel. An issue failure does not hide the
 * board, because issues only add details to cards. Responses for an older
 * project or an older reload are dropped.
 */
export function useProjectBoard(
  projectId: string,
  reloadKey?: unknown,
  scope?: BoardRequestScope,
  isScopeCurrent?: IsBoardScopeCurrent,
): ProjectBoardData {
  const { apiCall } = useAuth();
  const requestScope = scope;
  const [state, setState] = useState<BoardState>({
    projectId: '', scopeIdentity: null, graph: null, issues: EMPTY_ISSUES, error: null, issuesError: null, issuesTruncated: false, settled: false,
  });
  // Bumped by `reload` to read again.
  const [generation, setGeneration] = useState(0);
  const readSequence = useRef(0);

  useEffect(() => {
    if (!projectId) return;
    const sequence = ++readSequence.current;
    // Cleared on cleanup so a late response for an old project or reload is dropped.
    let active = true;
    let scopeChanged = false;
    const read = <T,>(operation: (token: string, tenant: string | null) => Promise<T>) =>
      apiCall((token, tenant) => {
        if (requestScope) {
          try {
            if (!active) throw new BoardScopeChangedError();
            assertBoardScope(requestScope, isScopeCurrent ?? (() => true), token, tenant);
          } catch (reason) {
            scopeChanged = true;
            throw reason;
          }
        }
        return operation(token, tenant);
      }, null);
    const timer = window.setTimeout(() => {
      const graphRead = read((token, tenant) => gatewayClient.readProjectBacklog(token, projectId, tenant));
      // Read issue pages one after another, because each page needs the cursor of
      // the one before it. Stop at the page limit so a huge project can't stall the board.
      const issueRead = (async () => {
        const byTask = new Map<string, ProjectIssueSummary>();
        let cursor: string | null = null;
        for (let page = 0; page < ISSUE_PAGE_LIMIT; page += 1) {
          const current: string | null = cursor;
          const result: ProjectIssuePage = await read((token, tenant) => gatewayClient.listProjectIssues(
            token, projectId, { state: 'all', cursor: current, pageSize: ISSUE_PAGE_SIZE }, tenant));
          for (const issue of result.items) byTask.set(issue.taskId, issue);
          cursor = result.nextCursor;
          if (!cursor) return { byTask, truncated: false };
        }
        return { byTask, truncated: true };
      })();
      void Promise.allSettled([graphRead, issueRead]).then(([graph, issues]) => {
        if (!active || sequence !== readSequence.current || scopeChanged
          || requestScope && isScopeCurrent && !isScopeCurrent(requestScope)) return;
        setState((previous) => {
          // On a failed reload, keep the last good graph for this project and show the error with it.
          const sameProject = previous.projectId === projectId
            && previous.scopeIdentity === (requestScope?.identity ?? null);
          return {
            projectId,
            scopeIdentity: requestScope?.identity ?? null,
            graph: graph.status === 'fulfilled' ? graph.value : sameProject ? previous.graph : null,
            issues: issues.status === 'fulfilled' ? issues.value.byTask : sameProject ? previous.issues : EMPTY_ISSUES,
            error: graph.status === 'rejected' ? graph.reason : null,
            issuesError: issues.status === 'rejected' ? issues.reason : null,
            issuesTruncated: issues.status === 'fulfilled' ? issues.value.truncated : false,
            settled: true,
          };
        });
      });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [apiCall, projectId, generation, reloadKey, requestScope, isScopeCurrent]);

  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const replaceGraph = useCallback((graph: BacklogGraph) => {
    // Accept only a graph for the current project and authorization scope.
    if (graph.projectId !== projectId || requestScope && isScopeCurrent && !isScopeCurrent(requestScope)) return;
    // An authoritative write wins over any earlier read in the same scope.
    readSequence.current += 1;
    setState((previous) => {
      if (previous.scopeIdentity !== (requestScope?.identity ?? null) || graph.projectId !== projectId) return previous;
      if (previous.graph && previous.graph.revision > graph.revision) return previous;
      return { ...previous, projectId, scopeIdentity: requestScope?.identity ?? null, graph, error: null, settled: true };
    });
  }, [isScopeCurrent, projectId, requestScope]);

  const current = state.projectId === projectId
    && state.scopeIdentity === (requestScope?.identity ?? null)
    && (!requestScope || !isScopeCurrent || isScopeCurrent(requestScope));
  return {
    graph: current ? state.graph : null,
    issues: current ? state.issues : EMPTY_ISSUES,
    loading: !!projectId && (!current || !state.settled),
    error: current ? state.error : null,
    issuesError: current ? state.issuesError : null,
    issuesTruncated: current ? state.issuesTruncated : false,
    reload,
    replaceGraph,
  };
}

type ApiCall = ReturnType<typeof useAuth>['apiCall'];

/**
 * Captures one titled task into the project backlog and returns the new graph.
 * The write needs the current graph revision. When the caller has none, the
 * graph is read first. If another write lands in between, the service rejects
 * the stale revision; the graph is read again and the same request is sent
 * once more with the same task ID, so the task can't be added twice.
 */
export async function captureTask(
  apiCall: ApiCall,
  projectId: string,
  title: string,
  knownRevision?: number,
): Promise<BacklogGraph> {
  const taskId = newBacklogTaskId();
  const send = (expectedGraphRevision: number) => apiCall((token, tenant) => gatewayClient.captureBacklogTask(
    token, projectId, { taskId, content: { title }, expectedGraphRevision }, tenant), null);
  const readRevision = async () => (await apiCall(
    (token, tenant) => gatewayClient.readProjectBacklog(token, projectId, tenant), null)).revision;
  const revision = knownRevision ?? await readRevision();
  try {
    return await send(revision);
  } catch (error) {
    if (!isStaleRevision(error)) throw error;
    return await send(await readRevision());
  }
}

/**
 * Replaces a task's title and description. Lifecycle and the linked issue are
 * sent back unchanged, because the write replaces the whole content and the
 * Board does not edit them. Both revisions come from the graph the user saw,
 * so a change made by someone else in between is rejected, not overwritten.
 */
export function editTaskContent(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  content: { title: string; description: string | null },
): Promise<BacklogGraph> {
  return apiCall((token, tenant) => gatewayClient.editBacklogTaskContent(token, graph.projectId, task.reference.taskId, {
    content: {
      title: content.title,
      description: content.description,
      lifecycle: task.lifecycle,
      repositoryIssue: task.repositoryIssue ?? null,
    },
    expectedGraphRevision: graph.revision,
    expectedTaskRevision: task.revision,
  }, tenant), null);
}

/** Moves an unclaimed task between Backlog and Ready using both observed revisions. */
export function setTaskState(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  state: Exclude<BacklogTaskState, 'claimed'>,
): Promise<BacklogGraph> {
  return apiCall((token, tenant) => gatewayClient.setProjectBacklogTaskState(
    token, graph.projectId, task.reference.taskId, {
      state,
      expectedGraphRevision: graph.revision,
      expectedTaskRevision: task.revision,
    }, tenant), null);
}

/** Places one task in a backlog or ready queue using both observed revisions. */
export function setTaskPosition(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  state: Exclude<BacklogTaskState, 'claimed'>,
  position: number,
  scope?: BoardRequestScope,
  isScopeCurrent?: IsBoardScopeCurrent,
): Promise<BacklogGraph> {
  const request: SetBacklogTaskPositionRequest = {
    state,
    position,
    expectedGraphRevision: graph.revision,
    expectedTaskRevision: task.revision,
  };
  return apiCall((token, tenant) => {
    if (scope && isScopeCurrent) assertBoardScope(scope, isScopeCurrent, token, tenant);
    return gatewayClient.setProjectBacklogTaskPosition(
      token, graph.projectId, task.reference.taskId, request, tenant);
  }, null);
}

    /** Moves all eligible Backlog tasks to Ready at the graph revision the user read. */
    export function setAllEligibleTasksReady(
      apiCall: ApiCall,
      graph: BacklogGraph,
    ): Promise<BacklogGraph> {
      const request: BacklogGraphRevisionRequest = { expectedGraphRevision: graph.revision };
      return apiCall((token, tenant) => gatewayClient.setAllProjectBacklogTasksReady(
        token, graph.projectId, request, tenant), null);
    }

/** Archives one task using both observed revisions. */
export function archiveTask(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
): Promise<BacklogGraph> {
  const request: BacklogTaskRevisionRequest = {
    expectedGraphRevision: graph.revision,
    expectedTaskRevision: task.revision,
  };
  return apiCall((token, tenant) => gatewayClient.archiveProjectBacklogTask(
    token, graph.projectId, task.reference.taskId, request, tenant), null);
}

/** Deletes one task using the graph and task revisions the user confirmed. */
export function deleteBacklogTask(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  scope: BoardRequestScope,
  isScopeCurrent: IsBoardScopeCurrent,
): Promise<BacklogGraph> {
  const request: BacklogTaskRevisionRequest = {
    expectedGraphRevision: graph.revision,
    expectedTaskRevision: task.revision,
  };
  return apiCall((token, tenant) => {
    assertBoardScope(scope, isScopeCurrent, token, tenant);
    return gatewayClient.deleteProjectBacklogTask(
      token, graph.projectId, task.reference.taskId, request, tenant,
    );
  }, null);
}

/** Adds a prerequisite edge at the graph revision the user read. */
export function addPrerequisite(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  prerequisiteTaskId: string,
): Promise<BacklogGraph> {
  return apiCall((token, tenant) => gatewayClient.addProjectBacklogDependency(
    token, graph.projectId, task.reference.taskId, prerequisiteTaskId, {
      expectedGraphRevision: graph.revision,
    }, tenant), null);
}

/** Removes a prerequisite edge at the graph revision the user read. */
export function removePrerequisite(
  apiCall: ApiCall,
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  prerequisiteTaskId: string,
): Promise<BacklogGraph> {
  return apiCall((token, tenant) => gatewayClient.removeProjectBacklogDependency(
    token, graph.projectId, task.reference.taskId, prerequisiteTaskId, {
      expectedGraphRevision: graph.revision,
    }, tenant), null);
}
