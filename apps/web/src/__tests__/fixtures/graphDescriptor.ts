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
    { id: 'agent',  label: 'Agent',        role: 'agent',  kind: 'live' },
    { id: 'rai',    label: 'Rai',           role: 'rai',    kind: 'live' },
    { id: 'review', label: 'Human Review',  role: 'review', kind: 'live' },
    { id: 'merge',  label: 'Merge',         role: 'merge',  kind: 'live' },
    { id: 'scribe', label: 'Scribe',        role: 'scribe', kind: 'live' },
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
    { id: 'agent',          label: 'Agent',          role: 'agent',   kind: 'live' },
    { id: 'rai',            label: 'Rai',             role: 'rai',     kind: 'live' },
    { id: 'assemble-ready', label: 'Assemble-ready',  role: 'assembly', kind: 'live' },
  ],
  edges: [
    { from: 'agent', to: 'rai',            cardinality: 'direct', loopback: false },
    { from: 'rai',   to: 'assemble-ready', cardinality: 'direct', loopback: false },
  ],
};
