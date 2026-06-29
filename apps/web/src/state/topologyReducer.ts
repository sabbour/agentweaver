/**
 * Pure reducer for the coordinator dynamic topology view (Feature 008 Phase 2).
 *
 * Principle III (thin client): this reducer performs NO topology computation.
 * It applies the server-authored `coordinator.topology` SNAPSHOT (seq 0), then
 * merges `coordinator.topology` DELTAS, `subtask.*` updates, `coordinator.work_plan`
 * and `coordinator.steering` directives — all keyed by node id. Components render
 * purely from the accumulated state; they never derive dependencies or status.
 */
import type { RunStreamEvent } from '../api/sse';
import type {
  CoordinatorChildResponse,
  CoordinatorWorkPlan,
  SteerKind,
  TopologyEdge,
  TopologyNode,
  WorkPlanResponse,
} from '../api/types';

export interface NodeSteering {
  directiveId: string;
  kind: SteerKind;
  status: string;
  instruction?: string;
}

export type TopologyNodeState = TopologyNode & { steering?: NodeSteering };

export interface CoordinatorTopologyState {
  hasSnapshot: boolean;
  version: number;
  /** Last applied topology seq (snapshot is 0, deltas are monotonic > 0). */
  topoSeq: number;
  /** Node ids in first-seen order so layout is stable. */
  nodeOrder: string[];
  nodes: Record<string, TopologyNodeState>;
  edges: TopologyEdge[];
  workPlan?: CoordinatorWorkPlan;
}

export const initialTopologyState: CoordinatorTopologyState = {
  hasSnapshot: false,
  version: 0,
  topoSeq: -1,
  nodeOrder: [],
  nodes: {},
  edges: [],
};

// Map a subtask.* event type to its implied status when the payload omits one.
const SUBTASK_STATUS_FROM_TYPE: Record<string, string> = {
  'subtask.dispatched': 'dispatched',
  'subtask.running': 'running',
  'subtask.assemble_ready': 'assemble_ready',
  'subtask.rai_flagged': 'rai_flagged',
  'subtask.completed': 'completed',
  'subtask.failed': 'failed',
  'subtask.pending_capacity': 'pending_capacity',
};

function str(value: unknown): string | undefined {
  return value != null ? String(value) : undefined;
}

// Read a node's display fields, tolerating both the field names in the Phase 2
// brief (title/assignedAgent/selectedModelId) and the names the backend actually
// emits (label/agent/model). Thin client — we surface whatever the server sends.
function readNodeFields(raw: Record<string, unknown>): Partial<TopologyNodeState> {
  return {
    kind: raw['kind'] === 'coordinator' ? 'coordinator' : raw['kind'] === 'subtask' ? 'subtask' : undefined,
    title: str(raw['title']) ?? str(raw['label']),
    status: str(raw['status']),
    assignedAgent: str(raw['assignedAgent']) ?? str(raw['agent']),
    selectedModelId: str(raw['selectedModelId']) ?? str(raw['model']),
    childRunId: str(raw['childRunId']),
    // Defensive read: null today (single-pod), non-null after spec-018 distributed phases.
    executionPodName: raw['executionPodName'] !== undefined
      ? (raw['executionPodName'] === null ? null : str(raw['executionPodName']) ?? null)
      : undefined,
  };
}

// Merge a partial node patch into the state by id, preserving prior fields and order.
function mergeNode(
  state: CoordinatorTopologyState,
  id: string,
  patch: Partial<TopologyNodeState>,
  defaults: Partial<TopologyNodeState> = {},
): CoordinatorTopologyState {
  const prev = state.nodes[id];
  const nodeOrder = prev ? state.nodeOrder : [...state.nodeOrder, id];
  const merged: TopologyNodeState = {
    id,
    kind: patch.kind ?? prev?.kind ?? defaults.kind ?? 'subtask',
    title: patch.title ?? prev?.title ?? defaults.title ?? id,
    status: patch.status ?? prev?.status ?? defaults.status ?? 'pending',
    assignedAgent: patch.assignedAgent ?? prev?.assignedAgent ?? defaults.assignedAgent,
    selectedModelId: patch.selectedModelId ?? prev?.selectedModelId ?? defaults.selectedModelId,
    childRunId: patch.childRunId ?? prev?.childRunId ?? defaults.childRunId,
    steering: patch.steering ?? prev?.steering,
    // Per-node pod name: use patch value if explicitly provided (even null); otherwise preserve prior.
    executionPodName: patch.executionPodName !== undefined
      ? patch.executionPodName
      : (prev?.executionPodName ?? defaults.executionPodName),
  };
  return { ...state, nodeOrder, nodes: { ...state.nodes, [id]: merged } };
}

