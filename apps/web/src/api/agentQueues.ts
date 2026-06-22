import type { AgentQueueDto, CoordinatorChildResponse, WorkPlanResponse } from './types';

// Locked component contract — Phase 2 will feed the same shape from a backend DTO.
export type AgentQueueItem = {
  agentName: string;
  active: number;
  queued: number;
  blocked: number;
  done: number;
  runIds?: string[];
  sampleTitles?: string[];
};

/**
 * Maps a subtask status string to a load bucket.
 *
 * active  — work is currently dispatched / running.
 * queued  — assigned but not yet started; dependency-blocked assigned tasks are
 *           still "queued" (they will run once their dependency resolves).
 * blocked — failed or RAI-flagged (needs human intervention).
 * done    — completed or ready for collective assembly.
 */
export function subtaskStatusToBucket(status: string): 'active' | 'queued' | 'blocked' | 'done' {
  switch (status.toLowerCase()) {
    case 'dispatched':
    case 'running':
    case 'in_progress':
      return 'active';
    case 'completed':
    case 'assemble_ready':
    case 'merged':
      return 'done';
    case 'failed':
    case 'rai_flagged':
      return 'blocked';
    default:
      // 'pending' + anything else = queued (not yet started)
      return 'queued';
  }
}

/**
 * Maps a snake_case AgentQueueDto (from the board API) to the camelCase
 * AgentQueueItem used by the AgentRail component.
 */
export function fromDto(dto: AgentQueueDto): AgentQueueItem {
  return {
    agentName:    dto.agent_name,
    active:       dto.active,
    queued:       dto.queued,
    blocked:      dto.blocked,
    done:         dto.done,
    runIds:       dto.run_ids,
    sampleTitles: dto.sample_titles,
  };
}

/**
 * Derives per-agent load items from a coordinator work-plan + children snapshot.
 *
 * Children are overlaid on top of the work-plan subtask status because they carry
 * the most current server-side status (subtask.* SSE deltas are already applied
 * server-side before the children endpoint responds).
 *
 * sampleTitles: up to 3 subtask titles per agent (for tooltip/preview use).
 * runIds: the current coordinator run id (Phase 2 will aggregate across runs).
 */
export function deriveAgentQueues(
  workPlan: WorkPlanResponse,
  children: CoordinatorChildResponse[],
  runId: string,
): AgentQueueItem[] {
  const childBySubtaskId = new Map<number, CoordinatorChildResponse>();
  for (const child of children) {
    childBySubtaskId.set(child.subtaskId, child);
  }

  const map = new Map<string, { active: number; queued: number; blocked: number; done: number; titles: string[] }>();

  for (const subtask of workPlan.subtasks) {
    const agentName = subtask.assignedAgent || 'Unassigned';
    const child = childBySubtaskId.get(subtask.subtaskId);
    // Children carry the more-current status; fall back to the work-plan status.
    const status = child?.subtaskStatus ?? subtask.status;
    const bucket = subtaskStatusToBucket(status);

    if (!map.has(agentName)) {
      map.set(agentName, { active: 0, queued: 0, blocked: 0, done: 0, titles: [] });
    }
    const entry = map.get(agentName)!;
    entry[bucket]++;
    if (entry.titles.length < 3) entry.titles.push(subtask.title);
  }

  const result: AgentQueueItem[] = [];
  for (const [agentName, counts] of map) {
    result.push({
      agentName,
      active:  counts.active,
      queued:  counts.queued,
      blocked: counts.blocked,
      done:    counts.done,
      runIds:      [runId],
      sampleTitles: counts.titles,
    });
  }

  // Most-active agents first; ties broken by queued, then blocked.
  result.sort(
    (a, b) =>
      (b.active * 4 + b.queued * 2 + b.blocked) -
      (a.active * 4 + a.queued * 2 + a.blocked),
  );
  return result;
}
