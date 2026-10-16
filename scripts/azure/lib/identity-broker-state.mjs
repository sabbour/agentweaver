import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { run, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';

const NAMESPACE = 'agentweaver-v1-p0';
const SIGNING = 'identity-broker-signing';
const KEY_RING = 'identity-broker-key-ring';
const OPTIONS = { check: false, timeout: 35_000 };

export function projectBrokerState(value) {
  if (Array.isArray(value?.items)) {
    if (value.items.length > 1) throw new Error('Exact Broker state lookup returned multiple objects.');
    return value.items.length ? projectBrokerState(value.items[0]) : null;
  }
  const result = {
    kind: value?.kind,
    metadata: {
      name: value?.metadata?.name, namespace: value?.metadata?.namespace, uid: value?.metadata?.uid,
      service: value?.metadata?.labels?.['agentweaver.io/service'],
      managedBy: value?.metadata?.labels?.['agentweaver.io/managed-by'],
    },
  };
  if (value?.kind === 'Secret') {
    result.type = value.type;
    result.keys = Object.keys(value.data ?? {}).sort();
    result.materialConfigured = ['password', 'signing.pfx'].every(key =>
      typeof value.data?.[key] === 'string' && Buffer.from(value.data[key], 'base64').length > 0);
  } else if (value?.kind === 'PersistentVolumeClaim') {
    result.spec = value.spec;
    result.phase = value.status?.phase;
  } else if (value?.kind === 'Namespace') {
    result.labels = value.metadata?.labels;
  } else {
    throw new Error('Unexpected Broker state resource kind.');
  }
  return result;
}

export function generateBrokerSigning(repoRoot) {
  // This is the only unredacted subprocess output: private bytes remain in memory
  // and go directly to kubectl stdin, never into a file, argument, log, or receipt.
  const result = spawnSync('dotnet', [
    'run', '--file', join(repoRoot, 'scripts', 'azure', 'lib', 'generate-broker-signing.cs'),
    '--verbosity', 'quiet',
  ], { encoding: 'utf8', timeout: 120_000, windowsHide: true });
  if (result.error || result.status !== 0) {
    throw new Error('Broker signing generation failed; .NET 10 SDK is required. Private subprocess output was omitted.');
  }
  let secret;
  try { secret = JSON.parse(result.stdout); } catch {
    throw new Error('Broker signing generator did not return its exact Secret envelope; private output was omitted.');
  }
  if (secret.kind !== 'Secret' || secret.metadata?.name !== SIGNING ||
      secret.metadata?.namespace !== NAMESPACE || secret.type !== 'Opaque' || secret.immutable !== true ||
      !secret.data?.['signing.pfx'] || !secret.data?.password || Object.keys(secret.data).length !== 2) {
    throw new Error('Broker signing generator returned an invalid Secret envelope; private output was omitted.');
  }
  return secret;
}

export function bootstrapIdentityBrokerState(config, {
  withKubeconfig = withP0UserKubeconfig,
  execKubectl = (args, options) => run('kubectl', args, options),
  generateSigning = generateBrokerSigning,
} = {}) {
  return withKubeconfig(config, base => {
    const args = [...base, '--namespace', NAMESPACE, '--request-timeout=30s'];
    function read(resource, name) {
      const result = execKubectl([...args, 'get', resource, '--field-selector', `metadata.name=${name}`, '-o', 'json'],
        { ...OPTIONS, projectJson: projectBrokerState, preserveProjectedJson: true });
      if (result.status !== 0) throw new Error(`Broker ${resource} readback failed: ${redact(result.stderr)}`);
      return JSON.parse(result.stdout);
    }
    function permission(verb, resource) {
      const result = execKubectl([...args, 'auth', 'can-i', verb, resource], OPTIONS);
      if (result.status !== 0 || result.stdout.trim() !== 'yes') {
        throw new Error(`Broker state permission denied: ${verb} ${resource}: ${redact(result.stderr || result.stdout)}`);
      }
    }
    const namespace = read('namespaces', NAMESPACE);
    if (namespace?.kind !== 'Namespace' || namespace.metadata.name !== NAMESPACE ||
        namespace.labels?.['pod-security.kubernetes.io/enforce'] !== 'restricted') {
      throw new Error('Broker state installation requires the existing restricted P0 namespace.');
    }
    const signing = read('secrets', SIGNING);
    const keyRing = read('persistentvolumeclaims', KEY_RING);
    function assertSigning(secret) {
      if (secret?.kind !== 'Secret' || secret.metadata.name !== SIGNING ||
          secret.metadata.namespace !== NAMESPACE || !secret.metadata.uid || secret.type !== 'Opaque' ||
          secret.metadata.service !== 'identity-broker' || secret.metadata.managedBy !== 'installer' ||
          !secret.materialConfigured ||
          !['password', 'signing.pfx'].every(key => secret.keys.includes(key))) {
        throw new Error('Existing Broker signing Secret is incomplete; refusing replacement or rotation.');
      }
    }
    function assertKeyRing(claim) {
      if (claim?.kind !== 'PersistentVolumeClaim' || claim.metadata.name !== KEY_RING ||
          claim.metadata.namespace !== NAMESPACE || !claim.metadata.uid ||
          claim.metadata.service !== 'identity-broker' || claim.metadata.managedBy !== 'installer' ||
          claim.spec?.accessModes?.length !== 1 || claim.spec.accessModes[0] !== 'ReadWriteOnce' ||
          (claim.spec.volumeMode ?? 'Filesystem') !== 'Filesystem' || claim.phase === 'Lost') {
        throw new Error('Existing Broker key-ring PVC is incompatible; refusing replacement or data loss.');
      }
    }
    if (signing) assertSigning(signing);
    if (keyRing) assertKeyRing(keyRing);
    // A surviving key ring cannot be unsealed with newly generated signing material.
    if (!signing && keyRing) throw new Error('Broker key-ring PVC exists without its signing Secret; recover the original signing material before retry.');
    if (!signing) permission('create', 'secrets');
    if (!keyRing) permission('create', 'persistentvolumeclaims');
    let signingReadback = signing;
    if (!signing) {
      const secret = generateSigning(config.repoRoot ?? process.cwd());
      const created = execKubectl([...args, 'create', '--filename', '-', '-o', 'name'],
        { ...OPTIONS, input: JSON.stringify(secret) });
      // API validation errors can echo supplied secret fields. Never surface write output.
      if (created.status !== 0) throw new Error(`Initial Broker signing Secret create failed (exit ${created.status}); private output omitted. No existing state was replaced.`);
      signingReadback = read('secrets', SIGNING);
      assertSigning(signingReadback);
    }
    let keyRingReadback = keyRing;
    if (!keyRing) {
      const created = execKubectl([...args, 'create', '--filename',
        join(config.repoRoot ?? process.cwd(), 'deploy', 'k8s', 'bootstrap', 'identity-broker', 'key-ring.yaml'),
      '-o', 'name'], OPTIONS);
      if (created.status !== 0) throw new Error(`Initial Broker key-ring PVC create failed: ${redact(created.stderr)}`);
      keyRingReadback = read('persistentvolumeclaims', KEY_RING);
      assertKeyRing(keyRingReadback);
    }
    return {
      namespace: NAMESPACE,
      signing: { name: SIGNING, uid: signingReadback.metadata.uid, created: !signing },
      keyRing: { name: KEY_RING, uid: keyRingReadback.metadata.uid, created: !keyRing, phase: keyRingReadback.phase },
      runtimeVerified: false,
    };
  });
}