function nodeFromPayload(raw: Record<string, unknown>): { id: string; node: Partial<TopologyNodeState> } | null {
  const id = str(raw['id']);
  if (!id) return null;
  return { id, node: readNodeFields(raw) };
}

// Resolve which existing node a subtask.* event refers to. The brief models the
// subtaskId AS the node id; the backend uses node ids like "subtask-5" while the
// event carries a numeric subtaskId. We match defensively and NEVER fabricate a
// node — topology snapshot/deltas are authoritative for node existence.
function resolveSubtaskNodeId(
  state: CoordinatorTopologyState,
  subtaskId: string,
  childRunId?: string,
): string | undefined {
  if (state.nodes[subtaskId]) return subtaskId;
  const suffixed = `subtask-${subtaskId}`;
  if (state.nodes[suffixed]) return suffixed;
  if (childRunId) {
    const byChild = state.nodeOrder.find((nid) => state.nodes[nid]?.childRunId === childRunId);
    if (byChild) return byChild;
  }
  return state.nodeOrder.find((nid) => {
    if (state.nodes[nid]?.kind !== 'subtask') return false;
    return nid === subtaskId || nid.endsWith(`-${subtaskId}`);
  });
}

export function topologyReducer(
  state: CoordinatorTopologyState,
  evt: RunStreamEvent,
): CoordinatorTopologyState {
  const p = evt.payload;

  switch (evt.type) {
    case 'coordinator.work_plan': {
      const workPlan: CoordinatorWorkPlan = {
        workPlanId: String(p['workPlanId'] ?? ''),
        status: String(p['status'] ?? ''),
        subtasks: Array.isArray(p['subtasks']) ? (p['subtasks'] as CoordinatorWorkPlan['subtasks']) : [],
      };
      return { ...state, workPlan };
    }

    case 'coordinator.topology': {
      const seq = typeof p['seq'] === 'number' ? (p['seq'] as number) : Number(p['seq'] ?? 0);
      const version = typeof p['version'] === 'number' ? (p['version'] as number) : state.version;
      const isSnapshot = Array.isArray(p['nodes']);

      if (isSnapshot) {
        // SNAPSHOT — establishes the full node set and immutable edges.
        const nodes: Record<string, TopologyNodeState> = {};
        const nodeOrder: string[] = [];
        for (const raw of p['nodes'] as Record<string, unknown>[]) {
          const parsed = nodeFromPayload(raw);
          if (!parsed) continue;
          nodes[parsed.id] = {
            id: parsed.id,
            kind: parsed.node.kind ?? 'subtask',
            title: parsed.node.title ?? parsed.id,
            status: parsed.node.status ?? 'pending',
            assignedAgent: parsed.node.assignedAgent,
            selectedModelId: parsed.node.selectedModelId,
            childRunId: parsed.node.childRunId,
            executionPodName: parsed.node.executionPodName,
          };
          nodeOrder.push(parsed.id);
        }        const edges: TopologyEdge[] = Array.isArray(p['edges'])
          ? (p['edges'] as Record<string, unknown>[])
              .map((e) => ({ from: String(e['from'] ?? ''), to: String(e['to'] ?? '') }))
              .filter((e) => e.from && e.to)
          : [];
        return { ...state, hasSnapshot: true, version, topoSeq: seq, nodeOrder, nodes, edges };
      }

      // DELTA — ignore stale/duplicate seq; merge changed nodes by id.
      if (state.hasSnapshot && seq <= state.topoSeq) return state;
      let next = state;
      if (Array.isArray(p['changed'])) {
        for (const raw of p['changed'] as Record<string, unknown>[]) {
          const parsed = nodeFromPayload(raw);
          if (!parsed) continue;
          next = mergeNode(next, parsed.id, parsed.node);
        }
      }
      return { ...next, version, topoSeq: Math.max(state.topoSeq, seq) };
    }

    case 'subtask.dispatched':
    case 'subtask.running':
    case 'subtask.assemble_ready':
    case 'subtask.rai_flagged':
    case 'subtask.completed':
    case 'subtask.failed':
    case 'subtask.pending_capacity': {
      const subtaskId = str(p['subtaskId']);
      if (!subtaskId) return state;
      const status = str(p['status']) ?? SUBTASK_STATUS_FROM_TYPE[evt.type];
      const childRunId = str(p['childRunId']);
      // Each subtask.* is paired with a coordinator.topology delta that already
      // updates the node; here we resolve the matching existing node (the event's
      // subtaskId is not always identical to the node id) and merge defensively.
      const targetId = resolveSubtaskNodeId(state, subtaskId, childRunId)
        ?? (state.hasSnapshot ? undefined : subtaskId);
      if (!targetId) return state;
      return mergeNode(
        state,
        targetId,
        {
          status,
          childRunId,
          assignedAgent: str(p['assignedAgent']),
          selectedModelId: str(p['selectedModelId']),
          // Read executionPodName defensively from subtask.* events (spec-018).
          executionPodName: p['executionPodName'] !== undefined
            ? (p['executionPodName'] === null ? null : str(p['executionPodName']) ?? null)
            : undefined,
        },
        { kind: 'subtask' },
      );
    }

    case 'coordinator.steering': {
      const directiveId = str(p['directiveId']);
      if (!directiveId) return state;
      const steering: NodeSteering = {
        directiveId,
        kind: (str(p['kind']) as SteerKind) ?? 'redirect',
        status: str(p['status']) ?? 'requested',
        instruction: str(p['instruction']),
      };
      const targetChildRunId = str(p['targetChildRunId']);
      // Attach to the targeted subtask node (matched by childRunId); when no target
      // is given, attach to the coordinator node (whole-orchestration directive).
      let targetId: string | undefined;
      if (targetChildRunId) {
        targetId = state.nodeOrder.find((nid) => state.nodes[nid]?.childRunId === targetChildRunId);
      } else {
        targetId = state.nodeOrder.find((nid) => state.nodes[nid]?.kind === 'coordinator');
      }
      if (!targetId) return state;
      return mergeNode(state, targetId, { steering });
    }

    default:
      return state;
  }
}

