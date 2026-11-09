#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { parse, stringify } from 'yaml';
import { parseKustomizeResources } from './kustomize-runtime.mjs';
import {
  findImmutableRuntimeConfigMapReplacements,
  replaceImmutableRuntimeConfigMaps,
  verifyImmutableRuntimeConfigMapReplacementPermissions,
} from './immutable-runtime-config-maps.mjs';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { DEFAULT_REGISTRY, measureAgentHostRegistryImage, parseRegistryTarget } from './registry.mjs';
import { run, redact } from '../azure/lib/exec.mjs';
import { preserveIdentityBrokerProviderSetup } from '../azure/lib/identity-broker-provider-setup.mjs';
import {
  applyModelBindingsOverlay,
  parseModelBindingsOverlay,
  requireLiveAzureCostPreserved,
  stampEventsRuntimeConfigDigest,
} from './model-bindings-overlay.mjs';

const NAMESPACE = 'agentweaver';
const BROKER_RUNTIME_CONFIG = 'identity-broker-runtime-config-v3';
const PROJECTS_CONFIG_RUNTIME_CONFIG = 'projects-config-runtime-config';
const PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION = 'agentweaver.io/local-deploy-restore-replicas';
const AGENTHOST_RUNTIME_CONFIG = 'agenthost-runtime-v1';
const ENVIRONMENT_RUNTIME_CONFIG = 'environment-runtime-config';
const EVENTS_RUNTIME_CONFIG = 'events-and-sessions-runtime-config';
const RUNTIME_CONFIG_MAPS = new Set([AGENTHOST_RUNTIME_CONFIG, ENVIRONMENT_RUNTIME_CONFIG]);
const AGENTHOST_CONFIGURATION_ENDPOINT =
  'https://agenthost-runtime.agentweaver.svc.cluster.local:8443/runtime/v1/configure';
const AGENT_SANDBOX_ENV_PREFIX = 'Environment__Sandbox__AgentSandbox__';
const AGENT_SANDBOX_IMAGE_SETTING = `${AGENT_SANDBOX_ENV_PREFIX}ContainerImage`;
const AGENT_SANDBOX_PLATFORM_SETTING = `${AGENT_SANDBOX_ENV_PREFIX}ContainerImagePlatform`;
const AGENT_SANDBOX_COMPRESSED_BYTES_SETTING =
  `${AGENT_SANDBOX_ENV_PREFIX}ContainerImageCompressedPullBytes`;
const ENVIRONMENT_SERVICE_ACCOUNT = 'system:serviceaccount:agentweaver:environment';
const rbacEntries = (group, resource, verbs, options = {}) => verbs.map(verb => ({
  verb,
  resource: group ? `${resource}.${group}` : resource,
  ...options,
}));
export const ENVIRONMENT_RBAC_REQUIREMENTS = [
  ...rbacEntries('extensions.agents.x-k8s.io', 'sandboxclaims', ['get', 'list', 'create', 'patch', 'delete']),
  ...rbacEntries('extensions.agents.x-k8s.io', 'sandboxtemplates', ['get', 'create', 'delete']),
  ...rbacEntries('extensions.agents.x-k8s.io', 'sandboxwarmpools', ['get', 'create', 'delete']),
  ...rbacEntries('agents.x-k8s.io', 'sandboxes', ['get', 'list']),
  ...rbacEntries('', 'pods', ['get', 'list', 'create', 'patch', 'delete']),
  ...rbacEntries('', 'services', ['get']),
  ...rbacEntries('', 'configmaps', ['get'], { resourceName: 'agenthost-runtime-v1' }),
  ...rbacEntries('', 'events', ['list']),
  ...rbacEntries('', 'persistentvolumeclaims', ['get', 'create', 'delete']),
  ...rbacEntries('cilium.io', 'ciliumnetworkpolicies', ['get', 'create', 'update', 'delete']),
  ...rbacEntries('', 'pods', ['get'], { subresource: 'log' }),
  ...rbacEntries('', 'pods', ['create'], { subresource: 'exec' }),
  ...rbacEntries('', 'pods', ['create'], { subresource: 'attach' }),
  ...rbacEntries('node.k8s.io', 'runtimeclasses', ['get'], {
    resourceName: 'kata-vm-isolation', cluster: true,
  }),
  ...rbacEntries('storage.k8s.io', 'storageclasses', ['get'], { cluster: true }),
  ...rbacEntries('', 'persistentvolumes', ['get', 'patch'], { cluster: true }),
];
const DEPLOYMENTS = [
  'identity-broker',
  'events-and-sessions',
  'gateway',
  'mcp',
  'web',
  'orchestrator',
  'environment',
  'knowledge',
  'projects-config',
];
const P1_MIGRATION_IMAGES = [
  ['environment-migration', 'agentweaver.environment'],
  ['orchestrator-migration', 'agentweaver.orchestrator'],
  ['events-and-sessions-migration', 'agentweaver.eventsandsessions'],
];
const MIGRATION_IMAGES = new Set([
  'agentweaver.identity.broker',
  ...P1_MIGRATION_IMAGES.map(([, repository]) => repository),
  'agentweaver.projects.config',
]);
const DEPLOYMENT_NAME = /^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/;
const IMAGE_TAG = /^sha-[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const JOB_TIMEOUT = '15m';
const POD_TERMINATION_TIMEOUT_MS = 5 * 60_000;
const IMMUTABLE_CONFIGMAP_CACHE_WAIT_MS = 70_000;
const CLIENT_ID = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const APPLY_RESOURCES = [
  ['', 'resourcequotas'],
  ['', 'limitranges'],
  ['', 'serviceaccounts'],
  ['', 'configmaps'],
  ['', 'services'],
  ['', 'networkpolicies'],
  ['cilium.io', 'ciliumnetworkpolicies'],
  ['gateway.networking.k8s.io', 'httproutes'],
];
const REQUIRED_AUTHORIZATION = [
  ...APPLY_RESOURCES.flatMap(([group, resource]) =>
    ['create', 'get', 'patch'].map(verb => [verb, group ? `${resource}.${group}` : resource])),
  ...['create', 'get', 'list', 'watch'].map(verb => [verb, 'jobs.batch']),
  ...['get', 'list', 'watch'].map(verb => [verb, 'pods']),
  ['get', 'pods/log'],
  ['get', 'serviceaccounts'],
  ['get', 'configmaps'],
  ['create', 'deployments.apps'],
];

export const DEPLOY_KUSTOMIZE_USAGE = `Usage: npm run deploy:kustomize -- --image-manifest <file.json> [--registry <registry>] [model options] --execute
   or: npm run deploy:kustomize -- --image-tag sha-<git-commit> [--registry <registry>] [model options] --execute

Model options:
  --model-bindings <file.json>         Add the environment BYOK model bindings in this file.
  --remove-model-binding <reference>   Remove this live BYOK model binding. You can repeat it.

The manifest selects published images to update; omitted images keep their
currently deployed references. The command checks the cluster prerequisites,
creates the workspace StorageClass when it is missing, runs migrations for
selected services, applies namespaced Kustomize resources, waits for every
Deployment rollout, and then runs a sandbox smoke check.
`;
export const DEPLOY_KUSTOMIZE_REGISTRY_OPTION = true;
export const DEPLOY_KUSTOMIZE_MODEL_BINDINGS_OPTION = true;

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

export function parseDeployKustomizeArgs(argv) {
  let imageTag;
  let imageManifest;
  let registry = DEFAULT_REGISTRY;
  let hasRegistry = false;
  let execute = false;
  let modelBindings;
  const removeModelBindings = [];
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--execute') execute = true;
    else if (arg === '--model-bindings') {
      if (modelBindings !== undefined) fail('--model-bindings may be specified only once.');
      modelBindings = argv[++index];
      if (!modelBindings || modelBindings.startsWith('--')) fail('--model-bindings requires a file path.');
    } else if (arg === '--remove-model-binding') {
      const reference = argv[++index];
      if (!reference || reference.startsWith('--')) fail('--remove-model-binding requires a model reference.');
      if (removeModelBindings.includes(reference)) fail(`--remove-model-binding names ${reference} more than once.`);
      removeModelBindings.push(reference);
    } else if (arg === '--image-tag') {
      if (imageTag !== undefined) fail('--image-tag may be specified only once.');
      imageTag = argv[++index];
    } else if (arg === '--image-manifest') {
      if (imageManifest !== undefined) fail('--image-manifest may be specified only once.');
      imageManifest = argv[++index];
      if (!imageManifest || imageManifest.startsWith('--')) fail('--image-manifest requires a file path.');
    } else if (arg === '--registry') {
      if (hasRegistry || argv[index + 1] === undefined || argv[index + 1].startsWith('--')) {
        fail('--registry requires a registry login server.');
      }
      hasRegistry = true;
      registry = argv[++index];
      try {
        parseRegistryTarget(registry);
      } catch {
        fail('registry must be ghcr.io/sabbour or an Azure Container Registry login server.');
      }
    } else fail(`unknown argument "${arg}".`);
  }
  if ((imageTag === undefined) === (imageManifest === undefined)) {
    fail('provide exactly one of --image-manifest or --image-tag.');
  }
  if (imageTag !== undefined && !IMAGE_TAG.test(imageTag ?? '')) {
    fail('--image-tag must be a normal sha-<40-character-commit> image tag.');
  }
  if (!execute) fail('--execute is required to run migrations and apply workloads.');
  const options = {
    ...(registry === DEFAULT_REGISTRY ? {} : { registry }),
    ...(modelBindings === undefined ? {} : { modelBindings }),
    ...(removeModelBindings.length ? { removeModelBindings } : {}),
  };
  return imageManifest === undefined
    ? { imageTag, ...options }
    : { imageManifest, ...options };
}

export function parseImageManifest(source) {
  let manifest;
  try {
    parse(source, { uniqueKeys: true });
    manifest = JSON.parse(source);
  } catch (error) {
    fail(`image manifest must be valid JSON with unique keys: ${error.message}`);
  }
  if (!manifest || Array.isArray(manifest) || typeof manifest !== 'object' ||
      Object.keys(manifest).length !== 1 || !manifest.images || Array.isArray(manifest.images) ||
      typeof manifest.images !== 'object') {
    fail('image manifest must contain only an "images" object mapping repository names to tags or digests.');
  }
  const entries = Object.entries(manifest.images);
  if (!entries.length) fail('image manifest must select at least one image.');
  const images = new Map();
  for (const [repository, reference] of entries) {
    if (!IMAGE_REPOSITORIES.includes(repository)) {
      fail(`image manifest contains unknown repository "${repository}".`);
    }
    if (typeof reference !== 'string' || (!IMAGE_TAG.test(reference) && !IMAGE_DIGEST.test(reference))) {
      fail(`image manifest reference for ${repository} must be sha-<40-character-commit> or sha256:<64-hex-digest>.`);
    }
    images.set(repository, reference);
  }
  return images;
}

