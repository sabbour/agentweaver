import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';

const NAMESPACE = 'agentweaver-v1-p0';
const MAX_ATTEMPTS = 12;
const RETRY_DELAY_MS = 5_000;
const COMMAND_TIMEOUT_MS = 15_000;
const REQUIRED_LABELS = {
  'agentweaver.io/environment': 'v1-p0',
  'agentweaver.io/managed-by': 'kustomize',
  'pod-security.kubernetes.io/enforce': 'restricted',
  'pod-security.kubernetes.io/audit': 'restricted',
  'pod-security.kubernetes.io/warn': 'restricted',
};
const PERMISSIONS = [
  ['create', 'namespaces'],
  ['get', `namespace/${NAMESPACE}`],
  ['patch', `namespace/${NAMESPACE}`],
];

function wait(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function diagnostic(result) {
  const output = [result.stderr, result.stdout].filter(Boolean).join('\n').trim();
  return redact(output || `process exited with status ${result.status}`);
}

function assertCommand(result, action) {
  if (result.status !== 0) throw new Error(`${action}: ${diagnostic(result)}`);
}

function assertNamespaceReadback(result) {
  if (result.status !== 0) return `namespace readback: ${diagnostic(result)}`;
  let namespace;
  try {
    namespace = JSON.parse(result.stdout);
  } catch {
    throw new Error('Namespace readback returned malformed JSON.');
  }
  if (namespace?.metadata?.name !== NAMESPACE) {
    throw new Error('Namespace readback did not identify the exact P0 namespace.');
  }
  const missingLabel = Object.entries(REQUIRED_LABELS)
    .find(([name, value]) => namespace.metadata.labels?.[name] !== value);
  return missingLabel
    ? `namespace readback is missing the required ${missingLabel[0]} label`
    : undefined;
}

export function bootstrapP0Namespace({
  resourceGroup, subscriptionId, repoRoot = process.cwd(), clusterName,
}, {
  execAz = runAz, execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options), pause = wait,
} = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'agentweaver-p0-kubeconfig-'));
  const kubeconfig = join(directory, 'config');
  let failure;
  let receipt;
  try {
    const credentials = execAz([
      'aks', 'get-credentials', '--resource-group', resourceGroup, '--name', clusterName,
      '--subscription', subscriptionId, '--file', kubeconfig,
    ], { check: false, timeout: COMMAND_TIMEOUT_MS });
    assertCommand(credentials, 'Could not get user-authenticated AKS credentials');

    const conversion = execKubelogin([
      'convert-kubeconfig', '--login', 'azurecli', '--kubeconfig', kubeconfig,
    ], { check: false, timeout: COMMAND_TIMEOUT_MS });
    assertCommand(conversion, 'Could not configure AKS kubeconfig to use the signed-in Azure CLI identity');

    const baseArgs = ['--kubeconfig', kubeconfig];
    let authorized = false;
    let authorizationFailure = '';
    for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt += 1) {
      authorized = true;
      for (const [verb, resource] of PERMISSIONS) {
        const result = execKubectl([...baseArgs, 'auth', 'can-i', verb, resource],
          { check: false, timeout: COMMAND_TIMEOUT_MS });
        if (result.status !== 0 || result.stdout.trim().toLowerCase() !== 'yes') {
          authorized = false;
          authorizationFailure = `${verb} ${resource}: ${diagnostic(result)}`;
          break;
        }
      }
      if (authorized) break;
      if (attempt < MAX_ATTEMPTS) pause(RETRY_DELAY_MS);
    }
    if (!authorized) {
      throw new Error(`Kubernetes namespace permissions did not become available within ${MAX_ATTEMPTS} attempts (${authorizationFailure}).`);
    }

    const manifest = join(repoRoot, 'deploy', 'k8s', 'base', 'namespace.yaml');
    const apply = execKubectl([...baseArgs, 'apply', '--filename', manifest],
      { check: false, timeout: COMMAND_TIMEOUT_MS });
    assertCommand(apply, `Could not apply the namespace-only manifest ${manifest}`);

    let readbackFailure = '';
    for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt += 1) {
      const result = execKubectl([...baseArgs, 'get', 'namespace', NAMESPACE, '-o', 'json'],
        { check: false, timeout: COMMAND_TIMEOUT_MS });
      readbackFailure = assertNamespaceReadback(result) ?? '';
      if (!readbackFailure) {
        receipt = { namespace: NAMESPACE, clusterName };
        break;
      }
      if (attempt < MAX_ATTEMPTS) pause(RETRY_DELAY_MS);
    }
    if (!receipt) {
      throw new Error(`P0 namespace readback did not converge within ${MAX_ATTEMPTS} attempts (${readbackFailure}).`);
    }
  } catch (error) {
    failure = error;
  }

  try {
    rmSync(directory, { recursive: true, force: true });
  } catch (cleanupError) {
    const cleanupMessage = redact(cleanupError.message);
    if (failure) {
      throw new Error(`${failure.message}; temporary kubeconfig cleanup failed: ${cleanupMessage}`);
    }
    throw new Error(`Temporary kubeconfig cleanup failed: ${cleanupMessage}`);
  }
  if (failure) throw failure;
  return receipt;
}