/** Fold the accumulated SSE event list into topology state, starting from `seed`. */
export function buildTopologyState(
  events: RunStreamEvent[],
  seed: CoordinatorTopologyState = initialTopologyState,
): CoordinatorTopologyState {
  let state = seed;
  for (const evt of events) state = topologyReducer(state, evt);
  return state;
}

// Backend node id scheme (CoordinatorTopology.cs): the coordinator is the single node
// "coordinator"; each subtask is "subtask-{id}". The REST seed below mirrors it exactly so
// the later SSE snapshot/deltas reconcile by id with no duplicated nodes.
const COORDINATOR_NODE_ID = 'coordinator';
const subtaskNodeId = (id: number | string): string => `subtask-${id}`;

/**
 * Seed topology state from the REST work-plan (+ optional children) so the graph populates
 * immediately on page load. The one-time SSE `coordinator.topology` snapshot is emitted before
 * the stream connects; without this seed the page stays empty until a manual reconnect.
 *
 * The seed deliberately leaves `hasSnapshot` false: SSE deltas (and the authoritative snapshot,
 * if/when it arrives) merge by id on top of it. `topoSeq` stays -1 so no early delta is dropped.
 */
export function seedTopologyFromWorkPlan(
  workPlan: WorkPlanResponse | null | undefined,
  children?: CoordinatorChildResponse[] | null,
): CoordinatorTopologyState {
  if (!workPlan) return initialTopologyState;

  const nodes: Record<string, TopologyNodeState> = {};
  const nodeOrder: string[] = [];

  nodes[COORDINATOR_NODE_ID] = {
    id: COORDINATOR_NODE_ID,
    kind: 'coordinator',
    title: 'Coordinator',
    status: workPlan.status ?? 'running',
  };
  nodeOrder.push(COORDINATOR_NODE_ID);

  for (const s of workPlan.subtasks ?? []) {
    const id = subtaskNodeId(s.subtaskId);
    nodes[id] = {
      id,
      kind: 'subtask',
      title: s.title ?? id,
      status: s.status ?? 'pending',
      assignedAgent: s.assignedAgent || undefined,
      selectedModelId: s.selectedModelId || undefined,
      childRunId: s.childRunId || undefined,
    };
    nodeOrder.push(id);
  }

  // Overlay dispatched-child details (childRunId / live status) keyed by subtaskId.
  for (const c of children ?? []) {
    const id = subtaskNodeId(c.subtaskId);
    const prev = nodes[id];
    if (!prev) continue;
    nodes[id] = {
      ...prev,
      status: c.subtaskStatus ?? prev.status,
      childRunId: c.childRunId ?? prev.childRunId,
      assignedAgent: c.assignedAgent || prev.assignedAgent,
      selectedModelId: c.selectedModelId || prev.selectedModelId,
    };
  }

  // from = dependency (dependsOnSubtaskId), to = dependent (subtaskId).
  const edges: TopologyEdge[] = (workPlan.dependencies ?? []).map((d) => ({
    from: subtaskNodeId(d.dependsOnSubtaskId),
    to: subtaskNodeId(d.subtaskId),
  }));

  return {
    hasSnapshot: false,
    version: 0,
    topoSeq: -1,
    nodeOrder,
    nodes,
    edges,
  };
}
