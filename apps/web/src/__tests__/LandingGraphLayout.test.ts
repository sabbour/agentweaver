import { describe, expect, it } from 'vitest';

import { SCENARIOS } from '../components/landing/scenarios';
import {
  LANDING_NODE_H,
  LANDING_NODE_W,
  LANDING_ROW_GAP,
  layoutScenarioGraph,
} from '../components/landing/layout';
import { computeDispatchWaves, isDispatchNode } from '../components/landing/waves';

/**
 * Invariants for the deterministic layered-DAG layout, asserted across all
 * eight authored scenarios. These guard the composition the user rejected:
 * right-clustered nodes, misaligned waves, overlaps, and non-deterministic
 * output.
 */

describe('layoutScenarioGraph invariants', () => {
  it('places nodes so every dependency edge points strictly left-to-right', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = layoutScenarioGraph(scenario);
      for (const [id, source, target] of scenario.edges) {
        const s = positions.get(source);
        const t = positions.get(target);
        expect(s, `${scenario.id}:${id} source ${source}`).toBeTruthy();
        expect(t, `${scenario.id}:${id} target ${target}`).toBeTruthy();
        expect(
          s!.x,
          `${scenario.id}: edge ${source}->${target} must go forward`,
        ).toBeLessThan(t!.x);
      }
    }
  });

  it('aligns same-wave specialists into the same column (vertical waves)', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = layoutScenarioGraph(scenario);
      const waves = computeDispatchWaves(scenario);
      const byWave = new Map<number, number[]>();
      for (const node of scenario.nodes) {
        if (!isDispatchNode(node)) continue;
        const wave = waves.get(node.id)!;
        const xs = byWave.get(wave) ?? [];
        xs.push(positions.get(node.id)!.x);
        byWave.set(wave, xs);
      }
      for (const [wave, xs] of byWave) {
        const first = xs[0];
        for (const x of xs) {
          expect(x, `${scenario.id}: wave ${wave} specialists must share a column`).toBe(first);
        }
      }
    }
  });

  it('never overlaps two node centres and keeps a column gap', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = layoutScenarioGraph(scenario);
      const entries = [...positions.entries()];
      for (let i = 0; i < entries.length; i += 1) {
        for (let j = i + 1; j < entries.length; j += 1) {
          const [idA, a] = entries[i];
          const [idB, b] = entries[j];
          const sameColumn = a.x === b.x;
          const overlap = a.x === b.x && a.y === b.y;
          expect(overlap, `${scenario.id}: ${idA} and ${idB} overlap`).toBe(false);
          if (sameColumn) {
            expect(
              Math.abs(a.y - b.y),
              `${scenario.id}: ${idA}/${idB} in one column too close`,
            ).toBeGreaterThanOrEqual(LANDING_ROW_GAP - 0.5);
          }
        }
      }
    }
  });

  it('starts at the coordinator column and ends with the review gate rightmost', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = layoutScenarioGraph(scenario);
      const xs = [...positions.values()].map((p) => p.x);
      const minX = Math.min(...xs);
      const maxX = Math.max(...xs);

      const coordinator = scenario.nodes.find((n) => n.role === 'coordinator');
      const review = scenario.nodes.find((n) => n.isReviewGate || n.role === 'review');
      expect(coordinator, `${scenario.id}: coordinator present`).toBeTruthy();
      expect(review, `${scenario.id}: review present`).toBeTruthy();

      expect(positions.get(coordinator!.id)!.x, `${scenario.id}: coordinator leftmost`).toBe(minX);
      expect(positions.get(review!.id)!.x, `${scenario.id}: review rightmost`).toBe(maxX);
      // The review gate is alone in the final column.
      const inReviewColumn = [...positions.values()].filter((p) => p.x === maxX);
      expect(inReviewColumn, `${scenario.id}: review column holds only the gate`).toHaveLength(1);
    }
  });

  it('produces a bounded, non-degenerate composition centred vertically', () => {
    for (const scenario of SCENARIOS) {
      const layout = layoutScenarioGraph(scenario);
      // Bounded envelope — wide but not runaway, and always has real height.
      expect(layout.width, `${scenario.id}: width bounded`).toBeLessThanOrEqual(2000);
      expect(layout.width, `${scenario.id}: width positive`).toBeGreaterThan(LANDING_NODE_W);
      expect(layout.height, `${scenario.id}: height bounded`).toBeLessThanOrEqual(900);
      expect(layout.height, `${scenario.id}: height positive`).toBeGreaterThanOrEqual(LANDING_NODE_H);

      // Vertical composition is balanced: the mean node centre sits near the
      // middle of the envelope rather than pooling to one edge.
      const centres = [...layout.positions.values()].map((p) => p.y + LANDING_NODE_H / 2);
      const mean = centres.reduce((a, b) => a + b, 0) / centres.length;
      expect(
        Math.abs(mean - layout.height / 2),
        `${scenario.id}: composition centred`,
      ).toBeLessThanOrEqual(LANDING_ROW_GAP);
    }
  });

  it('is deterministic — identical output across repeated runs', () => {
    for (const scenario of SCENARIOS) {
      const a = layoutScenarioGraph(scenario);
      const b = layoutScenarioGraph(scenario);
      const serialize = (l: ReturnType<typeof layoutScenarioGraph>) =>
        JSON.stringify(
          [...l.positions.entries()].sort(([x], [y]) => x.localeCompare(y)),
        );
      expect(serialize(a), `${scenario.id}: deterministic`).toBe(serialize(b));
    }
  });

  it('keeps the structural spine on a single centred track', () => {
    for (const scenario of SCENARIOS) {
      const { positions } = layoutScenarioGraph(scenario);
      const spine = ['coordinator', 'outcome', 'work-plan']
        .map((id) => positions.get(id))
        .filter(Boolean) as { x: number; y: number }[];
      // Spine nodes each occupy their own column (distinct x) and align closely
      // on the same horizontal track (small y spread).
      const ys = spine.map((p) => p.y);
      const spread = Math.max(...ys) - Math.min(...ys);
      expect(spread, `${scenario.id}: spine roughly aligned`).toBeLessThanOrEqual(LANDING_ROW_GAP);
      const xsUnique = new Set(spine.map((p) => p.x));
      expect(xsUnique.size, `${scenario.id}: spine columns distinct`).toBe(spine.length);
    }
  });
});
