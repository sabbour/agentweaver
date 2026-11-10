import { readFileSync } from 'node:fs';
import path from 'node:path';
import { installTarget } from '../azure/lib/targets.mjs';

/**
 * Reads the ordered [from, to] pairs that change the tracked default-target manifests into the
 * manifests of one registered install target. The default target uses the tracked files as they are.
 */
export function targetValuePairs(root, name = 'agentweaver') {
  const target = installTarget(name);
  if (!target) throw new Error(`'${name}' is not a registered install target.`);
  if (name === 'agentweaver') return [];
  if (!target.k8sValues) throw new Error(`Install target '${name}' has no Kubernetes values file.`);
  let pairs;
  try {
    pairs = JSON.parse(readFileSync(path.join(root, ...target.k8sValues.split('/')), 'utf8')).replace;
  } catch {
    throw new Error(`Could not read the Kubernetes values file ${target.k8sValues}.`);
  }
  if (!Array.isArray(pairs) || !pairs.length || pairs.some(pair => !Array.isArray(pair) || pair.length !== 2 ||
      pair.some(value => typeof value !== 'string' || !value) || pair[0] === pair[1])) {
    throw new Error(`${target.k8sValues} must hold a nonempty "replace" list of distinct [from, to] strings.`);
  }
  return pairs;
}

/** Applies the target pairs, in order, to rendered manifest text. */
export function applyTargetValues(text, pairs) {
  return pairs.reduce((value, [from, to]) => value.replaceAll(from, to), text);
}
