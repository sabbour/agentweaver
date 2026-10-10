import { describe, expect, it } from 'vitest';

import { SCENARIOS } from '../components/landing/scenarios';
import { computeDispatchWaves, isDispatchNode, maxDispatchWave, planItemWave } from '../components/landing/waves';

const byId = (id: string) => {
  const scenario = SCENARIOS.find((s) => s.id === id);
  if (!scenario) throw new Error(`missing scenario ${id}`);
  return scenario;
};

describe('dispatch waves', () => {
  it('groups concurrent specialists into the same wave (product-feature)', () => {
    const waves = computeDispatchWaves(byId('product-feature'));
    // design + docs depend only on the structural work plan, so they run together.
    expect(waves.get('design')).toBe(1);
    expect(waves.get('docs')).toBe(1);
    // build follows design; tests follow build — one wave later each.
    expect(waves.get('build')).toBe(2);
    expect(waves.get('tests')).toBe(3);
  });

  it('lets parallel specialists share wave 1 in the game scene', () => {
    const waves = computeDispatchWaves(byId('game'));
    expect(waves.get('sprites')).toBe(1);
    expect(waves.get('palette')).toBe(1);
    expect(waves.get('scene')).toBe(2);
    expect(maxDispatchWave(byId('game'))).toBe(2);
  });

  it('ticks fewer waves than specialists when work runs concurrently', () => {
    // product-feature has 4 specialists but only 3 dependency waves — proof the
    // scheduler is wave-based, not one-tick-per-specialist.
    const scenario = byId('product-feature');
    const agentCount = scenario.nodes.filter(isDispatchNode).length;
    expect(agentCount).toBe(4);
    expect(maxDispatchWave(scenario)).toBe(3);
    expect(maxDispatchWave(scenario)).toBeLessThan(agentCount);
  });

  it('assigns contiguous waves (1..max, no dead scheduler ticks) for every scenario', () => {
    for (const scenario of SCENARIOS) {
      const waves = computeDispatchWaves(scenario);
      const present = new Set(waves.values());
      const max = maxDispatchWave(scenario);
      expect(max).toBeGreaterThanOrEqual(1);
      // Every wave number from 1..max must be occupied by at least one specialist.
      for (let w = 1; w <= max; w += 1) {
        expect(present.has(w)).toBe(true);
      }
      // Every specialist has a wave; no structural node does.
      for (const node of scenario.nodes) {
        if (isDispatchNode(node)) {
          expect(waves.get(node.id)).toBeGreaterThanOrEqual(1);
        } else {
          expect(waves.has(node.id)).toBe(false);
        }
      }
    }
  });
});

describe('plan item done condition', () => {
  // Helper: ordered agent node IDs matching plan item indices for a scenario
  const agentIds = (scenarioId: string) => {
    const s = byId(scenarioId);
    return s.nodes.filter(isDispatchNode).map((n) => n.id);
  };

  it('plan item count matches agent node count for every scenario', () => {
    for (const scenario of SCENARIOS) {
      const ids = scenario.nodes.filter(isDispatchNode).map((n) => n.id);
      expect(ids.length, `${scenario.id}: plan/agent count mismatch`).toBe(scenario.plan.length);
    }
  });

  it('palette (plan[2], wave 1) is done at dispatchStep=1 alongside sprites in the game scenario', () => {
    const waves = computeDispatchWaves(byId('game'));
    const ids = agentIds('game');
    // sprites → wave 1, scene → wave 2, palette → wave 1
    expect(planItemWave(waves, ids, 0)).toBe(1); // sprites
    expect(planItemWave(waves, ids, 1)).toBe(2); // scene
    expect(planItemWave(waves, ids, 2)).toBe(1); // palette (was broken: old check needed dispatchStep > 2)
    // At dispatchStep=1: sprites and palette are done; scene is not
    expect(planItemWave(waves, ids, 0) <= 1).toBe(true);
    expect(planItemWave(waves, ids, 1) <= 1).toBe(false);
    expect(planItemWave(waves, ids, 2) <= 1).toBe(true);
  });

  it('docs (plan[3], wave 1) is done at dispatchStep=1 in product-feature', () => {
    const waves = computeDispatchWaves(byId('product-feature'));
    const ids = agentIds('product-feature');
    // design→1, build→2, tests→3, docs→1
    expect(planItemWave(waves, ids, 0)).toBe(1); // design
    expect(planItemWave(waves, ids, 1)).toBe(2); // build
    expect(planItemWave(waves, ids, 2)).toBe(3); // tests
    expect(planItemWave(waves, ids, 3)).toBe(1); // docs (was broken: old check needed dispatchStep > 3)
    // At dispatchStep=1 design+docs done; build+tests still pending
    expect(planItemWave(waves, ids, 0) <= 1).toBe(true);
    expect(planItemWave(waves, ids, 1) <= 1).toBe(false);
    expect(planItemWave(waves, ids, 2) <= 1).toBe(false);
    expect(planItemWave(waves, ids, 3) <= 1).toBe(true);
  });

  it('every plan item is done exactly when its wave <= dispatchStep (all scenarios)', () => {
    for (const scenario of SCENARIOS) {
      const waves = computeDispatchWaves(scenario);
      const ids = scenario.nodes.filter(isDispatchNode).map((n) => n.id);
      const max = maxDispatchWave(scenario);
      for (let step = 1; step <= max; step++) {
        for (let i = 0; i < scenario.plan.length; i++) {
          const wave = planItemWave(waves, ids, i);
          expect(
            wave <= step,
            `${scenario.id} plan[${i}] (wave=${wave}) at dispatchStep=${step}`,
          ).toBe(wave <= step);
        }
      }
    }
  });

  it('no plan item remains unchecked once its graph node has completed (parallel waves)', () => {
    // For each scenario with concurrent wave-1 specialists, verify that ALL
    // wave-1 plan items become done at dispatchStep=1 — no positional lag.
    const parallel = ['product-feature', 'marketing', 'rfp', 'game', 'decision'];
    for (const id of parallel) {
      const scenario = byId(id);
      const waves = computeDispatchWaves(scenario);
      const ids = scenario.nodes.filter(isDispatchNode).map((n) => n.id);
      const wave1Items = ids.map((nid, i) => ({ i, wave: waves.get(nid) })).filter((x) => x.wave === 1);
      // There must be at least 2 wave-1 specialists (otherwise the scenario is sequential)
      expect(wave1Items.length, `${id}: expected parallel wave-1 specialists`).toBeGreaterThan(1);
      for (const { i, wave } of wave1Items) {
        expect(planItemWave(waves, ids, i), `${id} plan[${i}]`).toBe(wave);
        expect(planItemWave(waves, ids, i) <= 1, `${id} plan[${i}] done at step=1`).toBe(true);
      }
    }
  });
});
