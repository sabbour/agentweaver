import { readFileSync } from 'node:fs';
import path from 'node:path';
import { installTarget } from '../azure/lib/targets.mjs';

const OMIT_KEYS = Symbol('omitKeys');

/**
 * Reads the ordered [from, to] pairs that change the tracked default-target manifests into the
 * manifests of one registered install target. The default target uses the tracked files as they are.
 */
export function targetValuePairs(root, name = 'agentweaver') {
  const target = installTarget(name);
  if (!target) throw new Error(`'${name}' is not a registered install target.`);
  if (name === 'agentweaver') return [];
  if (!target.k8sValues) throw new Error(`Install target '${name}' has no Kubernetes values file.`);
  let raw;
  try {
    raw = JSON.parse(readFileSync(path.join(root, ...target.k8sValues.split('/')), 'utf8'));
  } catch {
    throw new Error(`Could not read the Kubernetes values file ${target.k8sValues}.`);
  }
  const pairs = raw.replace;
  if (!Array.isArray(pairs) || !pairs.length || pairs.some(pair => !Array.isArray(pair) || pair.length !== 2 ||
      pair.some(value => typeof value !== 'string' || !value) || pair[0] === pair[1])) {
    throw new Error(`${target.k8sValues} must hold a nonempty "replace" list of distinct [from, to] strings.`);
  }
  const omitKeys = raw.omitKeys;
  if (omitKeys !== undefined) {
    if (!Array.isArray(omitKeys) || omitKeys.some(key => typeof key !== 'string' || !key)) {
      throw new Error(`${target.k8sValues} "omitKeys" must be an array of nonempty strings.`);
    }
    if (omitKeys.length) {
      Object.defineProperty(pairs, OMIT_KEYS, { value: omitKeys });
    }
  }
  return pairs;
}

/** Applies the target pairs, in order, to rendered manifest text. */
export function applyTargetValues(text, pairs) {
  let result = pairs.reduce((value, [from, to]) => value.replaceAll(from, to), text);
  const omitKeys = pairs?.[OMIT_KEYS];
  if (omitKeys?.length) {
    for (const key of omitKeys) {
      const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      result = result
        .replace(new RegExp(`^[ \\t]*${escaped}:[ \\t]*[^\\r\\n]*(?:\\r?\\n|$)`, 'gm'), '')
        .replace(new RegExp(`"${escaped}"\\s*:\\s*"[^"]*"\\s*,\\s*`, 'g'), '')
        .replace(new RegExp(`,\\s*"${escaped}"\\s*:\\s*"[^"]*"`, 'g'), '');
    }
  }
  return result;
}