function must(exec, command, args, options, action) {
  const result = exec(command, args, { check: false, ...options });
  if (result.status !== 0) {
    const diagnostic = (result.stderr || result.stdout || `exit ${result.status}`).trim();
    fail(`${action}: ${diagnostic}`);
  }
  return result;
}

function kubectl(exec, context, args, root, action, options = {}) {
  return must(exec, 'kubectl', [...args, '--context', context, '--namespace', NAMESPACE],
    { cwd: root, ...options }, action);
}

function verifyAuthorization(exec, context, root, migrationsRequired) {
  for (const [verb, resource] of REQUIRED_AUTHORIZATION) {
    if (!migrationsRequired && ['jobs.batch', 'pods', 'pods/log'].includes(resource)) continue;
    const result = kubectl(exec, context, ['auth', 'can-i', verb, resource], root,
      `Could not check Kubernetes permission ${verb} ${resource}`);
    if (result.stdout.trim() !== 'yes') {
      fail(`The selected identity cannot ${verb} ${resource} in namespace ${NAMESPACE}.`);
    }
  }
  for (const name of DEPLOYMENTS)
    for (const verb of ['get', 'list', 'patch', 'watch']) {
      const result = kubectl(exec, context, [
        'auth', 'can-i', verb, `deployment/${name}`,
      ], root, `Could not check Kubernetes permission ${verb} deployment/${name}`);
      if (result.stdout.trim() !== 'yes')
        fail(`The selected identity cannot ${verb} deployment/${name} in namespace ${NAMESPACE}.`);
    }
}

