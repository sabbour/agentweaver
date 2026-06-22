import type { AgentQueueDto, BoardDto } from '../../api/types';

// Minimal board fixture mirroring GET /api/projects/{id}/board (design 5.7).
// Backlog + Ready intake columns first, then server-ordered workflow columns
// (including an extra/custom stage to prove dynamic rendering — FR-015/SC-004).
export function makeBoard(overrides?: Partial<BoardDto>): BoardDto {
  return {
    project_id: 'proj-1',
    workflow_stages_available: true,
    columns: [
      {
        id: 'backlog',
        kind: 'intake',
        label: 'Backlog',
        cards: [
          { kind: 'task', task_id: 't1', title: 'First backlog task', description: null, state: 'backlog', order_key: 'a', captured_by: 'alice', created_at: '2026-01-01T00:00:00Z' },
          { kind: 'task', task_id: 't2', title: 'Second backlog task', description: 'with details', state: 'backlog', order_key: 'b', captured_by: 'alice', created_at: '2026-01-01T00:01:00Z' },
        ],
      },
      {
        id: 'ready',
        kind: 'intake',
        label: 'Ready',
        cards: [
          { kind: 'task', task_id: 't3', title: 'Ready task', description: null, state: 'ready', order_key: 'a', captured_by: 'bob', created_at: '2026-01-01T00:02:00Z' },
        ],
      },
      {
        id: 'coordinator',
        kind: 'workflow',
        label: 'Coordinator',
        cards: [
          { kind: 'run', run_id: 'r1', workflow_run_id: 'wr1', backlog_task_id: 't9', task: 'Run-backed work', status: 'in_progress', work_plan_status: 'planned', assembly_stage: null, stage_id: 'coordinator', agent_name: 'Coordinator', started_at: '2026-01-01T00:03:00Z' },
        ],
      },
      {
        id: 'planned:assembly-custom',
        kind: 'workflow',
        label: 'Custom Stage',
        cards: [],
      },
      {
        id: 'terminal',
        kind: 'workflow',
        label: 'Done',
        cards: [],
        collapsed_count: 5,
      },
    ],
    ...overrides,
  };
}

// FR-019: workflow unavailable — only the two intake columns are returned.
export function makeBoardWorkflowUnavailable(): BoardDto {
  return {
    project_id: 'proj-1',
    workflow_stages_available: false,
    columns: [
      { id: 'backlog', kind: 'intake', label: 'Backlog', cards: [] },
      { id: 'ready', kind: 'intake', label: 'Ready', cards: [] },
    ],
  };
}

// Board fixture with agent_queues for Phase 2 AgentRail tests.
export function makeAgentQueueDto(overrides?: Partial<AgentQueueDto>): AgentQueueDto {
  return {
    agent_name:    'Neo',
    active:        2,
    queued:        1,
    blocked:       0,
    done:          3,
    run_ids:       ['r1'],
    sample_titles: ['Task A', 'Task B'],
    ...overrides,
  };
}
