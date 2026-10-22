import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** Sorts object keys recursively so equivalent Kubernetes settings have stable hashes. */
export function canonicalJson(value) {
  return JSON.stringify(value, (_key, item) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.entries(item).sort(([left], [right]) => left.localeCompare(right)))
    : item);
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

export function imageDigest(image, where, fail) {
  if (typeof image !== 'string' || image.trim() !== image) fail(`${where} has an invalid image reference.`);
  const match = /@(sha256:[a-f0-9]{64})$/.exec(image);
  if (!match) fail(`${where} image is not pinned by an immutable sha256 digest.`);
  return match[1];
}

export function deploymentRecord(deployment, namespace, fail) {
  if (!isRecord(deployment) || deployment.apiVersion !== 'apps/v1' ||
      deployment.kind !== 'Deployment' || deployment.metadata?.namespace !== namespace) {
    fail(`Deployment list contains an item that is not an apps/v1 Deployment in namespace ${namespace}.`);
  }
  const name = deployment.metadata?.name;
  if (typeof name !== 'string' || name.trim() !== name || name.length === 0 ||
      !isRecord(deployment.spec) || !isRecord(deployment.spec.template?.spec)) {
    fail('a Deployment has an invalid name or pod template.');
  }
  const template = deployment.spec.template.spec;
  if (!Array.isArray(template.containers) || template.containers.length === 0) {
    fail(`Deployment ${name} has no application containers.`);
  }
  const initContainers = template.initContainers ?? [];
  if (!Array.isArray(initContainers)) fail(`Deployment ${name} has an invalid init container list.`);
  const names = new Set();
  const images = [...initContainers, ...template.containers].map((container, index) => {
    if (typeof container?.name !== 'string' || container.name.length === 0 || names.has(container.name)) {
      fail(`Deployment ${name} has a missing or duplicate container name.`);
    }
    names.add(container.name);
    const kind = index < initContainers.length ? 'initContainer' : 'container';
    return {
      kind,
      name: container.name,
      image: container.image,
      digest: imageDigest(container.image, `Deployment ${name} container ${container.name}`, fail),
    };
  });
  return {
    name,
    images,
    settingsSha256: sha256(canonicalJson({
      labels: deployment.metadata?.labels ?? {},
      annotations: deployment.metadata?.annotations ?? {},
      spec: deployment.spec,
    })),
  };
}

/**
 * Reads a Deployment inventory and rejects malformed wrappers or unrelated objects before planning.
 * kubectl may return a generic List for this query, so its item shape is checked explicitly.
 */
export function deploymentInventory(execKubectl, namespace, root, fail) {
  const result = execKubectl([
    '--namespace', namespace, 'get', 'deployments', '--output', 'json',
  ], { cwd: root, check: false });
  if (result.status !== 0) {
    fail(`could not read Deployments in namespace ${namespace}: ${result.stderr?.trim() ?? ''}`);
  }
  let list;
  try {
    list = JSON.parse(result.stdout);
  } catch (error) {
    fail(`Kubernetes Deployment list is not valid JSON: ${error.message}`);
  }
  if (!isRecord(list) || !['List', 'DeploymentList'].includes(list.kind) ||
      !Array.isArray(list.items) || list.items.length === 0) {
    fail(`namespace ${namespace} has no valid Deployment list.`);
  }
  const byName = new Map();
  const records = [];
  for (const deployment of list.items) {
    const record = deploymentRecord(deployment, namespace, fail);
    if (byName.has(record.name)) fail(`Deployment ${record.name} appears more than once.`);
    byName.set(record.name, deployment);
    records.push(record);
  }
  records.sort((left, right) => left.name.localeCompare(right.name));
  return { byName, records };
}

/** Applies planned image replacements to a cloned Deployment snapshot, never to the live objects. */
export function planDeploymentInventory(before, updates, namespace, fail) {
  const items = [...before.byName.values()].map(deployment => structuredClone(deployment));
  for (const [name, replacements] of updates) {
    const deployment = items.find(item => item.metadata.name === name);
    if (!deployment) fail(`Deployment ${name} is missing from the inventory.`);
    for (const replacement of replacements) {
      const container = deployment.spec.template.spec.containers[replacement.containerIndex];
      if (!container) fail(`Deployment ${name} has no selected application container.`);
      container.image = replacement.image;
    }
  }
  return {
    items,
    records: items.map(deployment => deploymentRecord(deployment, namespace, fail))
      .sort((left, right) => left.name.localeCompare(right.name)),
  };
}

/** Verifies that the rollout changed only the planned container images and controller revisions. */
export function verifyDeploymentPreservation(before, after, updates, {
  namespace, label, ignoredAnnotations = new Map(), fail,
}) {
  if (before.byName.size !== after.byName.size ||
      [...before.byName.keys()].some(name => !after.byName.has(name))) {
    fail(`the Deployment set changed during the ${label} rollout.`);
  }
  for (const [name, original] of before.byName) {
    const current = after.byName.get(name);
    const expected = structuredClone(original);
    for (const replacement of updates.get(name) ?? []) {
      const container = expected.spec.template.spec.containers[replacement.containerIndex];
      if (!container) fail(`Deployment ${name} has no selected application container.`);
      container.image = replacement.image;
    }
    const ignored = ignoredAnnotations.get(name) ?? [];
    const settings = deployment => {
      const annotations = { ...(deployment.metadata?.annotations ?? {}) };
      for (const annotation of ignored) delete annotations[annotation];
      return {
        labels: deployment.metadata?.labels ?? {},
        annotations,
        spec: deployment.spec,
      };
    };
    if (!isDeepStrictEqual(settings(current), settings(expected))) {
      fail(updates.has(name)
        ? `the ${label} rollout changed settings other than the selected container image.`
        : `non-${label} Deployment ${name} changed during the ${label} rollout.`);
    }
  }
}

export function verifyDeploymentReady(deployment, replicas, label, fail) {
  const generation = deployment.metadata?.generation;
  const observedGeneration = deployment.status?.observedGeneration;
  const status = deployment.status ?? {};
  if (!Number.isSafeInteger(generation) || !Number.isSafeInteger(observedGeneration) ||
      observedGeneration < generation || status.updatedReplicas !== replicas ||
      status.readyReplicas !== replicas || status.availableReplicas !== replicas) {
    fail(`${label} Deployment completed without every requested replica observed, updated, ready, and available.`);
  }
  return { generation, observedGeneration, replicas, readyReplicas: status.readyReplicas };
}

export function writeRolloutReceipt(file, receipt) {
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(receipt, null, 2)}\n`, { flag: 'wx' });
}
