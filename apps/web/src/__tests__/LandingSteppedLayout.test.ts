import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { SCENARIOS } from '../components/landing/scenarios';
import {
  LANDING_STAIRCASE_OPTS,
  buildScenarioGraph,
  scenarioNodeSizeHint,
} from '../components/landing/graph';
import {
  FIXED_NODE_H,
  FIXED_NODE_W,
  FIXED_NODE_WITH_CAPTION_H,
  layoutDagStaircase,
  routeGridEdges,
} from '../utils/dagLayout';
import { forwardEdge } from '../components/WorkflowGraphPanel';
import type { Node } from '@xyflow/react';

/**
 * The landing demo must REUSE the production stepped/staircase layout, not a
 * landing-only algorithm. These tests prove buildScenarioGraph is a thin wiring
 * layer whose output is byte-for-byte the shared layoutDagStaircase +
 * routeGridEdges result, for all eight scenarios — and that the deleted
 * landing-only layout module is really gone.
 */

describe('landing stepped-layout reuse', () => {
  it('uses the compact staircase config matching the Coordinator run graph', () => {
    // Same knobs CoordinatorRunPage feeds layoutDagStaircase.
    expect(LANDING_STAIRCASE_OPTS.rankSep).toBe(40);
    expect(LANDING_STAIRCASE_OPTS.nodeSep).toBe(20);
    expect(LANDING_STAIRCASE_OPTS.targetAspect).toBe(1.35);
    expect(LANDING_STAIRCASE_OPTS.minStepRanks).toBe(3);
  });

  it('sizes nodes by content: model-bearing nodes reserve the caption height', () => {
    const withModel = { id: 'a', label: 'A', role: 'agent', col: 0, row: 0, modelId: 'gpt' };
    const withoutModel = { id: 'b', label: 'B', role: 'work_plan', col: 0, row: 0 };
    expect(scenarioNodeSizeHint(withModel)).toEqual({ width: FIXED_NODE_W, height: FIXED_NODE_WITH_CAPTION_H });
    expect(scenarioNodeSizeHint(withoutModel)).toEqual({ width: FIXED_NODE_W, height: FIXED_NODE_H });
  });

  it('produces exactly the shared layoutDagStaircase + routeGridEdges output for all 8 scenarios', () => {
    expect(SCENARIOS).toHaveLength(8);

    for (const scenario of SCENARIOS) {
      const graph = buildScenarioGraph(scenario);

      // Re-derive the geometry directly from the shared helpers.
      const rawNodes: Node[] = scenario.nodes.map((n) => ({
        id: n.id,
        type: 'workflow',
        position: { x: 0, y: 0 },
        data: {},
      }));
      const hints: Record<string, { width: number; height: number }> = {};
      for (const n of scenario.nodes) hints[n.id] = scenarioNodeSizeHint(n);
      const fwd = scenario.edges.map(([id, s, t]) => forwardEdge(id, s, t));
      const laidOut = layoutDagStaircase(rawNodes, fwd, { ...LANDING_STAIRCASE_OPTS, rankdir: 'LR' }, hints);
      const expectedRouted = routeGridEdges(fwd, laidOut);

      // Positions match the shared staircase output node-for-node.
      for (const n of laidOut) {
        const pos = graph.positions.get(n.id);
        expect(pos, `${scenario.id}:${n.id}`).toEqual({ x: n.position.x, y: n.position.y });
      }

      // Routed edges match the shared routeGridEdges output (handles + flow).
      expect(graph.routedEdges).toHaveLength(expectedRouted.length);
      graph.routedEdges.forEach((edge, i) => {
        expect(edge.sourceHandle).toBe(expectedRouted[i].sourceHandle);
        expect(edge.targetHandle).toBe(expectedRouted[i].targetHandle);
      });
    }
  });

  it('never routes a dependency edge backward across scenarios (staircase forward order)', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = buildScenarioGraph(scenario);
      for (const [id, source, target] of scenario.edges) {
        const s = positions.get(source);
        const t = positions.get(target);
        expect(s, `${scenario.id}:${id} source`).toBeTruthy();
        expect(t, `${scenario.id}:${id} target`).toBeTruthy();
        // A staircase advances left-to-right by rank; nodes sharing a rank step
        // stack vertically. Forward therefore means "to the right, or same
        // column but lower" — never strictly to the left.
        const forward = t!.x > s!.x || (t!.x === s!.x && t!.y > s!.y);
        expect(forward, `${scenario.id}: ${source}->${target} must not go backward`).toBe(true);
      }
    }
  });

  it('assigns a routed handle to every landing edge (stepped connector routing)', () => {
    for (const scenario of SCENARIOS) {
      const { routedEdges } = buildScenarioGraph(scenario);
      for (const edge of routedEdges) {
        expect(edge.sourceHandle, `${scenario.id}:${edge.id}`).toMatch(/^source-(left|right|top|bottom)$/);
        expect(edge.targetHandle, `${scenario.id}:${edge.id}`).toMatch(/^target-(left|right|top|bottom)$/);
      }
    }
  });

  it('no longer ships a landing-only layout module', () => {
    const layoutPath = resolve(process.cwd(), 'src/components/landing/layout.ts');
    expect(existsSync(layoutPath)).toBe(false);
  });
});
