#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, stringify } from 'yaml';
import { parseKustomizeResources } from './kustomize-runtime.mjs';
import {
  findImmutableRuntimeConfigMapReplacements,
  replaceImmutableRuntimeConfigMaps,
  verifyImmutableRuntimeConfigMapReplacementPermissions,
} from './immutable-runtime-config-maps.mjs';
import { measureAgentHostRegistryImage } from './registry.mjs';
import { run, redact } from '../azure/lib/exec.mjs';

const REGISTRY = 'ghcr.io/sabbour';
const NAMESPACE = 'agentweaver';
const PROJECTS_CONFIG_RUNTIME_CONFIG = 'projects-config-runtime-config';
const PROJECTS_CONFIG_RESTORE_REPLICAS_ANNOTATION = 'agentweaver.io/local-deploy-restore-replicas';
const AGENTHOST_RUNTIME_CONFIG = 'agenthost-runtime-v1';
const ENVIRONMENT_RUNTIME_CONFIG = 'environment-runtime-config';
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
const IMAGE_REPOSITORIES = [
  'agentweaver-agenthost',
  'agentweaver.identity.broker',
  'agentweaver.eventsandsessions',
  'agentweaver.gateway',
  'agentweaver.mcp',
  'agentweaver-web',
  'agentweaver.orchestrator',
  'agentweaver.environment',
  'agentweaver.knowledge',
  'agentweaver.projects.config',
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

export const DEPLOY_KUSTOMIZE_USAGE = `Usage: npm run deploy:kustomize -- --image-manifest <file.json> --execute
   or: npm run deploy:kustomize -- --image-tag sha-<git-commit> --execute

The manifest selects published images to update; omitted images keep their
currently deployed references. The command runs migrations for selected services,
applies namespaced Kustomize resources, and waits for every Deployment rollout.
`;

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

export function parseDeployKustomizeArgs(argv) {
  let imageTag;
  let imageManifest;
  let execute = false;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--execute') execute = true;
    else if (arg === '--image-tag') {
      if (imageTag !== undefined) fail('--image-tag may be specified only once.');
      imageTag = argv[++index];
    } else if (arg === '--image-manifest') {
      if (imageManifest !== undefined) fail('--image-manifest may be specified only once.');
      imageManifest = argv[++index];
      if (!imageManifest || imageManifest.startsWith('--')) fail('--image-manifest requires a file path.');
    } else fail(`unknown argument "${arg}".`);
  }
  if ((imageTag === undefined) === (imageManifest === undefined)) {
    fail('provide exactly one of --image-manifest or --image-tag.');
  }
  if (imageTag !== undefined && !IMAGE_TAG.test(imageTag ?? '')) {
    fail('--image-tag must be a normal sha-<40-character-commit> image tag.');
  }
  if (!execute) fail('--execute is required to run migrations and apply workloads.');
  return imageManifest === undefined ? { imageTag } : { imageManifest };
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

async function waitForDeploymentPodsToStop(exec, context, root, selector, sleep, name = 'projects-config') {
  const deadline = Date.now() + POD_TERMINATION_TIMEOUT_MS;
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
    if (!pods.length) return;
    if (Date.now() >= deadline) {
      fail(`Timed out waiting for deployment/${name} Pods to terminate.`);
    }
    await sleep(5_000);
  }
}

