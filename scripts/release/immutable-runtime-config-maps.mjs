import { createHash } from 'node:crypto';

export const REPLACEABLE_IMMUTABLE_RUNTIME_CONFIG_MAPS = new Set([
  'agenthost-runtime-v1',
  'projects-config-runtime-config',
  'gateway-runtime-config',
]);

export const IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENTS = new Map([
  ['projects-config-runtime-config', ['projects-config']],
  ['gateway-runtime-config', ['gateway']],
]);

export const IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENT_NAMES = new Set(
  [...IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENTS.values()].flat(),
);

const replacementIdentity = new WeakMap();

function configMapDataDigest(data) {
  const entries = Object.entries(data ?? {}).sort(([left], [right]) => left.localeCompare(right));
  return createHash('sha256').update(JSON.stringify(entries)).digest('hex');
}

export function projectRuntimeConfigMap(resource) {
  return {
    kind: resource.kind,
    name: resource.metadata?.name,
    namespace: resource.metadata?.namespace,
    uid: resource.metadata?.uid,
    resourceVersion: resource.metadata?.resourceVersion,
    immutable: resource.immutable,
    dataDigest: configMapDataDigest(resource.data),
    binaryDataDigest: configMapDataDigest(resource.binaryData),
  };
}

function reject(message, fail) {
  if (fail) fail(message);
  throw new Error(message);
}

export function findImmutableRuntimeConfigMapReplacements(installationManifest, namespace, {
  root, run, fail,
}) {
  const replacements = [];
  const unapprovedChanges = [];
  const desiredMaps = installationManifest.items.filter(resource =>
    resource.kind === 'ConfigMap' && resource.immutable === true);

  for (const name of REPLACEABLE_IMMUTABLE_RUNTIME_CONFIG_MAPS) {
    const desired = desiredMaps.find(resource => resource.metadata?.name === name);
    if (!desired || desired.metadata?.namespace !== namespace) {
      reject(`source bundle must include immutable ConfigMap/${name} in namespace ${namespace}`, fail);
    }
  }

  // Inspect every immutable source map so a dry run cannot hide drift outside the allow-list.
  for (const desired of desiredMaps) {
    const name = desired.metadata?.name;
    if (typeof name !== 'string' || desired.metadata?.namespace !== namespace) {
      reject(`source bundle contains an immutable ConfigMap outside namespace ${namespace}`, fail);
    }
    const currentName = run('kubectl', [
      'get', 'configmap', name, '--namespace', namespace, '--ignore-not-found', '--output=name',
    ], { cwd: root });
    if (!currentName) continue;
    if (currentName !== `configmap/${name}`) {
      reject(`live ConfigMap lookup returned an unexpected resource for ${name}`, fail);
    }
    const currentText = run('kubectl', [
      'get', 'configmap', name, '--namespace', namespace, '--output=json',
    ], {
      cwd: root,
      projectJson: projectRuntimeConfigMap,
      preserveProjectedJson: true,
    });
    let current;
    try {
      current = JSON.parse(currentText);
    } catch (error) {
      reject(`ConfigMap/${name} projection is not valid JSON: ${error.message}`, fail);
    }
    if (current.kind !== 'ConfigMap' || current.name !== name || current.namespace !== namespace ||
        typeof current.dataDigest !== 'string' || typeof current.binaryDataDigest !== 'string') {
      reject(`live ConfigMap/${name} does not match its explicitly managed namespace and name`, fail);
    }
    if (current.immutable !== true) {
      reject(`live ConfigMap/${name} is not immutable as required by the source bundle`, fail);
    }
    const desiredProjection = projectRuntimeConfigMap(desired);
    if (current.dataDigest === desiredProjection.dataDigest &&
        current.binaryDataDigest === desiredProjection.binaryDataDigest) continue;
    if (!REPLACEABLE_IMMUTABLE_RUNTIME_CONFIG_MAPS.has(name)) {
      unapprovedChanges.push(name);
      continue;
    }
    replacementIdentity.set(desired, { uid: current.uid, resourceVersion: current.resourceVersion });
    replacements.push(desired);
  }
  if (unapprovedChanges.length > 0) {
    const changedNames = [...replacements.map(resource => resource.metadata.name), ...unapprovedChanges];
    reject(
      `changed immutable ConfigMaps: ${changedNames.join(', ')}. ` +
        `Not eligible for guarded replacement: ${unapprovedChanges.join(', ')}. ` +
        'Follow the reviewed maintenance procedure in ' +
        'docs/reference/runtime-configuration.md#update-an-immutable-configmap.',
      fail,
    );
  }
  return replacements;
}

export function verifyImmutableRuntimeConfigMapReplacementPermissions(replacements, namespace, {
  root, run, fail,
}) {
  for (const { metadata } of replacements) {
    const name = metadata.name;
    // Check create before delete because replacement removes the old object first.
    for (const [verb, resource] of [
      ['create', 'configmaps'],
      ['delete', `configmaps/${name}`],
    ]) {
      const allowed = run('kubectl', [
        'auth', 'can-i', verb, resource, '--namespace', namespace,
      ], { cwd: root }).trim().toLowerCase();
      if (allowed !== 'yes') {
        reject(`Kubernetes authorization must allow ${verb} ${resource} in namespace ${namespace} ` +
          `before replacing ConfigMap/${name}`, fail);
      }
    }
  }
}

export function replaceImmutableRuntimeConfigMaps(replacements, namespace, { root, run, fail }) {
  for (const resource of replacements) {
    const name = resource.metadata?.name;
    const identity = replacementIdentity.get(resource);
    if (typeof name !== 'string' || resource.metadata?.namespace !== namespace ||
        typeof identity?.uid !== 'string' || !identity.uid ||
        typeof identity.resourceVersion !== 'string' || !identity.resourceVersion) {
      reject('immutable ConfigMap replacement is missing its exact source object or live identity', fail);
    }
    // Bind deletion to the live object observed during planning so a concurrent change cannot be removed.
    // `kubectl delete` has no preconditions flag, so send a raw DELETE whose DeleteOptions body
    // carries the uid and resourceVersion; the API server rejects the delete if either changed.
    const deletePath = `/api/v1/namespaces/${encodeURIComponent(namespace)}` +
      `/configmaps/${encodeURIComponent(name)}`;
    run('kubectl', ['delete', '--raw', deletePath, '--filename', '-'], {
      cwd: root,
      input: JSON.stringify({
        kind: 'DeleteOptions',
        apiVersion: 'v1',
        preconditions: { uid: identity.uid, resourceVersion: identity.resourceVersion },
      }),
    });
    run('kubectl', ['create', '--filename', '-', '--namespace', namespace], {
      cwd: root, input: JSON.stringify(resource),
    });
    const readback = run('kubectl', [
      'get', 'configmap', name, '--namespace', namespace, '--output=json',
    ], {
      cwd: root,
      projectJson: projectRuntimeConfigMap,
      preserveProjectedJson: true,
    });
    let actual;
    try {
      actual = JSON.parse(readback);
    } catch (error) {
      reject(`replacement ConfigMap/${name} readback is not valid JSON: ${error.message}`, fail);
    }
    const expected = projectRuntimeConfigMap(resource);
    if (actual.kind !== 'ConfigMap' || actual.name !== name || actual.namespace !== namespace ||
        actual.immutable !== true || actual.dataDigest !== expected.dataDigest ||
        actual.binaryDataDigest !== expected.binaryDataDigest) {
      reject(`replacement ConfigMap/${name} does not match the source-owned data`, fail);
    }
  }
}
