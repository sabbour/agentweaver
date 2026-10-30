#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, stringify } from 'yaml';
import { parseKustomizeResources } from './kustomize-runtime.mjs';
import { run, redact } from '../azure/lib/exec.mjs';

const REGISTRY = 'ghcr.io/sabbour';
const NAMESPACE = 'agentweaver';
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
const DEPLOYMENT_NAME = /^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/;
const JOB_TIMEOUT = '15m';
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

export const DEPLOY_KUSTOMIZE_USAGE = `Usage: npm run deploy:kustomize -- --image-tag sha-<git-commit> --execute

The command runs the existing migration Jobs, renders deploy/k8s with the selected
images, applies namespaced resources to the selected kubectl context, and waits
for every Deployment rollout. It does not create migration credentials or write
deployment receipts.
`;

function fail(message) {
  throw new Error(`Kustomize deployment: ${message}`);
}

export function parseDeployKustomizeArgs(argv) {
  let imageTag;
  let execute = false;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--execute') execute = true;
    else if (arg === '--image-tag') imageTag = argv[++index];
    else fail(`unknown argument "${arg}".`);
  }
  if (!/^sha-[a-f0-9]{40}$/.test(imageTag ?? '')) {
    fail('--image-tag must be a normal sha-<40-character-commit> image tag.');
  }
  if (!execute) fail('--execute is required to run migrations and apply workloads.');
  return { imageTag };
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