async function activateRuntimeConfig(exec, context, root, runtimeConfig, deployment, sleep, log) {
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
    await waitForDeploymentPodsToStop(exec, context, root, selector, sleep);
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

function migrationImage(digests, repository) {
  const digest = digests.get(repository);
  if (!digest) fail(`No published image digest was resolved for ${repository}.`);
  return `${REGISTRY}/${repository}@${digest}`;
}

function resolveImageDigest(exec, root, repository, reference) {
  const image = `${REGISTRY}/${repository}${IMAGE_DIGEST.test(reference) ? `@${reference}` : `:${reference}`}`;
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
  if (typeof value !== 'string' || !value.startsWith(`${REGISTRY}/`)) return undefined;
  const reference = value.slice(`${REGISTRY}/`.length);
  for (const repository of IMAGE_REPOSITORIES) {
    if (reference.startsWith(`${repository}:`)) {
      const tag = reference.slice(repository.length + 1);
      if (!IMAGE_TAG.test(tag)) fail(`Current deployment has an invalid image tag for ${repository}.`);
      return { repository, reference: tag };
    }
    if (reference.startsWith(`${repository}@`)) {
      const digest = reference.slice(repository.length + 1);
      if (!IMAGE_DIGEST.test(digest)) fail(`Current deployment has an invalid image digest for ${repository}.`);
      return { repository, reference: digest };
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
      if (existing && existing !== image.reference) {
        fail(`Current deployment has conflicting image references for ${image.repository}; include it in the manifest.`);
      }
      found.set(image.repository, image.reference);
    }
  }
  for (const repository of needed) {
    if (!found.has(repository)) {
      fail(`Could not find the currently deployed image for ${repository}; include it in the manifest.`);
    }
  }
  return { references: found, agentHostSettings };
}

function liveAgentHostTuple(settings, digest) {
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
    image: `${REGISTRY}/agentweaver-agenthost@${digest}`,
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

function findJob(resources, name) {
  const matches = resources.filter(resource =>
    resource.kind === 'Job' && resource.metadata?.name === name);
  if (matches.length !== 1) fail(`Kustomize must render exactly one Job/${name}.`);
  return matches[0];
}

function migrationJobs(root, exec, context, digests, selectedRepositories) {
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
      image: migrationImage(digests, 'agentweaver.identity.broker'),
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
        image: migrationImage(digests, repository),
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
      image: migrationImage(digests, 'agentweaver.projects.config'),
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

function applyImageDigests(kustomization, digests) {
  if (!Array.isArray(kustomization?.images) || kustomization.images.length !== IMAGE_REPOSITORIES.length) {
    fail('deploy/k8s must pin exactly the ten published image repositories.');
  }
  const seen = new Set();
  for (const image of kustomization.images) {
    if (!image || typeof image.name !== 'string' || seen.has(image.name)) {
      fail('deploy/k8s contains an invalid or duplicate image pin.');
    }
    seen.add(image.name);
    const repository = image.name.slice(`${REGISTRY}/`.length);
    if (!IMAGE_REPOSITORIES.includes(repository) || image.name !== `${REGISTRY}/${repository}`) {
      fail(`Unexpected Kustomize image repository ${image.name}.`);
    }
    image.newName = image.name;
    image.digest = digests.get(repository);
    delete image.newTag;
  }
  if (IMAGE_REPOSITORIES.some(repository => !seen.has(`${REGISTRY}/${repository}`))) {
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
  const requestedImages = options.imageManifest === undefined
    ? new Map(IMAGE_REPOSITORIES.map(repository => [repository, options.imageTag]))
    : parseImageManifest(readFileSync(path.resolve(root, options.imageManifest), 'utf8'));
  const digests = new Map();
  for (const [repository, reference] of requestedImages) {
    digests.set(repository, resolveImageDigest(exec, root, repository, reference));
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
    for (const [repository, reference] of current.references) {
      digests.set(repository, resolveImageDigest(exec, root, repository, reference));
    }
    if (!requestedImages.has('agentweaver-agenthost')) {
      liveAgentHostSettings = current.agentHostSettings;
      agentHostTuple = liveAgentHostTuple(liveAgentHostSettings,
        digests.get('agentweaver-agenthost'));
    }
  }
  if (requestedImages.has('agentweaver-agenthost')) {
    const measured = await measureAgentHostRegistryImage(
      `${REGISTRY}/agentweaver-agenthost@${digests.get('agentweaver-agenthost')}`, fetchImpl);
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
    applyImageDigests(kustomization, digests);
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
    if (liveAgentHostSettings) {
      preserveLiveAgentHostSettings(resources, liveAgentHostSettings, agentHostTuple);
    }
    stampAgentHostRuntime(resources, agentHostTuple);
    const runtimeConfig = resources.find(resource => resource.kind === 'ConfigMap' &&
      resource.metadata?.name === PROJECTS_CONFIG_RUNTIME_CONFIG);
    if (!runtimeConfig) fail(`Kustomize must render ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}.`);
    const desiredRuntimeMaps = resources.filter(resource =>
      resource.kind === 'ConfigMap' && RUNTIME_CONFIG_MAPS.has(resource.metadata?.name));
    if (desiredRuntimeMaps.length !== RUNTIME_CONFIG_MAPS.size ||
        desiredRuntimeMaps.some(resource => resource.metadata?.namespace !== NAMESPACE || resource.immutable !== true)) {
      fail('Kustomize must render both immutable AgentHost runtime ConfigMaps in namespace agentweaver.');
    }
    const existingRuntimeMaps = new Map([...RUNTIME_CONFIG_MAPS].map(name => [
      name, readExistingNamedRuntimeConfigMap(exec, context, root, name),
    ]));
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
      items: resources.filter(resource => resource.kind !== 'Namespace'),
    };
    const jobs = migrationJobs(root, exec, context, digests, requestedImages.keys());

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
        overrides.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))), log);
    } else if (replaceRuntimeConfig) {
      kubectl(exec, context, ['delete', 'configmap', PROJECTS_CONFIG_RUNTIME_CONFIG], root,
        `Could not delete ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`);
      kubectl(exec, context, ['create', '--filename', '-'], root,
        `Could not create ConfigMap/${PROJECTS_CONFIG_RUNTIME_CONFIG}`, { input: stringify(runtimeConfig) });
    }

    kubectl(exec, context, ['apply', '--filename', '-'], root, 'Workload apply failed',
      { input: stringify(workloads) });
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
