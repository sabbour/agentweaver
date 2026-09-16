import { deriveAgentQueues, fromDto, subtaskStatusToBucket } from '../api/agentQueues';
import { describe, expect, it } from 'vitest';
import type {
  AgentQueueDto,
  CoordinatorChildResponse,
  WorkPlanResponse,
  WorkPlanSubtaskResponse,
} from '../api/types';

describe('agent queue mapping', () => {
  it('maps the board response to the web model', () => {
    const dto: AgentQueueDto = {
      agent_name: 'Trinity',
      active: 3,
      queued: 2,
      blocked: 1,
      done: 5,
      run_ids: ['run-1', 'run-2'],
      sample_titles: ['Fix auth', 'Add tests'],
      orchestrations: [],
    };

    expect(fromDto(dto)).toEqual({
      agentName: 'Trinity',
      active: 3,
      queued: 2,
      blocked: 1,
      done: 5,
      runIds: ['run-1', 'run-2'],
      sampleTitles: ['Fix auth', 'Add tests'],
      orchestrations: [],
    });
  });

  it.each([
    ['dispatched', 'active'],
    ['running', 'active'],
    ['in_progress', 'active'],
    ['completed', 'done'],
    ['assemble_ready', 'done'],
    ['merged', 'done'],
    ['failed', 'blocked'],
    ['rai_flagged', 'blocked'],
    ['pending', 'queued'],
    ['unknown_status', 'queued'],
  ] as const)('maps %s to %s', (status, expected) => {
    expect(subtaskStatusToBucket(status)).toBe(expected);
  });

  it('uses child status when deriving queue counts', () => {
    const workPlan: WorkPlanResponse = {
      workPlanId: 1,
      coordinatorRunId: 'run-1',
      outcomeSpecId: 1,
      status: 'in_progress',
      dependencies: [],
      subtasks: [
        makeSubtask({ subtaskId: 1, assignedAgent: 'Neo', status: 'pending', title: 'Task A' }),
        makeSubtask({ subtaskId: 2, assignedAgent: 'Neo', status: 'completed', title: 'Task B' }),
        makeSubtask({ subtaskId: 3, assignedAgent: 'Trinity', status: 'failed', title: 'Task C' }),
      ],
    };
    const children = [
      { subtaskId: 1, subtaskStatus: 'running' } as CoordinatorChildResponse,
    ];

    expect(deriveAgentQueues(workPlan, children, 'run-1')).toEqual([
      {
        agentName: 'Neo',
        active: 1,
        queued: 0,
        blocked: 0,
        done: 1,
        runIds: ['run-1'],
        sampleTitles: ['Task A', 'Task B'],
        orchestrations: [{
          runId: 'run-1',
          title: null,
          active: 1,
          queued: 0,
          blocked: 0,
          done: 1,
          sampleTitles: ['Task A', 'Task B'],
        }],
      },
      {
        agentName: 'Trinity',
        active: 0,
        queued: 0,
        blocked: 1,
        done: 0,
        runIds: ['run-1'],
        sampleTitles: ['Task C'],
        orchestrations: [{
          runId: 'run-1',
          title: null,
          active: 0,
          queued: 0,
          blocked: 1,
          done: 0,
          sampleTitles: ['Task C'],
        }],
      },
    ]);
  });
});

function makeSubtask(
  values: Partial<WorkPlanSubtaskResponse>
    & Pick<WorkPlanSubtaskResponse, 'subtaskId' | 'title' | 'assignedAgent' | 'status'>,
): WorkPlanSubtaskResponse {
  return {
    scope: 'feature',
    selectedModelId: 'gpt-4',
    phase: 'coding',
    isolation: 'branch',
    ...values,
  };
}