function verifyAuthorization(exec, context, root) {
  for (const [verb, resource] of REQUIRED_AUTHORIZATION) {
    const result = kubectl(exec, context, ['auth', 'can-i', verb, resource], root,
      `Could not check Kubernetes permission ${verb} ${resource}`);
    if (result.stdout.trim() !== 'yes') {
      fail(`The selected identity cannot ${verb} ${resource} in namespace ${NAMESPACE}.`);
    }
  }
  for (const name of DEPLOYMENTS)
    for (const verb of ['get', 'list', 'patch', 'watch']) {
      const result = kubectl(exec, context, [
        'auth', 'can-i', verb, 'deployments.apps', '--resource-name', name,
      ], root, `Could not check Kubernetes permission ${verb} deployment/${name}`);
      if (result.stdout.trim() !== 'yes')
        fail(`The selected identity cannot ${verb} deployment/${name} in namespace ${NAMESPACE}.`);
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

function findJob(resources, name) {
  const matches = resources.filter(resource =>
    resource.kind === 'Job' && resource.metadata?.name === name);
  if (matches.length !== 1) fail(`Kustomize must render exactly one Job/${name}.`);
  return matches[0];
}

function migrationJobs(root, exec, context, digests) {
  const p1Rendered = must(exec, 'kubectl', ['kustomize', path.join(root, 'deploy', 'k8s', 'migrations', 'p1-postgres')],
    { cwd: root }, 'P1 migration Job rendering failed').stdout;
  const brokerRendered = must(exec, 'kubectl', [
    'kustomize', path.join(root, 'deploy', 'k8s', 'migrations', 'identity-broker'),
  ], { cwd: root }, 'Broker migration Job rendering failed').stdout;
  const p1Resources = parseKustomizeResources(p1Rendered).items;
  const brokerResources = parseKustomizeResources(brokerRendered).items;
  const projectsConfig = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'migrations',
    'projects-config', 'job.yaml'), 'utf8'));

  const p1Identity = readResource(exec, context, 'serviceaccount', 'p1-postgres-migration', root);
  const p1ClientId = p1Identity.metadata?.annotations?.['azure.workload.identity/client-id'];
  if (!CLIENT_ID.test(p1ClientId ?? '')) {
    fail('ServiceAccount/p1-postgres-migration has no configured workload identity client ID.');
  }
  const projectsConfigIdentity = readResource(exec, context, 'serviceaccount', 'projects-config-migration', root);
  const projectsConfigClientId = projectsConfigIdentity.metadata?.annotations?.['azure.workload.identity/client-id'];
  const expectedProjectsConfigClientId = must(exec, 'az', [
    'identity', 'show', '--resource-group', 'agentweaver', '--name', 'agentweaver-id-projects-config-migration',
    '--query', 'clientId', '-o', 'tsv',
  ], { cwd: root }, 'Could not read the Projects & Config migration identity').stdout.trim();
  if (!CLIENT_ID.test(expectedProjectsConfigClientId) || projectsConfigClientId !== expectedProjectsConfigClientId) {
    fail('ServiceAccount/projects-config-migration does not name the approved migration identity.');
  }
  readResource(exec, context, 'serviceaccount', 'identity-broker-migration', root);
  readResource(exec, context, 'configmap', 'identity-broker-migration-config', root);

  const broker = prepareJob(findJob(brokerResources, 'identity-broker-migration'), {
    image: migrationImage(digests, 'agentweaver.identity.broker'),
    name: `identity-broker-migration-${randomUUID().slice(0, 8)}`,
  });
  const p1 = [
    ['environment-migration', 'agentweaver.environment'],
    ['orchestrator-migration', 'agentweaver.orchestrator'],
    ['events-and-sessions-migration', 'agentweaver.eventsandsessions'],
  ].map(([name, repository]) => {
    const job = prepareJob(findJob(p1Resources, name), {
      image: migrationImage(digests, repository),
      name: `${name}-${randomUUID().slice(0, 8)}`,
    });
    for (const entry of job.spec.template.spec.containers[0].env ?? []) {
      if (entry.name.endsWith('__WorkloadIdentity__ClientId')) entry.value = p1ClientId;
    }
    return job;
  });
  const projectsConfigJob = prepareJob(projectsConfig, {
    image: migrationImage(digests, 'agentweaver.projects.config'),
    name: `projects-config-migration-${randomUUID().slice(0, 8)}`,
  });
  const projectsConfigClientIdSetting = projectsConfigJob.spec.template.spec.containers[0].env
    .find(setting => setting.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId');
  if (!projectsConfigClientIdSetting) {
    fail('Projects Config migration Job has no workload identity client ID setting.');
  }
  projectsConfigClientIdSetting.value = projectsConfigClientId;
  return [broker, ...p1, projectsConfigJob];
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

export async function deployKustomize({ imageTag }, overrides = {}) {
  const root = overrides.root ?? process.cwd();
  const exec = overrides.exec ?? run;
  const log = overrides.log ?? (message => console.log(message));
  const digests = new Map();
  for (const repository of IMAGE_REPOSITORIES) {
    const reference = `${REGISTRY}/${repository}:${imageTag}`;
    const result = must(exec, 'docker', [
      'buildx', 'imagetools', 'inspect', reference, '--format', '{{.Manifest.Digest}}',
    ], { cwd: root }, `Could not read ${reference}`);
    const digest = result.stdout.trim();
    if (!/^sha256:[a-f0-9]{64}$/.test(digest)) fail(`Registry returned an invalid digest for ${reference}.`);
    digests.set(repository, digest);
  }

  const context = must(exec, 'kubectl', ['config', 'current-context'], { cwd: root },
    'Could not read the selected kubectl context').stdout.trim();
  if (!context) fail('No kubectl context is selected.');
  verifyAuthorization(exec, context, root);

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
    const workloads = {
      apiVersion: 'v1',
      kind: 'List',
      items: resources.filter(resource => resource.kind !== 'Namespace'),
    };
    const jobs = migrationJobs(root, exec, context, digests);

    for (const job of jobs) runMigrationJob(job, exec, context, root, log);
    log('Database migrations completed.');

    kubectl(exec, context, ['apply', '--filename', '-'], root, 'Workload apply failed',
      { input: stringify(workloads) });
    log(`Applied Kustomize workloads to ${context}/${NAMESPACE}.`);

    for (const name of deployments) {
      kubectl(exec, context, ['rollout', 'status', `deployment/${name}`, '--timeout=5m'], root,
        `Deployment ${name} did not become ready`);
      log(`Deployment ${name} is ready.`);
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
