/** Provides a temporary user-authenticated kubeconfig and bootstraps the dedicated P0 namespace. */
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';

const NAMESPACE = 'agentweaver';
const MAX_ATTEMPTS = 12;
const RETRY_DELAY_MS = 5_000;
const COMMAND_TIMEOUT_MS = 15_000;
const DEFAULT_REQUIRED_PERMISSIONS = [['get', `namespace/${NAMESPACE}`]];
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

/**
 * Checks the exact Kubernetes permissions a P0 operation needs before it reads or writes resources.
 * API failures and denied checks stop immediately; this tooling never grants a person AKS access.
 */
export function assertP0KubernetesPermissions(kubeconfigArgs, execKubectl, permissions, purpose) {
  for (const [verb, resource, namespace, subresource] of permissions) {
    const args = [...kubeconfigArgs, 'auth', 'can-i', verb, resource, '--request-timeout=15s'];
    if (namespace) args.push('--namespace', namespace);
    if (subresource) args.push(`--subresource=${subresource}`);
    let result;
    try {
      result = execKubectl(args, { check: false, timeout: COMMAND_TIMEOUT_MS });
    } catch (error) {
      result = { status: 1, stdout: '', stderr: redact(error?.message ?? String(error)) };
    }
    const output = typeof result.stdout === 'string' ? result.stdout.trim() : '';
    if (result.status === 0 && output.toLowerCase() === 'yes') continue;
    const detail = result.status === 0
      ? `authorization check returned "${output || 'no response'}"`
      : diagnostic(result);
    const permission = `"${verb} ${resource}${subresource ? ` (subresource ${subresource})` : ''}"` +
      `${namespace ? ` in namespace ${namespace}` : ''}`;
    throw new Error(
      `Kubernetes API access is unavailable or insufficient for ${purpose}: ` +
        `required permission ${permission}. ` +
        `Provision the required API connectivity and permissions outside this tooling, then retry. ` +
        `The tooling never grants a person AKS Cluster Admin. ${detail}`,
    );
  }
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

/**
 * Obtains a short-lived kubeconfig, runs the supplied action, then removes the temporary directory.
 * Required API permissions are checked before the action, and temporary credentials are always removed.
 */
export function withP0UserKubeconfig({
  resourceGroup, subscriptionId, clusterName,
}, action, {
  execAz = runAz, execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options),
  requiredPermissions = DEFAULT_REQUIRED_PERMISSIONS,
  permissionContext = 'P0 cluster operation',
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

    assertP0KubernetesPermissions(['--kubeconfig', kubeconfig], execKubectl,
      requiredPermissions, permissionContext);
    receipt = action(['--kubeconfig', kubeconfig]);
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

/**
 * Checks namespace permissions before applying the P0 namespace and retries only its readback.
 * It rejects denied access and unexpected namespace ownership rather than continuing.
 */
export function bootstrapP0Namespace({
  resourceGroup, subscriptionId, repoRoot = process.cwd(), clusterName,
}, {
  execAz = runAz, execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options), pause = wait,
} = {}) {
  return withP0UserKubeconfig({ resourceGroup, subscriptionId, clusterName }, baseArgs => {
    const manifest = join(repoRoot, 'deploy', 'k8s', 'base', 'namespace.yaml');
    const apply = execKubectl([...baseArgs, 'apply', '--filename', manifest],
      { check: false, timeout: COMMAND_TIMEOUT_MS });
    assertCommand(apply, `Could not apply the namespace-only manifest ${manifest}`);

    let receipt;
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
    return receipt;
  }, {
    execAz, execKubelogin, execKubectl,
    requiredPermissions: PERMISSIONS,
    permissionContext: 'P0 namespace bootstrap',
  });
}
