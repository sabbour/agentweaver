import { describe, it, expect } from 'vitest';
import { buildTopologyState } from '../state/topologyReducer';
import type { RunStreamEvent } from '../api/sse';

function makeEvent(
  type: RunStreamEvent['type'],
  payload: Record<string, unknown>,
  seq = 0,
): RunStreamEvent {
  return { sequence: seq, type, payload };
}

const SNAPSHOT = makeEvent('coordinator.topology', {
  version: 1,
  seq: 0,
  nodes: [
    { id: 'coord', kind: 'coordinator', title: 'Coordinator', status: 'running' },
    { id: 's1', kind: 'subtask', title: 'Build API', status: 'pending', assignedAgent: 'Neo', selectedModelId: 'gpt-5' },
    { id: 's2', kind: 'subtask', title: 'Build UI', status: 'pending' },
  ],
  edges: [{ from: 's1', to: 's2' }],
}, 1);

describe('topologyReducer', () => {
  it('applies a snapshot establishing nodes and edges', () => {
    const state = buildTopologyState([SNAPSHOT]);
    expect(state.hasSnapshot).toBe(true);
    expect(state.nodeOrder).toEqual(['coord', 's1', 's2']);
    expect(state.nodes['s1'].assignedAgent).toBe('Neo');
    expect(state.edges).toEqual([{ from: 's1', to: 's2' }]);
  });

  it('merges a delta by node id and ignores stale seq', () => {
    const delta = makeEvent('coordinator.topology', {
      version: 1, seq: 1, changed: [{ id: 's1', status: 'running' }],
    }, 2);
    const stale = makeEvent('coordinator.topology', {
      version: 1, seq: 1, changed: [{ id: 's1', status: 'pending' }],
    }, 3);
    const state = buildTopologyState([SNAPSHOT, delta, stale]);
    expect(state.nodes['s1'].status).toBe('running');
    // title preserved from snapshot (delta only changed status)
    expect(state.nodes['s1'].title).toBe('Build API');
  });

  it('merges subtask.* events by subtaskId and attaches childRunId', () => {
    const dispatched = makeEvent('subtask.dispatched', { subtaskId: 's2', childRunId: 'child-42' }, 2);
    const completed = makeEvent('subtask.completed', { subtaskId: 's2' }, 3);
    const state = buildTopologyState([SNAPSHOT, dispatched, completed]);
    expect(state.nodes['s2'].childRunId).toBe('child-42');
    expect(state.nodes['s2'].status).toBe('completed');
  });

  it('attaches steering to the node matched by targetChildRunId', () => {
    const dispatched = makeEvent('subtask.dispatched', { subtaskId: 's1', childRunId: 'child-1' }, 2);
    const steering = makeEvent('coordinator.steering', {
      directiveId: 'd1', kind: 'redirect', targetChildRunId: 'child-1', status: 'applied', instruction: 'use v2',
    }, 3);
    const state = buildTopologyState([SNAPSHOT, dispatched, steering]);
    expect(state.nodes['s1'].steering).toEqual({
      directiveId: 'd1', kind: 'redirect', status: 'applied', instruction: 'use v2',
    });
  });

  it('attaches a target-less steering directive to the coordinator node', () => {
    const steering = makeEvent('coordinator.steering', {
      directiveId: 'd2', kind: 'stop', status: 'requested',
    }, 2);
    const state = buildTopologyState([SNAPSHOT, steering]);
    expect(state.nodes['coord'].steering?.kind).toBe('stop');
  });

  it('captures the work plan', () => {
    const plan = makeEvent('coordinator.work_plan', {
      workPlanId: 'wp1', status: 'ready',
      subtasks: [{ id: 1, title: 'Build API', dependsOn: [] }],
    }, 1);
    const state = buildTopologyState([plan]);
    expect(state.workPlan?.workPlanId).toBe('wp1');
    expect(state.workPlan?.subtasks).toHaveLength(1);
  });

  // The backend actually emits label/agent/model and node ids like "subtask-5",
  // while subtask.* events carry a numeric subtaskId. The reducer must tolerate both.
  it('reads backend field names (label/agent/model) and matches subtask-{id} ids', () => {
    const snapshot = makeEvent('coordinator.topology', {
      version: 1, kind: 'snapshot', seq: 0,
      nodes: [
        { id: 'coordinator', kind: 'coordinator', label: 'Coordinator', status: 'dispatching' },
        { id: 'subtask-5', kind: 'subtask', label: 'Build API', status: 'pending', agent: 'morpheus', model: 'gpt-4o' },
      ],
      edges: [],
    }, 1);
    const running = makeEvent('subtask.running', {
      subtaskId: 5, childRunId: 'guid-5', assignedAgent: 'morpheus', selectedModelId: 'gpt-4o', status: 'running',
    }, 2);
    const state = buildTopologyState([snapshot, running]);
    // node read via label/agent/model
    expect(state.nodes['subtask-5'].title).toBe('Build API');
    expect(state.nodes['subtask-5'].assignedAgent).toBe('morpheus');
    expect(state.nodes['subtask-5'].selectedModelId).toBe('gpt-4o');
    // subtask.running (subtaskId 5) resolved to node "subtask-5" — no phantom node "5"
    expect(state.nodes['5']).toBeUndefined();
    expect(state.nodes['subtask-5'].status).toBe('running');
    expect(state.nodes['subtask-5'].childRunId).toBe('guid-5');
    expect(state.nodeOrder).toEqual(['coordinator', 'subtask-5']);
  });

  it('stores executionPodName from snapshot nodes', () => {
    const snapshot = makeEvent('coordinator.topology', {
      version: 1, seq: 0,
      nodes: [
        { id: 'coordinator', kind: 'coordinator', title: 'Coordinator', status: 'running', executionPodName: 'api-pod-abc' },
        { id: 's1', kind: 'subtask', title: 'Build API', status: 'pending' },
      ],
      edges: [],
    }, 1);
    const state = buildTopologyState([snapshot]);
    expect(state.nodes['coordinator'].executionPodName).toBe('api-pod-abc');
    // Node without executionPodName stays undefined
    expect(state.nodes['s1'].executionPodName).toBeUndefined();
  });

  it('stores executionPodName from coordinator.topology delta', () => {
    const delta = makeEvent('coordinator.topology', {
      version: 1, seq: 1, changed: [{ id: 's1', status: 'running', executionPodName: 'agent-pod-worker-7' }],
    }, 2);
    const state = buildTopologyState([SNAPSHOT, delta]);
    expect(state.nodes['s1'].executionPodName).toBe('agent-pod-worker-7');
    // Other nodes are unaffected
    expect(state.nodes['s2'].executionPodName).toBeUndefined();
  });

  it('stores executionPodName from subtask.* events', () => {
    const running = makeEvent('subtask.running', {
      subtaskId: 's1', status: 'running', executionPodName: 'agent-pod-worker-3',
    }, 2);
    const state = buildTopologyState([SNAPSHOT, running]);
    expect(state.nodes['s1'].executionPodName).toBe('agent-pod-worker-3');
  });

  it('executionPodName=null from subtask event is stored as null (explicit null overrides)', () => {
    const running = makeEvent('subtask.running', {
      subtaskId: 's1', status: 'running', executionPodName: null,
    }, 2);
    const state = buildTopologyState([SNAPSHOT, running]);
    expect(state.nodes['s1'].executionPodName).toBeNull();
  });

  it('preserves executionPodName across subsequent merges when not present in patch', () => {
    const delta1 = makeEvent('coordinator.topology', {
      version: 1, seq: 1, changed: [{ id: 's1', status: 'running', executionPodName: 'agent-pod-worker-3' }],
    }, 2);
    const delta2 = makeEvent('coordinator.topology', {
      version: 1, seq: 2, changed: [{ id: 's1', status: 'completed' }],
    }, 3);
    const state = buildTopologyState([SNAPSHOT, delta1, delta2]);
    // executionPodName was set in delta1 and should survive delta2 which doesn't include it
    expect(state.nodes['s1'].executionPodName).toBe('agent-pod-worker-3');
    expect(state.nodes['s1'].status).toBe('completed');
  });
});
