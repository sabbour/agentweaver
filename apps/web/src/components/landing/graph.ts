import type { Edge, Node } from '@xyflow/react';
import {
  FIXED_NODE_H,
  FIXED_NODE_W,
  FIXED_NODE_WITH_CAPTION_H,
  layoutDagStaircase,
  routeGridEdges,
  type NodeSizeHint,
} from '../../utils/dagLayout';
import { forwardEdge } from '../WorkflowGraphPanel';
import type { Scenario, ScenarioNode } from './types';

/**
 * Landing scenario graph geometry — a thin wiring layer over the PRODUCTION
 * stepped-DAG layout.
 *
 * The landing demo intentionally reuses the exact Coordinator run-graph helpers
 * (`layoutDagStaircase` for the compact alternating staircase and `routeGridEdges`
 * for the stepped connector routing) rather than a landing-only layout algorithm.
 * This module only prepares the per-node size hints and forward (spine) edges the
 * shared helpers expect, then returns their output. Positions are deterministic
 * and depend only on the scenario topology, so they can be memoised once per
 * scenario and asserted directly against a fresh `layoutDagStaircase` call.
 */

/** Rank/node separation mirrors the Coordinator graph's compact staircase spacing. */
export const LANDING_RANK_SEP = 40;
export const LANDING_NODE_SEP = 20;

/** Staircase options mirroring CoordinatorRunPage's compact alternating cascade. */
export const LANDING_STAIRCASE_OPTS = {
  rankSep: LANDING_RANK_SEP,
  nodeSep: LANDING_NODE_SEP,
  targetAspect: 1.35,
  minStepRanks: 3,
} as const;

export interface ScenarioGraph {
  /** Top-left position of each node id, in React Flow coordinates. */
  positions: Map<string, { x: number; y: number }>;
  /** Forward (spine) edges routed with handles + flowDirection by routeGridEdges. */
  routedEdges: Edge[];
  /** Per-node dagre size hints keyed by node id (drives readable packing). */
  sizeHints: Record<string, NodeSizeHint>;
  /** The staircase-laid nodes (carry initialWidth/initialHeight for edge routing). */
  laidOutNodes: Node[];
}

/**
 * Per-node dagre size hint. Every landing node renders as the compact WorkflowNode
 * card (FIXED_NODE_W wide); nodes that carry a model additionally render a model
 * caption below the card, so their height reserves the caption room — exactly the
 * content-driven rule CoordinatorRunPage uses for its compact WorkflowNodes.
 */
export function scenarioNodeSizeHint(node: ScenarioNode): NodeSizeHint {
  return {
    width: FIXED_NODE_W,
    height: node.modelId ? FIXED_NODE_WITH_CAPTION_H : FIXED_NODE_H,
  };
}

/**
 * Compute the deterministic stepped-staircase geometry for one scenario using the
 * shared production helpers. Pure — depends only on the scenario topology.
 */
export function buildScenarioGraph(scenario: Scenario): ScenarioGraph {
  const rawNodes: Node[] = scenario.nodes.map((node) => ({
    id: node.id,
    type: 'workflow',
    position: { x: 0, y: 0 },
    data: {},
  }));

  const sizeHints: Record<string, NodeSizeHint> = {};
  for (const node of scenario.nodes) {
    sizeHints[node.id] = scenarioNodeSizeHint(node);
  }

  const forwardEdges = scenario.edges.map(([id, source, target]) => forwardEdge(id, source, target));

  // LR alternating staircase — the same configuration CoordinatorRunPage renders.
  const laidOutNodes = layoutDagStaircase(
    rawNodes,
    forwardEdges,
    { ...LANDING_STAIRCASE_OPTS, rankdir: 'LR' },
    sizeHints,
  );

  const positions = new Map(
    laidOutNodes.map((node) => [node.id, { x: node.position.x, y: node.position.y }]),
  );

  const routedEdges = routeGridEdges(forwardEdges, laidOutNodes);

  return { positions, routedEdges, sizeHints, laidOutNodes };
}
