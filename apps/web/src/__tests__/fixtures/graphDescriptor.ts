/**
 * Local fixtures for GraphDescriptor — used in tests before the backend endpoint ships.
 * These mirror the frozen contract in the task spec (snake_case JSON).
 */
import type { GraphDescriptor } from '../../api/types';

export const FULL_GRAPH_DESCRIPTOR: GraphDescriptor = {
  graph_id: 'full-graph-fixture',
  variant: 'full',
  start_node_id: 'agent',
  nodes: [
    { id: 'agent',  label: 'Agent',        role: 'agent',  kind: 'live',    node_type: 'agent'  },
    { id: 'rai',    label: 'Rai',           role: 'rai',    kind: 'live',    node_type: 'gate'   },
    { id: 'review', label: 'Human Review',  role: 'review', kind: 'live',    node_type: 'gate'   },
    { id: 'merge',  label: 'Merge',         role: 'merge',  kind: 'live',    node_type: 'action' },
    { id: 'scribe', label: 'Scribe',        role: 'scribe', kind: 'live',    node_type: 'action' },
  ],
  edges: [
    { from: 'agent',  to: 'rai',    cardinality: 'direct', loopback: false },
    { from: 'rai',    to: 'review', cardinality: 'direct', loopback: false },
    { from: 'review', to: 'merge',  cardinality: 'direct', loopback: false },
    { from: 'merge',  to: 'scribe', cardinality: 'direct', loopback: false },
    { from: 'rai',    to: 'agent',  cardinality: 'direct', loopback: true  },
    { from: 'review', to: 'agent',  cardinality: 'direct', loopback: true  },
  ],
};

export const CHILD_GRAPH_DESCRIPTOR: GraphDescriptor = {
  graph_id: 'child-graph-fixture',
  variant: 'child',
  start_node_id: 'agent',
  nodes: [
    { id: 'agent',          label: 'Agent',          role: 'agent',    kind: 'live',  node_type: 'agent'    },
    { id: 'rai',            label: 'Rai',             role: 'rai',      kind: 'live',  node_type: 'gate'     },
    { id: 'assemble-ready', label: 'Assemble-ready',  role: 'assembly', kind: 'live',  node_type: 'terminal' },
  ],
  edges: [
    { from: 'agent', to: 'rai',            cardinality: 'direct', loopback: false },
    { from: 'rai',   to: 'assemble-ready', cardinality: 'direct', loopback: false },
  ],
};

/** Coordinator-variant descriptor (Feature 008 unified coordinator view). */
export const COORDINATOR_GRAPH_DESCRIPTOR: GraphDescriptor = {
  graph_id: 'coordinator:coord-run-1',
  variant: 'coordinator',
  start_node_id: 'coordinator',
  nodes: [
    // Coordinator orchestrator node
    { id: 'coordinator', label: 'Coordinator', role: 'coordinator', kind: 'live', node_type: 'agent' },
    // Subtask nodes — dispatched, with child run references
    {
      id: 'plan:subtask-1', label: 'Subtask 1', role: 'subtask', kind: 'live', node_type: 'subtask',
      child_graph_ref: 'run:child-run-1', child_run_id: 'child-run-1',
      agent: 'Neo', model: 'gpt-4o', phase: 'write',
    },
    {
      id: 'plan:subtask-2', label: 'Subtask 2', role: 'subtask', kind: 'live', node_type: 'subtask',
      child_graph_ref: 'run:child-run-2', child_run_id: 'child-run-2',
    },
    // Planned assembly nodes (kind=planned — never show running/pending spinners)
    { id: 'planned:assembly-rai',    label: 'RAI Review',   role: 'rai',    kind: 'planned', node_type: 'gate'   },
    { id: 'planned:assembly-review', label: 'Human Review', role: 'review', kind: 'planned', node_type: 'gate'   },
    { id: 'planned:assembly-merge',  label: 'Merge',        role: 'merge',  kind: 'planned', node_type: 'action' },
    { id: 'planned:assembly-scribe', label: 'Scribe',       role: 'scribe', kind: 'planned', node_type: 'action' },
  ],
  edges: [
    { from: 'coordinator',           to: 'plan:subtask-1',         cardinality: 'direct', loopback: false },
    { from: 'coordinator',           to: 'plan:subtask-2',         cardinality: 'direct', loopback: false },
    { from: 'plan:subtask-1',        to: 'planned:assembly-rai',   cardinality: 'fanin',  loopback: false },
    { from: 'plan:subtask-2',        to: 'planned:assembly-rai',   cardinality: 'fanin',  loopback: false },
    { from: 'planned:assembly-rai',    to: 'planned:assembly-review', cardinality: 'direct', loopback: false },
    { from: 'planned:assembly-review', to: 'planned:assembly-merge',  cardinality: 'direct', loopback: false },
    { from: 'planned:assembly-merge',  to: 'planned:assembly-scribe', cardinality: 'direct', loopback: false },
  ],
};

/**
 * Coordinator-variant descriptor WITH the two coordinator-level loopback back-edges Tank adds:
 * the RAI gate and the Human Review gate can each send the collective output back to the
 * coordinator for re-dispatch. GraphEdge has no label field — the renderer derives the label
 * from the SOURCE node's role (rai vs review).
 */
export const COORDINATOR_GRAPH_DESCRIPTOR_LOOPBACKS: GraphDescriptor = {
  ...COORDINATOR_GRAPH_DESCRIPTOR,
  graph_id: 'coordinator:coord-run-loopbacks',
  edges: [
    ...COORDINATOR_GRAPH_DESCRIPTOR.edges,
    { from: 'planned:assembly-rai',    to: 'coordinator', cardinality: 'direct', loopback: true },
    { from: 'planned:assembly-review', to: 'coordinator', cardinality: 'direct', loopback: true },
  ],
};