function verifyEnvironmentRuntimeAuthorization(exec, context, root) {
  for (const { verb, resource, resourceName, subresource, cluster } of ENVIRONMENT_RBAC_REQUIREMENTS) {
    const target = resourceName ? `${resource}/${resourceName}` : resource;
    const args = ['auth', 'can-i', verb, target];
    if (subresource) args.push(`--subresource=${subresource}`);
    args.push('--as', ENVIRONMENT_SERVICE_ACCOUNT);
    if (!cluster) args.push('--namespace', NAMESPACE);
    args.push('--context', context);
    const result = exec('kubectl', args, { check: false, cwd: root });
    if (result.status !== 0) {
      fail(`Could not check effective permission ${verb} ${target}` +
        `${subresource ? ` --subresource=${subresource}` : ''} for ${ENVIRONMENT_SERVICE_ACCOUNT}; ` +
        `the selected identity must be allowed to impersonate that ServiceAccount: ` +
        `${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
    }
    if (result.stdout.trim().toLowerCase() !== 'yes') {
      fail(`${ENVIRONMENT_SERVICE_ACCOUNT} lacks effective permission ${verb} ${target}` +
        `${subresource ? ` --subresource=${subresource}` : ''}` +
        `${cluster ? ' at cluster scope' : ` in namespace ${NAMESPACE}`}.`);
    }
  }
}

const WORKSPACE_MOUNT_OPTIONS = [
  'dir_mode=0755', 'file_mode=0644', 'uid=1000', 'gid=1000', 'mfsymlinks', 'cache=strict', 'actimeo=30',
];
const WORKSPACE_STORAGE_CLASS_FILE = path.join('deploy', 'k8s', 'cluster', 'workspace-storageclass.yaml');
const KATA_POOL_REMEDY = 'Run the infrastructure deployment (npm run azure:deploy) to create the kata node pool.';
const SMOKE_LABEL = 'agentweaver.io/install-smoke-check';
const SMOKE_AUTHORIZATION = [
  ...['create', 'get', 'list', 'delete'].flatMap(verb => [[verb, 'pods'], [verb, 'persistentvolumeclaims']]),
  ['get', 'pods/log'],
];
const SMOKE_POLLS = 120;
const SMOKE_POLL_MILLISECONDS = 5000;
const SMOKE_POD_DEADLINE_SECONDS = 900;

/** Reads one cluster-scoped object with the Environment identity; returns undefined when it does not exist. */
function readClusterResourceAsEnvironment(exec, context, root, kind, name) {
  const result = exec('kubectl', ['get', kind, name, '--ignore-not-found=true', '--output=json',
    '--as', ENVIRONMENT_SERVICE_ACCOUNT, '--context', context], { check: false, cwd: root });
  if (result.status !== 0) {
    fail(`Could not read ${kind}/${name} as ${ENVIRONMENT_SERVICE_ACCOUNT}: ` +
      `${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  if (!result.stdout.trim()) return undefined;
  try {
    return JSON.parse(result.stdout);
  } catch {
    fail(`Kubernetes returned invalid JSON for ${kind}/${name}.`);
  }
}

/**
 * Checks, before any write, the cluster-scoped prerequisites that Environment needs at run time:
 * the workspace StorageClass, a Ready node for the sandbox RuntimeClass, and the smoke check permissions.
 * Returns the product StorageClass to create when it is missing.
 */
function verifyClusterPrerequisites(exec, context, root, resources) {
  const data = resources.find(resource => resource.kind === 'ConfigMap' &&
    resource.metadata?.name === ENVIRONMENT_RUNTIME_CONFIG)?.data ?? {};
  const storageClassName = data.Environment__Storage__AzureFiles__StorageClassName;
  const runtimeClassName = data[`${AGENT_SANDBOX_ENV_PREFIX}RuntimeClassName`];
  if (!storageClassName || !runtimeClassName) {
    fail(`ConfigMap/${ENVIRONMENT_RUNTIME_CONFIG} must name the workspace StorageClass and sandbox RuntimeClass.`);
  }

  for (const [verb, resource] of SMOKE_AUTHORIZATION) {
    const result = kubectl(exec, context, ['auth', 'can-i', verb, resource], root,
      `Could not check Kubernetes permission ${verb} ${resource}`);
    if (result.stdout.trim() !== 'yes') {
      fail(`The selected identity cannot ${verb} ${resource} in namespace ${NAMESPACE}; ` +
        'this permission is required for the install smoke check.');
    }
  }

  let createStorageClass;
  const storageClass = readClusterResourceAsEnvironment(exec, context, root, 'storageclass', storageClassName);
  if (!storageClass) {
    const productClass = parse(readFileSync(path.join(root, WORKSPACE_STORAGE_CLASS_FILE), 'utf8'));
    if (productClass?.metadata?.name !== storageClassName) {
      fail(`StorageClass/${storageClassName} is missing. Create it before the deployment, ` +
        `or use the product StorageClass in ${WORKSPACE_STORAGE_CLASS_FILE}.`);
    }
    requireClusterPermission(exec, context, root, 'create', 'storageclasses',
      `create the missing StorageClass/${storageClassName}`);
    createStorageClass = productClass;
  } else {
    const mountOptions = storageClass.mountOptions ?? [];
    if (storageClass.provisioner !== 'file.csi.azure.com' ||
        mountOptions.length !== WORKSPACE_MOUNT_OPTIONS.length ||
        WORKSPACE_MOUNT_OPTIONS.some(option => !mountOptions.includes(option))) {
      fail(`StorageClass/${storageClassName} must use file.csi.azure.com with exactly these mount options: ` +
        `${WORKSPACE_MOUNT_OPTIONS.join(', ')}. These fields cannot change after creation: ` +
        'delete the StorageClass when no workspace volume uses it, then run the deployment again.');
    }
  }

  const runtimeClass = readClusterResourceAsEnvironment(exec, context, root, 'runtimeclass', runtimeClassName);
  if (!runtimeClass) fail(`RuntimeClass/${runtimeClassName} is missing. ${KATA_POOL_REMEDY}`);
  const selector = Object.entries(runtimeClass.scheduling?.nodeSelector ?? {})
    .map(([key, value]) => `${key}=${value}`).join(',');
  if (selector) {
    requireClusterPermission(exec, context, root, 'list', 'nodes',
      `check for RuntimeClass/${runtimeClassName} nodes`);
    const output = must(exec, 'kubectl', ['get', 'nodes', '--selector', selector, '--output=json',
      '--context', context], { cwd: root }, 'Could not list sandbox nodes').stdout;
    let nodes;
    try {
      nodes = JSON.parse(output).items ?? [];
    } catch {
      fail('Kubernetes returned invalid JSON for the sandbox node list.');
    }
    const schedulable = nodes.filter(node => node.spec?.unschedulable !== true &&
      node.status?.conditions?.some(condition => condition.type === 'Ready' && condition.status === 'True'));
    if (!schedulable.length) {
      fail(`No Ready, schedulable node matches RuntimeClass/${runtimeClassName} (${selector}); ` +
        `${nodes.length} matching node(s) found. ${KATA_POOL_REMEDY}`);
    }
  }
  return { storageClassName, runtimeClassName, createStorageClass };
}

function requireClusterPermission(exec, context, root, verb, resource, purpose) {
  const permission = exec('kubectl', ['auth', 'can-i', verb, resource, '--context', context],
    { check: false, cwd: root });
  if (permission.status !== 0 || permission.stdout.trim().toLowerCase() !== 'yes') {
    fail(`The selected identity cannot ${verb} ${resource} at cluster scope; ` +
      `this permission is required to ${purpose}.`);
  }
}

function smokePodFailure(pod) {
  const scheduled = pod.status?.conditions?.find(condition => condition.type === 'PodScheduled');
  if (scheduled?.status === 'False') return `not scheduled: ${scheduled.reason ?? ''} ${scheduled.message ?? ''}`.trim();
  const waiting = pod.status?.containerStatuses?.find(status => status.state?.waiting)?.state.waiting;
  if (waiting) return `waiting: ${waiting.reason ?? ''} ${waiting.message ?? ''}`.trim();
  return `phase ${pod.status?.phase ?? 'unknown'}`;
}

const SMOKE_FAILURES = {
  dns: 'could not resolve environment.agentweaver.svc.cluster.local; check cluster DNS and the AgentHost DNS egress rules',
  'environment-connect': 'could not open TCP 8443 to Environment; check the agenthost-egress and ' +
    'environment-agenthost-ingress network policies',
  'storage-write': 'could not write and read a file on the workspace volume',
  'storage-symlink': 'could not create a symbolic link on the workspace volume; the mfsymlinks mount option is missing',
  'storage-owner': 'workspace files are not owned by UID 1000; check the uid and gid mount options',
};

/**
 * Returns one keyless probe URL (`<BaseUrl path>/models`) for each BYOK endpoint host in the applied
 * AgentHost runtime map. It reads only the configured BaseUrls, not the egress policy, and drops
 * any query string or fragment so that no value from them reaches the network.
 */
export function byokEndpointProbeUrls(resources) {
  const runtimeConfig = resources.find(resource =>
    resource.kind === 'ConfigMap' && resource.metadata?.name === AGENTHOST_RUNTIME_CONFIG);
  let bindings;
  try {
    bindings = JSON.parse(runtimeConfig?.data?.['appsettings.json'])?.AgentHost?.ModelBindings ?? {};
  } catch {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain valid appsettings.json.`);
  }
  const urls = new Map();
  for (const [reference, binding] of Object.entries(bindings)) {
    if (binding?.SourceMode !== 'Byok') continue;
    let endpoint;
    try {
      endpoint = new URL(binding.Provider?.BaseUrl);
    } catch {
      endpoint = undefined;
    }
    if (!endpoint || endpoint.protocol !== 'https:' || endpoint.username || endpoint.password ||
        !DNS_NAME.test(endpoint.hostname)) {
      fail(`BYOK model binding ${reference} must use an HTTPS BaseUrl with a DNS host name.`);
    }
    if (!urls.has(endpoint.host)) {
      urls.set(endpoint.host, `${endpoint.origin}${endpoint.pathname.replace(/\/+$/, '')}/models`);
    }
  }
  return [...urls.values()];
}

/**
 * Starts a short-lived pod with the AgentHost labels, sandbox RuntimeClass, and a workspace volume.
 * The pod uses the same network policies as real AgentHost pods. It has no token, Secret, or settings.
 */
async function runInstallSmokeCheck(exec, context, root, prerequisites, image, probeUrls, sleep, log) {
  const name = `agentweaver-install-smoke-${randomUUID().slice(0, 8)}`;
  const selector = `${SMOKE_LABEL}=true`;
  kubectl(exec, context, ['delete', 'pods,persistentvolumeclaims', '--selector', selector,
    '--ignore-not-found=true', '--wait=false'], root, 'Could not delete earlier install smoke check resources');
  const labels = {
    'app.kubernetes.io/name': 'agenthost',
    'agentweaver.io/service': 'agenthost',
    'agentweaver.io/workload': 'agenthost',
    'agentweaver.io/identity-client': 'true',
    [SMOKE_LABEL]: 'true',
  };
  const script = [
    'fail() { echo "SMOKE_FAIL $1"; exit 1; }',
    'getent hosts environment.agentweaver.svc.cluster.local >/dev/null || fail dns',
    'timeout 10 bash -c ": </dev/tcp/environment.agentweaver.svc.cluster.local/8443" || fail environment-connect',
    'echo ok > /workspace/smoke.txt && [ "$(cat /workspace/smoke.txt)" = ok ] || fail storage-write',
    'ln -s smoke.txt /workspace/smoke-link && [ "$(cat /workspace/smoke-link)" = ok ] || fail storage-symlink',
    '[ "$(stat -c %u /workspace/smoke.txt)" = 1000 ] || fail storage-owner',
    // Keyless HTTPS GET for each BYOK URL (positional arguments). git has no credential or prompt here.
    // The trailing "#" makes libcurl drop the "/info/refs?service=..." suffix that git appends,
    // so the endpoint receives exactly GET <url> and answers 401 or 403 before routing.
    'for url in "$@"; do',
    '  host="${url#https://}"; host="${host%%/*}"',
    '  status="$(GIT_TRACE_CURL=1 GIT_TRACE_CURL_NO_DATA=1 GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/bin/false ' +
      'GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null timeout 30 git ls-remote "$url#" 2>&1 >/dev/null ' +
      '| sed -n -E \'s#.*Recv header: HTTP/[0-9.]+ ([0-9]{3}).*#\\1#p\' | head -n 1)"',
    '  [ -n "$status" ] || fail "byok:$host"',
    '  echo "SMOKE_BYOK $host $status"',
    'done',
    'echo SMOKE_OK',
  ].join('\n');
  const manifest = {
    apiVersion: 'v1',
    kind: 'List',
    items: [{
      apiVersion: 'v1',
      kind: 'PersistentVolumeClaim',
      metadata: { name, namespace: NAMESPACE, labels: { [SMOKE_LABEL]: 'true' } },
      spec: {
        accessModes: ['ReadWriteMany'],
        storageClassName: prerequisites.storageClassName,
        resources: { requests: { storage: '1Gi' } },
      },
    }, {
      apiVersion: 'v1',
      kind: 'Pod',
      metadata: { name, namespace: NAMESPACE, labels },
      spec: {
        runtimeClassName: prerequisites.runtimeClassName,
        restartPolicy: 'Never',
        automountServiceAccountToken: false,
        enableServiceLinks: false,
        activeDeadlineSeconds: SMOKE_POD_DEADLINE_SECONDS,
        securityContext: {
          runAsUser: 1000,
          runAsGroup: 1000,
          fsGroup: 1000,
          runAsNonRoot: true,
          seccompProfile: { type: 'RuntimeDefault' },
        },
        containers: [{
          name: 'smoke',
          image,
          command: ['/bin/bash', '-c', script, 'smoke', ...probeUrls],
          resources: {
            requests: { cpu: '100m', memory: '128Mi' },
            limits: { cpu: '250m', memory: '256Mi' },
          },
          securityContext: {
            allowPrivilegeEscalation: false,
            readOnlyRootFilesystem: true,
            capabilities: { drop: ['ALL'] },
          },
          volumeMounts: [{ name: 'workspace', mountPath: '/workspace' }],
        }],
        volumes: [{ name: 'workspace', persistentVolumeClaim: { claimName: name } }],
      },
    }],
  };
  log(`Starting install smoke check pod ${name} with RuntimeClass/${prerequisites.runtimeClassName}.`);
  try {
    const created = exec('kubectl', ['create', '--filename', '-', '--context', context, '--namespace', NAMESPACE],
      { check: false, cwd: root, input: stringify(manifest) });
    if (created.status !== 0) {
      const diagnostic = (created.stderr || created.stdout || `exit ${created.status}`).trim();
      if (/exceeded quota/i.test(diagnostic)) {
        fail('The namespace ResourceQuota has no room for the install smoke check (1 pod, 1 PVC, ' +
          `100m CPU, 128Mi memory). Wait for running sandboxes to finish, then run the deployment again: ${diagnostic}`);
      }
      fail(`Could not create the install smoke check: ${diagnostic}`);
    }
    let pod;
    for (let poll = 0; poll < SMOKE_POLLS; poll += 1) {
      const output = kubectl(exec, context, ['get', 'pod', name, '--output=json'], root,
        'Could not read the install smoke check pod').stdout;
      try {
        pod = JSON.parse(output);
      } catch {
        fail('Kubernetes returned invalid JSON for the install smoke check pod.');
      }
      if (['Succeeded', 'Failed'].includes(pod.status?.phase)) break;
      await sleep(SMOKE_POLL_MILLISECONDS);
    }
    const phase = pod?.status?.phase;
    if (!['Succeeded', 'Failed'].includes(phase)) {
      fail(`The install smoke check pod did not finish in ${SMOKE_POLLS * SMOKE_POLL_MILLISECONDS / 60000} ` +
        `minutes (${smokePodFailure(pod ?? {})}). Check the Kata node pool and StorageClass/` +
        `${prerequisites.storageClassName}.`);
    }
    const logs = kubectl(exec, context, ['logs', `pod/${name}`], root,
      'Could not read the install smoke check logs').stdout;
    for (const [, host, status] of logs.matchAll(/^SMOKE_BYOK (\S+) (\d{3})$/gm)) {
      log(['401', '403'].includes(status)
        ? `BYOK endpoint ${host} answered HTTP ${status} without a key: DNS, egress, and TLS work.`
        : `Warning: BYOK endpoint ${host} answered HTTP ${status} without a key (expected 401 or 403). ` +
          'DNS, egress, and TLS work; check that the BaseUrl is correct.');
    }
    if (phase === 'Succeeded' && /^SMOKE_OK$/m.test(logs)) {
      log('Install smoke check passed: AgentHost DNS and the workspace volume work in the sandbox runtime.');
      return;
    }
    const reason = logs.match(/^SMOKE_FAIL (\S+)$/m)?.[1];
    const byokHost = reason?.startsWith('byok:') ? reason.slice(5) : undefined;
    fail(`Install smoke check failed: the AgentHost-labelled sandbox pod ` +
      (byokHost
        ? `got no HTTPS response from BYOK endpoint ${byokHost} (DNS, connection, or TLS failure, or a ` +
          '30-second timeout); check DNS for the host, the AgentHost model egress policy, and TLS.'
        : `${SMOKE_FAILURES[reason] ?? `stopped in phase ${phase} without a result`}.`));
  } finally {
    const cleanup = exec('kubectl', ['delete', `pod/${name}`, `persistentvolumeclaim/${name}`,
      '--ignore-not-found=true', '--wait=false', '--context', context, '--namespace', NAMESPACE],
    { check: false, cwd: root });
    if (cleanup.status !== 0) log(`Could not delete install smoke check resources ${name}; delete them manually.`);
  }
}

function verifyAgentHostTlsSecret(exec, context, root) {
  const result = kubectl(exec, context, [
    'get', 'secret', 'agenthost-tls', '--ignore-not-found=true', '--output=wide',
  ], root, 'Could not check Secret/agenthost-tls metadata');
  const rows = result.stdout.trim().split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const row = rows.find(line => line.split(/\s+/)[0] === 'agenthost-tls');
  if (!row) fail('Secret/agenthost-tls is missing; the operator must provide the TLS Secret.');
  const [name, type] = row.split(/\s+/);
  if (name !== 'agenthost-tls' || type !== 'kubernetes.io/tls') {
    fail('Secret/agenthost-tls must exist in namespace agentweaver with type kubernetes.io/tls.');
  }
}

function verifyEnvironmentActivationAuthorization(exec, context, root) {
  for (const { verb, resource, subresource } of [
    { verb: 'update', resource: 'deployments.apps/environment', subresource: 'scale' },
    { verb: 'patch', resource: 'deployments.apps/environment', subresource: 'scale' },
    { verb: 'patch', resource: 'deployment/environment' },
    { verb: 'list', resource: 'pods' },
  ]) {
    const args = ['auth', 'can-i', verb, resource];
    if (subresource) args.push(`--subresource=${subresource}`);
    const permission = `${verb} ${resource}${subresource ? ` --subresource=${subresource}` : ''}`;
    const result = kubectl(exec, context, args, root,
      `Could not check Kubernetes permission ${permission} for Environment activation`);
    if (result.stdout.trim().toLowerCase() !== 'yes') {
      fail(`The selected identity cannot ${permission} in namespace ${NAMESPACE}; ` +
        'this permission is required before Environment activation.');
    }
  }
}

function readResource(exec, context, kind, name, root) {
  const result = kubectl(exec, context, ['get', kind, name, '--output=json'], root,
    `Required ${kind}/${name} is unavailable`);
  try {
    return JSON.parse(result.stdout);
  } catch {
    fail(`Kubernetes returned invalid JSON for ${kind}/${name}.`);
  }
}

function readExistingRuntimeConfigMap(exec, context, root) {
  return readExistingNamedRuntimeConfigMap(exec, context, root, PROJECTS_CONFIG_RUNTIME_CONFIG);
}

function readExistingNamedRuntimeConfigMap(exec, context, root, name) {
  const result = kubectl(exec, context, [
    'get', 'configmap', name, '--ignore-not-found=true', '--output=json',
  ], root, `Could not read ConfigMap/${name}`);
  if (!result.stdout.trim()) return undefined;
  let resource;
  try {
    resource = JSON.parse(result.stdout);
  } catch {
    fail(`Kubernetes returned invalid JSON for ConfigMap/${name}.`);
  }
  if (resource.kind !== 'ConfigMap' || resource.metadata?.name !== name ||
      resource.metadata?.namespace !== NAMESPACE) {
    fail(`Kubernetes returned an unexpected object for ConfigMap/${name}.`);
  }
  return resource;
}

function readExistingProjectsConfigDeployment(exec, context, root) {
  return readExistingDeployment(exec, context, root, 'projects-config');
}

function readExistingDeployment(exec, context, root, name) {
  const result = kubectl(exec, context, [
    'get', 'deployment', name, '--ignore-not-found=true', '--output=json',
  ], root, `Could not read deployment/${name}`);
  if (!result.stdout.trim()) return undefined;
  try {
    return JSON.parse(result.stdout);
  } catch {
    fail(`Kubernetes returned invalid JSON for deployment/${name}.`);
  }
}

function sameConfigMapData(left, right) {
  const leftData = left.data ?? {};
  const rightData = right.data ?? {};
  return Object.keys(leftData).length === Object.keys(rightData).length &&
    Object.entries(leftData).every(([key, value]) => rightData[key] === value);
}

function sameRuntimeConfig(left, right) {
  return left.immutable === right.immutable && sameConfigMapData(left, right);
}

function configMapReferences(deployment) {
  const podSpec = deployment.spec?.template?.spec;
  const containers = [
    ...(Array.isArray(podSpec?.containers) ? podSpec.containers : []),
    ...(Array.isArray(podSpec?.initContainers) ? podSpec.initContainers : []),
    ...(Array.isArray(podSpec?.ephemeralContainers) ? podSpec.ephemeralContainers : []),
  ].filter(container => container && typeof container === 'object' && !Array.isArray(container));
  const references = new Set();
  for (const container of containers) {
    for (const source of Array.isArray(container.envFrom) ? container.envFrom : []) {
      if (source && typeof source === 'object' &&
          typeof source.configMapRef?.name === 'string') references.add(source.configMapRef.name);
    }
    for (const setting of Array.isArray(container.env) ? container.env : []) {
      const name = setting?.valueFrom?.configMapKeyRef?.name;
      if (typeof name === 'string') references.add(name);
    }
  }
  for (const volume of Array.isArray(podSpec?.volumes) ? podSpec.volumes : []) {
    if (!volume || typeof volume !== 'object' || Array.isArray(volume)) continue;
    if (typeof volume.configMap?.name === 'string') references.add(volume.configMap.name);
    for (const source of Array.isArray(volume.projected?.sources) ? volume.projected.sources : []) {
      if (!source || typeof source !== 'object' || Array.isArray(source)) continue;
      if (typeof source.configMap?.name === 'string') references.add(source.configMap.name);
    }
  }
  return references;
}

function findBrokerRuntimeConfig(resources) {
  const namedResources = resources.filter(resource =>
    resource && typeof resource === 'object' &&
    resource.metadata?.name === BROKER_RUNTIME_CONFIG);
  if (namedResources.length !== 1) {
    fail(`Kustomize must render exactly one ConfigMap/${BROKER_RUNTIME_CONFIG}.`);
  }
  const runtimeConfig = namedResources[0];
  if (runtimeConfig.apiVersion !== 'v1' || runtimeConfig.kind !== 'ConfigMap' ||
      runtimeConfig.metadata?.namespace !== NAMESPACE || runtimeConfig.immutable !== true) {
    fail(`Kustomize must render immutable v1 ConfigMap/${BROKER_RUNTIME_CONFIG} in namespace ${NAMESPACE}.`);
  }
  const deployments = resources.filter(resource =>
    resource && typeof resource === 'object' &&
    resource.kind === 'Deployment' && resource.metadata?.name === 'identity-broker');
  if (deployments.length !== 1 || deployments[0].apiVersion !== 'apps/v1' ||
      deployments[0].metadata?.namespace !== NAMESPACE ||
      !configMapReferences(deployments[0]).has(BROKER_RUNTIME_CONFIG)) {
    fail(`Kustomize must render deployment/identity-broker with a reference to ConfigMap/${BROKER_RUNTIME_CONFIG}.`);
  }
  return runtimeConfig;
}

function configMapStringData(resource, field, differences) {
  const values = resource[field];
  if (values === undefined) return {};
  if (!values || typeof values !== 'object' || Array.isArray(values)) {
    differences.add(field);
    return {};
  }
  for (const [key, value] of Object.entries(values)) {
    if (typeof value !== 'string') differences.add(`${field}[${key}]`);
  }
  return values;
}

function brokerRuntimeConfigDifferences(live, candidate) {
  const differences = new Set();
  for (const field of ['apiVersion', 'kind']) {
    if (live[field] !== candidate[field]) differences.add(field);
  }
  for (const field of ['name', 'namespace']) {
    if (live.metadata?.[field] !== candidate.metadata?.[field]) differences.add(`metadata.${field}`);
  }
  if (live.immutable !== true || candidate.immutable !== true) differences.add('immutable');
  for (const field of ['data', 'binaryData']) {
    const liveValues = configMapStringData(live, field, differences);
    const candidateValues = configMapStringData(candidate, field, differences);
    for (const key of new Set([...Object.keys(liveValues), ...Object.keys(candidateValues)])) {
      if (!Object.hasOwn(liveValues, key) || !Object.hasOwn(candidateValues, key) ||
          liveValues[key] !== candidateValues[key]) {
        differences.add(`${field}[${key}]`);
      }
    }
  }
  return [...differences].sort();
}

function preflightBrokerRuntimeConfig(exec, context, root, candidate, resources) {
  let result;
  try {
    result = exec('kubectl', [
      'get', 'configmap', BROKER_RUNTIME_CONFIG, '--ignore-not-found=true', '--output=json',
      '--context', context, '--namespace', NAMESPACE,
    ], { check: false, cwd: root });
  } catch {
    fail(`Could not read ConfigMap/${BROKER_RUNTIME_CONFIG} for the Broker preflight.`);
  }
  if (!result || result.status !== 0) {
    fail(`Could not read ConfigMap/${BROKER_RUNTIME_CONFIG} for the Broker preflight.`);
  }
  const source = typeof result.stdout === 'string' ? result.stdout.trim() : '';
  if (!source) {
    fail(`Live ConfigMap/${BROKER_RUNTIME_CONFIG} is missing; the Broker preflight requires an exact match.`);
  }
  let live;
  try {
    live = JSON.parse(source);
  } catch {
    fail(`Kubernetes returned invalid JSON for ConfigMap/${BROKER_RUNTIME_CONFIG} during the Broker preflight.`);
  }
  if (!live || typeof live !== 'object' || Array.isArray(live)) {
    fail(`Kubernetes returned an invalid object for ConfigMap/${BROKER_RUNTIME_CONFIG} during the Broker preflight.`);
  }
  const differences = brokerRuntimeConfigDifferences(live, candidate);
  if (differences.length) {
    fail(`Live ConfigMap/${BROKER_RUNTIME_CONFIG} differs from the rendered candidate: ${differences.join(', ')}.`);
  }
  const liveDeployment = readExistingDeployment(exec, context, root, 'identity-broker');
  if (!liveDeployment) return undefined;
  return preserveIdentityBrokerProviderSetup(resources, liveDeployment, name =>
    name === BROKER_RUNTIME_CONFIG
      ? live
      : readExistingNamedRuntimeConfigMap(exec, context, root, name), root);
}

function deploymentReplicas(deployment, name = 'projects-config') {
  const saved = deployment.metadata?.annotations?.[PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION];
  if (saved !== undefined) {
    if (!/^(0|[1-9]\d*)$/.test(saved)) {
      fail(`deployment/${name} has an invalid ${PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION} annotation.`);
    }
    return Number(saved);
  }
  const replicas = deployment.spec?.replicas ?? 1;
  if (!Number.isSafeInteger(replicas) || replicas < 0) {
    fail(`deployment/${name} has an invalid replica count.`);
  }
  return replicas;
}

function deploymentSelector(deployment, name = 'projects-config') {
  const labels = deployment.spec?.selector?.matchLabels;
  if (!labels || !Object.keys(labels).length) {
    fail(`deployment/${name} has no pod selector for safe ConfigMap replacement.`);
  }
  return Object.entries(labels).map(([key, value]) => `${key}=${value}`).join(',');
}

function isRetainedProjectsConfigMigrationPod(pod) {
  if (!pod || typeof pod !== 'object') return false;
  const owners = pod.metadata?.ownerReferences;
  const owner = Array.isArray(owners) && owners.find(reference =>
    reference && typeof reference === 'object' &&
    reference.apiVersion === 'batch/v1' && reference.kind === 'Job' && reference.controller === true);
  if (pod.status?.phase !== 'Succeeded' ||
      pod.metadata?.labels?.['agentweaver.io/migration'] !== 'projects-config' ||
      pod.spec?.serviceAccountName !== 'projects-config-migration' ||
      !owner || typeof owner.uid !== 'string' || !owner.uid.trim() ||
      !/^projects-config-migration-[a-f0-9]{8}$/.test(owner.name ?? '')) {
    return false;
  }

  const spec = pod.spec;
  if (!Array.isArray(spec.containers) || !spec.containers.length ||
      [spec.initContainers, spec.ephemeralContainers, spec.volumes]
        .some(value => value !== undefined && !Array.isArray(value))) return false;
  const referencesRuntimeConfig = container => {
    if (!container || typeof container !== 'object' ||
        [container.envFrom, container.env].some(value => value !== undefined && !Array.isArray(value))) {
      return true;
    }
    return (container.envFrom ?? []).some(source => {
      if (!source || typeof source !== 'object') return true;
      const reference = source.configMapRef;
      return reference !== undefined && (!reference || typeof reference !== 'object' ||
        typeof reference.name !== 'string' || reference.name === PROJECTS_CONFIG_RUNTIME_CONFIG);
    }) || (container.env ?? []).some(setting => {
      if (!setting || typeof setting !== 'object') return true;
      const valueFrom = setting.valueFrom;
      if (valueFrom === undefined) return false;
      if (!valueFrom || typeof valueFrom !== 'object') return true;
      const reference = valueFrom.configMapKeyRef;
      return reference !== undefined && (!reference || typeof reference !== 'object' ||
        typeof reference.name !== 'string' || reference.name === PROJECTS_CONFIG_RUNTIME_CONFIG);
    });
  };
  if ([
    ...(spec.containers ?? []),
    ...(spec.initContainers ?? []),
    ...(spec.ephemeralContainers ?? []),
  ].some(referencesRuntimeConfig)) return false;
  return !(spec.volumes ?? []).some(volume => {
    if (!volume || typeof volume !== 'object') return true;
    const configMap = volume.configMap;
    if (configMap !== undefined && (!configMap || typeof configMap !== 'object' ||
        typeof configMap.name !== 'string' || configMap.name === PROJECTS_CONFIG_RUNTIME_CONFIG)) return true;
    const projected = volume.projected;
    if (projected === undefined) return false;
    if (!projected || typeof projected !== 'object' || !Array.isArray(projected.sources)) return true;
    return projected.sources.some(source => {
      if (!source || typeof source !== 'object') return true;
      const reference = source.configMap;
      return reference !== undefined && (!reference || typeof reference !== 'object' ||
        typeof reference.name !== 'string' || reference.name === PROJECTS_CONFIG_RUNTIME_CONFIG);
    });
  });
}

async function waitForDeploymentPodsToStop(
  exec, context, root, selector, sleep, name = 'projects-config',
  ignorePod = () => false, now = Date.now,
) {
  const deadline = now() + POD_TERMINATION_TIMEOUT_MS;
  for (;;) {
    const result = kubectl(exec, context, [
      'get', 'pods', '--selector', selector, '--output=json',
    ], root, `Could not check deployment/${name} Pods`);
    let pods;
    try {
      pods = JSON.parse(result.stdout).items;
    } catch {
      fail(`Kubernetes returned invalid Pod list JSON while stopping deployment/${name}.`);
    }
    if (!Array.isArray(pods)) fail(`Kubernetes returned an invalid Pod list for deployment/${name}.`);
    if (!pods.some(pod => !ignorePod(pod))) return;
    if (now() >= deadline) {
      fail(`Timed out waiting for deployment/${name} Pods to terminate.`);
    }
    await sleep(5_000);
  }
}

async function activateRuntimeConfig(exec, context, root, runtimeConfig, deployment, sleep, log, now) {
  const replicas = deploymentReplicas(deployment);
  const selector = deploymentSelector(deployment);
  const marker = deployment.metadata?.annotations?.[PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION];
  if (marker === undefined) {
    kubectl(exec, context, [
      'annotate', 'deployment/projects-config',
      `${PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION}=${replicas}`, '--overwrite',
    ], root, 'Could not save deployment/projects-config replicas for ConfigMap recovery');
  }

  try {
    kubectl(exec, context, ['scale', 'deployment/projects-config', '--replicas=0'], root,
      'Could not stop deployment/projects-config for immutable ConfigMap replacement');
    await waitForDeploymentPodsToStop(exec, context, root, selector, sleep, 'projects-config',
      isRetainedProjectsConfigMigrationPod, now);
    await sleep(IMMUTABLE_CONFIGMAP_CACHE_WAIT_MS);

    const current = readExistingRuntimeConfigMap(exec, context, root);
    if (!current || !sameRuntimeConfig(current, runtimeConfig)) {
      if (current) {
        kubectl(exec, context, ['delete', 'configmap', PROJECTS_CONFIG_RUNTIME_CONFIG], root,
          `Could not delete ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`);
      }
      kubectl(exec, context, ['create', '--filename', '-'], root,
        `Could not create ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`,
        { input: stringify(runtimeConfig) });
      log(`Recreated immutable ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}.`);
    }

    kubectl(exec, context, ['scale', 'deployment/projects-config', `--replicas=${replicas}`], root,
      'Could not restore deployment/projects-config replicas after ConfigMap replacement');
    return replicas;
  } catch (error) {
    let recoveryFailure;
    try {
      if (readExistingRuntimeConfigMap(exec, context, root)) {
        kubectl(exec, context, ['scale', 'deployment/projects-config', `--replicas=${replicas}`], root,
          'Could not restore deployment/projects-config after ConfigMap replacement failed');
      } else {
        recoveryFailure = 'The ConfigMap is absent; deployment/projects-config remains stopped. Rerun the deploy command to recover.';
      }
    } catch (restoreError) {
      recoveryFailure = restoreError.message;
    }
    if (recoveryFailure) {
      throw new Error(`${error.message}; ${recoveryFailure}`);
    }
    throw error;
  }
}

async function activateEnvironmentRuntimeConfigs(
  exec, context, root, desiredMaps, replacements, missingNames, deployment, sleep, log, mapRun,
) {
  const replicas = deploymentReplicas(deployment, 'environment');
  const selector = deploymentSelector(deployment, 'environment');
  const marker = deployment.metadata?.annotations?.[PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION];
  if (marker === undefined) {
    kubectl(exec, context, [
      'annotate', 'deployment/environment',
      `${PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION}=${replicas}`, '--overwrite',
    ], root, 'Could not save deployment/environment replicas for AgentHost ConfigMap recovery');
  }

  try {
    kubectl(exec, context, ['scale', 'deployment/environment', '--replicas=0'], root,
      'Could not stop deployment/environment for immutable ConfigMap replacement');
    await waitForDeploymentPodsToStop(exec, context, root, selector, sleep, 'environment');
    await sleep(IMMUTABLE_CONFIGMAP_CACHE_WAIT_MS);
    replaceImmutableRuntimeConfigMaps(replacements, NAMESPACE, { root, run: mapRun, fail });
    for (const resource of desiredMaps) {
      if (missingNames.has(resource.metadata.name)) {
        kubectl(exec, context, ['create', '--filename', '-'], root,
          `Could not create ConfigMap/${resource.metadata.name}`, { input: JSON.stringify(resource) });
      }
    }
    for (const resource of desiredMaps) {
      const current = readExistingNamedRuntimeConfigMap(exec, context, root, resource.metadata.name);
      if (!current || !sameRuntimeConfig(current, resource)) {
        fail(`ConfigMap/${resource.metadata.name} does not match the desired runtime settings after replacement.`);
      }
    }
    kubectl(exec, context, ['scale', 'deployment/environment', `--replicas=${replicas}`], root,
      'Could not restore deployment/environment replicas after ConfigMap replacement');
    log('Activated the AgentHost runtime ConfigMaps on deployment/environment.');
  } catch (error) {
    let recoveryFailure;
    try {
      const mapsReady = desiredMaps.every(resource => {
        const current = readExistingNamedRuntimeConfigMap(exec, context, root, resource.metadata.name);
        return current && sameRuntimeConfig(current, resource);
      });
      if (mapsReady) {
        kubectl(exec, context, ['scale', 'deployment/environment', `--replicas=${replicas}`], root,
          'Could not restore deployment/environment after ConfigMap activation failed');
      } else {
        recoveryFailure = 'The AgentHost runtime ConfigMap pair is absent or incomplete; ' +
          'deployment/environment remains stopped. Rerun the deploy command to recover.';
      }
    } catch (restoreError) {
      recoveryFailure = restoreError.message;
    }
    if (recoveryFailure) throw new Error(`${error.message}; ${recoveryFailure}`);
    throw error;
  }
}

function prepareJob(resource, { image, name }) {
  const job = structuredClone(resource);
  job.metadata.name = name;
  job.spec.ttlSecondsAfterFinished ??= 3600;
  job.spec.template.metadata.labels ??= {};
  const container = job.spec.template.spec.containers[0];
  if (!container) fail(`Migration Job ${name} has no container.`);
  container.image = image;
  return job;
}

function imageName(registry, repository) {
  return `${registry}/${repository}`;
}

function migrationImage(digests, repository, registries) {
  const digest = digests.get(repository);
  if (!digest) fail(`No published image digest was resolved for ${repository}.`);
  return `${imageName(registries.get(repository), repository)}@${digest}`;
}

function resolveImageDigest(exec, root, repository, reference, registry) {
  const image = `${imageName(registry, repository)}${IMAGE_DIGEST.test(reference) ? `@${reference}` : `:${reference}`}`;
  const result = must(exec, 'docker', [
    'buildx', 'imagetools', 'inspect', image, '--format', '{{.Manifest.Digest}}',
  ], { cwd: root }, `Could not read ${image}`);
  const digest = result.stdout.trim();
  if (!IMAGE_DIGEST.test(digest) || (IMAGE_DIGEST.test(reference) && digest !== reference)) {
    fail(`Registry returned an invalid digest for ${image}.`);
  }
  return digest;
}

function deployedImageReference(value) {
  if (typeof value !== 'string') return undefined;
  const slash = value.indexOf('/');
  if (slash < 0) return undefined;
  const host = value.slice(0, slash);
  const registry = host === 'ghcr.io' ? DEFAULT_REGISTRY : host;
  try {
    parseRegistryTarget(registry);
  } catch {
    return undefined;
  }
  if (!value.startsWith(`${registry}/`)) return undefined;
  const reference = value.slice(`${registry}/`.length);
  for (const repository of IMAGE_REPOSITORIES) {
    if (reference.startsWith(`${repository}:`)) {
      const tag = reference.slice(repository.length + 1);
      if (!IMAGE_TAG.test(tag)) fail(`Current deployment has an invalid image tag for ${repository}.`);
      return { repository, reference: tag, registry };
    }
    if (reference.startsWith(`${repository}@`)) {
      const digest = reference.slice(repository.length + 1);
      if (!IMAGE_DIGEST.test(digest)) fail(`Current deployment has an invalid image digest for ${repository}.`);
      return { repository, reference: digest, registry };
    }
  }
  return undefined;
}

function currentImageReferences(exec, context, root, repositories) {
  const needed = new Set(repositories);
  const found = new Map();
  let agentHostSettings = [];
  for (const name of DEPLOYMENTS) {
    const deployment = readResource(exec, context, 'deployment', name, root);
    const containers = [
      ...(deployment.spec?.template?.spec?.containers ?? []),
      ...(deployment.spec?.template?.spec?.initContainers ?? []),
    ];
    if (name === 'environment') {
      agentHostSettings = containers
        .filter(container => container.name === 'environment')
        .flatMap(container => (container.env ?? [])
          .filter(setting => setting.name?.startsWith(AGENT_SANDBOX_ENV_PREFIX))
          .map(setting => structuredClone(setting)));
    }
    const values = containers.flatMap(container => [
      container.image,
      ...(container.env ?? []).map(setting => setting.value),
    ]);
    for (const value of values) {
      const image = deployedImageReference(value);
      if (!image || !needed.has(image.repository)) continue;
      const existing = found.get(image.repository);
      if (existing && (existing.reference !== image.reference || existing.registry !== image.registry)) {
        fail(`Current deployment has conflicting image references for ${image.repository}; include it in the manifest.`);
      }
      found.set(image.repository, { reference: image.reference, registry: image.registry });
    }
  }
  for (const repository of needed) {
    if (!found.has(repository)) {
      fail(`Could not find the currently deployed image for ${repository}; include it in the manifest.`);
    }
  }
  return { references: found, agentHostSettings };
}

function liveAgentHostTuple(settings, digest, registry) {
  const values = new Map();
  for (const setting of settings) {
    if (values.has(setting.name)) fail(`Current deployment has duplicate AgentHost environment setting ${setting.name}.`);
    values.set(setting.name, setting.value);
  }
  const image = values.get(AGENT_SANDBOX_IMAGE_SETTING);
  const currentImage = deployedImageReference(image);
  if (!currentImage || currentImage.repository !== 'agentweaver-agenthost' ||
      !IMAGE_DIGEST.test(currentImage.reference ?? '') || currentImage.reference !== digest ||
      !IMAGE_DIGEST.test(digest ?? '')) {
    fail(`Current deployment has no valid immutable ${AGENT_SANDBOX_IMAGE_SETTING} reference ` +
      `matching the resolved digest; include agentweaver-agenthost in the manifest.`);
  }
  const platform = values.get(AGENT_SANDBOX_PLATFORM_SETTING);
  if (platform !== 'linux/amd64') {
    fail(`Current deployment has no supported ${AGENT_SANDBOX_PLATFORM_SETTING} value.`);
  }
  const bytesText = values.get(AGENT_SANDBOX_COMPRESSED_BYTES_SETTING);
  const compressedPullBytes = Number(bytesText);
  if (typeof bytesText !== 'string' || !/^[1-9]\d*$/.test(bytesText) ||
      !Number.isSafeInteger(compressedPullBytes)) {
    fail(`Current deployment has no valid positive ${AGENT_SANDBOX_COMPRESSED_BYTES_SETTING} value.`);
  }
  return {
    digest,
    platform,
    compressedPullBytes,
    image: `${imageName(registry, 'agentweaver-agenthost')}@${digest}`,
  };
}

function preserveLiveAgentHostSettings(resources, settings, tuple) {
  const environment = resources.find(resource =>
    resource.kind === 'Deployment' && resource.metadata?.name === 'environment');
  const container = environment?.spec?.template?.spec?.containers?.find(value => value.name === 'environment');
  if (!container) fail('Kustomize must render the Environment container to preserve AgentHost settings.');
  if (!settings.some(setting => setting.name === AGENT_SANDBOX_IMAGE_SETTING)) {
    fail(`Current deployment is missing ${AGENT_SANDBOX_IMAGE_SETTING}; include agentweaver-agenthost in the manifest.`);
  }
  const preservedSettings = settings.map(setting => ({
    ...setting,
    ...(setting.name === AGENT_SANDBOX_IMAGE_SETTING ? { value: tuple.image } : {}),
    ...(setting.name === AGENT_SANDBOX_PLATFORM_SETTING ? { value: tuple.platform } : {}),
    ...(setting.name === AGENT_SANDBOX_COMPRESSED_BYTES_SETTING
      ? { value: String(tuple.compressedPullBytes) } : {}),
  }));
  const have = new Set(preservedSettings.map(setting => setting.name));
  for (const [name, value] of [
    [AGENT_SANDBOX_IMAGE_SETTING, tuple.image],
    [AGENT_SANDBOX_PLATFORM_SETTING, tuple.platform],
    [AGENT_SANDBOX_COMPRESSED_BYTES_SETTING, String(tuple.compressedPullBytes)],
  ]) {
    if (!have.has(name)) preservedSettings.push({ name, value });
  }
  container.env = [
    ...(container.env ?? []).filter(setting => !setting.name?.startsWith(AGENT_SANDBOX_ENV_PREFIX)),
    ...preservedSettings,
  ];
}

/**
 * Stop a deployment that would remove a live BYOK model binding, or change one without a new model
 * binding revision. A removal is allowed only when the operator names the reference and the revision
 * changes. BYOK bindings come from a model bindings file (`--model-bindings`) or the source files.
 */
export function requireLiveByokBindingsPreserved(live, desired, removals = []) {
  if (!live) {
    if (removals.length) {
      fail(`--remove-model-binding names ${removals.join(', ')}, but no live ConfigMap/${AGENTHOST_RUNTIME_CONFIG} exists.`);
    }
    return;
  }
  const settings = (resource, label) => {
    try {
      return JSON.parse(resource?.data?.['appsettings.json'] ?? '{}')?.AgentHost ?? {};
    } catch {
      fail(`${label} ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain valid appsettings.json.`);
    }
  };
  const current = settings(live, 'Live');
  const next = settings(desired, 'Rendered');
  const revised = next.ModelBindingsRevision !== current.ModelBindingsRevision;
  const liveByok = new Set(Object.entries(current.ModelBindings ?? {})
    .filter(([, binding]) => binding?.SourceMode === 'Byok').map(([reference]) => reference));
  const invalid = removals.filter(reference =>
    !liveByok.has(reference) || next.ModelBindings?.[reference]?.SourceMode === 'Byok');
  if (invalid.length) {
    fail(`--remove-model-binding names ${invalid.join(', ')}, which is not a live BYOK binding that this ` +
      'deploy removes.');
  }
  if (removals.length && !revised) {
    fail('A BYOK model binding can be removed only with a new model binding revision.');
  }
  const lost = Object.entries(current.ModelBindings ?? {})
    .filter(([, binding]) => binding?.SourceMode === 'Byok')
    .filter(([reference, binding]) => {
      const rendered = next.ModelBindings?.[reference];
      return revised ? rendered?.SourceMode !== 'Byok' : !isDeepStrictEqual(rendered, binding);
    })
    .map(([reference]) => reference)
    .filter(reference => !removals.includes(reference));
  if (lost.length) {
    fail(`The rendered ConfigMap/${AGENTHOST_RUNTIME_CONFIG} would remove or change live BYOK model ` +
      `binding(s) ${lost.join(', ')}. To keep them, deploy with --model-bindings <file> and the model ` +
      'bindings file from `agentweaver models byok plan --overlay-output`. To remove them, deploy with ' +
      '--remove-model-binding <reference>.');
  }
}

function stampAgentHostRuntime(resources, tuple) {
  const environment = resources.find(resource =>
    resource.kind === 'Deployment' && resource.metadata?.name === 'environment');
  const container = environment?.spec?.template?.spec?.containers?.find(value => value.name === 'environment');
  if (!container) fail('Kustomize must render the Environment container to stamp AgentHost image facts.');
  const runtimeConfig = resources.find(resource =>
    resource.kind === 'ConfigMap' && resource.metadata?.name === AGENTHOST_RUNTIME_CONFIG);
  if (!runtimeConfig || runtimeConfig.metadata?.namespace !== NAMESPACE || runtimeConfig.immutable !== true) {
    fail(`Kustomize must render immutable ConfigMap/${AGENTHOST_RUNTIME_CONFIG} in namespace ${NAMESPACE}.`);
  }
  let settings;
  try {
    settings = JSON.parse(runtimeConfig.data?.['appsettings.json']);
  } catch {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain valid appsettings.json.`);
  }
  if (!settings.AgentHost || !settings.AgentHost.Authentication) {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain AgentHost settings.`);
  }
  settings.AgentHost.ConfigureEndpoint = AGENTHOST_CONFIGURATION_ENDPOINT;
  settings.AgentHost.Authentication.Audience = AGENTHOST_CONFIGURATION_ENDPOINT;
  settings.AgentHost.MaximumPendingTurns = 4;
  settings.AgentHost.Image = {
    Digest: tuple.digest,
    Platform: tuple.platform,
    CompressedPullBytes: tuple.compressedPullBytes,
  };
  runtimeConfig.data['appsettings.json'] = JSON.stringify(settings, null, 2);

  const stamped = new Map();
  for (const [name, value] of [
    [AGENT_SANDBOX_IMAGE_SETTING, tuple.image],
    [AGENT_SANDBOX_PLATFORM_SETTING, tuple.platform],
    [AGENT_SANDBOX_COMPRESSED_BYTES_SETTING, String(tuple.compressedPullBytes)],
  ]) {
    const matches = (container.env ?? []).filter(setting => setting.name === name);
    if (matches.length > 1) fail(`Kustomize rendered duplicate Environment setting ${name}.`);
    stamped.set(name, { ...(matches[0] ?? {}), name, value });
  }
  container.env = [
    ...(container.env ?? []).filter(setting => !stamped.has(setting.name)),
    ...stamped.values(),
  ];
}

const AGENTHOST_MODEL_EGRESS_POLICY = 'agenthost-model-egress';
const BYOK_HOST_SUFFIXES = ['.openai.azure.com', '.cognitiveservices.azure.com', '.services.ai.azure.com'];
// The Copilot CLI reads endpoints.api from api.github.com/copilot_internal/user. The value depends
// on the Copilot plan of the user; api.githubcopilot.com is the default.
const COPILOT_MODEL_HOSTS = [
  'api.github.com',
  'api.githubcopilot.com',
  'api.individual.githubcopilot.com',
  'api.business.githubcopilot.com',
  'api.enterprise.githubcopilot.com',
];
const DNS_NAME = /^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;

/**
 * Allow AgentHost to reach exactly the HTTPS hosts of the BYOK model bindings in the rendered
 * AgentHost runtime map, and the GitHub Copilot hosts when the map has a HostedCopilot binding.
 * The checked-in policy allows only DNS, so this derives the hosts from
 * the same map that AgentHost reads and removes hosts when bindings are removed.
 */
export function stampAgentHostModelEgress(resources) {
  const runtimeConfig = resources.find(resource =>
    resource.kind === 'ConfigMap' && resource.metadata?.name === AGENTHOST_RUNTIME_CONFIG);
  let bindings;
  try {
    bindings = JSON.parse(runtimeConfig?.data?.['appsettings.json'])?.AgentHost?.ModelBindings ?? {};
  } catch {
    fail(`ConfigMap/${AGENTHOST_RUNTIME_CONFIG} must contain valid appsettings.json.`);
  }
  const hosts = new Set();
  for (const [reference, binding] of Object.entries(bindings)) {
    if (binding?.SourceMode === 'HostedCopilot') {
      for (const host of COPILOT_MODEL_HOSTS) hosts.add(host);
      continue;
    }
    if (binding?.SourceMode !== 'Byok') continue;
    let endpoint;
    try {
      endpoint = new URL(binding.Provider?.BaseUrl);
    } catch {
      endpoint = undefined;
    }
    const host = endpoint?.hostname;
    if (!endpoint || endpoint.protocol !== 'https:' || endpoint.port !== '' ||
        endpoint.username || endpoint.password || !DNS_NAME.test(host) ||
        !BYOK_HOST_SUFFIXES.some(suffix => host.endsWith(suffix))) {
      fail(`BYOK model binding ${reference} must use an HTTPS Azure OpenAI BaseUrl on port 443. ` +
        'Fix it with `agentweaver models byok plan`.');
    }
    hosts.add(host);
  }
  const policies = resources.filter(resource =>
    resource.kind === 'CiliumNetworkPolicy' && resource.metadata?.name === AGENTHOST_MODEL_EGRESS_POLICY);
  const policy = policies[0];
  const dnsRule = policy?.spec?.egress?.[0];
  if (policies.length !== 1 || policy.metadata?.namespace !== NAMESPACE ||
      policy.spec?.endpointSelector?.matchLabels?.['agentweaver.io/workload'] !== 'agenthost' ||
      policy.spec.egress?.length !== 1 || !dnsRule?.toEndpoints || dnsRule.toPorts?.length !== 1 ||
      dnsRule.toPorts[0].rules) {
    fail(`Kustomize must render the DNS-only CiliumNetworkPolicy/${AGENTHOST_MODEL_EGRESS_POLICY} ` +
      `for AgentHost in namespace ${NAMESPACE}.`);
  }
  if (!hosts.size) return [];
  const names = [...hosts].sort().map(matchName => ({ matchName }));
  dnsRule.toPorts[0].rules = { dns: names };
  policy.spec.egress.push({
    toFQDNs: names.map(name => ({ ...name })),
    toPorts: [{ ports: [{ port: '443', protocol: 'TCP' }] }],
  });
  return names.map(name => name.matchName);
}

function findJob(resources, name) {
  const matches = resources.filter(resource =>
    resource.kind === 'Job' && resource.metadata?.name === name);
  if (matches.length !== 1) fail(`Kustomize must render exactly one Job/${name}.`);
  return matches[0];
}

function migrationJobs(root, exec, context, digests, registries, selectedRepositories) {
  const selected = new Set(selectedRepositories);
  const jobs = [];
  if (selected.has('agentweaver.identity.broker')) {
    const rendered = must(exec, 'kubectl', [
      'kustomize', path.join(root, 'deploy', 'k8s', 'migrations', 'identity-broker'),
    ], { cwd: root }, 'Broker migration Job rendering failed').stdout;
    const resources = parseKustomizeResources(rendered).items;
    readResource(exec, context, 'serviceaccount', 'identity-broker-migration', root);
    readResource(exec, context, 'configmap', 'identity-broker-migration-config', root);
    jobs.push(prepareJob(findJob(resources, 'identity-broker-migration'), {
      image: migrationImage(digests, 'agentweaver.identity.broker', registries),
      name: `identity-broker-migration-${randomUUID().slice(0, 8)}`,
    }));
  }

  const p1Selected = P1_MIGRATION_IMAGES.filter(([, repository]) => selected.has(repository));
  if (p1Selected.length) {
    const rendered = must(exec, 'kubectl', [
      'kustomize', path.join(root, 'deploy', 'k8s', 'migrations', 'p1-postgres'),
    ], { cwd: root }, 'P1 migration Job rendering failed').stdout;
    const resources = parseKustomizeResources(rendered).items;
    const identity = readResource(exec, context, 'serviceaccount', 'p1-postgres-migration', root);
    const clientId = identity.metadata?.annotations?.['azure.workload.identity/client-id'];
    if (!CLIENT_ID.test(clientId ?? '')) {
      fail('ServiceAccount/p1-postgres-migration has no configured workload identity client ID.');
    }
    jobs.push(...p1Selected.map(([name, repository]) => {
      const job = prepareJob(findJob(resources, name), {
        image: migrationImage(digests, repository, registries),
        name: `${name}-${randomUUID().slice(0, 8)}`,
      });
      for (const entry of job.spec.template.spec.containers[0].env ?? []) {
        if (entry.name.endsWith('__WorkloadIdentity__ClientId')) entry.value = clientId;
      }
      return job;
    }));
  }

  if (selected.has('agentweaver.projects.config')) {
    const projectsConfig = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'migrations',
      'projects-config', 'job.yaml'), 'utf8'));
    const identity = readResource(exec, context, 'serviceaccount', 'projects-config-migration', root);
    const clientId = identity.metadata?.annotations?.['azure.workload.identity/client-id'];
    const expectedClientId = must(exec, 'az', [
      'identity', 'show', '--resource-group', 'agentweaver', '--name', 'agentweaver-id-projects-config-migration',
      '--query', 'clientId', '-o', 'tsv',
    ], { cwd: root }, 'Could not read the Projects & Config migration identity').stdout.trim();
    if (!CLIENT_ID.test(expectedClientId) || clientId !== expectedClientId) {
      fail('ServiceAccount/projects-config-migration does not name the approved migration identity.');
    }
    const job = prepareJob(projectsConfig, {
      image: migrationImage(digests, 'agentweaver.projects.config', registries),
      name: `projects-config-migration-${randomUUID().slice(0, 8)}`,
    });
    const setting = job.spec.template.spec.containers[0].env
      .find(entry => entry.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId');
    if (!setting) fail('Projects Config migration Job has no workload identity client ID setting.');
    setting.value = clientId;
    jobs.push(job);
  }
  return jobs;
}

function applyImageDigests(kustomization, digests, registries) {
  if (!Array.isArray(kustomization?.images) || kustomization.images.length !== IMAGE_REPOSITORIES.length) {
    fail('deploy/k8s must pin exactly the ten published image repositories.');
  }
  const seen = new Set();
  for (const image of kustomization.images) {
    if (!image || typeof image.name !== 'string' || seen.has(image.name)) {
      fail('deploy/k8s contains an invalid or duplicate image pin.');
    }
    seen.add(image.name);
    const repository = image.name.slice(`${DEFAULT_REGISTRY}/`.length);
    if (!IMAGE_REPOSITORIES.includes(repository) || image.name !== `${DEFAULT_REGISTRY}/${repository}`) {
      fail(`Unexpected Kustomize image repository ${image.name}.`);
    }
    image.newName = imageName(registries.get(repository), repository);
    image.digest = digests.get(repository);
    delete image.newTag;
  }
  if (IMAGE_REPOSITORIES.some(repository => !seen.has(`${DEFAULT_REGISTRY}/${repository}`))) {
    fail('deploy/k8s is missing a published image repository.');
  }
}

function runMigrationJob(job, exec, context, root, log) {
  const name = job.metadata.name;
  kubectl(exec, context, ['create', '--filename', '-'], root, `Could not create migration Job ${name}`,
    { input: stringify(job) });
  const wait = exec('kubectl', [
    'wait', '--for=condition=complete', `job/${name}`, `--timeout=${JOB_TIMEOUT}`,
    '--context', context, '--namespace', NAMESPACE,
  ], { check: false, cwd: root });
  if (wait.status !== 0) {
    const logs = exec('kubectl', [
      'logs', `job/${name}`, '--all-containers=true', '--context', context, '--namespace', NAMESPACE,
    ], { check: false, cwd: root });
    const output = redact((logs.stdout || logs.stderr || '').trim());
    fail(`Migration Job ${name} failed: ${(wait.stderr || wait.stdout || '').trim()}${output ? `\n${output}` : ''}`);
  }
  log(`Migration Job ${name} completed.`);
}

export async function deployKustomize(options, overrides = {}) {
  const root = overrides.root ?? process.cwd();
  const exec = overrides.exec ?? run;
  const fetchImpl = overrides.fetchImpl ?? exec.fetchImpl ?? globalThis.fetch;
  const log = overrides.log ?? (message => console.log(message));
  const now = overrides.now ?? Date.now;
  const selectedRegistry = options.registry ?? DEFAULT_REGISTRY;
  try {
    parseRegistryTarget(selectedRegistry);
  } catch {
    fail('registry must be ghcr.io/sabbour or an Azure Container Registry login server.');
  }
  const removals = options.removeModelBindings ?? [];
  const modelBindings = options.modelBindings === undefined ? undefined
    : parseModelBindingsOverlay(readFileSync(path.resolve(root, options.modelBindings), 'utf8'));
  const requestedImages = options.imageManifest === undefined
    ? new Map(IMAGE_REPOSITORIES.map(repository => [repository, options.imageTag]))
    : parseImageManifest(readFileSync(path.resolve(root, options.imageManifest), 'utf8'));
  const digests = new Map();
  const registries = new Map();
  for (const [repository, reference] of requestedImages) {
    registries.set(repository, selectedRegistry);
    digests.set(repository, resolveImageDigest(exec, root, repository, reference, selectedRegistry));
  }

  const context = must(exec, 'kubectl', ['config', 'current-context'], { cwd: root },
    'Could not read the selected kubectl context').stdout.trim();
  if (!context) fail('No kubectl context is selected.');
  verifyAuthorization(exec, context, root,
    [...requestedImages.keys()].some(repository => MIGRATION_IMAGES.has(repository)));
  verifyAgentHostTlsSecret(exec, context, root);
  verifyEnvironmentRuntimeAuthorization(exec, context, root);
  const missingRepositories = IMAGE_REPOSITORIES.filter(repository => !digests.has(repository));
  let liveAgentHostSettings;
  let agentHostTuple;
  if (missingRepositories.length) {
    const current = currentImageReferences(exec, context, root, missingRepositories);
    for (const [repository, { reference, registry }] of current.references) {
      registries.set(repository, registry);
      digests.set(repository, resolveImageDigest(exec, root, repository, reference, registry));
    }
    if (!requestedImages.has('agentweaver-agenthost')) {
      liveAgentHostSettings = current.agentHostSettings;
      agentHostTuple = liveAgentHostTuple(liveAgentHostSettings,
        digests.get('agentweaver-agenthost'), registries.get('agentweaver-agenthost'));
    }
  }
  if (requestedImages.has('agentweaver-agenthost')) {
    const measured = await measureAgentHostRegistryImage(
      `${imageName(registries.get('agentweaver-agenthost'), 'agentweaver-agenthost')}@` +
        digests.get('agentweaver-agenthost'),
      fetchImpl, undefined, overrides.registryOptions);
    agentHostTuple = {
      digest: measured.digest,
      platform: measured.platform,
      compressedPullBytes: measured.compressedPullBytes,
      image: measured.image,
    };
    digests.set('agentweaver-agenthost', measured.digest);
  }

  const kustomizationPath = path.join(root, 'deploy', 'k8s', 'kustomization.yaml');
  const originalKustomization = readFileSync(kustomizationPath, 'utf8');
  try {
    const kustomization = parse(originalKustomization);
    applyImageDigests(kustomization, digests, registries);
    writeFileSync(kustomizationPath, stringify(kustomization));

    const rendered = must(exec, 'kubectl', ['kustomize', path.join(root, 'deploy', 'k8s')], { cwd: root },
      'Kustomize rendering failed').stdout;
    const resources = parseKustomizeResources(rendered).items;
    const deployments = resources.filter(resource => resource.kind === 'Deployment')
      .map(resource => resource.metadata?.name);
    if (deployments.length !== DEPLOYMENTS.length || deployments.some(name => !DEPLOYMENT_NAME.test(name ?? '')) ||
        new Set(deployments).size !== deployments.length ||
        DEPLOYMENTS.some(name => !deployments.includes(name))) {
      fail('Kustomize must render exactly the nine named v1 Deployments.');
    }
    if (!agentHostTuple) fail('No validated AgentHost image tuple is available for the runtime ConfigMaps.');
    if (modelBindings) {
      const merged = applyModelBindingsOverlay(resources, modelBindings);
      log(`Added environment model binding(s) ${merged.references.join(', ')} at revision ${merged.revision}.`);
    }
    stampEventsRuntimeConfigDigest(resources);
    if (liveAgentHostSettings) {
      preserveLiveAgentHostSettings(resources, liveAgentHostSettings, agentHostTuple);
    }
    stampAgentHostRuntime(resources, agentHostTuple);
    const modelHosts = stampAgentHostModelEgress(resources);
    log(modelHosts.length
      ? `Allowed AgentHost model egress to ${modelHosts.join(', ')} on TCP 443.`
      : 'No HostedCopilot or BYOK model bindings; AgentHost model egress allows DNS only.');
    const probeUrls = byokEndpointProbeUrls(resources);
    const brokerRuntimeConfig = findBrokerRuntimeConfig(resources);
    const providerSetup = preflightBrokerRuntimeConfig(exec, context, root, brokerRuntimeConfig, resources);
    if (providerSetup?.configMap) {
      log(`Preserved Identity Broker provider setup from ConfigMap/${providerSetup.configMap}.`);
    }
    const brokerDeployment = resources.find(resource =>
      resource.kind === 'Deployment' && resource.metadata?.name === 'identity-broker');
    const runtimeConfig = resources.find(resource => resource.kind === 'ConfigMap' &&
      resource.metadata?.name === PROJECTS_CONFIG_RUNTIME_CONFIG);
    if (!runtimeConfig) fail(`Kustomize must render ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}.`);
    const desiredRuntimeMaps = resources.filter(resource =>
      resource.kind === 'ConfigMap' && RUNTIME_CONFIG_MAPS.has(resource.metadata?.name));
    if (desiredRuntimeMaps.length !== RUNTIME_CONFIG_MAPS.size ||
        desiredRuntimeMaps.some(resource => resource.metadata?.namespace !== NAMESPACE || resource.immutable !== true)) {
      fail('Kustomize must render both immutable AgentHost runtime ConfigMaps in namespace agentweaver.');
    }
    const prerequisites = verifyClusterPrerequisites(exec, context, root, resources);
    const existingRuntimeMaps = new Map([...RUNTIME_CONFIG_MAPS].map(name => [
      name, readExistingNamedRuntimeConfigMap(exec, context, root, name),
    ]));
    requireLiveByokBindingsPreserved(existingRuntimeMaps.get(AGENTHOST_RUNTIME_CONFIG),
      desiredRuntimeMaps.find(resource => resource.metadata.name === AGENTHOST_RUNTIME_CONFIG), removals);
    requireLiveAzureCostPreserved(readExistingNamedRuntimeConfigMap(exec, context, root, EVENTS_RUNTIME_CONFIG),
      resources.find(resource => resource.kind === 'ConfigMap' && resource.metadata?.name === EVENTS_RUNTIME_CONFIG));
    const missingRuntimeMaps = new Set([...existingRuntimeMaps]
      .filter(([, resource]) => resource === undefined).map(([name]) => name));
    const runtimeMapManifest = { apiVersion: 'v1', kind: 'List', items: desiredRuntimeMaps };
    const replacements = findImmutableRuntimeConfigMapReplacements(runtimeMapManifest, NAMESPACE, {
      root,
      run: (command, args, settings = {}) => must(exec, command, [...args, '--context', context],
        { cwd: root, ...settings }, `Could not ${args[0]} immutable runtime ConfigMap`).
        stdout.trim(),
      fail,
      replaceableNames: RUNTIME_CONFIG_MAPS,
    });
    const mapRun = (command, args, settings = {}) => must(
      exec, command, [...args, '--context', context], { cwd: root, ...settings },
      `Could not ${args[0]} immutable runtime ConfigMap`,
    ).stdout.trim();
    verifyImmutableRuntimeConfigMapReplacementPermissions(replacements, NAMESPACE, {
      root, run: mapRun, fail,
    });
    const environmentDeployment = readExistingDeployment(exec, context, root, 'environment');
    const pendingEnvironmentActivation = environmentDeployment?.metadata?.annotations?.[
      PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION
    ] !== undefined;
    const replaceEnvironmentRuntimeConfig = replacements.some(resource =>
      resource.metadata.name === ENVIRONMENT_RUNTIME_CONFIG);
    const activateEnvironment = environmentDeployment !== undefined &&
      (pendingEnvironmentActivation || replaceEnvironmentRuntimeConfig ||
        missingRuntimeMaps.has(ENVIRONMENT_RUNTIME_CONFIG));
    if (activateEnvironment) {
      verifyEnvironmentActivationAuthorization(exec, context, root);
      const deployment = resources.find(resource =>
        resource.kind === 'Deployment' && resource.metadata?.name === 'environment');
      if (!deployment?.spec) fail('Kustomize must render deployment/environment for runtime ConfigMap activation.');
      deployment.spec.replicas = deploymentReplicas(environmentDeployment, 'environment');
    }
    const currentRuntimeConfig = readExistingRuntimeConfigMap(exec, context, root);
    const projectsConfigDeployment = readExistingProjectsConfigDeployment(exec, context, root);
    const replaceRuntimeConfig = currentRuntimeConfig !== undefined &&
      !sameRuntimeConfig(currentRuntimeConfig, runtimeConfig);
    const pendingRuntimeConfigActivation = projectsConfigDeployment?.metadata?.annotations?.[
      PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION
    ] !== undefined;
    const activateProjectsConfig = projectsConfigDeployment !== undefined &&
      (pendingRuntimeConfigActivation || replaceRuntimeConfig || currentRuntimeConfig === undefined);
    if (activateProjectsConfig) {
      const permission = kubectl(exec, context, ['auth', 'can-i', 'list', 'pods'], root,
        'Could not check Kubernetes permission list pods for ConfigMap replacement');
      if (permission.stdout.trim() !== 'yes') {
        fail(`The selected identity cannot list pods in namespace ${NAMESPACE}, required to safely replace ${PROJECTS_CONFIG_RUNTIME_CONFIG}.`);
      }
    }
    if (replaceRuntimeConfig) {
      const resource = `configmap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`;
      const permission = kubectl(exec, context, ['auth', 'can-i', 'delete', resource], root,
        `Could not check Kubernetes permission delete ${resource}`);
      if (permission.stdout.trim() !== 'yes') {
        fail(`The selected identity cannot delete ${resource} in namespace ${NAMESPACE}, required to update the immutable ConfigMap.`);
      }
    }
    if (activateProjectsConfig) {
      const deployment = resources.find(resource =>
        resource.kind === 'Deployment' && resource.metadata?.name === 'projects-config');
      if (!deployment?.spec) fail('Kustomize must render deployment/projects-config for safe ConfigMap replacement.');
      deployment.spec.replicas = deploymentReplicas(projectsConfigDeployment);
    }
    const workloads = {
      apiVersion: 'v1',
      kind: 'List',
      items: resources.filter(resource => resource.kind !== 'Namespace' &&
        resource !== brokerRuntimeConfig && resource !== brokerDeployment),
    };
    const jobs = migrationJobs(root, exec, context, digests, registries, requestedImages.keys());

    if (prerequisites.createStorageClass) {
      must(exec, 'kubectl', ['create', '--filename', '-', '--context', context], {
        cwd: root, input: stringify(prerequisites.createStorageClass),
      }, `Could not create StorageClass/${prerequisites.storageClassName}`);
      log(`Created StorageClass/${prerequisites.storageClassName}.`);
    }
    for (const job of jobs) runMigrationJob(job, exec, context, root, log);
    log('Database migrations completed.');

    if (activateEnvironment) {
      await activateEnvironmentRuntimeConfigs(exec, context, root, desiredRuntimeMaps, replacements,
        missingRuntimeMaps, environmentDeployment,
        overrides.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))),
        log, mapRun);
    } else {
      replaceImmutableRuntimeConfigMaps(replacements, NAMESPACE, { root, run: mapRun, fail });
    }
    if (activateProjectsConfig) {
      await activateRuntimeConfig(exec, context, root, runtimeConfig, projectsConfigDeployment,
        overrides.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))),
        log, now);
    } else if (replaceRuntimeConfig) {
      kubectl(exec, context, ['delete', 'configmap', PROJECTS_CONFIG_RUNTIME_CONFIG], root,
        `Could not delete ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`);
      kubectl(exec, context, ['create', '--filename', '-'], root,
        `Could not create ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`, { input: stringify(runtimeConfig) });
    }

    kubectl(exec, context, ['apply', '--filename', '-'], root, 'Workload apply failed',
      { input: stringify(workloads) });
    if (providerSetup) {
      const patch = [
        { op: 'test', path: '/metadata/uid', value: providerSetup.deploymentUid },
        { op: 'test', path: '/metadata/resourceVersion', value: providerSetup.deploymentResourceVersion },
        { op: 'replace', path: '/spec', value: brokerDeployment.spec },
      ];
      kubectl(exec, context, ['patch', 'deployment/identity-broker', '--type=json',
        '--patch', JSON.stringify(patch)], root, 'Identity Broker Deployment update failed');
    } else {
      kubectl(exec, context, ['create', '--filename', '-'], root,
        'Could not create deployment/identity-broker', { input: stringify(brokerDeployment) });
    }
    log(`Applied Kustomize workloads to ${context}/${NAMESPACE}.`);

    for (const name of deployments) {
      kubectl(exec, context, ['rollout', 'status', `deployment/${name}`, '--timeout=5m'], root,
        `Deployment ${name} did not become ready`);
      log(`Deployment ${name} is ready.`);
    }
    if (activateProjectsConfig) {
      kubectl(exec, context, [
        'annotate', 'deployment/projects-config', `${PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION}-`,
      ], root, 'Could not clear the Projects & Config ConfigMap recovery marker');
    }
    if (activateEnvironment) {
      kubectl(exec, context, [
        'annotate', 'deployment/environment', `${PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION}-`,
      ], root, 'Could not clear the Environment AgentHost ConfigMap recovery marker');
    }
    await runInstallSmokeCheck(exec, context, root, prerequisites, agentHostTuple.image, probeUrls,
      overrides.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))), log);
    return { context, namespace: NAMESPACE, deployments, images: Object.fromEntries(digests) };
  } finally {
    writeFileSync(kustomizationPath, originalKustomization);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  try {
    await deployKustomize(parseDeployKustomizeArgs(process.argv.slice(2)));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
