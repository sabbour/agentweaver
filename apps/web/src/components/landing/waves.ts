import type { Scenario, ScenarioNode } from './types';

/** A dispatchable specialist is any `agent` role node. Structural nodes
 *  (coordinator / outcome_plan / work_plan / review) are never dispatched on a
 *  wave — they gate the run around the specialists. */
export function isDispatchNode(node: ScenarioNode): boolean {
  return node.role === 'agent';
}

/**
 * Dispatch waves derived purely from the scenario dependency edges.
 *
 * Each dispatchable specialist gets a 1-based wave equal to the longest chain of
 * specialist dependencies leading into it. Specialists fed only by structural
 * nodes (e.g. the work plan) start at wave 1; a specialist that depends on
 * another specialist follows exactly one wave later. Specialists that can run
 * concurrently therefore share a wave and light up together — the scheduler
 * advances one wave per tick, never one specialist at a time.
 *
 * Waves are always contiguous 1..max (a wave is only produced by adding one to a
 * real predecessor's wave), so no wave number is ever skipped and the scheduler
 * never spends a tick on an empty wave.
 */
export function computeDispatchWaves(scenario: Scenario): Map<string, number> {
  const agentIds = new Set(scenario.nodes.filter(isDispatchNode).map((n) => n.id));

  const preds = new Map<string, string[]>();
  for (const id of agentIds) preds.set(id, []);
  for (const [, source, target] of scenario.edges) {
    if (agentIds.has(target) && agentIds.has(source)) {
      preds.get(target)!.push(source);
    }
  }

  const wave = new Map<string, number>();
  const visiting = new Set<string>();
  const resolve = (id: string): number => {
    const cached = wave.get(id);
    if (cached != null) return cached;
    // Authored scenario graphs are DAGs; the guard keeps a hypothetical cycle
    // from recursing forever without inventing a bogus wave.
    if (visiting.has(id)) return 1;
    visiting.add(id);
    const p = preds.get(id) ?? [];
    const w = p.length === 0 ? 1 : 1 + Math.max(...p.map(resolve));
    visiting.delete(id);
    wave.set(id, w);
    return w;
  };
  for (const id of agentIds) resolve(id);
  return wave;
}

/** The number of dispatch waves (max wave, or 0 when a scenario has no
 *  specialists). The player ticks the dispatch stage exactly this many times. */
export function maxDispatchWave(scenario: Scenario): number {
  let max = 0;
  for (const w of computeDispatchWaves(scenario).values()) max = Math.max(max, w);
  return max;
}

/**
 * Returns the 1-based dispatch wave for the plan item at `planIndex`.
 *
 * Plan items are authored in the same order as the agent-role nodes in every
 * scenario, so `plan[i]` always corresponds to `agentNodeIds[i]`.  A plan
 * item is considered done when `wave <= currentDispatchStep`.
 *
 * Returns `Infinity` when `planIndex` is out of range or the node has no
 * recorded wave — both are safe fall-throughs that keep the item un-checked.
 */
export function planItemWave(
  waves: Map<string, number>,
  agentNodeIds: readonly string[],
  planIndex: number,
): number {
  const nodeId = agentNodeIds[planIndex];
  if (nodeId === undefined) return Infinity;
  return waves.get(nodeId) ?? Infinity;
}
