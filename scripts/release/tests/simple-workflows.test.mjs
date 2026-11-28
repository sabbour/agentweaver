import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { EventEmitter, once } from 'node:events';
import { chmodSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse, stringify } from 'yaml';
import {
  assertTargetContext,
  BYOK_SMOKE_LOOP,
  deleteContainerNetworkLog,
  deleteKubernetesResourceWithPreconditions,
  deployKustomize as realDeployKustomize,
  ENVIRONMENT_RBAC_REQUIREMENTS,
  verifyImageCapability,
  ensureAgentHostPlatformEgressSeed,
  parseDeployKustomizeArgs,
  parseImageManifest,
  requireLiveByokBindingsPreserved,
  stampAgentHostModelEgress,
  byokEndpointProbeUrls,
} from '../deploy-kustomize.mjs';
import {
  applyModelBindingsOverlay,
  parseModelBindingsOverlay,
  requireLiveAzureCostPreserved,
  stampEventsRuntimeConfigDigest,
} from '../model-bindings-overlay.mjs';
import { applyTargetValues, targetValuePairs } from '../target-values.mjs';
import { run } from '../../azure/lib/exec.mjs';

const deployKustomize = (options, overrides = {}) =>
  realDeployKustomize(options, {
    verifyImageCapability: async () => {},
    deleteMaintenanceLease: overrides.deleteMaintenanceLease ?? overrides.exec?.deleteMaintenanceLease ?? (async () => {}),
    ...overrides,
  });
import { buildIdentityBrokerProviderPlan } from '../../azure/lib/identity-broker-provider-setup.mjs';
import { classify } from '../../ci/v1-changes.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const REGISTRY = 'ghcr.io/sabbour';
const runtimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'projects-config-runtime-config.json'), 'utf8',
));
const brokerRuntimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'identity-broker-runtime-config.json'), 'utf8',
));
const brokerRuntimeConfigName = 'identity-broker-runtime-config-v4';
const readYaml = relativePath => parse(readFileSync(path.join(root, relativePath), 'utf8'));
const brokerMigrationServiceAccount = readYaml('deploy/k8s/migrations/identity-broker/serviceaccount.yaml');
const brokerMigrationClientId =
  brokerMigrationServiceAccount.metadata.annotations['azure.workload.identity/client-id'];
const brokerMigrationTenantId =
  brokerMigrationServiceAccount.metadata.annotations['azure.workload.identity/tenant-id'];
const agentHostRuntimeConfig = readYaml('deploy/k8s/profiles/agenthost/runtime-config.example.yaml');
const agentHostModelEgress = readYaml('deploy/k8s/base/agenthost-model-egress.yaml');
const copilotModelHosts = [
  'api.business.githubcopilot.com',
  'api.enterprise.githubcopilot.com',
  'api.github.com',
  'api.githubcopilot.com',
  'api.individual.githubcopilot.com',
];
const environmentRuntimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'environment-runtime-config.json'), 'utf8',
));
const eventsRuntimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'events-and-sessions-runtime-config.json'), 'utf8',
));
const imageDigest = `sha256:${'a'.repeat(64)}`;
const serviceClientId = '12345678-1234-4123-8123-123456789abc';
const serviceNames = [
  'identity-broker', 'events-and-sessions', 'gateway', 'mcp', 'web',
  'orchestrator', 'environment', 'knowledge', 'projects-config',
];
const jobNames = [
  'identity-broker-migration', 'environment-migration', 'orchestrator-migration',
  'events-and-sessions-migration', 'projects-config-credential-settlement', 'projects-config-migration',
];
const imageRepositories = readYaml('deploy/k8s/kustomization.yaml').images
  .map(({ name }) => name.replace(`${REGISTRY}/`, ''));
const deploymentImages = {
  'identity-broker': 'agentweaver.identity.broker',
  'events-and-sessions': 'agentweaver.eventsandsessions',
  gateway: 'agentweaver.gateway',
  mcp: 'agentweaver.mcp',
  web: 'agentweaver-web',
  orchestrator: 'agentweaver.orchestrator',
  environment: 'agentweaver.environment',
  knowledge: 'agentweaver.knowledge',
  'projects-config': 'agentweaver.projects.config',
};
const deploymentFiles = {
  'identity-broker': '../identity-broker/deployment.yaml',
  'events-and-sessions': 'events-and-sessions-deployment.json',
  gateway: 'gateway-deployment.json',
  mcp: 'mcp-deployment.json',
  web: 'web-deployment.json',
  orchestrator: 'orchestrator-deployment.json',
  environment: 'environment-deployment.json',
  knowledge: 'knowledge-deployment.json',
  'projects-config': 'projects-config-deployment.json',
};
const selectedTag = `sha-${'b'.repeat(40)}`;
const existingTag = `sha-${'c'.repeat(40)}`;
const digestForTag = tag => `sha256:${tag.slice(4)}${'0'.repeat(24)}`;
const restoreReplicasAnnotation = 'agentweaver.io/local-deploy-restore-replicas';
const providerSetupInput = {
  IdentityBroker: {
    WebOrigin: 'https://portal.example.test',
    CopilotConnection: {
      ProjectsOwnerAddress: 'https://projects.example.test/',
      ClientId: 'copilot-client-id',
      CallbackUri: 'https://portal.example.test/auth/github/copilot-app/callback',
      ClientSecretReference: {
        Id: 'copilot-client-secret',
        Version: '0123456789abcdef0123456789abcdef',
      },
    },
    GitHubRepoApp: {
      OAuthClientId: 'repo-app-client-id',
      CallbackUri: 'https://portal.example.test/auth/github/repo-app/callback',
      AppId: 12345,
      AppSlug: 'agentweaver',
      PrivateKeySecretId: 'repo-app-private-key',
      PrivateKeySecretVersion: '0123456789abcdef0123456789abcdef',
    },
  },
  SecretBindings: {
    IdentityBroker__GitHubRepoApp__OAuthClientSecret: {
      SecretKeyRef: { Name: 'github-repo-app-oauth', Key: 'oauth-client-secret' },
    },
  },
};

function agentHostRegistryFixture(layerBytes) {
  const digest = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  const configBytes = Buffer.from(JSON.stringify({
    os: 'linux',
    architecture: 'amd64',
    config: {
      User: '1654:1654',
      Entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
      Labels: {
        'org.opencontainers.image.revision': 'a'.repeat(40),
        'io.agentweaver.source-tree': 'b'.repeat(40),
        'org.opencontainers.image.version': '1.1.1',
      },
    },
  }));
  const configDigest = digest(configBytes);
  const childBytes = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: {
      mediaType: 'application/vnd.oci.image.config.v1+json',
      digest: configDigest,
      size: configBytes.length,
    },
    layers: [{
      mediaType: 'application/vnd.oci.image.layer.v1.tar+gzip',
      digest: 'sha256:' + 'c'.repeat(64),
      size: layerBytes,
    }],
  }));
  const childDigest = digest(childBytes);
  const topBytes = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.index.v1+json',
    manifests: [{
      mediaType: 'application/vnd.oci.image.manifest.v1+json',
      digest: childDigest,
      size: childBytes.length,
      platform: { os: 'linux', architecture: 'amd64' },
    }],
  }));
  const topDigest = digest(topBytes);
  const response = (bytes, manifestDigest) => ({
    ok: true,
    status: 200,
    headers: { get: name => name.toLowerCase() === 'docker-content-digest' ? manifestDigest : null },
    text: async () => bytes.toString('utf8'),
    arrayBuffer: async () => bytes,
  });
  return {
    configDigest, configBytes, childDigest, childBytes, topDigest, topBytes,
    layerSize: layerBytes,
    fetchImpl: async url => {
      const value = String(url);
      if (value.startsWith('https://ghcr.io/token?')) {
        return { ok: true, status: 200, json: async () => ({ token: 'fixture-token' }) };
      }
      if (value.endsWith(`/manifests/${topDigest}`)) return response(topBytes, topDigest);
      if (value.endsWith(`/manifests/${childDigest}`)) return response(childBytes, childDigest);
      if (value.endsWith(`/blobs/${configDigest}`)) return response(configBytes, null);
      assert.fail(`unexpected AgentHost registry request: ${value}`);
    },
  };
}

const selectedAgentHostRegistry = agentHostRegistryFixture(1024);
const existingAgentHostRegistry = agentHostRegistryFixture(2048);

function scratch() {
  const directory = path.join(root, '.test-scratch', `simple-deploy-${process.pid}-${Math.random().toString(16).slice(2)}`);
  mkdirSync(directory, { recursive: true });
  cpSync(path.join(root, 'deploy', 'k8s'), path.join(directory, 'deploy', 'k8s'), { recursive: true });
  return directory;
}

function boundedClock() {
  let current = 0;
  const sleeps = [];
  return {
    now: () => current,
    sleep: async milliseconds => {
      sleeps.push(milliseconds);
      current += milliseconds;
    },
    sleeps,
  };
}

function job(name) {
  return {
    apiVersion: 'batch/v1',
    kind: 'Job',
    metadata: { name, namespace: 'agentweaver' },
    spec: { template: { metadata: { labels: {} }, spec: { containers: [{
      name: 'migration',
      image: 'registry.invalid/image@sha256:0000000000000000000000000000000000000000000000000000000000000000',
      env: [{ name: 'Service__Migration__WorkloadIdentity__ClientId', value: '00000000-0000-0000-0000-000000000000' }],
    }] } } },
  };
}

function brokerMigrationSetupResources(directory, resourceGroup = 'agentweaver') {
  const overlay = path.join(directory, 'deploy', 'k8s', 'migrations', 'identity-broker');
  const resources = ['serviceaccount.yaml', 'configmap.yaml', 'egress.yaml'].map(file =>
    parse(readFileSync(path.join(overlay, file), 'utf8')));
  const egress = resources.find(resource => resource.kind === 'CiliumNetworkPolicy');
  for (const rule of egress.spec.egress) {
    for (const name of [
      ...(rule.toFQDNs ?? []),
      ...(rule.toPorts ?? []).flatMap(port => port.rules?.dns ?? []),
    ]) {
      if (name.matchName === 'CHANGEME-POSTGRES-HOST') {
        name.matchName = 'agentweaver-pg.postgres.database.azure.com';
      }
    }
  }
  const pairs = targetValuePairs(directory, resourceGroup);
  return resources.map(resource => parse(applyTargetValues(stringify(resource), pairs)));
}

function writeBrokerOnlyImageManifest(directory) {
  writeFileSync(path.join(directory, 'images.json'), JSON.stringify({
    images: { 'agentweaver.identity.broker': selectedTag },
  }));
}

function createdBrokerMigrationResources(calls) {
  const names = new Set([
    'identity-broker-migration',
    'identity-broker-migration-egress',
    'identity-broker-migration-config',
  ]);
  return calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create' &&
    !call.args.includes('--dry-run=server'))
    .map(call => parse(call.options.input))
    .filter(resource => names.has(resource.metadata?.name));
}

function workloadSource(kustomizationPath, name) {
  return parse(readFileSync(path.join(path.dirname(kustomizationPath), 'base', 'workloads',
    deploymentFiles[name]), 'utf8'));
}

function projectsMigrationPod(jobResource) {
  return {
    metadata: {
      name: `${jobResource.metadata.name}-abcde`,
      namespace: 'agentweaver',
      labels: structuredClone(jobResource.spec.template.metadata.labels),
      ownerReferences: [{
        apiVersion: 'batch/v1',
        kind: 'Job',
        name: jobResource.metadata.name,
        uid: 'projects-migration-job-uid',
        controller: true,
        blockOwnerDeletion: true,
      }],
    },
    spec: structuredClone(jobResource.spec.template.spec),
    status: { phase: 'Succeeded' },
  };
}

function projectsWorkloadPod(deployment, phase, deletionTimestamp) {
  const metadata = {
    name: 'projects-config-7d9c6f4f8d-abcde',
    namespace: 'agentweaver',
    labels: structuredClone(deployment.spec.template.metadata.labels),
    ownerReferences: [{
      apiVersion: 'apps/v1',
      kind: 'ReplicaSet',
      name: 'projects-config-7d9c6f4f8d',
      uid: 'projects-config-replicaset-uid',
      controller: true,
      blockOwnerDeletion: true,
    }],
  };
  if (deletionTimestamp) metadata.deletionTimestamp = deletionTimestamp;
  return {
    metadata,
    spec: structuredClone(deployment.spec.template.spec),
    status: { phase },
  };
}

function projectsActivationFixture(directory) {
  const imageManifest = path.join(directory, 'images.json');
  writeFileSync(imageManifest, JSON.stringify({ images: { 'agentweaver.projects.config': selectedTag } }));
  const liveRuntimeConfig = structuredClone(runtimeConfig);
  liveRuntimeConfig.data.AgentHost__ModelBindingsRevision = 'old-revision';
  const projectsConfigDeployment = workloadSource(
    path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'), 'projects-config');
  projectsConfigDeployment.spec.replicas = 3;
  for (const container of [
    ...(projectsConfigDeployment.spec.template.spec.containers ?? []),
    ...(projectsConfigDeployment.spec.template.spec.initContainers ?? []),
  ]) {
    if (container.image?.startsWith(`${REGISTRY}/agentweaver.projects.config:`)) {
      container.image = `${REGISTRY}/agentweaver.projects.config:${existingTag}`;
    }
  }
  return {
    imageManifest,
    liveRuntimeConfig,
    state: {
      runtimeConfig: liveRuntimeConfig,
      deployments: { 'projects-config': projectsConfigDeployment },
    },
  };
}

function createdProjectsMigrationPod(calls) {
  const jobCall = calls.find(call => call.command === 'kubectl' && call.args[0] === 'create' &&
    parse(call.options.input).kind === 'Job');
  assert.ok(jobCall, 'Projects migration Job must be created before its Pod is listed');
  const jobResource = parse(jobCall.options.input);
  assert.match(jobResource.metadata.name, /^projects-config-migration-[a-f0-9]{8}$/);
  return projectsMigrationPod(jobResource);
}

function renderedRoot(kustomizationPath, {
  brokerConfigMapCount = 1,
  brokerConfigMapReference = brokerRuntimeConfigName,
} = {}) {
  const source = parse(readFileSync(kustomizationPath, 'utf8'));
  assert.equal(source.images.length, 10);
  assert.equal(source.images.every(image => /^sha256:[a-f0-9]{64}$/.test(image.digest)), true);
  const resources = serviceNames.map(name => workloadSource(kustomizationPath, name));
  const broker = resources.find(resource => resource.metadata.name === 'identity-broker');
  for (const container of broker.spec.template.spec.containers ?? []) {
    for (const source of container.envFrom ?? []) {
      if (source.configMapRef?.name === brokerRuntimeConfigName) {
        source.configMapRef.name = brokerConfigMapReference;
      }
    }
  }
  for (const image of source.images) {
    const oldImage = `${image.name}:`;
    const newImage = `${image.newName}@${image.digest}`;
    for (const resource of resources) {
      for (const container of [
        ...(resource.spec?.template?.spec?.containers ?? []),
        ...(resource.spec?.template?.spec?.initContainers ?? []),
      ]) {
        if (container.image?.startsWith(oldImage)) container.image = newImage;
        for (const setting of container.env ?? []) {
          if (setting.value?.startsWith(oldImage)) setting.value = newImage;
        }
      }
    }
  }
  return [
    'apiVersion: v1\nkind: Namespace\nmetadata:\n  name: agentweaver\n  labels:\n    pod-security.kubernetes.io/enforce: restricted\n',
    ...Array.from({ length: brokerConfigMapCount }, () => stringify(brokerRuntimeConfig)),
    stringify(runtimeConfig),
    stringify(environmentRuntimeConfig),
    stringify(agentHostRuntimeConfig),
    stringify(eventsRuntimeConfig),
    stringify(agentHostModelEgress),
    ...resources.map(stringify),
  ].join('---\n');
}

const canaryNode = region => ({ metadata: { labels: { 'topology.kubernetes.io/region': region } } });

function fakeExec(calls, {
  failedJob, failedRollout, missingProjectsConfigClientId, liveImageRefs, distinctDigests = false,
  brokerMigrationLiveResources = [], brokerMigrationReadFailure,
  brokerMigrationIdentityMismatch = false, brokerMigrationTenantMismatch = false,
  brokerMigrationCreateRace, brokerMigrationCreateRaceConflict = false, brokerMigrationCreateFailure,
  brokerMigrationDryRunRace, brokerMigrationDryRunRaceConflict = false,
  selectedContextServer = 'selected-context',
  liveRuntimeConfig = runtimeConfig, denyRuntimeConfigDelete = false,
  liveBrokerRuntimeConfig = brokerRuntimeConfig, missingBrokerRuntimeConfig = false,
  brokerConfigMapReadFailure = false, brokerConfigMapReadOutput,
  brokerConfigMapCount = 1, brokerConfigMapReference = brokerRuntimeConfigName,
  liveAgentHostSettings = {}, failApplyCount = 0, remainingPodPolls = 0, podList, state,
  denyEnvironmentPermission, denyEnvironmentPermissionExitCode = 0,
  denyEnvironmentPermissionOutput = 'no\n', environmentPermissionCheckError,
  denyRuntimePermission, missingSecrets = [], secretTypes = {},
  liveStorageClass, kataNodes, clusterNodes, liveEnvironmentPresent = false,
  amaLogs = { metadata: { generation: 2 }, status: { observedGeneration: 2, desiredNumberScheduled: 3,
    numberReady: 3, numberAvailable: 3, updatedNumberScheduled: 3 } },
  amaLogsPods, denyKubeSystemPodList = false,
  smokePod = { status: { phase: 'Succeeded' } }, smokeLogs = 'SMOKE_OK\n', smokeCreateError,
  networkLogApiAvailable = false, networkLogResource, networkLogRace,
  liveProviderSetup,
  activateProviderDuringMigration,
  selectedAgentHostImage = selectedAgentHostRegistry,
  existingAgentHostImage = existingAgentHostRegistry,
} = {}) {
  const live = state ?? { runtimeConfig: structuredClone(liveRuntimeConfig), deployments: {} };
  live.configMaps ??= {};
  live.migrationResources ??= {};
  if (networkLogResource !== undefined) live.networkLogResource = structuredClone(networkLogResource);
  const withIdentity = resource => {
    const value = structuredClone(resource);
    value.metadata.uid ??= `uid-${value.metadata.name}`;
    value.metadata.resourceVersion ??= '1';
    return value;
  };
  if (live.networkLogResource) live.networkLogResource = withIdentity(live.networkLogResource);
  let pendingNetworkLogRace = networkLogRace;
  const raceNetworkLogWrite = operation => {
    if (pendingNetworkLogRace !== operation) return;
    pendingNetworkLogRace = undefined;
    if (operation === 'create') {
      live.networkLogResource = withIdentity({
        apiVersion: 'acn.azure.com/v1alpha1',
        kind: 'ContainerNetworkLog',
        metadata: { name: 'agentweaver-agenthost-egress', labels: { 'agentweaver.dev/managed-by': 'third-party' } },
        spec: {},
      });
    } else {
      live.networkLogResource.metadata.labels['agentweaver.dev/managed-by'] = 'third-party';
      live.networkLogResource.metadata.resourceVersion =
        String(Number(live.networkLogResource.metadata.resourceVersion) + 1);
    }
  };
  for (const resource of brokerMigrationLiveResources) {
    const value = withIdentity(resource);
    if (value.kind === 'ConfigMap') live.configMaps[value.metadata.name] = value;
    else live.migrationResources[value.metadata.name] = value;
  }
  live.configMaps['agenthost-runtime-v1'] ??= withIdentity(agentHostRuntimeConfig);
  live.configMaps['environment-runtime-config'] ??= withIdentity(environmentRuntimeConfig);
  if (!missingBrokerRuntimeConfig) {
    live.configMaps[brokerRuntimeConfigName] ??= withIdentity(liveBrokerRuntimeConfig);
  }
  if (liveProviderSetup) {
    live.configMaps[liveProviderSetup.configMap.metadata.name] ??=
      withIdentity(liveProviderSetup.configMap);
  }
  let podPolls = remainingPodPolls;
  let podListIndex = 0;
  const racedBrokerMigrationResources = new Set();
  const storeBrokerMigrationResource = resource => {
    const value = withIdentity(resource);
    if (value.kind === 'ConfigMap') live.configMaps[value.metadata.name] = value;
    else live.migrationResources[value.metadata.name] = value;
  };
  const raceBrokerMigrationCreate = (resource, raceName, conflicting) => {
    const name = resource.metadata.name;
    if (name !== raceName || racedBrokerMigrationResources.has(name)) return false;
    const value = structuredClone(resource);
    if (conflicting) {
      if (value.kind === 'ServiceAccount') {
        value.metadata.annotations['azure.workload.identity/client-id'] = serviceClientId;
      } else if (value.kind === 'CiliumNetworkPolicy') {
        value.spec.egress.at(-1).toFQDNs[0].matchName = 'conflict.example.test';
      } else if (value.kind === 'ConfigMap') {
        value.data.ConnectionStrings__IdentityBrokerMigration = 'conflict';
      }
    }
    storeBrokerMigrationResource(value);
    racedBrokerMigrationResources.add(name);
    return true;
  };
  let failedApplies = failApplyCount;
  let activatedProviderDuringMigration = false;
  const liveDeployment = name => {
    if (!live.deployments[name]) {
      const kustomizationPath = path.join(calls[0].options.cwd, 'deploy', 'k8s', 'kustomization.yaml');
      const deployment = workloadSource(kustomizationPath, name);
      for (const container of [
        ...(deployment.spec?.template?.spec?.containers ?? []),
        ...(deployment.spec?.template?.spec?.initContainers ?? []),
      ]) {
        for (const repository of imageRepositories) {
          const prefix = `${REGISTRY}/${repository}:`;
          const reference = liveImageRefs && Object.hasOwn(liveImageRefs, repository)
            ? liveImageRefs[repository]
            : repository === 'agentweaver-agenthost' ? existingAgentHostRegistry.childDigest : existingTag;
          if (reference === null) {
            if (container.image?.startsWith(prefix)) container.image = undefined;
          } else if (container.image?.startsWith(prefix)) {
            container.image = `${REGISTRY}/${repository}${reference.startsWith('sha256:')
              ? `@${reference}` : `:${reference}`}`;
          }
          for (const setting of container.env ?? []) {
            if (reference === null) {
              if (setting.value?.startsWith(prefix)) setting.value = undefined;
            } else if (setting.value?.startsWith(prefix)) {
              setting.value = `${REGISTRY}/${repository}${reference.startsWith('sha256:')
                ? `@${reference}` : `:${reference}`}`;
            }
          }
        }
      }
      if (name === 'environment') {
        for (const [settingName, value] of Object.entries(liveAgentHostSettings)) {
          const setting = deployment.spec.template.spec.containers
            .find(container => container.name === 'environment').env
            .find(entry => entry.name === settingName);
          if (setting) setting.value = value;
        }
      }
      if (name === 'identity-broker') {
        deployment.metadata.uid ??= 'uid-identity-broker';
        deployment.metadata.resourceVersion ??= '1';
        deployment.metadata.generation ??= 1;
        if (liveProviderSetup) {
          const container = deployment.spec.template.spec.containers
            .find(item => item.name === 'identity-broker');
          container.envFrom.find(item => item.configMapRef).configMapRef.name =
            liveProviderSetup.configMap.metadata.name;
          container.env.push(...structuredClone(liveProviderSetup.deployment.env));
          deployment.spec.template.metadata.annotations = {
            ...(deployment.spec.template.metadata.annotations ?? {}),
            ...liveProviderSetup.deployment.podTemplateAnnotations,
          };
        }
      }
      live.deployments[name] = deployment;
    }
    return live.deployments[name];
  };
  const exec = (command, args, options = {}) => {
    calls.push({ command, args, options });
    if (command === 'docker') {
      const reference = args[3];
      if (reference.startsWith(`${REGISTRY}/agentweaver-agenthost`)) {
        const fixture = reference.endsWith(`:${existingTag}`)
          ? existingAgentHostImage
          : selectedAgentHostImage;
        const digest = reference.includes('@sha256:')
          ? reference.slice(reference.lastIndexOf('@') + 1)
          : fixture.topDigest;
        return { status: 0, stdout: `${digest}\n`, stderr: '' };
      }
      const digest = !distinctDigests ? imageDigest
        : reference.includes('@sha256:') ? reference.slice(reference.lastIndexOf('@') + 1)
          : reference.endsWith(`:${selectedTag}`) ? digestForTag(selectedTag)
            : digestForTag(existingTag);
      return { status: 0, stdout: `${digest}\n`, stderr: '' };
    }
    if (command === 'az') {
      if (args[0] === 'identity' && args[1] === 'show' &&
          args.includes('--query') && args.includes('{clientId:clientId,tenantId:tenantId}')) {
        const resourceGroup = args[args.indexOf('--resource-group') + 1];
        const clientId = targetValuePairs(options.cwd, resourceGroup).reduce(
          (value, [from, to]) => value === from ? to : value,
          brokerMigrationClientId,
        );
        return {
          status: 0,
          stdout: JSON.stringify({
            clientId: brokerMigrationIdentityMismatch ? serviceClientId : clientId,
            tenantId: brokerMigrationTenantMismatch ? serviceClientId : brokerMigrationTenantId,
          }),
          stderr: '',
        };
      }
      return { status: 0, stdout: `${serviceClientId}\n`, stderr: '' };
    }
    if (command !== 'kubectl') return { status: 1, stdout: '', stderr: `unexpected command ${command}` };
    if (args[0] === 'config') return { status: 0, stdout: `${selectedContextServer}\n`, stderr: '' };
    if (args[0] === 'api-resources' && args.includes('--api-group=acn.azure.com')) {
      return { status: 0, stdout: networkLogApiAvailable ? 'containernetworklogs.acn.azure.com\n' : '', stderr: '' };
    }
    if (args[0] === 'auth') {
      if (args.some(arg => arg === '--resource-name' || arg.startsWith('--resource-name='))) {
        return { status: 1, stdout: '', stderr: 'error: unknown flag: --resource-name' };
      }
      const isEnvironmentCheck = args.includes('system:serviceaccount:agentweaver:environment');
      const subresource = args.find(arg => arg.startsWith('--subresource='));
      const permission = `${args[2]} ${args[3]}${subresource ? ` ${subresource}` : ''}`;
      if (isEnvironmentCheck && environmentPermissionCheckError === permission) {
        return { status: 1, stdout: '', stderr: 'error: cannot impersonate ServiceAccount' };
      }
      const denied = denyRuntimeConfigDelete && args[2] === 'delete' &&
        args[3] === 'configmap/projects-config-runtime-config' ||
        denyKubeSystemPodList && permission === 'list pods' && args.includes('kube-system') ||
        isEnvironmentCheck && denyEnvironmentPermission === permission ||
        !isEnvironmentCheck && denyRuntimePermission === permission;
      return {
        status: denied && isEnvironmentCheck ? denyEnvironmentPermissionExitCode : 0,
        stdout: denied && isEnvironmentCheck ? denyEnvironmentPermissionOutput : denied ? 'no\n' : 'yes\n',
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'ciliumnetworkpolicies.cilium.io' &&
        args[2] === 'agenthost-platform-egress') {
      return {
        status: 0,
        stdout: live.platformEgressPolicy ? JSON.stringify(live.platformEgressPolicy) : '',
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'containernetworklogs.acn.azure.com' &&
        args[2] === 'agentweaver-agenthost-egress') {
      return {
        status: 0,
        stdout: live.networkLogResource ? JSON.stringify(live.networkLogResource) : '',
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
      if (live.maintenanceLease) {
        if (args.includes('--output=jsonpath={.spec.holderIdentity}')) {
          return { status: 0, stdout: live.maintenanceLease.spec?.holderIdentity ?? '', stderr: '' };
        }
        return {
          status: 0,
          stdout: JSON.stringify({
            ...live.maintenanceLease,
            metadata: {
              uid: 'lease-uid-1',
              resourceVersion: '1',
              ...live.maintenanceLease.metadata,
            },
          }),
          stderr: '',
        };
      }
      return { status: 1, stdout: '', stderr: 'Error from server (NotFound): leases.coordination.k8s.io "p1-db-maintenance-lock" not found' };
    }
    if (args[0] === 'create' && args.includes('--filename') && options?.input?.trimStart().startsWith('{')) {
      const parsed = JSON.parse(options.input);
      if (parsed.kind === 'Lease' && parsed.metadata?.name === 'p1-db-maintenance-lock') {
        live.maintenanceLease = {
          ...parsed,
          metadata: { uid: 'lease-uid-1', resourceVersion: '1', ...parsed.metadata },
        };
        return { status: 0, stdout: 'lease.coordination.k8s.io/p1-db-maintenance-lock created', stderr: '' };
      }
    }
    if (args[0] === 'delete' && args[1] === '--raw' &&
        args[2] === '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock') {
      if (!args.includes('--filename') || args[args.indexOf('--filename') + 1] !== '-' ||
          !args.includes('--context')) {
        return { status: 1, stdout: '', stderr: `unexpected raw delete ${args.join(' ')}` };
      }
      const body = JSON.parse(options.input ?? '{}');
      if (body.apiVersion !== 'v1' || body.kind !== 'DeleteOptions' ||
          !live.maintenanceLease ||
          body.preconditions?.uid !== live.maintenanceLease.metadata?.uid ||
          body.preconditions?.resourceVersion !== live.maintenanceLease.metadata?.resourceVersion) {
        return { status: 1, stdout: '', stderr: 'Conflict: UID or resourceVersion mismatch' };
      }
      live.maintenanceLease = undefined;
      return { status: 0, stdout: 'lease.coordination.k8s.io/p1-db-maintenance-lock deleted', stderr: '' };
    }
    if (args[0] === 'get' && (args[1] === 'jobs' || args[1] === 'pods') &&
        args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: [] }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'configmap') {
      const name = args[2];
      if (name === 'identity-broker-migration-config' &&
          (brokerMigrationReadFailure === name || brokerMigrationReadFailure === true)) {
        return { status: 1, stdout: '', stderr: 'Error from server (Forbidden): cannot read resource' };
      }
      if (name === brokerRuntimeConfigName && brokerConfigMapReadFailure) {
        return { status: 1, stdout: 'private-response-value', stderr: 'private-error-value' };
      }
      if (name === brokerRuntimeConfigName && brokerConfigMapReadOutput !== undefined) {
        return { status: 0, stdout: brokerConfigMapReadOutput, stderr: '' };
      }
      const resource = name === 'projects-config-runtime-config'
        ? live.runtimeConfig
        : live.configMaps[name];
      if (args.includes('--output=name')) {
        return { status: 0, stdout: resource ? `configmap/${name}\n` : '', stderr: '' };
      }
      if (!resource) {
        return {
          status: 0,
          stdout: args.some(arg => arg.startsWith('--ignore-not-found'))
            ? ''
            : JSON.stringify({ kind: 'ConfigMap', metadata: { name, namespace: 'agentweaver' } }),
          stderr: '',
        };
      }
      return {
        status: 0,
        stdout: JSON.stringify(options.projectJson ? options.projectJson(resource) : resource),
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'deployment') {
      if (args.includes('--ignore-not-found=true') && args[2] === 'environment' &&
          !liveEnvironmentPresent && !live.deployments?.environment) {
        return { status: 0, stdout: '', stderr: '' };
      }
      return { status: 0, stdout: JSON.stringify(live.deployments?.[args[2]] ?? liveDeployment(args[2])), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'secret') {
      const names = args.slice(2, args.findIndex(arg => arg.startsWith('--')))
        .filter(name => !missingSecrets.includes(name));
      return {
        status: 0,
        stdout: names.length
          ? ['NAME TYPE DATA AGE', ...names.map(name => `${name} ${secretTypes[name] ??
            (name.endsWith('-tls') ? 'kubernetes.io/tls' : 'Opaque')} 2 1d`)].join('\n') + '\n'
          : 'No resources found in agentweaver namespace.\n',
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'pods' && args.includes('kube-system')) {
      return { status: 0, stdout: JSON.stringify({ items: amaLogsPods ?? [] }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'pods') {
      const items = podList
        ? podList(podListIndex++, calls)
        : podPolls > 0 ? [{ metadata: { name: 'projects-config-0' } }] : [];
      podPolls = Math.max(0, podPolls - 1);
      return { status: 0, stdout: JSON.stringify({ items }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'serviceaccount' && args[2] === 'p1-postgres-migration') {
      return { status: 0, stdout: JSON.stringify({ metadata: {
        annotations: { 'azure.workload.identity/client-id': serviceClientId },
      } }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'serviceaccount' && args[2] === 'projects-config-migration') {
      return { status: 0, stdout: JSON.stringify({ metadata: {
        annotations: missingProjectsConfigClientId ? {} : {
          'azure.workload.identity/client-id': serviceClientId,
        },
      } }), stderr: '' };
    }
    if (args[0] === 'get' &&
        ((args[1] === 'serviceaccount' && args[2] === 'identity-broker-migration') ||
         (args[1] === 'ciliumnetworkpolicy' && args[2] === 'identity-broker-migration-egress'))) {
      if (brokerMigrationReadFailure === args[2] || brokerMigrationReadFailure === true) {
        return { status: 1, stdout: '', stderr: 'Error from server (Forbidden): cannot read resource' };
      }
      const resource = live.migrationResources[args[2]];
      return {
        status: 0,
        stdout: resource ? JSON.stringify(resource) : '',
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'storageclass') {
      const storageClass = liveStorageClass === undefined ? {
        metadata: { name: args[2] }, provisioner: 'file.csi.azure.com', mountOptions: [
          'actimeo=30', 'cache=strict', 'dir_mode=0755', 'file_mode=0644', 'gid=1000', 'mfsymlinks', 'uid=1000',
        ],
      } : liveStorageClass;
      return { status: 0, stdout: storageClass ? JSON.stringify(storageClass) : '', stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'runtimeclass') {
      return { status: 0, stdout: JSON.stringify({ metadata: { name: args[2] }, handler: 'kata',
        scheduling: { nodeSelector: { 'kubernetes.azure.com/kata-vm-isolation': 'true' } } }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'nodes') {
      if (!args.includes('--selector') && clusterNodes !== undefined) {
        return { status: 0, stdout: typeof clusterNodes === 'string' ? clusterNodes :
          JSON.stringify({ items: clusterNodes }), stderr: '' };
      }
      const items = kataNodes ?? [{ spec: {}, status: { conditions: [{ type: 'Ready', status: 'True' }] } }];
      return { status: 0, stdout: JSON.stringify({ items }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'daemonset' && args[2] === 'ama-logs') {
      return { status: 0, stdout: amaLogs ? JSON.stringify(amaLogs) : '', stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'pod' && args[2].startsWith('agentweaver-install-smoke-')) {
      return { status: 0, stdout: JSON.stringify(smokePod), stderr: '' };
    }
    if (args[0] === 'get') {
      const name = args[2];
      return { status: 0, stdout: JSON.stringify({ kind: args[1], metadata: { name } }), stderr: '' };
    }
    if (args[0] === 'kustomize') {
      const relative = path.relative(calls[0].options.cwd, args[1]).replaceAll('\\', '/');
      if (relative === 'deploy/k8s') {
        calls.at(-1).renderedImages = parse(readFileSync(path.join(args[1], 'kustomization.yaml'), 'utf8')).images;
        return { status: 0, stdout: renderedRoot(path.join(args[1], 'kustomization.yaml'), {
          brokerConfigMapCount,
          brokerConfigMapReference,
        }), stderr: '' };
      }
      if (relative.endsWith('migrations/p1-postgres')) {
        return { status: 0, stdout: [
          job('environment-migration'), job('orchestrator-migration'), job('events-and-sessions-migration'),
        ].map(value => `---\n${JSON.stringify(value)}`).join('\n'), stderr: '' };
      }
      if (relative.endsWith('migrations/identity-broker')) {
        const resources = [
          ...brokerMigrationSetupResources(calls[0].options.cwd),
          parse(readFileSync(path.join(args[1], 'job.yaml'), 'utf8')),
        ];
        return {
          status: 0,
          stdout: resources.map(resource => `---\n${stringify(resource)}`).join('\n'),
          stderr: '',
        };
      }
      return { status: 1, stdout: '', stderr: `unexpected kustomize path ${relative}` };
    }
    if (args[0] === 'create') {
      const resource = parse(options.input);
      if (resource.kind === 'ContainerNetworkLog') {
        raceNetworkLogWrite('create');
        if (live.networkLogResource) {
          return {
            status: 1, stdout: '',
            stderr: 'Error from server (AlreadyExists): containernetworklog "agentweaver-agenthost-egress" already exists',
          };
        }
        live.networkLogResource = withIdentity(resource);
        return { status: 0, stdout: 'containernetworklog created\n', stderr: '' };
      }
      if (args.includes('--dry-run=server')) {
        if (raceBrokerMigrationCreate(
          resource, brokerMigrationDryRunRace, brokerMigrationDryRunRaceConflict,
        )) {
          return {
            status: 1, stdout: '',
            stderr: `Error from server (AlreadyExists): ${resource.kind.toLowerCase()} "${resource.metadata.name}" already exists`,
          };
        }
        return { status: 0, stdout: `${resource.kind.toLowerCase()}/${resource.metadata.name}\n`, stderr: '' };
      }
      if (resource.kind === 'List' && smokeCreateError) {
        return { status: 1, stdout: '', stderr: smokeCreateError };
      }
      if (resource.kind === 'CiliumNetworkPolicy' &&
          resource.metadata?.name === 'agenthost-platform-egress') {
        if (live.platformEgressPolicy) {
          return {
            status: 1,
            stdout: '',
            stderr: 'Error from server (AlreadyExists): ciliumnetworkpolicies.cilium.io "agenthost-platform-egress" already exists',
          };
        }
        live.platformEgressPolicy = structuredClone(resource);
        live.platformEgressPolicy.metadata.resourceVersion = '1';
        return { status: 0, stdout: 'ciliumnetworkpolicy created\n', stderr: '' };
      }
      if (resource.kind === 'ServiceAccount' || resource.kind === 'CiliumNetworkPolicy' ||
          resource.kind === 'ConfigMap' && resource.metadata.name === 'identity-broker-migration-config') {
        const existing = resource.kind === 'ConfigMap'
          ? live.configMaps[resource.metadata.name]
          : live.migrationResources[resource.metadata.name];
        if (existing || raceBrokerMigrationCreate(
          resource, brokerMigrationCreateRace, brokerMigrationCreateRaceConflict,
        )) {
          return {
            status: 1, stdout: '',
            stderr: `Error from server (AlreadyExists): ${resource.kind.toLowerCase()} "${resource.metadata.name}" already exists`,
          };
        }
        if (resource.metadata.name === brokerMigrationCreateFailure) {
          return { status: 1, stdout: '', stderr: 'Error from server (Forbidden): cannot create resource' };
        }
        storeBrokerMigrationResource(resource);
        return { status: 0, stdout: `${resource.kind.toLowerCase()}/${resource.metadata.name} created\n`, stderr: '' };
      }
      if (resource.kind === 'ConfigMap') {
        const name = resource.metadata.name;
        resource.metadata.uid ??= `uid-${name}-new`;
        resource.metadata.resourceVersion ??= '2';
        if (name === 'projects-config-runtime-config') live.runtimeConfig = resource;
        else live.configMaps[name] = resource;
        return { status: 0, stdout: 'configmap created\n', stderr: '' };
      }
      return { status: 0, stdout: 'job created\n', stderr: '' };
    }
    if (args[0] === 'delete' && args[1] === '--raw') {
      const name = decodeURIComponent(args[2].split('/').at(-1));
      const resource = live.configMaps[name];
      const preconditions = JSON.parse(options.input).preconditions;
      if (!resource || resource.metadata.uid !== preconditions.uid ||
          resource.metadata.resourceVersion !== preconditions.resourceVersion) {
        return { status: 1, stdout: '', stderr: 'Conflict: UID or resourceVersion precondition failed' };
      }
      delete live.configMaps[name];
      return { status: 0, stdout: 'configmap deleted\n', stderr: '' };
    }
    if (args[0] === 'delete' && args[1] === 'containernetworklogs.acn.azure.com' &&
        args[2] === 'agentweaver-agenthost-egress') {
      raceNetworkLogWrite('delete');
      const preconditionArg = args.find(arg => arg.startsWith('--preconditions='));
      if (preconditionArg) {
        const preconditions = Object.fromEntries(preconditionArg.slice('--preconditions='.length)
          .split(',').map(value => value.split('=')));
        if (preconditions.uid !== live.networkLogResource?.metadata.uid ||
            preconditions.resourceVersion !== live.networkLogResource?.metadata.resourceVersion) {
          return { status: 1, stdout: '', stderr: 'Conflict: UID or resourceVersion precondition failed' };
        }
      }
      live.networkLogResource = undefined;
      return { status: 0, stdout: 'containernetworklog deleted\n', stderr: '' };
    }
    if (args[0] === 'delete' && args[1] === 'configmap') {
      live.runtimeConfig = undefined;
      return { status: 0, stdout: 'configmap deleted\n', stderr: '' };
    }
    if (args[0] === 'annotate') {
      const deployment = liveDeployment(args[1].slice('deployment/'.length));
      deployment.metadata.annotations ??= {};
      const annotation = args[2];
      if (annotation.endsWith('-')) delete deployment.metadata.annotations[annotation.slice(0, -1)];
      else {
        const [key, value] = annotation.split('=');
        deployment.metadata.annotations[key] = value;
      }
      return { status: 0, stdout: 'deployment annotated\n', stderr: '' };
    }
    if (args[0] === 'scale') {
      const replicas = Number(args.find(arg => arg.startsWith('--replicas=')).split('=')[1]);
      liveDeployment(args[1].slice('deployment/'.length)).spec.replicas = replicas;
      return { status: 0, stdout: 'deployment scaled\n', stderr: '' };
    }
    if (args[0] === 'wait') {
      const name = args[2].slice('job/'.length);
      if (name.startsWith(failedJob ?? '\u0000')) {
        return { status: 1, stdout: '', stderr: 'job failed' };
      }
      if (activateProviderDuringMigration && !activatedProviderDuringMigration) {
        activatedProviderDuringMigration = true;
        const deployment = liveDeployment('identity-broker');
        const container = deployment.spec.template.spec.containers
          .find(item => item.name === 'identity-broker');
        const providerNames = new Set(activateProviderDuringMigration.deployment.env
          .map(entry => entry.name));
        container.env = [
          ...container.env.filter(entry => !providerNames.has(entry.name)),
          ...structuredClone(activateProviderDuringMigration.deployment.env),
        ];
        container.envFrom.find(item => item.configMapRef).configMapRef.name =
          activateProviderDuringMigration.configMap.metadata.name;
        deployment.spec.template.metadata.annotations ??= {};
        Object.assign(deployment.spec.template.metadata.annotations,
          activateProviderDuringMigration.deployment.podTemplateAnnotations);
        deployment.metadata.resourceVersion = '2';
        live.configMaps[activateProviderDuringMigration.configMap.metadata.name] =
          withIdentity(activateProviderDuringMigration.configMap);
      }
      return { status: 0, stdout: 'condition met\n', stderr: '' };
    }
    if (args[0] === 'logs' && args[1]?.startsWith('pod/agentweaver-install-smoke-')) {
      return { status: 0, stdout: smokeLogs, stderr: '' };
    }
    if (args[0] === 'logs') return { status: 0, stdout: 'migration failed safely\n', stderr: '' };
    if (args[0] === 'patch' && args[1] === 'deployment/identity-broker') {
      const deployment = liveDeployment('identity-broker');
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      for (const operation of patch.filter(value => value.op === 'test')) {
        const actual = operation.path === '/metadata/uid'
          ? deployment.metadata.uid
          : operation.path === '/metadata/resourceVersion'
            ? deployment.metadata.resourceVersion
            : undefined;
        if (actual !== operation.value) {
          return { status: 1, stdout: '', stderr: 'Conflict: UID or resourceVersion precondition failed' };
        }
      }
      const spec = patch.find(value => value.op === 'replace' && value.path === '/spec');
      deployment.spec = structuredClone(spec.value);
      deployment.metadata.resourceVersion = String(Number(deployment.metadata.resourceVersion) + 1);
      return { status: 0, stdout: 'deployment patched\n', stderr: '' };
    }
    if (args[0] === 'patch' && args[1] === 'containernetworklogs.acn.azure.com' &&
        args[2] === 'agentweaver-agenthost-egress') {
      raceNetworkLogWrite('update');
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      for (const operation of patch.filter(value => value.op === 'test')) {
        const actual = operation.path === '/metadata/uid'
          ? live.networkLogResource?.metadata.uid
          : operation.path === '/metadata/resourceVersion'
            ? live.networkLogResource?.metadata.resourceVersion
            : operation.path === '/metadata/labels/agentweaver.dev~1managed-by'
              ? live.networkLogResource?.metadata.labels?.['agentweaver.dev/managed-by']
              : undefined;
        if (actual !== operation.value) {
          return { status: 1, stdout: '', stderr: 'Conflict: collector ownership or resourceVersion changed' };
        }
      }
      const spec = patch.find(value => value.op === 'replace' && value.path === '/spec');
      live.networkLogResource.spec = structuredClone(spec.value);
      live.networkLogResource.metadata.resourceVersion =
        String(Number(live.networkLogResource.metadata.resourceVersion) + 1);
      return { status: 0, stdout: 'containernetworklog patched\n', stderr: '' };
    }
    if (args[0] === 'apply') {
      if (failedApplies > 0) {
        failedApplies -= 1;
        return { status: 1, stdout: '', stderr: 'simulated workload apply failure' };
      }
      const applied = parse(options.input);
      if (applied.kind === 'ContainerNetworkLog') {
        raceNetworkLogWrite('update');
        live.networkLogResource = withIdentity(applied);
        return { status: 0, stdout: 'containernetworklog applied\n', stderr: '' };
      }
      const resources = applied.items;
      for (const resource of resources) {
        if (resource.kind === 'ConfigMap') {
          const name = resource.metadata.name;
          const current = name === 'projects-config-runtime-config'
            ? live.runtimeConfig
            : live.configMaps[name];
          resource.metadata.uid ??= current?.metadata.uid ?? `uid-${name}`;
          resource.metadata.resourceVersion ??= current?.metadata.resourceVersion ?? '1';
          if (name === 'projects-config-runtime-config') live.runtimeConfig = resource;
          else live.configMaps[name] = resource;
        }
        if (resource.kind === 'Deployment') {
          const current = live.deployments[resource.metadata.name];
          const marker = current?.metadata?.annotations?.[restoreReplicasAnnotation];
          if (marker !== undefined) {
            resource.metadata.annotations ??= {};
            resource.metadata.annotations[restoreReplicasAnnotation] = marker;
          }
          live.deployments[resource.metadata.name] = resource;
        }
      }
      return { status: 0, stdout: 'resources applied\n', stderr: '' };
    }
    if (args[0] === 'rollout' && args[2] === `deployment/${failedRollout}`) {
      return { status: 1, stdout: '', stderr: 'CrashLoopBackOff' };
    }
    if (args[0] === 'rollout') return { status: 0, stdout: 'deployment successfully rolled out\n', stderr: '' };
    return { status: 0, stdout: '', stderr: '' };
  };
  exec.fetchImpl = async (url, options) => {
    const value = String(url);
    if (value.startsWith('https://ghcr.io/token?')) {
      return selectedAgentHostImage.fetchImpl(url, options);
    }
    for (const fixture of [selectedAgentHostImage, existingAgentHostImage]) {
      if ([fixture.topDigest, fixture.childDigest].some(digest => value.endsWith(`/manifests/${digest}`)) ||
          value.endsWith(`/blobs/${fixture.configDigest}`)) {
        return fixture.fetchImpl(url, options);
      }
    }
    assert.fail(`unexpected AgentHost registry request: ${value}`);
  };
  exec.deleteMaintenanceLease = async ({ apiPath, uid, resourceVersion }) => {
    const expectedPath = '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock';
    if (apiPath !== expectedPath) throw new Error(`unexpected apiPath ${apiPath}`);
    if (!live.maintenanceLease ||
        uid !== live.maintenanceLease.metadata?.uid ||
        resourceVersion !== live.maintenanceLease.metadata?.resourceVersion) {
      throw new Error('Conflict: UID or resourceVersion mismatch');
    }
    live.maintenanceLease = undefined;
  };
  return exec;
}

function isMaintenanceLeaseCall(call) {
  if (call.command !== 'kubectl') return false;
  if (call.args[0] === 'create' && call.options?.input?.trimStart().startsWith('{')) {
    try {
      const doc = JSON.parse(call.options.input);
      if (doc.kind === 'Lease' && doc.metadata?.name === 'p1-db-maintenance-lock') return true;
    } catch {
      return false;
    }
  }
  return call.args[0] === 'delete' && call.args[1] === '--raw' &&
    call.args[2] === '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock';
}

function kubernetesWriteCalls(calls) {
  const writes = new Set(['create', 'delete', 'apply', 'scale', 'annotate', 'patch', 'replace']);
  return calls.filter(call =>
    call.command === 'kubectl' && writes.has(call.args[0]) && !isMaintenanceLeaseCall(call));
}

function isSmokeCall(call) {
  return call.args.some(arg => /agentweaver-install-smoke|install-smoke-check/.test(arg));
}

test('deploy stops when the rendered AgentHost map drops a live BYOK binding or changes it without a new revision', () => {
  const map = (revision, bindings) => ({
    data: { 'appsettings.json': JSON.stringify({ AgentHost: { ModelBindingsRevision: revision, ModelBindings: bindings } }) },
  });
  const hosted = { ModelId: 'hosted', SourceMode: 'HostedCopilot' };
  const byok = {
    ModelId: 'gpt-5.4', SourceMode: 'Byok',
    Provider: { Type: 'openai', BaseUrl: 'https://example.openai.azure.com/openai/v1', WireApi: 'responses' },
  };
  const changed = { ...byok, Provider: { ...byok.Provider, WireApi: 'completions' } };
  const live = map('r1', { hosted, byok });

  assert.doesNotThrow(() => requireLiveByokBindingsPreserved(undefined, map('r1', { hosted })));
  assert.doesNotThrow(() => requireLiveByokBindingsPreserved(live, map('r1', { hosted, byok })));
  assert.doesNotThrow(() => requireLiveByokBindingsPreserved(live, map('r2', { byok: changed })));
  for (const desired of [map('r1', { hosted }), map('r2', { hosted }), map('r1', { hosted, byok: changed })]) {
    assert.throws(() => requireLiveByokBindingsPreserved(live, desired), /would remove or change live BYOK model binding\(s\) byok/);
  }

  assert.doesNotThrow(() => requireLiveByokBindingsPreserved(live, map('r2', { hosted }), ['byok']));
  assert.throws(() => requireLiveByokBindingsPreserved(live, map('r1', { hosted }), ['byok']),
    /only with a new model binding revision/);
  for (const [desired, removals] of [
    [map('r2', { hosted, byok }), ['byok']],
    [map('r2', { hosted }), ['byok', 'hosted']],
    [map('r2', { hosted }), ['missing', 'byok']],
  ]) {
    assert.throws(() => requireLiveByokBindingsPreserved(live, desired, removals),
      /--remove-model-binding names .* which is not a live BYOK binding that this deploy removes/);
  }
  assert.throws(() => requireLiveByokBindingsPreserved(undefined, map('r2', { hosted }), ['byok']),
    /no live ConfigMap\/agenthost-runtime-v1 exists/);
});

test('model bindings file reproduces the environment BYOK keys in every runtime map', () => {
  const product = JSON.parse(agentHostRuntimeConfig.data['appsettings.json']).AgentHost;
  const revision = `${product.ModelBindingsRevision}-byok-20261109`;
  const binding = {
    ModelId: 'gpt-5.4', SourceMode: 'Byok',
    Provider: { Type: 'openai', BaseUrl: 'https://asabbour-demo-resource.openai.azure.com/openai/v1', WireApi: 'responses' },
    SupportedReasoningEfforts: ['low', 'medium', 'high', 'xhigh'],
  };
  const file = {
    kind: 'agentweaver.model-bindings-overlay', schemaVersion: 1,
    baseRevision: product.ModelBindingsRevision, baseModelBindings: product.ModelBindings,
    revision, modelBindings: { 'byok-gpt-5.4': binding },
    rateCard: { id: 'azure-retail-prices.standard.swedencentral', version: '2026-11-09' },
    modelRates: { 'gpt-5.4': { input: '2.5', cachedInput: '0.25', output: '15' } },
  };
  const render = () => structuredClone([agentHostRuntimeConfig, eventsRuntimeConfig, runtimeConfig]);
  const resources = render();
  assert.deepEqual(applyModelBindingsOverlay(resources, parseModelBindingsOverlay(JSON.stringify(file))),
    { revision, references: ['byok-gpt-5.4'] });

  const agentHost = JSON.parse(resources[0].data['appsettings.json']).AgentHost;
  assert.equal(agentHost.ModelBindingsRevision, revision);
  assert.deepEqual(agentHost.ModelBindings, { ...product.ModelBindings, 'byok-gpt-5.4': binding });
  const prefix = 'AgentHost__ModelBindings__byok-gpt-5.4__';
  const bindingKeys = {
    [`${prefix}ModelId`]: 'gpt-5.4',
    [`${prefix}SourceMode`]: 'Byok',
    [`${prefix}Provider__Type`]: 'openai',
    [`${prefix}Provider__BaseUrl`]: 'https://asabbour-demo-resource.openai.azure.com/openai/v1',
    [`${prefix}Provider__WireApi`]: 'responses',
    [`${prefix}SupportedReasoningEfforts__0`]: 'low',
    [`${prefix}SupportedReasoningEfforts__1`]: 'medium',
    [`${prefix}SupportedReasoningEfforts__2`]: 'high',
    [`${prefix}SupportedReasoningEfforts__3`]: 'xhigh',
  };
  const cost = 'EventsAndSessions__Cost__Azure__';
  const costKeys = {
    [`${cost}ResourceId`]: `azure-cost:${revision}`,
    [`${cost}ResourceGeneration`]: '1',
    [`${cost}OptionsRevision`]: revision,
    [`${cost}OptionsSchemaVersion`]: '1',
    [`${cost}PricingModel`]: 'StandardTokens',
    [`${cost}RateCard__Id`]: 'azure-retail-prices.standard.swedencentral',
    [`${cost}RateCard__Version`]: '2026-11-09',
    [`${cost}RateCard__MeterSource`]: 'byok.tokens',
    [`${cost}RateCard__Unit`]: 'USD',
    [`${cost}RateCard__NanoUnitsPerUnit`]: '1000000',
    [`${cost}RateCard__ModelRates__gpt-5.4__input`]: '2.5',
    [`${cost}RateCard__ModelRates__gpt-5.4__cachedInput`]: '0.25',
    [`${cost}RateCard__ModelRates__gpt-5.4__output`]: '15',
  };
  const added = (before, after) => Object.fromEntries(Object.entries(after).filter(([key]) => !(key in before)));
  assert.deepEqual(added(eventsRuntimeConfig.data, resources[1].data), { ...bindingKeys, ...costKeys });
  assert.deepEqual(added(runtimeConfig.data, resources[2].data), {
    [`${prefix}SourceMode`]: 'Byok',
    [`${prefix}Enabled`]: 'true',
    [`${prefix}SupportedReasoningEfforts__0`]: 'low',
    [`${prefix}SupportedReasoningEfforts__1`]: 'medium',
    [`${prefix}SupportedReasoningEfforts__2`]: 'high',
    [`${prefix}SupportedReasoningEfforts__3`]: 'xhigh',
  });
  for (const map of [resources[1], resources[2]]) {
    assert.equal(map.data.AgentHost__ModelBindingsRevision, revision);
  }
  const eventsKeys = Object.keys(resources[1].data);
  assert.equal(eventsKeys.indexOf(`${prefix}ModelId`),
    eventsKeys.indexOf('AgentHost__ModelBindings__gpt-6-luna__SourceMode') + 1);

  const live = render();
  applyModelBindingsOverlay(live, parseModelBindingsOverlay(JSON.stringify(file)));
  assert.doesNotThrow(() => requireLiveAzureCostPreserved(live[1], resources[1]));
  assert.doesNotThrow(() => requireLiveAzureCostPreserved(undefined, resources[1]));
  const repriced = structuredClone(resources[1]);
  repriced.data[`${cost}RateCard__ModelRates__gpt-5.4__output`] = '16';
  assert.throws(() => requireLiveAzureCostPreserved(live[1], repriced), /would change the live Azure rate card/);

  for (const [change, pattern] of [
    [overlay => { overlay.baseRevision = 'other'; }, /are not the base of the model/],
    [overlay => { overlay.baseModelBindings = { coding: product.ModelBindings.coding }; }, /are not the base of the model/],
    [overlay => { overlay.modelBindings = { coding: binding }; }, /"coding" already exists in the product source/],
  ]) {
    const overlay = structuredClone(file);
    change(overlay);
    assert.throws(() => applyModelBindingsOverlay(render(), parseModelBindingsOverlay(JSON.stringify(overlay))), pattern);
  }
  const byokProduct = structuredClone(resources);
  assert.throws(() => applyModelBindingsOverlay(byokProduct, parseModelBindingsOverlay(JSON.stringify({
    ...file, baseRevision: revision, baseModelBindings: agentHost.ModelBindings, revision: `${revision}-2`,
    modelBindings: { other: binding },
  }))), /already has a BYOK binding or an Azure rate card/);

  for (const [change, pattern] of [
    [overlay => { overlay.modelBindings['byok-gpt-5.4'].Provider.ApiKey = 'secret'; }, /unsupported field "ApiKey". Do not put an API key/],
    [overlay => { overlay.revision = overlay.baseRevision; }, /must differ from its baseRevision/],
    [overlay => { overlay.modelBindings['byok-gpt-5.4'].SourceMode = 'HostedCopilot'; }, /must have SourceMode Byok/],
    [overlay => { overlay.modelRates = {}; }, /no modelRates for gpt-5.4/],
    [overlay => { overlay.modelRates.other = { input: '1', output: '1' }; }, /rates for "other", but no binding uses it/],
    [overlay => { overlay.modelRates['gpt-5.4'].input = '2.50'; }, /must be a positive decimal string/],
    [overlay => { overlay.modelRates['gpt-5.4'].output = '0'; }, /must be a positive decimal string/],
  ]) {
    const overlay = structuredClone(file);
    change(overlay);
    assert.throws(() => parseModelBindingsOverlay(JSON.stringify(overlay)), pattern);
  }
});

test('Events rolls out when its runtime map data changes', () => {
  const render = data => [
    { kind: 'ConfigMap', metadata: { name: 'events-and-sessions-runtime-config' }, data },
    { kind: 'Deployment', metadata: { name: 'events-and-sessions' }, spec: { template: { metadata: {} } } },
  ];
  const digest = resources => {
    stampEventsRuntimeConfigDigest(resources);
    return resources[1].spec.template.metadata.annotations['agentweaver.io/runtime-config-sha256'];
  };
  assert.equal(digest(render({ a: '1', b: '2' })), digest(render({ b: '2', a: '1' })));
  assert.notEqual(digest(render({ a: '1' })), digest(render({ a: '2' })));
});

test('deploy adds environment model bindings from a file to all runtime maps and rolls out Events', async () => {
  const directory = scratch();
  const bindingsFile = path.join(directory, 'model-bindings.json');
  const product = JSON.parse(agentHostRuntimeConfig.data['appsettings.json']).AgentHost;
  writeFileSync(bindingsFile, JSON.stringify({
    kind: 'agentweaver.model-bindings-overlay',
    schemaVersion: 1,
    baseRevision: product.ModelBindingsRevision,
    baseModelBindings: product.ModelBindings,
    revision: `${product.ModelBindingsRevision}-byok`,
    modelBindings: {
      'byok-gpt-5.4': {
        ModelId: 'gpt-5.4', SourceMode: 'Byok',
        Provider: { Type: 'openai', BaseUrl: 'https://demo.openai.azure.com/openai/v1', WireApi: 'responses' },
        SupportedReasoningEfforts: ['low', 'high'],
      },
    },
    rateCard: { id: 'azure-retail-prices.standard.swedencentral', version: '2026-11-09' },
    modelRates: { 'gpt-5.4': { input: '2.5', output: '15' } },
  }));
  try {
    const calls = [];
    const messages = [];
    await deployKustomize({ imageTag: selectedTag, modelBindings: bindingsFile }, {
      root: directory,
      exec: fakeExec(calls, { smokeLogs: 'SMOKE_BYOK demo.openai.azure.com 404\nSMOKE_BYOK b.example.com 401\nSMOKE_OK\n' }),
      log: message => messages.push(message),
    });
    const smokePod = parse(calls.find(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'List').options.input).items[1];
    const command = smokePod.spec.containers[0].command;
    assert.deepEqual(command.slice(3), ['smoke', 'https://demo.openai.azure.com/openai/v1/models']);
    assert.doesNotMatch(command[2], /demo\.openai/);
    assert.equal(smokePod.spec.containers[0].env, undefined);
    assert.ok(messages.some(message =>
      /Warning: BYOK endpoint demo\.openai\.azure\.com answered HTTP 404 without a key/.test(message)));
    assert.ok(messages.some(message => /^BYOK endpoint b\.example\.com answered HTTP 401 without a key/.test(message)));
    assert.equal(messages.some(message => /Warning: .*b\.example\.com/.test(message)), false);
    const applied = parse(calls.find(call => call.command === 'kubectl' && call.args[0] === 'apply').options.input);
    const item = (kind, name) => applied.items.find(resource => resource.kind === kind && resource.metadata.name === name);
    const agentHost = JSON.parse(item('ConfigMap', 'agenthost-runtime-v1').data['appsettings.json']).AgentHost;
    assert.equal(agentHost.ModelBindingsRevision, `${product.ModelBindingsRevision}-byok`);
    assert.equal(agentHost.ModelBindings['byok-gpt-5.4'].SourceMode, 'Byok');
    const events = item('ConfigMap', 'events-and-sessions-runtime-config').data;
    assert.equal(events['AgentHost__ModelBindings__byok-gpt-5.4__Provider__BaseUrl'],
      'https://demo.openai.azure.com/openai/v1');
    assert.equal(events.EventsAndSessions__Cost__Azure__ResourceId, `azure-cost:${product.ModelBindingsRevision}-byok`);
    assert.match(item('Deployment', 'events-and-sessions').spec.template.metadata.annotations[
      'agentweaver.io/runtime-config-sha256'], /^[a-f0-9]{64}$/);
    assert.deepEqual(item('CiliumNetworkPolicy', 'agenthost-model-egress').spec.egress[1].toFQDNs,
      [...copilotModelHosts, 'demo.openai.azure.com'].sort().map(matchName => ({ matchName })));
    const projects = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create')
      .map(call => parse(call.options.input))
      .find(resource => resource.metadata?.name === 'projects-config-runtime-config');
    assert.equal(projects.data['AgentHost__ModelBindings__byok-gpt-5.4__SourceMode'], 'Byok');
    assert.equal(projects.data.AgentHost__ModelBindingsRevision, `${product.ModelBindingsRevision}-byok`);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
test('deploy allows AgentHost model egress to exactly the rendered HostedCopilot and BYOK hosts on TCP 443', () => {
  const render = bindings => [
    { kind: 'ConfigMap', metadata: { name: 'agenthost-runtime-v1' },
      data: { 'appsettings.json': JSON.stringify({ AgentHost: { ModelBindings: bindings } }) } },
    structuredClone(agentHostModelEgress),
  ];
  const byok = baseUrl => ({ SourceMode: 'Byok', Provider: { BaseUrl: baseUrl } });
  const hosted = { ModelId: 'gpt-6.1-sol', SourceMode: 'HostedCopilot' };
  const stamped = hostNames => {
    const names = hostNames.map(matchName => ({ matchName }));
    return [
      { ...agentHostModelEgress.spec.egress[0],
        toPorts: [{ ...agentHostModelEgress.spec.egress[0].toPorts[0], rules: { dns: names } }] },
      { toFQDNs: names, toPorts: [{ ports: [{ port: '443', protocol: 'TCP' }] }] },
    ];
  };

  const none = render({});
  assert.deepEqual(stampAgentHostModelEgress(none), []);
  assert.deepEqual(none[1].spec, agentHostModelEgress.spec);

  const copilot = render({ hosted, other: { ...hosted, ModelId: 'gpt-6-luna' } });
  assert.deepEqual(stampAgentHostModelEgress(copilot), copilotModelHosts);
  assert.deepEqual(copilot[1].spec.egress, stamped(copilotModelHosts));

  const byokOnly = render({ a: byok('https://demo.openai.azure.com/openai/v1') });
  assert.deepEqual(stampAgentHostModelEgress(byokOnly), ['demo.openai.azure.com']);
  assert.deepEqual(byokOnly[1].spec.egress, stamped(['demo.openai.azure.com']));

  const resources = render({
    hosted,
    a: byok('https://Demo.openai.azure.com/openai/v1'),
    b: byok('https://demo.openai.azure.com:443/openai/v1/'),
    c: byok('https://other.cognitiveservices.azure.com'),
  });
  const merged = [...copilotModelHosts, 'demo.openai.azure.com', 'other.cognitiveservices.azure.com'].sort();
  assert.deepEqual(stampAgentHostModelEgress(resources), merged);
  assert.deepEqual(resources[1].spec.egress, stamped(merged));

  for (const baseUrl of [
    'http://demo.openai.azure.com/openai/v1',
    'https://demo.openai.azure.com:8443/openai/v1',
    'https://user@demo.openai.azure.com/openai/v1',
    'https://example.com/openai/v1',
    'https://openai.azure.com.example.com/openai/v1',
    'not a url',
  ]) {
    assert.throws(() => stampAgentHostModelEgress(render({ a: byok(baseUrl) })),
      /BYOK model binding a must use an HTTPS Azure OpenAI BaseUrl on port 443/, baseUrl);
  }
  const missing = render({ a: byok('https://demo.openai.azure.com/openai/v1') }).slice(0, 1);
  assert.throws(() => stampAgentHostModelEgress(missing), /DNS-only CiliumNetworkPolicy\/agenthost-model-egress/);
  const widened = render({});
  widened[1].spec.egress.push({ toEntities: ['world'] });
  assert.throws(() => stampAgentHostModelEgress(widened), /DNS-only CiliumNetworkPolicy\/agenthost-model-egress/);
});

test('provider egress seed preserves an existing object on every run', () => {
  const existing = {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name: 'agenthost-platform-egress',
      namespace: 'agentweaver',
      resourceVersion: '42',
      annotations: { 'provider.example/owner': 'environment' },
    },
    spec: { egress: [{ toEndpoints: [{ matchLabels: { 'example.com/target': 'kept' } }] }] },
  };
  const bytes = JSON.stringify(existing);
  const calls = [];
  const exec = (command, args, options) => {
    calls.push({ command, args, options });
    return { status: 0, stdout: bytes, stderr: '' };
  };

  assert.equal(ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root), false);
  assert.equal(ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root), false);
  assert.deepEqual(calls.map(call => call.args[0]), ['get', 'get']);
  assert.equal(JSON.stringify(existing), bytes);
  assert.equal(existing.metadata.resourceVersion, '42');
});

test('provider egress seed creates the exact DNS-only policy only when absent', () => {
  const calls = [];
  const exec = (command, args, options) => {
    calls.push({ command, args, options });
    return args[0] === 'get'
      ? { status: 0, stdout: '', stderr: '' }
      : { status: 0, stdout: 'created\n', stderr: '' };
  };

  assert.equal(ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root), true);
  assert.deepEqual(calls.map(call => call.args[0]), ['get', 'create']);
  assert.deepEqual(parse(calls[1].options.input), {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name: 'agenthost-platform-egress',
      namespace: 'agentweaver',
      labels: { 'agentweaver.io/service': 'agenthost' },
    },
    spec: {
      endpointSelector: { matchLabels: { 'agentweaver.io/workload': 'agenthost' } },
      egress: [{
        toEndpoints: [{
          matchLabels: {
            'k8s:io.kubernetes.pod.namespace': 'kube-system',
            'k8s:k8s-app': 'kube-dns',
          },
        }],
        toPorts: [{
          ports: [
            { port: '53', protocol: 'UDP' },
            { port: '53', protocol: 'TCP' },
          ],
        }],
      }],
    },
  });
  const kustomization = readYaml('deploy/k8s/base/kustomization.yaml');
  assert.equal(kustomization.resources.includes('agenthost-platform-egress.yaml'), false);
});

test('provider egress seed stops after a failed read without creating', () => {
  const calls = [];
  const exec = (command, args) => {
    calls.push({ command, args });
    return { status: 1, stdout: '', stderr: 'Error from server (Forbidden): access denied' };
  };

  assert.throws(() => ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root),
    /Could not read CiliumNetworkPolicy\/agenthost-platform-egress/);
  assert.equal(calls.length, 1);
});

test('provider egress seed stops after a failed create', () => {
  const calls = [];
  const exec = (command, args) => {
    calls.push({ command, args });
    return args[0] === 'get'
      ? { status: 0, stdout: '', stderr: '' }
      : { status: 1, stdout: '', stderr: 'Error from server (Forbidden): create denied' };
  };

  assert.throws(() => ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root),
    /Could not create CiliumNetworkPolicy\/agenthost-platform-egress/);
  assert.deepEqual(calls.map(call => call.args[0]), ['get', 'create']);
});

test('provider egress seed treats an AlreadyExists race as a no-op', () => {
  const calls = [];
  const exec = (command, args) => {
    calls.push({ command, args });
    return args[0] === 'get'
      ? { status: 0, stdout: '', stderr: '' }
      : { status: 1, stdout: '', stderr: 'Error from server (AlreadyExists): policy already exists' };
  };

  assert.equal(ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root), false);
  assert.deepEqual(calls.map(call => call.args[0]), ['get', 'create']);
});

test('provider egress seed does not treat other errors mentioning AlreadyExists as a race', () => {
  const exec = (command, args) => args[0] === 'get'
    ? { status: 0, stdout: '', stderr: '' }
    : {
      status: 1,
      stdout: '',
      stderr: 'Error from server (InternalError): failed to write audit record: AlreadyExists',
    };

  assert.throws(() => ensureAgentHostPlatformEgressSeed(exec, 'selected-context', root),
    /Could not create CiliumNetworkPolicy\/agenthost-platform-egress/);
});

test('install smoke check probes one keyless URL per configured BYOK host', () => {
  const render = bindings => [{ kind: 'ConfigMap', metadata: { name: 'agenthost-runtime-v1' },
    data: { 'appsettings.json': JSON.stringify({ AgentHost: { ModelBindings: bindings } }) } }];
  const byok = baseUrl => ({ SourceMode: 'Byok', Provider: { BaseUrl: baseUrl } });
  assert.deepEqual(byokEndpointProbeUrls(render({
    hosted: { SourceMode: 'HostedCopilot', Provider: { BaseUrl: 'https://api.githubcopilot.com' } },
    a: byok('https://demo.openai.azure.com/openai/v1/?api-key=secret#x'),
    b: byok('https://Demo.openai.azure.com/other'),
    c: byok('https://models.example.com'),
  })), ['https://demo.openai.azure.com/openai/v1/models', 'https://models.example.com/models']);
  assert.deepEqual(byokEndpointProbeUrls(render({})), []);
  for (const baseUrl of ['http://demo.openai.azure.com', 'https://user@demo.openai.azure.com', 'not a url']) {
    assert.throws(() => byokEndpointProbeUrls(render({ a: byok(baseUrl) })),
      /BYOK model binding a must use an HTTPS BaseUrl/, baseUrl);
  }
});

// Header lines captured from Git 2.43/libcurl 8.5 in the AgentHost image with a loopback proxy.
const imageHttp1Trace = `05:16:08.176628 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1
05:16:08.177866 http.c:804 <= Recv header: HTTP/1.1 200 Connection established
05:16:08.890802 http.c:804 => Send header: GET /openai/v1/models HTTP/1.1
05:16:08.890805 http.c:804 => Send header: Host: byok.invalid
05:16:08.934741 http.c:804 <= Recv header: HTTP/1.1 401 Unauthorized`;
const imageHttp2Trace = `05:17:06.487120 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1
05:17:06.488400 http.c:804 <= Recv header: HTTP/1.1 200 Connection established
05:17:06.611972 http.c:804 => Send header: GET /openai/v1/models HTTP/2
05:17:06.611978 http.c:804 => Send header: Host: byok.invalid
05:17:06.849316 http.c:804 <= Recv header: HTTP/2 403`;
const imageProxy403Trace = `05:15:05.608193 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1
05:15:05.609301 http.c:804 <= Recv header: HTTP/1.1 403 Forbidden`;
const imageProxy407Trace = `05:15:21.770152 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1
05:15:21.771518 http.c:804 <= Recv header: HTTP/1.1 407 Proxy Authentication Required`;
const imageProxyCloseTrace = `05:15:36.593820 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1
05:15:36.595251 http.c:804 <= Recv header: HTTP/1.1 200 Connection established
fatal: unable to access endpoint: TLS connection terminated`;
const retryTrace = `${imageHttp2Trace.split('\n').slice(0, -1).join('\n')}
05:17:06.849300 http.c:804 == Info: HTTP/2 stream 1 was not closed cleanly: REFUSED_STREAM
05:17:06.849310 http.c:804 => Send header: CONNECT byok.invalid:443 HTTP/1.1`;

function timeoutShim(bin) {
  const file = path.join(bin, 'timeout');
  writeFileSync(file, '#!/bin/sh\n[ "$1" = 30 ] || exit 1\nshift\nexec "$@"\n');
  chmodSync(file, 0o755);
}

function probeTools() {
  const git = spawnSync('git', ['--exec-path'], { encoding: 'utf8' });
  assert.equal(git.status, 0, git.error?.message ?? git.stderr);
  if (process.platform !== 'win32') return { bash: 'bash', path: process.env.PATH };
  const gitRoot = path.resolve(git.stdout.trim(), '..', '..', '..');
  return {
    bash: path.join(gitRoot, 'bin', 'bash.exe'),
    path: [path.join(gitRoot, 'bin'), path.join(gitRoot, 'usr', 'bin'),
      path.join(gitRoot, 'cmd'), path.join(process.env.SystemRoot, 'System32')].join(path.delimiter),
  };
}

function probeEnvironment(directory, tools, extra = {}) {
  return {
    PATH: tools.path,
    HOME: directory,
    GIT_CEILING_DIRECTORIES: path.dirname(directory),
    ...(process.platform === 'win32' ? { SystemRoot: process.env.SystemRoot } : {}),
    ...extra,
  };
}

async function emittedByokLoop(directory) {
  const calls = [];
  await deployKustomize({ imageTag: selectedTag }, {
    root: directory, exec: fakeExec(calls), log() {},
  });
  const smoke = parse(calls.find(call => call.args[0] === 'create' &&
    parse(call.options.input).kind === 'List').options.input).items[1];
  const script = smoke.spec.containers[0].command[2];
  assert.equal(script.split(BYOK_SMOKE_LOOP).length, 2);
  const fail = script.split('\n').find(line => line.startsWith('fail() '));
  assert.ok(fail);
  return `${fail}\n${BYOK_SMOKE_LOOP}\n`;
}

function processGroup(pid) {
  const result = spawnSync('ps', ['-o', 'pgid=', '-p', String(pid)], { encoding: 'utf8' });
  assert.equal(result.status, 0, `Cannot find process group for ${pid}: ${result.stderr}`);
  const group = Number(result.stdout.trim());
  assert.ok(Number.isInteger(group) && group > 0);
  return group;
}

function assertTerminated(pid) {
  try {
    process.kill(pid, 0);
  } catch (error) {
    if (error.code === 'ESRCH') return;
    throw error;
  }
  const result = spawnSync('ps', ['-o', 'stat=', '-p', String(pid)], { encoding: 'utf8' });
  if (result.status !== 0 && !result.stdout.trim()) {
    assert.throws(() => process.kill(pid, 0), { code: 'ESRCH' });
    return;
  }
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout.trim(), /^Z/, `Process ${pid} is still running`);
}

async function waitForTermination(pid) {
  for (let attempt = 0; ; attempt++) {
    try {
      assertTerminated(pid);
      return;
    } catch (error) {
      if (!(error instanceof assert.AssertionError) || attempt === 99) throw error;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  }
}

async function supervisedShell(tools, directory, env, script, args = [], milliseconds = 10000) {
  const bootstrap = `sentinel=
cleanup() { if [ -n "$sentinel" ]; then kill "$sentinel" 2>/dev/null || :; wait "$sentinel" 2>/dev/null || :; sentinel=; fi; }
trap 'cleanup; exit 125' TERM
trap cleanup EXIT
sleep 60 </dev/null >/dev/null 2>&1 3>&- &
sentinel=$!
printf '%s\\n' "$sentinel" >&3
IFS= read -r ready || exit 125
[ "$ready" = go ] || exit 125
trap - EXIT TERM
`;
  const child = spawn(tools.bash, ['--noprofile', '--norc', '-c', bootstrap + script, 'smoke', ...args], {
    cwd: directory, env, detached: true, stdio: ['pipe', 'pipe', 'pipe', 'pipe'],
  });
  let stdout = '';
  let stderr = '';
  child.stdout.setEncoding('utf8').on('data', value => { stdout += value; });
  child.stderr.setEncoding('utf8').on('data', value => { stderr += value; });
  const closed = once(child, 'close');
  let sentinel;
  let timer;
  let startupTimer;
  let timedOut = false;
  let timerError;
  let released = false;
  let group;
  const killGroup = () => {
    assert.equal(group, child.pid);
    assert.notEqual(processGroup(process.pid), group);
    assert.equal(processGroup(sentinel), group);
    const state = spawnSync('ps', ['-o', 'stat=', '-p', String(sentinel)], { encoding: 'utf8' });
    assert.equal(state.status, 0, `Cannot find sentinel ${sentinel}: ${state.stderr}`);
    assert.match(state.stdout.trim(), /^[^Z]/, 'Sentinel is not alive');
    process.kill(-group, 'SIGKILL');
  };
  try {
    const [data] = await Promise.race([
      once(child.stdio[3], 'data'),
      closed.then(() => { throw new Error('Shell closed before the sentinel was ready'); }),
      new Promise((_, reject) => {
        startupTimer = setTimeout(() => reject(new Error('Sentinel did not start')), 10000);
      }),
    ]);
    clearTimeout(startupTimer);
    sentinel = Number(data.toString().trim());
    assert.ok(Number.isInteger(sentinel) && sentinel > 0);
    group = processGroup(child.pid);
    assert.equal(group, child.pid);
    assert.notEqual(group, processGroup(process.pid));
    assert.equal(processGroup(sentinel), group);
    timer = setTimeout(() => {
      try {
        killGroup();
        timedOut = true;
      } catch (error) {
        timerError = error;
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
        child.stdout.destroy();
        child.stderr.destroy();
        child.stdio[3].destroy();
      }
    }, milliseconds);
    child.stdin.end('go\n');
    released = true;
    const [code, signal] = await closed;
    if (timerError) throw timerError;
    return { code, signal, stdout, stderr, timedOut };
  } finally {
    clearTimeout(startupTimer);
    clearTimeout(timer);
    if (!released && child.exitCode === null && child.signalCode === null) {
      child.kill('SIGTERM');
      await closed.catch(() => {});
    }
    if (released && !timedOut && !timerError) killGroup();
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
  }
}

test('emitted BYOK loop selects endpoint responses from image traces', async () => {
  const directory = scratch();
  try {
    const script = await emittedByokLoop(directory);
    const tools = probeTools();
    const bin = path.join(directory, 'fake-bin');
    mkdirSync(bin);
    const git = path.join(bin, 'git');
    writeFileSync(git, `#!/bin/sh
[ "$#" = 2 ] && [ "$1" = ls-remote ] || exit 2
[ "$GIT_TRACE_CURL" = 1 ] && [ "$GIT_TRACE_CURL_NO_DATA" = 1 ] || exit 2
[ "$GIT_TERMINAL_PROMPT" = 0 ] && [ "$GIT_ASKPASS" = /bin/false ] || exit 2
[ "$GIT_CONFIG_NOSYSTEM" = 1 ] && [ "$GIT_CONFIG_GLOBAL" = /dev/null ] || exit 2
printf '%s\\n' "$1" "$2" >> "$ARG_LOG"
case "$2" in *two.invalid*) printf '%s\\n' "$TRACE_TWO" >&2 ;; *) printf '%s\\n' "$TRACE" >&2 ;; esac
exit 1
`);
    chmodSync(git, 0o755);
    timeoutShim(bin);
    writeFileSync(path.join(bin, 'awk'), '#!/bin/sh\nexec "$AWK_PATH" "$@"\n');
    chmodSync(path.join(bin, 'awk'), 0o755);
    const fixturePath = process.platform === 'win32'
      ? 'FIXTURE_BIN="$(cygpath -u "$FIXTURE_BIN")"\n' : '';
    const setup = `${fixturePath}AWK_PATH="$(command -v awk)"; export AWK_PATH
ARG_LOG="$FIXTURE_BIN/arguments"; export ARG_LOG
PATH="$FIXTURE_BIN"; export PATH
`;
    const run = (trace, urls = ['https://byok.invalid/openai/v1/models'], traceTwo = '', loop = script) =>
      spawnSync(tools.bash, ['--noprofile', '--norc', '-c', setup + loop, 'smoke', ...urls], {
        cwd: directory,
        env: probeEnvironment(directory, tools, {
          FIXTURE_BIN: bin, TRACE: trace, TRACE_TWO: traceTwo,
        }),
        encoding: 'utf8', timeout: 10000,
      });

    const cases = [
      [imageHttp1Trace, 0, 'SMOKE_BYOK byok.invalid 401\n'],
      [imageHttp2Trace, 0, 'SMOKE_BYOK byok.invalid 403\n'],
      [imageHttp1Trace.replace('401 Unauthorized', '404 Not Found'), 0, 'SMOKE_BYOK byok.invalid 404\n'],
      [imageProxy403Trace, 1, 'SMOKE_FAIL byok:byok.invalid\n'],
      [imageProxy407Trace, 1, 'SMOKE_FAIL byok:byok.invalid\n'],
      [imageProxyCloseTrace, 1, 'SMOKE_FAIL byok:byok.invalid\n'],
      ...['403 Forbidden', '407 Proxy Authentication Required', '200 Connection established'].map(response =>
        [`${retryTrace}\n05:17:06.849320 http.c:804 <= Recv header: HTTP/1.1 ${response}`,
          1, 'SMOKE_FAIL byok:byok.invalid\n']),
      [`${retryTrace}\n05:17:06.849320 http.c:804 <= Recv header: HTTP/1.1 200 Connection established\n` +
        '05:17:06.849330 http.c:804 => Send header: GET /openai/v1/models HTTP/2\n' +
        '05:17:06.849340 http.c:804 <= Recv header: HTTP/2 401', 0, 'SMOKE_BYOK byok.invalid 401\n'],
      [imageHttp1Trace.replace('05:16:08.934741 http.c:804 <=',
        '05:16:08.934740 http.c:804 => Send header: POST /openai/v1/models HTTP/1.1\n' +
        '05:16:08.934741 http.c:804 <='), 1, 'SMOKE_FAIL byok:byok.invalid\n'],
      [imageHttp1Trace.replace('05:16:08.934741 http.c:804 <=',
        '05:16:08.934738 http.c:804 => Send header: POST /openai/v1/models HTTP/1.1\n' +
        '05:16:08.934739 http.c:804 => Send header: GET /openai/v1/models HTTP/1.1\n' +
        '05:16:08.934740 http.c:804 => Send header: GET /openai/v1/models HTTP/1.1\n' +
        '05:16:08.934741 http.c:804 <='), 0, 'SMOKE_BYOK byok.invalid 401\n'],
      [imageProxyCloseTrace.replace('fatal: unable to access endpoint: TLS connection terminated', ''), 1,
        'SMOKE_FAIL byok:byok.invalid\n'],
      [imageProxyCloseTrace.replace('TLS connection terminated', 'operation timed out'), 1,
        'SMOKE_FAIL byok:byok.invalid\n'],
      [imageHttp1Trace.replace('HTTP/1.1 401 Unauthorized', 'HTTP/1.1 invalid'), 1,
        'SMOKE_FAIL byok:byok.invalid\n'],
      [imageHttp1Trace.replace('05:16:08.934741 http.c:804 <= Recv header: HTTP/1.1 401',
        '05:16:08.934740 http.c:804 <= Recv header: HTTP/1.1 100 Continue\n' +
        '05:16:08.934741 http.c:804 <= Recv header: HTTP/1.1 401'), 0, 'SMOKE_BYOK byok.invalid 401\n'],
      [imageHttp1Trace.replace('401 Unauthorized', '302 Found') + '\n' + imageHttp2Trace,
        0, 'SMOKE_BYOK byok.invalid 302\n'],
      [imageProxyCloseTrace + '\n05:15:36.595300 http.c:804 <= Recv header: X-A: x 1 => Send header: GET / HTTP/1.1' +
        '\n05:15:36.595301 http.c:804 <= Recv header: X-B: <= Recv header: HTTP/1.1 401 Unauthorized',
      1, 'SMOKE_FAIL byok:byok.invalid\n'],
      [imageHttp1Trace.replace('05:16:08.934741 http.c:804 <= Recv header: HTTP/1.1 401 Unauthorized',
        '05:16:08.934741 http.c:804 <= Recv header: X-B: <= Recv header: HTTP/1.1 401 Unauthorized'),
      1, 'SMOKE_FAIL byok:byok.invalid\n'],
    ];
    for (const [trace, status, output] of cases) {
      const result = run(`${trace}\n05:16:08.934900 http.c:804 <= Recv header: X-Fixture: trace-sensitive-fixture`);
      assert.equal(result.error, undefined, result.error?.message);
      assert.equal(result.status, status, trace);
      assert.equal(result.stdout, output, trace);
      assert.equal(result.stderr, '');
    }

    const multiple = run(imageHttp1Trace,
      ['https://byok.invalid/openai/v1/models', 'https://two.invalid/openai/v1/models'], imageHttp2Trace);
    assert.equal(multiple.status, 0);
    assert.equal(multiple.stdout, 'SMOKE_BYOK byok.invalid 401\nSMOKE_BYOK two.invalid 403\n');
    assert.equal(run('', []).stdout, '');
    const argumentLog = path.join(bin, 'arguments');
    const assertArguments = loop => {
      for (const url of ['https://byok.invalid/openai/v1/models', 'https://byok.invalid/openai/v1/models;probe']) {
        writeFileSync(argumentLog, '');
        const result = run(imageHttp1Trace, [url], '', loop);
        assert.equal(result.status, 0);
        assert.equal(result.stdout, 'SMOKE_BYOK byok.invalid 401\n');
        assert.equal(readFileSync(argumentLog, 'utf8'), `ls-remote\n${url}#\n`);
      }
    };
    assertArguments(script);
    const noFragment = script.replace('git ls-remote "$url#"', 'git ls-remote "$url"');
    assert.notEqual(noFragment, script);
    assert.throws(() => assertArguments(noFragment), assert.AssertionError);

    const currentRule = String.raw`$3 == "=>" && $4 == "Send" && $5 == "header:" && $NF ~ /^HTTP\/[0-9.]+$/ && $6 ~ /^[A-Z]+$/ { get = ($6 == "GET"); next }`;
    const oldRule = '$3 == "=>" && $4 == "Send" && $5 == "header:" && $6 == "GET" { get = 1; next }';
    const oldParser = script.replace(currentRule, oldRule);
    assert.notEqual(oldParser, script);
    const assertRetryResults = loop => {
      for (const response of ['403 Forbidden', '407 Proxy Authentication Required', '200 Connection established']) {
        const trace = `${retryTrace}\n05:17:06.849320 http.c:804 <= Recv header: HTTP/1.1 ${response}`;
        const result = run(trace, undefined, '', loop);
        assert.equal(result.status, 1, response);
        assert.equal(result.stdout, 'SMOKE_FAIL byok:byok.invalid\n');
      }
    };
    assertRetryResults(script);
    assert.throws(() => assertRetryResults(oldParser), assert.AssertionError);

    const messages = [];
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory,
      exec: fakeExec([], { smokeLogs: `${multiple.stdout}SMOKE_OK\n` }),
      log: message => messages.push(message),
    });
    assert.ok(messages.some(message => /BYOK endpoint byok\.invalid answered HTTP 401/.test(message)));
    assert.ok(messages.some(message => /BYOK endpoint two\.invalid answered HTTP 403/.test(message)));
    assert.equal(messages.some(message => /Warning: BYOK endpoint/.test(message)), false);
    const warning = [];
    const status404 = run(imageHttp1Trace.replace('401 Unauthorized', '404 Not Found'));
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory,
      exec: fakeExec([], { smokeLogs: `${status404.stdout}SMOKE_OK\n` }),
      log: message => warning.push(message),
    });
    assert.ok(warning.some(message => /Warning: BYOK endpoint byok\.invalid answered HTTP 404/.test(message)));
    const calls = [];
    const failed = run(imageProxy403Trace);
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, {
        smokePod: { status: { phase: 'Failed' } }, smokeLogs: failed.stdout,
      }), log() {},
    }), /no HTTPS response from BYOK endpoint byok\.invalid \(DNS, proxy, connection, or TLS failure/);
    assert.ok(calls.some(call => call.args[0] === 'delete' &&
      call.args[1]?.startsWith('pod/agentweaver-install-smoke-') &&
      call.args[2]?.startsWith('persistentvolumeclaim/agentweaver-install-smoke-')));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('emitted BYOK loop rejects real Git proxy CONNECT responses offline', {
  skip: process.platform === 'win32' ? 'AgentHost runs on Linux; offline fixtures run on Windows' : false,
}, async () => {
  const directory = scratch();
  const poisoned = ['HTTPS_PROXY', 'https_proxy', 'NO_PROXY', 'no_proxy', 'ALL_PROXY', 'all_proxy',
    'HTTP_PROXY', 'http_proxy', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0',
    'GIT_CONFIG_SYSTEM', 'GIT_CONFIG_PARAMETERS', 'GIT_DIR', 'GIT_WORK_TREE'];
  const saved = new Map(poisoned.map(name => [name, process.env[name]]));
  try {
    const script = await emittedByokLoop(directory);
    const tools = probeTools();
    const bin = path.join(directory, 'real-git-bin');
    mkdirSync(bin);
    timeoutShim(bin);
    tools.path = [bin, tools.path].join(path.delimiter);
    for (const name of poisoned) process.env[name] = name.includes('PROXY') || name.includes('proxy')
      ? 'http://127.0.0.1:9' : 'poison';
    process.env.GIT_CONFIG_COUNT = '1';
    process.env.GIT_CONFIG_KEY_0 = 'http.proxy';
    process.env.GIT_CONFIG_VALUE_0 = 'http://127.0.0.1:9';
    for (const response of ['403 Forbidden', '407 Proxy Authentication Required', '200 Connection established']) {
      const requests = [];
      const server = createServer(socket => {
        let request = '';
        let responded = false;
        socket.on('data', chunk => {
          if (responded) return;
          request += chunk.toString('latin1');
          if (!request.includes('\r\n\r\n')) return;
          responded = true;
          requests.push(request.split('\r\n')[0]);
          socket.end(`HTTP/1.1 ${response}\r\nContent-Length: 0\r\n\r\n`);
        });
      });
      server.listen(0, '127.0.0.1');
      await once(server, 'listening');
      try {
        const proxy = `http://127.0.0.1:${server.address().port}`;
        const env = probeEnvironment(directory, tools, {
          HTTPS_PROXY: proxy, https_proxy: proxy, GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: '/dev/null',
        });
        assert.equal(env.HTTPS_PROXY, proxy);
        assert.equal(env.https_proxy, proxy);
        for (const name of poisoned.filter(name => !['HTTPS_PROXY', 'https_proxy'].includes(name))) {
          assert.equal(Object.hasOwn(env, name), false, name);
        }
        assert.deepEqual(Object.keys(env).filter(name => name.startsWith('GIT_')).sort(),
          ['GIT_CEILING_DIRECTORIES', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM']);
        const git = spawnSync('git', ['rev-parse', '--is-inside-work-tree'], {
          cwd: directory, env, encoding: 'utf8',
        });
        assert.notEqual(git.status, 0, 'Git must not discover the parent worktree');

        const result = await supervisedShell(tools, directory, env, script,
          ['https://byok.invalid/openai/v1/models']);
        assert.equal(result.timedOut, false);
        assert.equal(result.code, 1, result.stderr);
        assert.equal(result.stdout, 'SMOKE_FAIL byok:byok.invalid\n');
        assert.equal(result.stderr, '');
        assert.deepEqual(requests, ['CONNECT byok.invalid:443 HTTP/1.1']);
      } finally {
        await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
      }
    }
  } finally {
    for (const [name, value] of saved) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    rmSync(directory, { recursive: true, force: true });
  }
});

test('BYOK supervision closes a stuck descendant without affecting another process', {
  skip: process.platform === 'win32' ? 'Unix process groups are required' : false,
}, async () => {
  const directory = scratch();
  const tools = probeTools();
  const other = spawn('sleep', ['60'], { stdio: 'ignore' });
  const otherClosed = once(other, 'close');
  try {
    assert.equal(processGroup(other.pid), processGroup(process.pid));
    const started = Date.now();
    const result = await supervisedShell(tools, directory, probeEnvironment(directory, tools),
      'sleep 60 &\nprintf "STUCK %s\\n" "$!"\nexit 0\n', [], 1000);
    assert.equal(result.timedOut, true);
    assert.equal(result.code, 0);
    assert.equal(result.signal, null);
    assert.ok(Date.now() - started < 10000, 'Stuck descendant did not close in time');
    const match = /^STUCK (\d+)\n$/.exec(result.stdout);
    assert.ok(match, result.stdout);
    const pid = Number(match[1]);
    await waitForTermination(pid);
    assert.throws(() => assertTerminated(other.pid), /still running/);
    process.kill(other.pid, 0);
  } finally {
    other.kill('SIGKILL');
    await otherClosed;
    rmSync(directory, { recursive: true, force: true });
  }
});

test('BYOK supervision cleans a detached-stream descendant on normal exit', {
  skip: process.platform === 'win32' ? 'Unix process groups are required' : false,
}, async () => {
  const directory = scratch();
  const tools = probeTools();
  const other = spawn('sleep', ['60'], { stdio: 'ignore' });
  const otherClosed = once(other, 'close');
  try {
    assert.equal(processGroup(other.pid), processGroup(process.pid));
    const result = await supervisedShell(tools, directory, probeEnvironment(directory, tools),
      'sleep 60 </dev/null >/dev/null 2>&1 3>&- &\nprintf "DETACHED %s\\n" "$!"\nexit 0\n');
    assert.equal(result.timedOut, false);
    assert.equal(result.code, 0);
    const match = /^DETACHED (\d+)\n$/.exec(result.stdout);
    assert.ok(match, result.stdout);
    await waitForTermination(Number(match[1]));
    assert.throws(() => assertTerminated(other.pid), /still running/);
    process.kill(other.pid, 0);
  } finally {
    other.kill('SIGKILL');
    await otherClosed;
    rmSync(directory, { recursive: true, force: true });
  }
});

function zombieFixture(stalled = false) {
  const parent = spawn(process.execPath, ['-e', `
const { spawn } = require('node:child_process');
const { readSync, writeSync } = require('node:fs');
// The child's pipe closes if this parent is killed, so it exits without a signal.
const child = spawn(process.execPath, ['-e', ${JSON.stringify(stalled
    ? "require('node:fs').readSync(0, Buffer.alloc(1))" : '')}], { stdio: ['pipe', 'ignore', 'ignore'] });
child.on('error', () => { process.exitCode = 1; });
child.on('close', (code, signal) => {
  if (code !== 0 || signal !== null) process.exitCode = 1;
  writeSync(1, 'REAPED\\n');
});
writeSync(1, String(child.pid) + '\\n');
readSync(0, Buffer.alloc(1));
${stalled ? 'Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);' : ''}
`], { stdio: ['pipe', 'pipe', 'ignore'] });
  return { parent, closed: once(parent, 'close') };
}

function fixtureProcess(pid) {
  try {
    const stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
    const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
    assert.match(fields[0], /^[A-Za-z]$/);
    assert.match(fields[1], /^\d+$/);
    assert.match(fields[19], /^\d+$/);
    return { state: fields[0], parent: Number(fields[1]), started: fields[19] };
  } catch (error) {
    if (error.code === 'ENOENT' || error.code === 'ESRCH') return null;
    throw error;
  }
}

async function bounded(promise, milliseconds, message) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), milliseconds); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function finishZombieFixture(parent, closed, child, milliseconds = 10000) {
  parent.stdin.end('\n');
  try {
    const [code, signal] = await bounded(closed, milliseconds, 'Zombie fixture did not reap its child');
    assert.equal(code, 0);
    assert.equal(signal, null);
  } catch (error) {
    try {
      const state = fixtureProcess(parent.pid);
      if (state && state.state !== 'Z' && parent.exitCode === null && parent.signalCode === null) {
        assert.equal(state.parent, process.pid, 'Zombie fixture parent is no longer owned');
        if (child) {
          const current = fixtureProcess(child.pid);
          if (current && current.started === child.started && current.state !== 'Z') {
            assert.equal(current.parent, parent.pid, 'Zombie fixture child is no longer owned');
          }
        }
        assert.equal(parent.kill('SIGKILL'), true);
      }
      await bounded(closed, 3000, 'Zombie fixture parent did not close after cleanup');
      if (child) {
        for (let attempt = 0; ; attempt++) {
          const current = fixtureProcess(child.pid);
          if (!current || current.started !== child.started || current.state === 'Z') break;
          assert.ok(attempt < 299, 'Zombie fixture child did not exit after cleanup');
          await new Promise(resolve => setTimeout(resolve, 10));
        }
      }
    } catch (cleanupError) {
      parent.stdin.destroy();
      parent.stdout.destroy();
      parent.unref();
      throw new AggregateError([error, cleanupError], error.message);
    }
    throw error;
  }
}

test('BYOK termination check accepts an owned zombie but rejects a live process', {
  skip: process.platform !== 'linux' ? 'Zombie state is checked through Linux /proc' : false,
}, async () => {
  const { parent, closed } = zombieFixture();
  let output = '';
  parent.stdout.on('data', data => { output += data; });
  let pid;
  let child;
  let failure;
  try {
    assert.throws(() => assertTerminated(parent.pid), /still running/);
    const [data] = await Promise.race([
      once(parent.stdout, 'data', { signal: AbortSignal.timeout(10000) }),
      closed.then(() => { throw new Error('Zombie fixture exited before reporting its child'); }),
    ]);
    pid = Number(data.toString().trim());
    assert.ok(Number.isInteger(pid) && pid > 0);
    const state = fixtureProcess(pid);
    assert.ok(state);
    assert.equal(state.parent, parent.pid);
    child = { pid, ...state };
    let status;
    for (let attempt = 0; attempt < 1000; attempt++) {
      status = readFileSync(`/proc/${pid}/status`, 'utf8');
      if (/^State:\s+Z\b/m.test(status)) break;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.match(status, /^State:\s+Z\b/m);
    assert.match(status, new RegExp(`^PPid:\\s+${parent.pid}$`, 'm'));
    process.kill(pid, 0);
    assertTerminated(pid);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    try {
      await finishZombieFixture(parent, closed, child);
    } catch (error) {
      if (failure) throw new AggregateError([failure, error], failure.message);
      throw error;
    }
  }
  assert.equal(output, `${pid}\nREAPED\n`);
});

test('BYOK zombie fixture timeout cleans its owned processes without affecting another process', {
  skip: process.platform !== 'linux' ? 'Zombie state is checked through Linux /proc' : false,
}, async () => {
  const other = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
  const otherClosed = once(other, 'close');
  const { parent, closed } = zombieFixture(true);
  let child;
  let finishStarted = false;
  try {
    const [data] = await bounded(once(parent.stdout, 'data'), 10000, 'Zombie fixture did not report its child');
    const pid = Number(data.toString().trim());
    assert.ok(Number.isInteger(pid) && pid > 0);
    const state = fixtureProcess(pid);
    assert.ok(state);
    assert.equal(state.parent, parent.pid);
    assert.notEqual(state.state, 'Z');
    child = { pid, ...state };
    finishStarted = true;
    await assert.rejects(finishZombieFixture(parent, closed, child, 100), error => {
      assert.equal(error.constructor, Error);
      assert.equal(error.message, 'Zombie fixture did not reap its child');
      return true;
    });
    assert.equal(fixtureProcess(parent.pid), null);
    const current = fixtureProcess(child.pid);
    assert.ok(!current || current.started !== child.started || current.state === 'Z');
    const independent = fixtureProcess(other.pid);
    assert.ok(independent);
    assert.match(independent.state, /^[RSDITt]$/);
    assert.equal(independent.parent, process.pid);
    process.kill(other.pid, 0);
  } finally {
    try {
      if (!finishStarted) {
        await finishZombieFixture(parent, closed, child, 100).catch(error => {
          if (error.constructor !== Error || error.message !== 'Zombie fixture did not reap its child') throw error;
        });
      }
    } finally {
      const state = fixtureProcess(other.pid);
      if (state && state.state !== 'Z' && other.exitCode === null && other.signalCode === null) {
        assert.equal(state.parent, process.pid, 'Independent process is no longer owned');
        assert.equal(other.kill('SIGKILL'), true);
      }
      await bounded(otherClosed, 3000, 'Independent process did not close');
    }
  }
});

test('BYOK changes select the Linux release test job', () => {
  for (const file of ['scripts/release/deploy-kustomize.mjs', 'scripts/release/tests/simple-workflows.test.mjs']) {
    assert.equal(classify([file]).node, true, file);
  }
  const workflow = readYaml('.github/workflows/v1-ci.yml');
  assert.equal(workflow.on.pull_request.paths, undefined);
  assert.ok(workflow.on.pull_request.branches.includes('v1'));
  const job = workflow.jobs.node;
  assert.equal(job['runs-on'], 'ubuntu-latest');
  assert.equal(job.if, "${{ !cancelled() && needs.changes.result == 'success' }}");
  const step = job.steps.find(item => item.name === 'Test release and Azure tooling without coverage');
  assert.equal(step.if, "${{ !cancelled() && env.COLLECT_COVERAGE != 'true' && needs.changes.outputs.node == 'true' }}");
  assert.match(step.run, /node --test scripts\/release\/tests\/\*\.test\.mjs/);
  assert.ok(workflow.jobs.foundation.needs.includes('node'));
  assert.match(workflow.jobs.foundation.steps[0].run, /\bnode: true\b/);
});

test('v1 image workflow builds the ten existing Kustomize images from exact source commits', () => {
  const workflow = readYaml('.github/workflows/v1-integration-images.yml');
  const entries = workflow.jobs['build-and-push'].strategy.matrix.include;
  const job = workflow.jobs['build-and-push'];
  const overlay = readYaml('deploy/k8s/kustomization.yaml');

  assert.deepEqual(workflow.on.push.branches, ['v1']);
  assert.deepEqual(workflow.on.workflow_dispatch.inputs.source_sha, {
    description: 'Full commit SHA to build',
    required: true,
    type: 'string',
  });
  assert.equal(job.if, "github.event_name != 'workflow_dispatch' || github.ref == 'refs/heads/v1'");
  for (const input of ['.github/workflows/v1-integration-images.yml', 'Directory.Build.*',
    '**/packages.linux-x64.lock.json', 'releases/foundation.json'])
    assert.ok(workflow.on.push.paths.includes(input), input);
  assert.equal(entries.length, 10);
  assert.deepEqual(
    entries.map(({ repository }) => repository).sort(),
    overlay.images.map(({ name }) => name.replace('ghcr.io/sabbour/', '')).sort(),
  );
  assert.equal(new Set(entries.map(({ id }) => id)).size, 10);

  for (const entry of entries) {
    if (entry.mode === 'dotnet' || entry.mode === 'web')
      assert.equal(existsSync(path.join(root, entry.project)), true, `${entry.id} project exists`);
    if (entry.mode === 'docker')
      assert.equal(existsSync(path.join(root, entry.dockerfile)), true, `${entry.id} Dockerfile exists`);
  }

  const steps = job.steps;
  const checkout = steps.find(({ uses }) => uses === 'actions/checkout@v4');
  assert.deepEqual(checkout.with, {
    ref: '${{ inputs.source_sha || github.sha }}',
    'fetch-depth': 0,
  });
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6').length, 5);
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6')
    .every(({ with: options }) => options.provenance === false), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('PublishContainer')), true);
  const digest = steps.find(({ name }) => name === 'Report pushed image digest');
  assert.equal(digest.env.IMAGE, 'ghcr.io/sabbour/${{ matrix.repository }}:sha-${{ steps.source.outputs.sha }}');
  assert.equal(digest.run.includes('imagetools inspect "$IMAGE"'), true);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);

  // Every image must carry the exact source commit, Git tree, and manifest component version.
  const identity = steps.findIndex(({ id }) => id === 'source');
  assert.ok(identity >= 0 && identity < steps.findIndex(({ uses = '' }) => uses === 'docker/build-push-action@v6'));
  assert.equal(steps[identity].env.COMPONENT_ID, '${{ matrix.id }}');
  assert.equal(steps[identity].env.REQUESTED_SOURCE_SHA, '${{ inputs.source_sha }}');
  assert.match(steps[identity].run, /git rev-parse --verify 'HEAD\^\{commit\}'/);
  assert.match(steps[identity].run, /\[\[ "\$GITHUB_EVENT_NAME" == "workflow_dispatch" \]\]/);
  assert.match(steps[identity].run, /\[\[ ! "\$REQUESTED_SOURCE_SHA" =~ \^\[0-9a-f\]\{40\}\$ \]\]/);
  assert.match(steps[identity].run, /git fetch --no-tags origin v1/);
  assert.match(steps[identity].run, /git merge-base --is-ancestor "\$source_sha" FETCH_HEAD/);
  assert.match(steps[identity].run, /git rev-parse --verify "\$\{source_sha\}\^\{tree\}"/);
  assert.match(steps[identity].run, /select\(\.id == \$id\) \| \.version\] \| if length == 1/);
  assert.match(steps[identity].run, /releases\/foundation\.json/);
  for (const output of [
    'echo "sha=${source_sha}"',
    'echo "tree=${tree}"',
    'echo "version=${version}"',
  ]) assert.ok(steps[identity].run.includes(output), output);
  assert.match(steps[identity].run, /\} >> "\$GITHUB_OUTPUT"/);
  const manifest = JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
  for (const { id } of entries)
    assert.equal(manifest.components.filter(component => component.id === id).length, 1, `${id} manifest version`);

  const buildProps = readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  for (const label of [
    '<ContainerLabel Include="org.opencontainers.image.revision" Value="$(AgentweaverImageSourceSha)" />',
    '<ContainerLabel Include="io.agentweaver.source-tree" Value="$(AgentweaverImageSourceTree)" />',
    '<ContainerLabel Include="org.opencontainers.image.version" Value="$(AgentweaverImageVersion)" />',
  ]) assert.ok(buildProps.includes(label), label);

  const labels = [
    'org.opencontainers.image.revision=${{ steps.source.outputs.sha }}',
    'io.agentweaver.source-tree=${{ steps.source.outputs.tree }}',
    'org.opencontainers.image.version=${{ steps.source.outputs.version }}',
  ];
  const dockerSteps = steps.filter(({ uses }) => uses === 'docker/build-push-action@v6');
  assert.deepEqual(dockerSteps.map(({ if: condition }) => condition).sort(), [
    "matrix.id == 'Agentweaver.AgentHost'", "matrix.id == 'Agentweaver.Identity.Broker'",
    "matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'docker'",
    "matrix.id == 'Agentweaver.Projects.Config' && steps.projects-config-mode.outputs.mode == 'docker'",
    "matrix.mode == 'web'",
  ]);
  for (const { with: options } of dockerSteps) {
    assert.match(options.tags, /:sha-\$\{\{ steps\.source\.outputs\.sha \}\}$/);
    for (const label of labels) assert.ok(options.labels.split('\n').includes(label), label);
  }
  const webBuild = dockerSteps.find(({ if: condition }) => condition === "matrix.mode == 'web'");
  assert.ok(webBuild.with['build-args'].includes('GIT_SHA=${{ steps.source.outputs.sha }}'));
  const publish = steps.find(({ run = '' }) => run.includes('PublishContainer'));
  assert.equal(publish.if,
    "matrix.mode == 'dotnet' && (matrix.id != 'Agentweaver.Projects.Config' || steps.projects-config-mode.outputs.mode == 'dotnet')");
  assert.ok(publish.run.includes('-p:ContainerImageTag=sha-${{ steps.source.outputs.sha }}'));
  for (const property of [
    '-p:AgentweaverImageSourceSha=${{ steps.source.outputs.sha }}',
    '-p:AgentweaverImageSourceTree=${{ steps.source.outputs.tree }}',
    '-p:AgentweaverImageVersion=${{ steps.source.outputs.version }}',
  ]) assert.ok(publish.run.includes(property), property);

  const orchestratorMode = steps.find(({ id }) => id === 'orchestrator-mode');
  assert.equal(orchestratorMode.if, "matrix.id == 'Agentweaver.Orchestrator'");
  assert.match(orchestratorMode.run, /-f "\$dockerfile" && -s "\$package"/);
  assert.match(orchestratorMode.run, /! -f "\$dockerfile" && ! -s "\$package"/);
  assert.match(orchestratorMode.run, /Orchestrator Dockerfile and pinned ACS package must both exist/);
  const legacyRestore = steps.find(({ name }) => name === 'Restore legacy Orchestrator');
  assert.equal(legacyRestore.if,
    "matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'dotnet'");
  const legacyPublish = steps.find(({ name }) => name === 'Build and push legacy Orchestrator');
  assert.equal(legacyPublish.if,
    "matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'dotnet'");
  assert.ok(legacyPublish.run.includes('-p:ContainerTargetOs=linux'));
  assert.ok(legacyPublish.run.includes('-p:ContainerTargetArchitecture=x64'));
  assert.ok(legacyPublish.run.includes('-p:AgentweaverImageSourceSha=${{ steps.source.outputs.sha }}'));
  const acsOpa = steps.find(({ name }) => name === 'Download and verify pinned OPA');
  assert.equal(acsOpa.if,
    "matrix.id == 'Agentweaver.AgentHost' || (matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'docker') || (matrix.id == 'Agentweaver.Projects.Config' && steps.projects-config-mode.outputs.mode == 'docker')");
  const orchestratorDocker = steps.find(({ name }) => name === 'Build and push Orchestrator');
  assert.equal(orchestratorDocker.if,
    "matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'docker'");
  const projectsConfigMode = steps.find(({ id }) => id === 'projects-config-mode');
  assert.equal(projectsConfigMode.if, "matrix.id == 'Agentweaver.Projects.Config'");
  assert.match(projectsConfigMode.run, /-f "\$dockerfile" && -s "\$package"/);
  assert.match(projectsConfigMode.run, /Projects Config Dockerfile requires the pinned ACS package/);
  const projectsConfigDocker = steps.find(({ name }) => name === 'Build and push Projects Config');
  assert.equal(projectsConfigDocker.with.file, 'services/projects-config/Agentweaver.Projects.Config/Dockerfile');
});

test('v1 package workflow publishes versioned NuGet packages without image receipts', () => {
  const workflow = readYaml('.github/workflows/v1-release-pack.yml');
  const steps = workflow.jobs['publish-packages'].steps;

  assert.deepEqual(workflow.on.push.branches, ['v1']);
  assert.deepEqual(workflow.on.push.paths, ['releases/foundation.json']);
  assert.equal(steps.some(({ run = '' }) => run.includes('dotnet pack')), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('dotnet nuget push')), true);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);
  assert.equal(JSON.stringify(workflow).includes('publication'), false);
  assert.equal(JSON.stringify(workflow).includes('source_sha'), false);
});

test('v1 workload deployment is operator-run rather than a hosted AKS workflow', () => {
  assert.equal(existsSync(path.join(root, '.github/workflows/v1-deploy.yml')), false);
  const runbook = readFileSync(path.join(root, 'releases/README.md'), 'utf8');
  assert.match(runbook, /az login/);
  assert.match(runbook, /npm run deploy:kustomize -- --image-manifest/);
  assert.doesNotMatch(runbook, /gh workflow run .*v1-deploy/);
});

test('Broker preflight accepts an exact order-independent match and omits the immutable map from apply', async () => {
  const directory = scratch();
  const calls = [];
  const liveBrokerRuntimeConfig = structuredClone(brokerRuntimeConfig);
  liveBrokerRuntimeConfig.data = Object.fromEntries(Object.entries(liveBrokerRuntimeConfig.data).reverse());
  try {
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, { liveBrokerRuntimeConfig }), log() {},
    });

    const brokerRead = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'configmap' && call.args[2] === brokerRuntimeConfigName);
    const firstWrite = calls.findIndex(call => kubernetesWriteCalls([call]).length > 0);
    const apply = calls.find(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const appliedResources = parse(apply.options.input).items;
    assert.ok(brokerRead >= 0 && brokerRead < firstWrite);
    assert.equal(appliedResources.some(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === brokerRuntimeConfigName), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['create', 'delete'].includes(call.args[0]) &&
      (call.args[1] === 'configmap' && call.args[2] === brokerRuntimeConfigName ||
        call.args[0] === 'create' && parse(call.options.input).kind === 'ConfigMap' &&
          parse(call.options.input).metadata.name === brokerRuntimeConfigName)), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Kustomize release preserves a validated active provider successor with a resource-version fence', async () => {
  const directory = scratch();
  const calls = [];
  const plan = buildIdentityBrokerProviderPlan(providerSetupInput, root);
  try {
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, { liveProviderSetup: plan }), log() {},
    });

    const patchCall = calls.find(call => call.command === 'kubectl' &&
      call.args[0] === 'patch' && call.args[1] === 'deployment/identity-broker');
    const patch = JSON.parse(patchCall.args[patchCall.args.indexOf('--patch') + 1]);
    const spec = patch.find(operation => operation.path === '/spec').value;
    const container = spec.template.spec.containers
      .find(item => item.name === 'identity-broker');
    const secretEnv = container.env.find(entry =>
      entry.name === 'IdentityBroker__GitHubRepoApp__OAuthClientSecret');
    assert.equal(container.envFrom.find(item => item.configMapRef).configMapRef.name,
      plan.configMap.metadata.name);
    assert.deepEqual(secretEnv.valueFrom.secretKeyRef, {
      name: 'github-repo-app-oauth', key: 'oauth-client-secret', optional: false,
    });
    assert.equal(secretEnv.value, undefined);
    assert.equal(spec.template.metadata.annotations[
      'agentweaver.io/provider-setup-fingerprint'],
    plan.deployment.podTemplateAnnotations['agentweaver.io/provider-setup-fingerprint']);
    const appliedResources = parse(calls.find(call =>
      call.command === 'kubectl' && call.args[0] === 'apply').options.input).items;
    assert.equal(appliedResources.some(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'identity-broker'), false);
    assert.equal(appliedResources.some(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === plan.configMap.metadata.name), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker quiesce refreshes providerSetup resourceVersion after simulated kubectl scale', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const plan = buildIdentityBrokerProviderPlan(providerSetupInput, root);
  const liveImageRefs = Object.fromEntries(imageRepositories.map(repository => [repository, existingTag]));
  liveImageRefs['agentweaver-agenthost'] = existingAgentHostRegistry.childDigest;
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver.identity.broker': selectedTag } }));
  let brokerResourceVersion = '10';
  const baseExec = fakeExec(calls, { liveProviderSetup: plan, liveImageRefs, distinctDigests: true });
  const scalingExec = (command, args, options) => {
    if (command === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'identity-broker') {
      const res = baseExec(command, args, options);
      if (res.status === 0 && res.stdout.trim()) {
        const dep = JSON.parse(res.stdout);
        dep.metadata.resourceVersion = brokerResourceVersion;
        return { ...res, stdout: JSON.stringify(dep) };
      }
      return res;
    }
    if (command === 'kubectl' && args[0] === 'scale' && args[1] === 'deployment/identity-broker') {
      brokerResourceVersion = '11';
      calls.push({ command, args, options });
      return { status: 0, stdout: '', stderr: '' };
    }
    if (command === 'kubectl' && args[0] === 'annotate' && args[1] === 'deployment/identity-broker') {
      calls.push({ command, args, options });
      return { status: 0, stdout: '', stderr: '' };
    }
    if (command === 'kubectl' && args[0] === 'get' && args[1] === 'pods' && args.includes('--selector')) {
      calls.push({ command, args, options });
      return { status: 0, stdout: JSON.stringify({ items: [] }), stderr: '' };
    }
    if (command === 'kubectl' && args[0] === 'patch' && args[1] === 'deployment/identity-broker') {
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      const testedRv = patch.find(op => op.op === 'test' && op.path === '/metadata/resourceVersion')?.value;
      if (testedRv !== brokerResourceVersion) {
        return { status: 1, stdout: '', stderr: `Conflict: expected ${brokerResourceVersion}, got ${testedRv}` };
      }
      calls.push({ command, args, options });
      return { status: 0, stdout: '', stderr: '' };
    }
    return baseExec(command, args, options);
  };
  try {
    await deployKustomize({ imageManifest: manifestPath }, {
      root: directory, exec: scalingExec, sleep: async () => {}, log() {},
    });
    const patchCall = calls.find(call => call.command === 'kubectl' &&
      call.args[0] === 'patch' && call.args[1] === 'deployment/identity-broker');
    const patch = JSON.parse(patchCall.args[patchCall.args.indexOf('--patch') + 1]);
    assert.equal(patch.find(op => op.path === '/metadata/resourceVersion').value, '11');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('provider activation during release migrations blocks the stale Broker release write', async () => {
  const directory = scratch();
  const calls = [];
  const planA = buildIdentityBrokerProviderPlan(providerSetupInput, root);
  const inputB = structuredClone(providerSetupInput);
  inputB.IdentityBroker.CopilotConnection.ClientId = 'activated-during-release';
  const planB = buildIdentityBrokerProviderPlan(inputB, root);
  const state = { runtimeConfig: structuredClone(runtimeConfig), deployments: {} };
  try {
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root,
      exec: fakeExec(calls, {
        liveProviderSetup: planA,
        activateProviderDuringMigration: planB,
        state,
      }),
      log() {},
    }), /Identity Broker Deployment update failed: Conflict: UID or resourceVersion precondition failed/);

    const deployment = state.deployments['identity-broker'];
    const container = deployment.spec.template.spec.containers
      .find(item => item.name === 'identity-broker');
    assert.equal(container.envFrom.find(item => item.configMapRef).configMapRef.name,
      planB.configMap.metadata.name);
    assert.equal(container.env.find(entry =>
      entry.name === 'IdentityBroker__CopilotConnection__ClientId').value,
    'activated-during-release');
    const patchCall = calls.find(call => call.command === 'kubectl' &&
      call.args[0] === 'patch' && call.args[1] === 'deployment/identity-broker');
    assert.ok(patchCall);
    assert.equal(parse(calls.find(call => call.command === 'kubectl' &&
      call.args[0] === 'apply').options.input).items.some(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'identity-broker'), false);
    assert.ok(state.configMaps[planA.configMap.metadata.name]);
    assert.ok(state.configMaps[planB.configMap.metadata.name]);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker preflight rejects any data drift before Kubernetes writes without exposing values', async () => {
  const cases = [
    ['leaf pin', config => { config.data.IdentityBroker__RunBinding__ProjectsServerCertificateSha256 = 'private-pin-value'; },
      'data[IdentityBroker__RunBinding__ProjectsServerCertificateSha256]'],
    ['unrelated value', config => { config.data.IdentityBroker__Issuer = 'private-issuer-value'; },
      'data[IdentityBroker__Issuer]'],
    ['extra key', config => { config.data.extra = 'private-extra-value'; }, 'data[extra]'],
    ['missing key', config => { delete config.data.IdentityBroker__Issuer; }, 'data[IdentityBroker__Issuer]'],
    ['non-string value', config => { config.data.IdentityBroker__Issuer = { value: 'private-object-value' }; },
      'data[IdentityBroker__Issuer]'],
    ['binaryData', config => { config.binaryData = { 'private.bin': 'private-binary-value' }; },
      'binaryData[private.bin]'],
  ];
  for (const [name, change, key] of cases) {
    const directory = scratch();
    const calls = [];
    const logs = [];
    const liveBrokerRuntimeConfig = structuredClone(brokerRuntimeConfig);
    change(liveBrokerRuntimeConfig);
    const kustomizationPath = path.join(directory, 'deploy', 'k8s', 'kustomization.yaml');
    const originalKustomization = readFileSync(kustomizationPath, 'utf8');
    try {
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, { liveBrokerRuntimeConfig }),
        log: message => logs.push(message),
      }), error => {
        assert.match(error.message, /differs from the rendered candidate/);
        assert.ok(error.message.includes(key), error.message);
        assert.doesNotMatch(error.message, /private-(?:pin|issuer|extra|binary|object)-value/);
        return true;
      }, name);
      assert.deepEqual(kubernetesWriteCalls(calls), [], name);
      assert.equal(readFileSync(kustomizationPath, 'utf8'), originalKustomization, name);
      assert.equal(logs.some(message => /private-(?:pin|issuer|extra|binary|object)-value/.test(message)), false, name);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  }
});

test('Kustomize accepts the exact rendered Broker ConfigMap before its first write', async () => {
  const directory = scratch();
  const calls = [];
  try {
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls), log() {},
    });
    const sourceMap = JSON.parse(readFileSync(path.join(directory, 'deploy', 'k8s',
      'runtime-configs', 'identity-broker-runtime-config.json'), 'utf8'));
    assert.deepEqual(brokerRuntimeConfig.data, sourceMap.data);
    const preflight = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'get' && call.args[1] === 'configmap' && call.args[2] === brokerRuntimeConfigName);
    const firstWrite = calls.findIndex(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['create', 'delete', 'apply', 'scale', 'annotate', 'patch', 'replace'].includes(call.args[0]));
    assert.ok(preflight >= 0);
    assert.ok(firstWrite > preflight);
    assert.equal(kubernetesWriteCalls(calls).some(call =>
      call.args[0] === 'create' && call.args.includes(`configmap/${brokerRuntimeConfigName}`)), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker preflight fails closed for unreadable, malformed, missing, or invalid resources', async () => {
  const cases = [
    ['missing live map', { missingBrokerRuntimeConfig: true }, /Live ConfigMap\/identity-broker-runtime-config-v4 is missing/],
    ['read failure', { brokerConfigMapReadFailure: true }, /Could not read ConfigMap\/identity-broker-runtime-config-v4/],
    ['malformed JSON', { brokerConfigMapReadOutput: 'private malformed response' }, /invalid JSON/],
    ['wrong identity', {
      liveBrokerRuntimeConfig: {
        ...structuredClone(brokerRuntimeConfig),
        kind: 'Secret',
      },
    }, /kind/],
    ['wrong API version', {
      liveBrokerRuntimeConfig: { ...structuredClone(brokerRuntimeConfig), apiVersion: 'v2' },
    }, /apiVersion/],
    ['wrong map name', {
      liveBrokerRuntimeConfig: {
        ...structuredClone(brokerRuntimeConfig),
        metadata: { ...brokerRuntimeConfig.metadata, name: 'other-config' },
      },
    }, /metadata\.name/],
    ['wrong namespace', {
      liveBrokerRuntimeConfig: {
        ...structuredClone(brokerRuntimeConfig),
        metadata: { ...brokerRuntimeConfig.metadata, namespace: 'other-namespace' },
      },
    }, /metadata\.namespace/],
    ['mutable map', {
      liveBrokerRuntimeConfig: { ...structuredClone(brokerRuntimeConfig), immutable: false },
    }, /immutable/],
    ['malformed data map', {
      liveBrokerRuntimeConfig: { ...structuredClone(brokerRuntimeConfig), data: [] },
    }, /data/],
    ['malformed binaryData map', {
      liveBrokerRuntimeConfig: { ...structuredClone(brokerRuntimeConfig), binaryData: 'private-binary-value' },
    }, /binaryData/],
    ['missing rendered map', { brokerConfigMapCount: 0 }, /exactly one ConfigMap/],
    ['duplicate rendered map', { brokerConfigMapCount: 2 }, /exactly one ConfigMap/],
    ['wrong Deployment reference', { brokerConfigMapReference: 'other-config' }, /deployment\/identity-broker/],
  ];
  for (const [name, options, expected] of cases) {
    const directory = scratch();
    const calls = [];
    const kustomizationPath = path.join(directory, 'deploy', 'k8s', 'kustomization.yaml');
    const originalKustomization = readFileSync(kustomizationPath, 'utf8');
    try {
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, options), log() {},
      }), error => {
        assert.match(error.message, expected, name);
        assert.doesNotMatch(error.message, /private-response-value|private-error-value|private malformed response/, name);
        return true;
      });
      assert.deepEqual(kubernetesWriteCalls(calls), [], name);
      assert.equal(readFileSync(kustomizationPath, 'utf8'), originalKustomization, name);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  }
});

test('active installer stamps the selected AgentHost platform digest and measured bytes into both resources', async () => {
  const directory = scratch();
  const facts = [];
  try {
    for (const [imageTag, fixture] of [
      [`sha-${'d'.repeat(40)}`, agentHostRegistryFixture(1024)],
      [`sha-${'e'.repeat(40)}`, agentHostRegistryFixture(2048)],
    ]) {
      const calls = [];
      await deployKustomize({ imageTag }, {
        root: directory,
        exec: fakeExec(calls, { selectedAgentHostImage: fixture }),
        log() {},
      });

      const applied = parse(calls.find(call =>
        call.command === 'kubectl' && call.args[0] === 'apply').options.input);
      const environment = applied.items.find(resource =>
        resource.kind === 'Deployment' && resource.metadata.name === 'environment');
      const envSettings = Object.fromEntries(environment.spec.template.spec.containers
        .find(container => container.name === 'environment').env.map(({ name, value }) => [name, value]));
      const agentHostConfig = applied.items.find(resource =>
        resource.kind === 'ConfigMap' && resource.metadata.name === 'agenthost-runtime-v1');
      const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
      const tuple = {
        Digest: fixture.childDigest,
        Platform: 'linux/amd64',
        CompressedPullBytes: fixture.childBytes.length + fixture.configBytes.length + fixture.layerSize,
      };

      assert.deepEqual(agentHostImage, tuple);
      assert.equal(envSettings.Environment__Sandbox__AgentSandbox__ContainerImage,
        `${REGISTRY}/agentweaver-agenthost@${tuple.Digest}`);
      assert.equal(envSettings.Environment__Sandbox__AgentSandbox__ContainerImagePlatform, tuple.Platform);
      assert.equal(Number(envSettings.Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes),
        tuple.CompressedPullBytes);
      assert.notEqual(tuple.CompressedPullBytes, 322214685);
      assert.deepEqual(applied.items.find(resource => resource.kind === 'CiliumNetworkPolicy' &&
        resource.metadata.name === 'agenthost-model-egress')?.spec.egress[1],
      { toFQDNs: copilotModelHosts.map(matchName => ({ matchName })),
        toPorts: [{ ports: [{ port: '443', protocol: 'TCP' }] }] });
      facts.push({ digest: tuple.Digest, bytes: tuple.CompressedPullBytes });
    }
    assert.notEqual(facts[0].digest, facts[1].digest);
    assert.notEqual(facts[0].bytes, facts[1].bytes);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration creates target dependencies before running its Job', async () => {
  const directory = scratch();
  const calls = [];
  const logs = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    const targetPairs = targetValuePairs(directory, 'awwus2np01');
    const targetClientId = targetPairs.reduce(
      (value, [from, to]) => value === from ? to : value,
      brokerMigrationClientId,
    );
    const targetBrokerRuntimeConfig = parse(applyTargetValues(
      JSON.stringify(brokerRuntimeConfig), targetPairs,
    ));
    await deployKustomize({
      imageManifest: 'images.json',
      resourceGroup: 'awwus2np01',
    }, {
      root: directory,
      exec: fakeExec(calls, {
        liveBrokerRuntimeConfig: targetBrokerRuntimeConfig,
        selectedContextServer: 'https://awwus2np01-aks-88n4s6n7.hcp.westus2.azmk8s.io:443',
      }),
      log: message => logs.push(message),
      sleep: async () => {},
    });

    const created = createdBrokerMigrationResources(calls);
    assert.deepEqual(created.map(resource => `${resource.kind}/${resource.metadata.name}`), [
      'ServiceAccount/identity-broker-migration',
      'CiliumNetworkPolicy/identity-broker-migration-egress',
      'ConfigMap/identity-broker-migration-config',
    ]);
    const brokerJob = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job');
    assert.ok(created.every(resource => calls.findIndex(call =>
      call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).metadata?.name === resource.metadata.name) < brokerJob));

    const [serviceAccount, egress, configMap] = created;
    assert.equal(serviceAccount.metadata.annotations['azure.workload.identity/client-id'], targetClientId);
    assert.equal(serviceAccount.metadata.annotations['azure.workload.identity/tenant-id'], brokerMigrationTenantId);
    assert.equal(configMap.data.ConnectionStrings__IdentityBrokerMigration,
      'Host=awwus2np01-pg.postgres.database.azure.com;Database=agentweaver;' +
      'Username=awwus2np01-id-identity-broker-migration;SSL Mode=VerifyFull');
    assert.equal(configMap.data.IdentityBroker__Migration__WorkloadIdentityClientId, targetClientId);
    assert.equal(configMap.data.IdentityBroker__Migration__WorkloadIdentityTenantId, brokerMigrationTenantId);
    assert.equal(configMap.data.IdentityBroker__Migration__WorkloadIdentityTokenFilePath,
      '/var/run/secrets/azure/tokens/azure-identity-token');
    assert.equal(egress.spec.egress.at(-1).toFQDNs[0].matchName,
      'awwus2np01-pg.postgres.database.azure.com');
    assert.ok(logs.some(message => message.startsWith('Migration Job identity-broker-migration-') &&
      message.endsWith(' completed.')));
    assert.ok(calls.some(call => call.command === 'az' && call.args.includes('awwus2np01-id-identity-broker-migration')));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration accepts matching existing resources without changing them', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    const existing = brokerMigrationSetupResources(directory);
    existing[0].metadata.labels['operator.example/owner'] = 'kept';
    existing[0].secrets = [{ name: 'kubernetes-added-token' }];
    existing[0].metadata.uid = 'existing-sa-uid';
    existing[0].metadata.resourceVersion = '9';
    existing[1].immutable = false;
    existing[1].metadata.managedFields = [{ manager: 'operator' }];
    existing[1].metadata.creationTimestamp = '2026-11-10T00:00:00Z';
    existing[2].metadata.managedFields = [{ manager: 'operator' }];
    existing[2].status = { conditions: [] };
    await deployKustomize({ imageManifest: 'images.json' }, {
      root: directory,
      exec: fakeExec(calls, { brokerMigrationLiveResources: existing }),
      log() {},
    });

    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), true);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration reports an existing resource conflict before creating anything', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    const existing = brokerMigrationSetupResources(directory);
    existing[1].data.ConnectionStrings__IdentityBrokerMigration = 'different-connection';
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationLiveResources: existing }),
        log() {},
      }),
      /Existing ConfigMap\/identity-broker-migration-config conflicts .*data\.ConnectionStrings__IdentityBrokerMigration/,
    );
    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration rejects additional Cilium policy specs', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    const existing = brokerMigrationSetupResources(directory);
    existing[2].specs = [{ egress: [{ toEntities: ['world'] }] }];
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationLiveResources: existing }),
        log() {},
      }),
      /Existing CiliumNetworkPolicy\/identity-broker-migration-egress conflicts .*specs/,
    );
    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration resumes safely after a partial resource creation', async () => {
  const directory = scratch();
  const firstCalls = [];
  const retryCalls = [];
  const state = {
    runtimeConfig: structuredClone(runtimeConfig),
    deployments: {},
    configMaps: {},
    migrationResources: {},
  };
  try {
    writeBrokerOnlyImageManifest(directory);
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(firstCalls, { state, brokerMigrationCreateFailure: 'identity-broker-migration-egress' }),
        log() {},
      }),
      /Could not create CiliumNetworkPolicy\/identity-broker-migration-egress/,
    );
    assert.ok(state.migrationResources['identity-broker-migration']);
    assert.equal(state.migrationResources['identity-broker-migration-egress'], undefined);
    assert.equal(state.configMaps['identity-broker-migration-config'], undefined);
    assert.equal(firstCalls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);

    await deployKustomize({ imageManifest: 'images.json' }, {
      root: directory,
      exec: fakeExec(retryCalls, { state }),
      log() {},
    });
    assert.deepEqual(createdBrokerMigrationResources(retryCalls).map(resource =>
      `${resource.kind}/${resource.metadata.name}`), [
      'CiliumNetworkPolicy/identity-broker-migration-egress',
      'ConfigMap/identity-broker-migration-config',
    ]);
    assert.equal(retryCalls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), true);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration accepts a matching create race and rejects a conflicting race', async t => {
  await t.test('matching resource', async () => {
    const directory = scratch();
    const calls = [];
    try {
      writeBrokerOnlyImageManifest(directory);
      await deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationCreateRace: 'identity-broker-migration' }),
        log() {},
      });
      assert.deepEqual(createdBrokerMigrationResources(calls).map(resource =>
        `${resource.kind}/${resource.metadata.name}`), [
        'ServiceAccount/identity-broker-migration',
        'CiliumNetworkPolicy/identity-broker-migration-egress',
        'ConfigMap/identity-broker-migration-config',
      ]);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  await t.test('conflicting resource', async () => {
    const directory = scratch();
    const calls = [];
    try {
      writeBrokerOnlyImageManifest(directory);
      await assert.rejects(
        deployKustomize({ imageManifest: 'images.json' }, {
          root: directory,
          exec: fakeExec(calls, {
            brokerMigrationCreateRace: 'identity-broker-migration',
            brokerMigrationCreateRaceConflict: true,
          }),
          log() {},
        }),
        /Existing ServiceAccount\/identity-broker-migration conflicts .*metadata\.annotations\.azure\.workload\.identity\/client-id/,
      );
      assert.deepEqual(createdBrokerMigrationResources(calls).map(resource =>
        `${resource.kind}/${resource.metadata.name}`), [
        'ServiceAccount/identity-broker-migration',
      ]);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
        parse(call.options.input).kind === 'Job'), false);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  await t.test('matching resource during server dry-run', async () => {
    const directory = scratch();
    const calls = [];
    try {
      writeBrokerOnlyImageManifest(directory);
      await deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationDryRunRace: 'identity-broker-migration' }),
        log() {},
      });
      assert.deepEqual(createdBrokerMigrationResources(calls).map(resource =>
        `${resource.kind}/${resource.metadata.name}`), [
        'CiliumNetworkPolicy/identity-broker-migration-egress',
        'ConfigMap/identity-broker-migration-config',
      ]);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
        parse(call.options.input).kind === 'Job'), true);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  await t.test('conflicting resource during server dry-run', async () => {
    const directory = scratch();
    const calls = [];
    try {
      writeBrokerOnlyImageManifest(directory);
      await assert.rejects(
        deployKustomize({ imageManifest: 'images.json' }, {
          root: directory,
          exec: fakeExec(calls, {
            brokerMigrationDryRunRace: 'identity-broker-migration',
            brokerMigrationDryRunRaceConflict: true,
          }),
          log() {},
        }),
        /Existing ServiceAccount\/identity-broker-migration conflicts .*metadata\.annotations\.azure\.workload\.identity\/client-id/,
      );
      assert.deepEqual(createdBrokerMigrationResources(calls), []);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
        parse(call.options.input).kind === 'Job'), false);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });
});

test('Broker migration does not treat resource read errors as missing', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationReadFailure: 'identity-broker-migration-egress' }),
        log() {},
      }),
      /Could not read CiliumNetworkPolicy\/identity-broker-migration-egress/,
    );
    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration rejects an Azure identity that differs from target values', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationIdentityMismatch: true }),
        log() {},
      }),
      /rendered Broker migration identity does not match the Azure identity/,
    );
    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker migration rejects an Azure tenant that differs from target values', async () => {
  const directory = scratch();
  const calls = [];
  try {
    writeBrokerOnlyImageManifest(directory);
    await assert.rejects(
      deployKustomize({ imageManifest: 'images.json' }, {
        root: directory,
        exec: fakeExec(calls, { brokerMigrationTenantMismatch: true }),
        log() {},
      }),
      /rendered Broker migration identity does not match the Azure identity/,
    );
    assert.deepEqual(createdBrokerMigrationResources(calls), []);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('deployment creates existing migration Jobs directly before one namespaced apply', async () => {
  const directory = scratch();
  const kustomizationPath = path.join(directory, 'deploy', 'k8s', 'kustomization.yaml');
  const original = readFileSync(kustomizationPath, 'utf8');
  const calls = [];
  const logs = [];
  try {
    const exec = fakeExec(calls);
    await deployKustomize({ imageTag: `sha-${'b'.repeat(40)}` }, {
      root: directory, exec, log: message => logs.push(message),
    });

    const createCalls = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create');
    const createdJobs = createCalls.map(call => parse(call.options.input)).filter(value => value.kind === 'Job');
    assert.deepEqual(createdJobs.map(value => value.metadata.name.split('-').slice(0, -1).join('-')), jobNames);
    assert.deepEqual(readYaml('deploy/k8s/migrations/identity-broker/job.yaml')
      .spec.template.spec.containers[0].command,
    ['dotnet', 'Agentweaver.Identity.Broker.dll', '--migrate']);
    assert.equal(createdJobs.every(value => value.spec.ttlSecondsAfterFinished === 3600), true);
    assert.equal(createdJobs.every(value => value.spec.template.spec.containers[0].image.startsWith(`${REGISTRY}/`)), true);
    assert.equal(createdJobs[1].spec.template.spec.containers[0].env[0].value, serviceClientId);
    const projectsConfigJob = createdJobs[5];
    const projectsConfigSpec = projectsConfigJob.spec.template.spec;
    const projectsConfigEnv = Object.fromEntries(
      projectsConfigSpec.containers[0].env.map(({ name, value }) => [name, value]),
    );
    assert.equal(projectsConfigSpec.serviceAccountName, 'projects-config-migration');
    assert.equal(projectsConfigJob.spec.template.metadata.labels['azure.workload.identity/use'], 'true');
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__ClientId, serviceClientId);
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__TenantId,
      '72f988bf-86f1-41af-91ab-2d7cd011db47');
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__TokenFilePath,
      '/var/run/secrets/azure/tokens/azure-identity-token');
    assert.match(projectsConfigEnv.ConnectionStrings__ProjectsConfigMigration, /SSL Mode=VerifyFull/);
    assert.match(projectsConfigEnv.ConnectionStrings__ProjectsConfigMigration, /Username=agentweaver-id-projects-config-migration/);
    assert.equal(projectsConfigSpec.containers[0].env.some(setting => setting.valueFrom?.secretKeyRef), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      call.args[3] === 'secrets'), false);
    assert.deepEqual(
      calls.filter(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
        call.args[1] === 'can-i' && call.args[3]?.startsWith('deployment/') &&
        !call.args.includes('system:serviceaccount:agentweaver:environment'))
        .map(call => `${call.args[2]}:${call.args[3]}`).sort(),
      serviceNames.flatMap(name => ['get', 'list', 'patch', 'watch']
        .map(verb => `${verb}:deployment/${name}`)).sort(),
    );
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args.includes('--resource-name')), false);
    assert.deepEqual(
      calls.filter(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
        call.args[1] === 'can-i' && call.args.includes('system:serviceaccount:agentweaver:environment'))
        .map(({ args }) => {
          const subresource = args.find(arg => arg.startsWith('--subresource='));
          return `${args[2]} ${args[3]}${subresource ? ` ${subresource}` : ''}` +
            `${args.includes('--namespace') ? '' : ' cluster'}`;
        }).sort(),
      ENVIRONMENT_RBAC_REQUIREMENTS.map(({ verb, resource, resourceName, subresource, cluster }) =>
        `${verb} ${resource}${resourceName ? `/${resourceName}` : ''}` +
        `${subresource ? ` --subresource=${subresource}` : ''}${cluster ? ' cluster' : ''}`).sort(),
    );
    assert.equal(calls.some(call => call.command === 'npm'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), true);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'replace'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'rollout' &&
      call.args[1] === 'restart'), false);

    const render = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'kustomize' && call.args[1].endsWith(path.join('deploy', 'k8s')));
    const firstJob = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job');
    const brokerJobWait = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'wait' &&
      call.args.includes(`job/${createdJobs[0].metadata.name}`));
    const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const brokerPatch = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'patch' && call.args[1] === 'deployment/identity-broker');
    const rollouts = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout');
    assert.ok(render >= 0 && render < firstJob && firstJob < brokerJobWait && brokerJobWait < apply &&
      apply < brokerPatch);
    assert.deepEqual(JSON.parse(calls[brokerPatch].args[
      calls[brokerPatch].args.indexOf('--patch') + 1]).slice(0, 2), [
      { op: 'test', path: '/metadata/uid', value: 'uid-identity-broker' },
      { op: 'test', path: '/metadata/resourceVersion', value: '1' },
    ]);
    assert.equal(apply, calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply'));
    assert.deepEqual(calls[apply].args.slice(1, 5), [
      '--filename', '-', '--context', 'selected-context',
    ]);
    const applied = parse(calls[apply].options.input);
    assert.equal(applied.kind, 'List');
    assert.equal(applied.items.some(resource => resource.kind === 'Namespace'), false);
    assert.equal(applied.items.filter(resource => resource.kind === 'Deployment').length, 8);
    assert.equal(applied.items.some(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'identity-broker'), false);
    assert.equal(applied.items.some(resource =>
      ['Role', 'RoleBinding', 'ClusterRole', 'ClusterRoleBinding'].includes(resource.kind)), false);
    assert.equal(rollouts.length, 9);
    assert.equal(logs.some(message => /receipt|evidence/i.test(message)), false);
    assert.equal(readFileSync(kustomizationPath, 'utf8'), original);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a failed migration Job prevents workload apply and rollout', async () => {
  const directory = scratch();
  const calls = [];
  try {
    const exec = fakeExec(calls, { failedJob: 'orchestrator-migration-' });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'c'.repeat(40)}` }, { root: directory, exec, log() {} }),
      /Migration Job orchestrator-migration-.* failed: job failed/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' &&
      ['apply', 'rollout'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a Projects Config migration ServiceAccount without a client ID prevents every migration Job and workload apply', async () => {
  const directory = scratch();
  const calls = [];
  try {
    const exec = fakeExec(calls, { missingProjectsConfigClientId: true });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'e'.repeat(40)}` }, { root: directory, exec, log() {} }),
      /ServiceAccount\/projects-config-migration does not name the approved migration identity/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['create', 'apply', 'rollout'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a failed Deployment rollout does not report a complete deployment', async () => {
  const directory = scratch();
  const calls = [];
  const logs = [];
  try {
    const exec = fakeExec(calls, { failedRollout: 'projects-config' });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'d'.repeat(40)}` }, {
        root: directory, exec, log: message => logs.push(message),
      }),
      /Deployment projects-config did not become ready: CrashLoopBackOff/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), true);
    assert.equal(logs.filter(message => message.startsWith('Deployment ') && message.endsWith(' is ready.')).length, 8);
    assert.equal(logs.includes('Deployment projects-config is ready.'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('image manifest arguments select exactly one input and require execution', () => {
  assert.deepEqual(parseDeployKustomizeArgs(['--image-manifest', 'images.json', '--execute']),
    { imageManifest: 'images.json' });
  assert.deepEqual(parseDeployKustomizeArgs(['--image-tag', selectedTag, '--execute']),
    { imageTag: selectedTag });
  assert.deepEqual(parseDeployKustomizeArgs([
    '--image-manifest', 'images.json', '--registry', 'agentweaverasabbour.azurecr.io', '--execute',
  ]), { imageManifest: 'images.json', registry: 'agentweaverasabbour.azurecr.io' });
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--registry', 'invalid', '--execute',
  ]), /registry must be/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--registry', 'ghcr.io/sabbour', '--registry',
    'agentweaverasabbour.azurecr.io', '--execute',
  ]), /requires a registry/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-manifest', 'images.json', '--image-tag', selectedTag, '--execute',
  ]), /exactly one/);
  assert.throws(() => parseDeployKustomizeArgs(['--image-manifest', '--execute']), /requires a file path/);
  assert.deepEqual(parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--model-bindings', 'bindings.json',
    '--remove-model-binding', 'a', '--remove-model-binding', 'b', '--execute',
  ]), { imageTag: selectedTag, modelBindings: 'bindings.json', removeModelBindings: ['a', 'b'] });
  assert.deepEqual(parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--agenthost-egress-audit', 'denied', '--execute',
  ]), { imageTag: selectedTag, agentHostEgressAudit: 'denied' });
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--agenthost-egress-audit', 'all', '--execute',
  ]), /must be off, allowed, or denied/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--agenthost-egress-audit', '--execute',
  ]), /must be off, allowed, or denied/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--agenthost-egress-audit', 'off',
    '--agenthost-egress-audit', 'allowed', '--execute',
  ]), /may be specified only once/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--model-bindings', '--execute',
  ]), /--model-bindings requires a file path/);
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', selectedTag, '--remove-model-binding', 'a', '--remove-model-binding', 'a', '--execute',
  ]), /names a more than once/);
});

test('a separate target needs its own API server; the default target must avoid it', () => {
  assert.deepEqual(parseDeployKustomizeArgs(['--image-tag', selectedTag, '--resource-group', 'awwus2np01', '--execute']),
    { imageTag: selectedTag, resourceGroup: 'awwus2np01' });
  assert.deepEqual(parseDeployKustomizeArgs(['--image-tag', selectedTag, '--resource-group', 'agentweaver', '--execute']),
    { imageTag: selectedTag });
  assert.throws(() => parseDeployKustomizeArgs(['--image-tag', selectedTag, '--resource-group', 'other', '--execute']),
    /registered install target/);
  const separate = 'https://awwus2np01-aks-88n4s6n7.hcp.westus2.azmk8s.io:443';
  const exec = server => (name, args) => {
    assert.deepEqual([name, ...args], ['kubectl', 'config', 'view', '--minify', '--context', 'ctx',
      '--output', 'jsonpath={.clusters[0].cluster.server}']);
    return { status: 0, stdout: `${server}\n`, stderr: '' };
  };
  assertTargetContext(exec(separate), 'ctx', root, 'awwus2np01');
  assertTargetContext(exec('https://agentweaver.example:443'), 'ctx', root);
  assert.throws(() => assertTargetContext(exec('https://agentweaver.example:443'), 'ctx', root, 'awwus2np01'),
    /does not point at the awwus2np01 cluster/);
  assert.throws(() => assertTargetContext(exec(separate), 'ctx', root), /does not point at the agentweaver cluster/);
});

test('image manifest accepts published tags or digests and rejects invalid or duplicate entries', () => {
  assert.deepEqual([...parseImageManifest(JSON.stringify({ images: {
    'agentweaver-web': selectedTag,
    'agentweaver.identity.broker': imageDigest,
  } }))], [
    ['agentweaver-web', selectedTag],
    ['agentweaver.identity.broker', imageDigest],
  ]);
  assert.throws(() => parseImageManifest(JSON.stringify({ images: { unknown: selectedTag } })),
    /unknown repository/);
  assert.throws(() => parseImageManifest(JSON.stringify({ images: { 'agentweaver-web': 'latest' } })),
    /must be sha-/);
  assert.throws(() => parseImageManifest(`images:\n  agentweaver-web: ${selectedTag}`), /valid JSON/);
  assert.throws(() => parseImageManifest(
    `{"images":{"agentweaver-web":"${selectedTag}","agentweaver-web":"${existingTag}"}}`),
  /unique keys/);
});

test('partial deployment preserves unselected live images and skips unrelated migration Jobs', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const liveImageRefs = Object.fromEntries(imageRepositories.map(repository => [repository, existingTag]));
  liveImageRefs['agentweaver-agenthost'] = existingAgentHostRegistry.childDigest;
  const liveAgentHostSettings = {
    Environment__Sandbox__AgentSandbox__OptionsRevision: 'p1-v1.2.3',
    Environment__Sandbox__AgentSandbox__ContainerImagePlatform: 'linux/amd64',
    Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes: '987654321',
  };
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    await deployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec: fakeExec(calls, { liveImageRefs, liveAgentHostSettings, distinctDigests: true }),
      log() {},
    });

    const rootRender = calls.find(call => call.command === 'kubectl' && call.args[0] === 'kustomize' &&
      call.args[1].endsWith(path.join('deploy', 'k8s')));
    const rendered = Object.fromEntries(rootRender.renderedImages.map(image => [
      image.name.replace(`${REGISTRY}/`, ''), image.digest,
    ]));
    assert.equal(rendered['agentweaver-web'], digestForTag(selectedTag));
    for (const repository of imageRepositories.filter(repository => repository !== 'agentweaver-web')) {
      assert.equal(rendered[repository], repository === 'agentweaver-agenthost'
        ? existingAgentHostRegistry.childDigest
        : digestForTag(existingTag), `${repository} keeps its deployed image`);
    }
    assert.equal(calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create')
      .map(call => parse(call.options.input)).some(resource => resource.kind === 'Job'), false);
    assert.equal(calls.some(call => call.command === 'az'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      (call.args[3] === 'jobs.batch' || call.args[2] === 'watch' && call.args[3] === 'pods') &&
      !call.args.includes('system:serviceaccount:agentweaver:environment')), false);
    assert.equal(calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout').length, 9);

    const applied = parse(calls.find(call => call.command === 'kubectl' && call.args[0] === 'apply').options.input);
    const environment = applied.items.find(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'environment');
    const environmentContainer = environment.spec.template.spec.containers
      .find(container => container.name === 'environment');
    const environmentSettings = Object.fromEntries(environmentContainer.env.map(({ name, value }) => [name, value]));
    assert.equal(environmentContainer.image,
      `${REGISTRY}/agentweaver.environment@${digestForTag(existingTag)}`);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__ContainerImage,
      `${REGISTRY}/agentweaver-agenthost@${existingAgentHostRegistry.childDigest}`);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__OptionsRevision,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__OptionsRevision);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__ContainerImagePlatform,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__ContainerImagePlatform);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes);
    const agentHostConfigMap = applied.items.find(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === 'agenthost-runtime-v1');
    assert.deepEqual(JSON.parse(agentHostConfigMap.data['appsettings.json']).AgentHost.Image, {
      Digest: existingAgentHostRegistry.childDigest,
      Platform: 'linux/amd64',
      CompressedPullBytes: 987654321,
    });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('ACR partial deployment keeps omitted live images in their existing registries', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    await deployKustomize({ imageManifest: manifestPath, registry: 'agentweaverasabbour.azurecr.io' }, {
      root: directory, exec: fakeExec(calls), log() {},
    });

    const pins = calls.find(call => call.command === 'kubectl' && call.args[0] === 'kustomize')
      .renderedImages;
    const selected = pins.find(image => image.name === `${REGISTRY}/agentweaver-web`);
    assert.equal(selected.newName, 'agentweaverasabbour.azurecr.io/agentweaver-web');
    for (const repository of imageRepositories.filter(repository => repository !== 'agentweaver-web')) {
      const image = pins.find(pin => pin.name === `${REGISTRY}/${repository}`);
      assert.equal(image.newName, `${REGISTRY}/${repository}`, `${repository} keeps its live registry`);
    }

    const applied = parse(calls.find(call => call.command === 'kubectl' && call.args[0] === 'apply').options.input);
    const environment = applied.items.find(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'environment');
    const environmentContainer = environment.spec.template.spec.containers
      .find(container => container.name === 'environment');
    const settings = Object.fromEntries(environmentContainer.env.map(({ name, value }) => [name, value]));
    assert.equal(settings.Environment__Sandbox__AgentSandbox__ContainerImage,
      `${REGISTRY}/agentweaver-agenthost@${existingAgentHostRegistry.childDigest}`);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('partial deployment rejects an omitted tagged AgentHost image before Kubernetes writes', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec: fakeExec(calls, { liveImageRefs: { 'agentweaver-agenthost': existingTag } }),
      log() {},
    }), /no valid immutable Environment__Sandbox__AgentSandbox__ContainerImage reference matching the resolved digest/);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('omitted AgentHost facts that are missing or conflict fail before Kubernetes writes', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  const invalidFacts = [
    [{ Environment__Sandbox__AgentSandbox__ContainerImagePlatform: 'linux/arm64' },
      /no supported Environment__Sandbox__AgentSandbox__ContainerImagePlatform/],
    [{ Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes: '' },
      /no valid positive Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes/],
  ];
  try {
    for (const [liveAgentHostSettings, expected] of invalidFacts) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
        root: directory, exec: fakeExec(calls, { liveAgentHostSettings }), log() {},
      }), expected);
      assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
        ['annotate', 'create', 'delete', 'apply', 'scale'].includes(call.args[0])), false);
    }

    const calls = [];
    const environment = workloadSource(path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'), 'environment');
    for (const container of environment.spec.template.spec.containers) {
      if (container.image?.startsWith(`${REGISTRY}/`)) {
        container.image = container.image.replace(/:receipt-pinned$/, `:${existingTag}`);
      }
      for (const setting of container.env ?? []) {
        if (setting.value?.startsWith(`${REGISTRY}/`)) {
          setting.value = setting.value.replace(/:receipt-pinned$/, `:${existingTag}`);
        }
      }
    }
    environment.spec.template.spec.containers[0].env.push({
      name: 'Environment__Sandbox__AgentSandbox__ContainerImage',
      value: `${REGISTRY}/agentweaver-agenthost@sha256:${'f'.repeat(64)}`,
    });
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec: fakeExec(calls, { state: { runtimeConfig: structuredClone(runtimeConfig),
        deployments: { environment }, configMaps: {} } }),
      log() {},
    }), /conflicting image references for agentweaver-agenthost/);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['annotate', 'create', 'delete', 'apply', 'scale'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('single Broker image update runs only its matching migration Job', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const liveImageRefs = Object.fromEntries(imageRepositories.map(repository => [repository, existingTag]));
  liveImageRefs['agentweaver-agenthost'] = existingAgentHostRegistry.childDigest;
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver.identity.broker': selectedTag } }));
  try {
    await deployKustomize({ imageManifest: manifestPath }, {
      root: directory, exec: fakeExec(calls, { liveImageRefs, distinctDigests: true }), log() {},
    });
    const createdJobs = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create')
      .map(call => parse(call.options.input))
      .filter(value => value.kind === 'Job');
    assert.deepEqual(createdJobs.map(value => value.metadata.name.split('-').slice(0, -1).join('-')),
      ['identity-broker-migration', 'projects-config-credential-settlement']);
    const identityReads = calls.filter(call => call.command === 'az' && call.args[0] === 'identity' &&
      call.args[1] === 'show' && call.args.includes('agentweaver-id-identity-broker-migration'));
    assert.equal(identityReads.length, 1);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Broker update quiesces identity-broker and projects-config, settles credentials, and blocks rollback', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const gatewayOnlyPath = path.join(directory, 'gateway-only.json');
  const calls = [];
  const liveImageRefs = Object.fromEntries(imageRepositories.map(repository => [repository, existingTag]));
  liveImageRefs['agentweaver-agenthost'] = existingAgentHostRegistry.childDigest;
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver.identity.broker': selectedTag } }));
  writeFileSync(gatewayOnlyPath, JSON.stringify({ images: { 'agentweaver.gateway': selectedTag } }));
  try {
    const baseExec = fakeExec(calls, { liveImageRefs, distinctDigests: true });
    const execWithDeployments = (command, args, options) => {
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' &&
          args.includes('--ignore-not-found=true') && args[2] === 'projects-config') {
        calls.push({ command, args, options });
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'apps/v1',
            kind: 'Deployment',
            metadata: { name: args[2], namespace: 'agentweaver' },
            spec: {
              replicas: 2,
              selector: { matchLabels: { 'app.kubernetes.io/name': args[2] } },
              template: { spec: { containers: [{ image: existingTag }] } },
            },
          }),
          stderr: '',
        };
      }
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'pods' && args.includes('--selector')) {
        calls.push({ command, args, options });
        return { status: 0, stdout: JSON.stringify({ items: [] }), stderr: '' };
      }
      if (command === 'kubectl' && args[0] === 'annotate') {
        calls.push({ command, args, options });
        return { status: 0, stdout: '', stderr: '' };
      }
      if (command === 'kubectl' && args[0] === 'scale') {
        calls.push({ command, args, options });
        return { status: 0, stdout: '', stderr: '' };
      }
      return baseExec(command, args, options);
    };

    // 1. Old Projects image lacking --settle-credential-operations fails BEFORE scaling anything to 0.
    await assert.rejects(
      deployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: execWithDeployments,
        deleteMaintenanceLease: baseExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async ({ capability }) => {
          if (capability === 'projects-config-settlement') {
            throw new Error('Projects & Config image does not support --settle-credential-operations');
          }
        },
        log() {},
      }),
      /does not support --settle-credential-operations/);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'scale'), false);

    // 2. Interrupted cutover rerun (projects-config at replicas: 0 with restore annotation=2) restores 2 and clears annotation.
    calls.length = 0;
    const rerunExec = (command, args, options) => {
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' &&
          args.includes('--ignore-not-found=true') && args[2] === 'projects-config') {
        calls.push({ command, args, options });
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'apps/v1',
            kind: 'Deployment',
            metadata: {
              name: 'projects-config',
              namespace: 'agentweaver',
              annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
            },
            spec: {
              replicas: 0,
              selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
              template: { spec: { containers: [{ image: existingTag }] } },
            },
          }),
          stderr: '',
        };
      }
      return execWithDeployments(command, args, options);
    };
    await deployKustomize({ imageManifest: manifestPath }, {
      root: directory, exec: rerunExec, deleteMaintenanceLease: baseExec.deleteMaintenanceLease,
      sleep: async () => {}, verifyImageCapability: async () => {}, log() {},
    });
    const scaleProjectsBeforeSettle = calls.findIndex((call, index) => index < calls.length &&
      call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/projects-config' && call.args.includes('--replicas=0'));
    const persistFloor = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      call.options?.input?.includes('identityBroker.disableFenceRequired'));
    const settleCreate = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      call.options?.input?.includes('projects-config-credential-settlement'));
    const applyCall = calls.find(call => call.command === 'kubectl' && call.args[0] === 'apply');
    assert.ok(scaleProjectsBeforeSettle === -1 || scaleProjectsBeforeSettle > settleCreate);
    assert.ok(persistFloor >= 0 && settleCreate >= 0);
    assert.ok(settleCreate < calls.indexOf(applyCall));
    assert.ok(applyCall.options.input.includes('replicas: 2'));
    assert.ok(calls.some(call => call.command === 'kubectl' && call.args[0] === 'annotate' &&
      call.args[1] === 'deployment/projects-config' &&
      call.args[2] === 'agentweaver.io/local-deploy-restore-replicas-'));

    // 3. Durable floor blocks rollback even on a partial deploy that omits agentweaver.identity.broker.
    const rollbackExec = (command, args, options) => {
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' &&
          args[2] === 'p1-db-maintenance-state') {
        return {
          status: 0,
          stdout: JSON.stringify({ data: { 'identityBroker.disableFenceRequired': 'true' } }),
          stderr: '',
        };
      }
      return execWithDeployments(command, args, options);
    };
    await assert.rejects(
      deployKustomize({ imageManifest: gatewayOnlyPath }, {
        root: directory,
        exec: rollbackExec,
        sleep: async () => {},
        verifyImageCapability: async ({ capability }) => {
          if (capability === 'identity-broker-disable-fence') {
            throw new Error('Identity Broker rollback guard blocks pre-fence image');
          }
        },
        log() {},
      }),
      /Identity Broker rollback guard blocks/);

    // Both fixture commits are ancestors of the capable origin/v1 tip.
    const bareOrigin = path.join(directory, 'origin.git');
    const cloneRepo = path.join(directory, 'repo');
    assert.equal(run('git', ['init', '--bare', bareOrigin], { cwd: directory }).status, 0);
    assert.equal(run('git', ['init', '-b', 'v1', cloneRepo], { cwd: directory }).status, 0);
    assert.equal(run('git', ['config', 'user.name', 'Test'], { cwd: cloneRepo }).status, 0);
    assert.equal(run('git', ['config', 'user.email', 'test@example.com'], { cwd: cloneRepo }).status, 0);
    const projectsProgram = path.join(cloneRepo, 'services', 'projects-config', 'Agentweaver.Projects.Config', 'Program.cs');
    const brokerMigrations = path.join(cloneRepo, 'services', 'identity', 'Agentweaver.Identity.Broker', 'Migrations');
    mkdirSync(path.dirname(projectsProgram), { recursive: true });
    writeFileSync(projectsProgram, 'var started = true;\n');
    assert.equal(run('git', ['add', '.'], { cwd: cloneRepo }).status, 0);
    assert.equal(run('git', ['commit', '--no-gpg-sign', '-m', 'pre-capability'], { cwd: cloneRepo }).status, 0);
    const preCapabilitySha = run('git', ['rev-parse', 'HEAD'], { cwd: cloneRepo }).stdout.trim();
    const preCapabilityTree = run('git', ['rev-parse', '--verify', `${preCapabilitySha}^{tree}`], { cwd: cloneRepo }).stdout.trim();

    writeFileSync(projectsProgram, 'var settle = "--settle-credential-operations";\n');
    mkdirSync(brokerMigrations, { recursive: true });
    writeFileSync(
      path.join(brokerMigrations, '20261124020000_AddCredentialDisableClaims.cs'),
      'public partial class AddCredentialDisableClaims {}\n',
    );
    assert.equal(run('git', ['add', '.'], { cwd: cloneRepo }).status, 0);
    assert.equal(run('git', ['commit', '--no-gpg-sign', '-m', 'capable'], { cwd: cloneRepo }).status, 0);
    assert.equal(run('git', ['remote', 'add', 'origin', bareOrigin], { cwd: cloneRepo }).status, 0);
    assert.equal(run('git', ['push', 'origin', 'v1'], { cwd: cloneRepo }).status, 0);
    const capableSha = run('git', ['rev-parse', 'HEAD'], { cwd: cloneRepo }).stdout.trim();
    const capableTree = run('git', ['rev-parse', '--verify', `${capableSha}^{tree}`], { cwd: cloneRepo }).stdout.trim();

    await assert.rejects(
      verifyImageCapability({
        image: `${REGISTRY}/agentweaver.projects.config@${'a'.repeat(64)}`,
        capability: 'projects-config-settlement',
        root: cloneRepo,
        exec: run,
        readImage: async () => ({ sourceSha: '0'.repeat(40), sourceTree: '0'.repeat(40) }),
      }),
      /is not an ancestor of origin\/v1/);
    await assert.rejects(
      verifyImageCapability({
        image: `${REGISTRY}/agentweaver.projects.config@${'a'.repeat(64)}`,
        capability: 'projects-config-settlement',
        root: cloneRepo,
        exec: run,
        readImage: async () => ({ sourceSha: capableSha, sourceTree: 'f'.repeat(40) }),
      }),
      /does not match commit/);
    await assert.rejects(
      verifyImageCapability({
        image: `${REGISTRY}/agentweaver.projects.config@${'a'.repeat(64)}`,
        capability: 'projects-config-settlement',
        root: cloneRepo,
        exec: run,
        readImage: async () => ({ sourceSha: preCapabilitySha, sourceTree: preCapabilityTree }),
      }),
      /does not support --settle-credential-operations/);
    await assert.rejects(
      verifyImageCapability({
        image: `${REGISTRY}/agentweaver.identity.broker@${'a'.repeat(64)}`,
        capability: 'identity-broker-disable-fence',
        root: cloneRepo,
        exec: run,
        readImage: async () => ({ sourceSha: preCapabilitySha, sourceTree: preCapabilityTree }),
      }),
      /database requires AddCredentialDisableClaims/);

    await verifyImageCapability({
      image: `${REGISTRY}/agentweaver.projects.config@${'a'.repeat(64)}`,
      capability: 'projects-config-settlement',
      root: cloneRepo,
      exec: run,
      readImage: async () => ({ sourceSha: capableSha, sourceTree: capableTree }),
    });
    await verifyImageCapability({
      image: `${REGISTRY}/agentweaver.identity.broker@${'a'.repeat(64)}`,
      capability: 'identity-broker-disable-fence',
      root: cloneRepo,
      exec: run,
      readImage: async () => ({ sourceSha: capableSha, sourceTree: capableTree }),
    });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('active installer guards both runtime maps, activates Environment, and recovers a pending marker', async () => {
  const directory = scratch();
  const kustomizationPath = path.join(directory, 'deploy', 'k8s', 'kustomization.yaml');
  const originalKustomization = readFileSync(kustomizationPath, 'utf8');
  const calls = [];
  const sleeps = [];
  const agentHostMap = structuredClone(agentHostRuntimeConfig);
  const agentHostSettings = JSON.parse(agentHostMap.data['appsettings.json']);
  agentHostSettings.AgentHost.Image.Digest = `sha256:${'1'.repeat(64)}`;
  agentHostSettings.AgentHost.Image.CompressedPullBytes = 123456;
  agentHostMap.data['appsettings.json'] = JSON.stringify(agentHostSettings);
  agentHostMap.metadata.uid = 'old-agenthost-uid';
  agentHostMap.metadata.resourceVersion = '10';
  const environmentMap = structuredClone(environmentRuntimeConfig);
  environmentMap.data.Environment__RuntimeBootstrap__DefaultSourceProfileId = 'old-profile';
  environmentMap.metadata.uid = 'old-environment-uid';
  environmentMap.metadata.resourceVersion = '20';
  const environmentDeployment = workloadSource(kustomizationPath, 'environment');
  for (const container of environmentDeployment.spec.template.spec.containers) {
    if (container.image?.startsWith(`${REGISTRY}/`))
      container.image = container.image.replace(/:receipt-pinned$/, `:${existingTag}`);
    for (const setting of container.env ?? []) {
      if (setting.value?.startsWith(`${REGISTRY}/`))
        setting.value = setting.value.replace(/:receipt-pinned$/, `:${existingTag}`);
    }
  }
  environmentDeployment.spec.replicas = 3;
  const state = {
    runtimeConfig: structuredClone(runtimeConfig),
    deployments: { environment: environmentDeployment },
    configMaps: {
      'agenthost-runtime-v1': agentHostMap,
      'environment-runtime-config': environmentMap,
    },
  };
  const exec = fakeExec(calls, {
    state,
    failApplyCount: 1,
    remainingPodPolls: 1,
  });
  const options = {
    root: directory,
    exec,
    sleep: async milliseconds => sleeps.push(milliseconds),
    log() {},
  };
  const manifestPath = path.join(directory, 'runtime-map-images.json');
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-agenthost': selectedTag } }));
  try {
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, options),
      /Workload apply failed: simulated workload apply failure/);

    const scaleDown = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/environment' && call.args.includes('--replicas=0'));
    const podWait = calls.findIndex((call, index) => index > scaleDown &&
      call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'pods' && call.args.includes('--selector'));
    const deletions = calls.map((call, index) => ({ call, index }))
      .filter(({ call }) => call.command === 'kubectl' && call.args[0] === 'delete' &&
        call.args[1] === '--raw' && !isMaintenanceLeaseCall(call));
    const creations = calls.map((call, index) => ({ call, index }))
      .filter(({ call }) => call.command === 'kubectl' && call.args[0] === 'create' &&
        ['agenthost-runtime-v1', 'environment-runtime-config']
          .includes(parse(call.options.input).metadata?.name));
    const scaleUp = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/environment' && call.args.includes('--replicas=3'));
    const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    assert.ok(scaleDown < podWait && podWait < deletions[0].index);
    assert.deepEqual(deletions.map(({ call }) => decodeURIComponent(call.args[2].split('/').at(-1))).sort(),
      ['agenthost-runtime-v1', 'environment-runtime-config']);
    assert.deepEqual(creations.map(({ call }) => parse(call.options.input).metadata.name).sort(),
      ['agenthost-runtime-v1', 'environment-runtime-config']);
    for (const { call, index } of deletions) {
      const name = decodeURIComponent(call.args[2].split('/').at(-1));
      const previous = name === 'agenthost-runtime-v1' ? agentHostMap : environmentMap;
      assert.deepEqual(JSON.parse(call.options.input).preconditions, {
        uid: previous.metadata.uid,
        resourceVersion: previous.metadata.resourceVersion,
      });
      assert.ok(index < scaleUp);
    }
    assert.ok(creations.every(({ index }) => index < scaleUp));
    assert.ok(scaleUp < apply);
    assert.deepEqual(sleeps, [5_000, 70_000]);
    assert.equal(state.deployments.environment.spec.replicas, 3);
    assert.equal(state.deployments.environment.metadata.annotations[restoreReplicasAnnotation], '3');
    const applied = parse(calls[apply].options.input);
    const activeEnvironmentMap = applied.items.find(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === 'environment-runtime-config');
    assert.equal(activeEnvironmentMap.data.Environment__RuntimeBootstrap__DefaultSourceProfileId,
      'agenthost-default');
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'rollout' &&
      /agenthost/.test(call.args[2] ?? '')), false);

    const beforeRetry = calls.length;
    await deployKustomize({ imageManifest: manifestPath }, options);
    const retry = calls.slice(beforeRetry);
    assert.equal(retry.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
      call.args[1] === '--raw' && !isMaintenanceLeaseCall(call)), false);
    assert.equal(retry.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap'), false);
    assert.deepEqual(retry.filter(call => call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/environment')
      .map(call => call.args[1] + ':' + call.args.find(arg => arg.startsWith('--replicas='))),
    ['deployment/environment:--replicas=0', 'deployment/environment:--replicas=3']);
    assert.equal(state.deployments.environment.spec.replicas, 3);
    assert.equal(state.deployments.environment.metadata.annotations[restoreReplicasAnnotation], undefined);
  } finally {
    readFileSync(kustomizationPath, 'utf8') === originalKustomization ||
      writeFileSync(kustomizationPath, originalKustomization);
    rmSync(directory, { recursive: true, force: true });
  }
});

test('partial deployment fails before writes when an omitted live image is unavailable', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const liveImageRefs = Object.fromEntries(imageRepositories
    .filter(repository => repository !== 'agentweaver-agenthost')
    .map(repository => [repository, existingTag]));
  liveImageRefs['agentweaver-agenthost'] = null;
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
      root: directory, exec: fakeExec(calls, { liveImageRefs, distinctDigests: true }), log() {},
    }), /Could not find the currently deployed image for agentweaver-agenthost/);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['create', 'apply', 'rollout'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('selective deployment replaces the Projects ConfigMap with run-binding TLS settings and retries activation', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const liveRuntimeConfig = structuredClone(runtimeConfig);
  Object.assign(liveRuntimeConfig.data, {
    ProjectsConfig__Authentication__BrokerRunBindingAudience: 'https://projects.agentweaver.io/run-binding',
    ProjectsConfig__Tls__CertificatePath: '/var/run/projects-config-tls/tls.crt',
    ProjectsConfig__Tls__KeyPath: '/var/run/projects-config-tls/tls.key',
    ProjectsConfig__Tls__ServerCertificateSha256:
      'd55bd61d14cd86a3b00a6911d763561bb904000be5b433d065d11d600d6e9fe9',
  });
  liveRuntimeConfig.data.AgentHost__ModelBindingsRevision = 'old-revision';
  const projectConfigDeployment = workloadSource(path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'),
    'projects-config');
  projectConfigDeployment.spec.replicas = 3;
  projectConfigDeployment.spec.template.spec.containers[0].image =
    `${REGISTRY}/agentweaver.projects.config:${existingTag}`;
  const state = {
    runtimeConfig: liveRuntimeConfig,
    deployments: { 'projects-config': projectConfigDeployment },
  };
  const clock = boundedClock();
  const podLists = [];
  const removedWorkloadPod = projectsWorkloadPod(projectConfigDeployment, 'Running');
  const olderMigrationJob = readYaml('deploy/k8s/migrations/projects-config/job.yaml');
  olderMigrationJob.metadata.name = 'projects-config-migration-1234abcd';
  const retainedMigrationPod = projectsMigrationPod(olderMigrationJob);
  retainedMigrationPod.metadata.creationTimestamp = '2026-11-03T00:00:00Z';
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    const exec = fakeExec(calls, {
      liveRuntimeConfig,
      state,
      failApplyCount: 1,
      distinctDigests: true,
      podList: index => {
        const items = index === 0
          ? [removedWorkloadPod, retainedMigrationPod]
          : [retainedMigrationPod];
        podLists.push(items);
        return items;
      },
    });
    const options = { root: directory, exec, sleep: clock.sleep, now: clock.now, log() {} };
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, options),
      /Workload apply failed: simulated workload apply failure/);

    const firstDelete = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'delete' && call.args[1] === 'configmap');
    const firstCreate = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap' &&
      parse(call.options.input).metadata.name === 'projects-config-runtime-config');
    const firstScaleDown = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'scale' && call.args[2] === '--replicas=0');
    const firstScaleUp = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'scale' && call.args[2] === '--replicas=3');
    const firstApply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const brokerRead = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'configmap' && call.args[2] === brokerRuntimeConfigName);
    const firstWrite = calls.findIndex(call => kubernetesWriteCalls([call]).length > 0);
    assert.ok(firstScaleDown < firstDelete && firstDelete < firstCreate &&
      firstCreate < firstScaleUp && firstScaleUp < firstApply);
    assert.ok(brokerRead >= 0 && brokerRead < firstWrite);
    assert.equal(calls[firstDelete].args[2], 'projects-config-runtime-config');
    const replacedRuntimeConfig = parse(calls[firstCreate].options.input);
    assert.equal(replacedRuntimeConfig.metadata.name, 'projects-config-runtime-config');
    assert.equal(replacedRuntimeConfig.data.ProjectsConfig__Authentication__BrokerRunBindingAudience,
      'https://projects.agentweaver.io/run-binding');
    assert.equal(replacedRuntimeConfig.data.ProjectsConfig__Tls__CertificatePath,
      '/var/run/projects-config-tls/tls.crt');
    assert.equal(replacedRuntimeConfig.data.ProjectsConfig__Tls__KeyPath,
      '/var/run/projects-config-tls/tls.key');
    assert.equal(replacedRuntimeConfig.data.ProjectsConfig__Tls__ServerCertificateSha256,
      'd55bd61d14cd86a3b00a6911d763561bb904000be5b433d065d11d600d6e9fe9');
    assert.equal(state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], '3');
    const appliedResources = parse(calls[firstApply].options.input).items;
    assert.equal(appliedResources.some(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === brokerRuntimeConfigName), false);
    const appliedRuntimeConfig = appliedResources.find(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === 'projects-config-runtime-config');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Authentication__BrokerRunBindingAudience,
      'https://projects.agentweaver.io/run-binding');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__CertificatePath,
      '/var/run/projects-config-tls/tls.crt');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__KeyPath,
      '/var/run/projects-config-tls/tls.key');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__ServerCertificateSha256,
      'd55bd61d14cd86a3b00a6911d763561bb904000be5b433d065d11d600d6e9fe9');
    const appliedProjectsDeployment = appliedResources.find(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'projects-config');
    assert.equal(appliedProjectsDeployment.spec.replicas, 3);
    assert.equal(appliedProjectsDeployment.spec.template.spec.containers[0].image,
      `${REGISTRY}/agentweaver.projects.config@${digestForTag(existingTag)}`);

    const callsBeforeRetry = calls.length;
    await deployKustomize({ imageManifest: manifestPath }, options);
    const retryCalls = calls.slice(callsBeforeRetry);
    const retryBrokerRead = retryCalls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'get' && call.args[1] === 'configmap' && call.args[2] === brokerRuntimeConfigName);
    const retryFirstWrite = retryCalls.findIndex(call => kubernetesWriteCalls([call]).length > 0);
    assert.ok(retryBrokerRead >= 0 && retryBrokerRead < retryFirstWrite);
    assert.equal(retryCalls.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
      !isSmokeCall(call) && !isMaintenanceLeaseCall(call)), false);
    assert.equal(retryCalls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap'), false);
    assert.deepEqual(retryCalls.filter(call => call.command === 'kubectl' && call.args[0] === 'scale')
      .map(call => call.args[2]), ['--replicas=0', '--replicas=3']);
    assert.equal(state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], undefined);
    assert.equal(retainedMigrationPod.metadata.ownerReferences[0].name, olderMigrationJob.metadata.name);
    assert.equal(retainedMigrationPod.metadata.creationTimestamp, '2026-11-03T00:00:00Z');
    assert.deepEqual(podLists, [
      [removedWorkloadPod, retainedMigrationPod],
      [retainedMigrationPod],
      [retainedMigrationPod],
    ]);
    assert.equal(podLists[1][0], retainedMigrationPod);
    assert.equal(podLists[2][0], retainedMigrationPod);
    assert.deepEqual(clock.sleeps, [5_000, 70_000, 70_000]);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      call.args[1] === 'can-i' && call.args[2] === 'delete' &&
      call.args[3] === 'configmap/projects-config-runtime-config'), true);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('retained completed Projects migration Pod does not block ConfigMap activation', async () => {
  const directory = scratch();
  const fixture = projectsActivationFixture(directory);
  const calls = [];
  const logs = [];
  const clock = boundedClock();
  const exec = fakeExec(calls, {
    liveRuntimeConfig: fixture.liveRuntimeConfig,
    state: fixture.state,
    podList: (_, recordedCalls) => [createdProjectsMigrationPod(recordedCalls)],
  });
  try {
    await deployKustomize({ imageManifest: fixture.imageManifest }, {
      root: directory, exec, log: message => logs.push(message),
      sleep: clock.sleep, now: clock.now,
    });

    const jobCall = calls.find(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'Job');
    const migrationJob = parse(jobCall.options.input);
    const migrationWait = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'wait' &&
      call.args[2] === `job/${migrationJob.metadata.name}`);
    const podPoll = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'pods' && call.args.includes('--selector'));
    const mapDelete = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'delete' && call.args[1] === 'configmap' &&
      call.args[2] === 'projects-config-runtime-config');
    const mapCreate = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap' &&
      parse(call.options.input).metadata.name === 'projects-config-runtime-config');
    const replacement = parse(calls[mapCreate].options.input);
    const scaleUp = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/projects-config' && call.args.includes('--replicas=3'));
    const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const rollouts = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout');
    const markerRemoval = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'annotate' && call.args[1] === 'deployment/projects-config' &&
      call.args[2] === `${restoreReplicasAnnotation}-`);

    assert.match(migrationJob.metadata.name, /^projects-config-migration-[a-f0-9]{8}$/);
    assert.ok(migrationWait >= 0 && migrationWait < podPoll);
    assert.ok(podPoll < mapDelete && mapDelete < mapCreate && mapCreate < scaleUp && scaleUp < apply,
      JSON.stringify({ podPoll, mapDelete, mapCreate, scaleUp, apply }));
    assert.deepEqual(replacement.data, runtimeConfig.data);
    assert.equal(clock.sleeps.includes(5_000), false);
    assert.deepEqual(clock.sleeps, [70_000]);
    assert.equal(rollouts.length, 9);
    assert.ok(markerRemoval > calls.lastIndexOf(rollouts.at(-1)));
    assert.equal(fixture.state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(fixture.state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], undefined);
    assert.ok(logs.includes(`Migration Job ${migrationJob.metadata.name} completed.`));
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'logs' && !isSmokeCall(call)), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
      call.args[1] === 'pod' && !isSmokeCall(call)), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('selected Projects workload Pods block activation until removed', async () => {
  const directory = scratch();
  const cases = [
    { phase: 'Pending' },
    { phase: 'Running' },
    { phase: 'Unknown' },
    { phase: 'Succeeded' },
    { phase: 'Failed' },
    { phase: 'Running', deletionTimestamp: '2026-11-04T00:00:00Z' },
    { phase: 'Succeeded', deletionTimestamp: '2026-11-04T00:00:00Z' },
  ];
  try {
    for (const { phase, deletionTimestamp } of cases) {
      const fixture = projectsActivationFixture(directory);
      const calls = [];
      const clock = boundedClock();
      const projectsDeployment = workloadSource(
        path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'), 'projects-config');
      const exec = fakeExec(calls, {
        liveRuntimeConfig: fixture.liveRuntimeConfig,
        state: fixture.state,
        podList: (index, recordedCalls) => {
          const migration = createdProjectsMigrationPod(recordedCalls);
          return index === 0
            ? [migration, projectsWorkloadPod(projectsDeployment, phase, deletionTimestamp)]
            : [migration];
        },
      });

      await deployKustomize({ imageManifest: fixture.imageManifest }, {
        root: directory, exec, log() {}, sleep: clock.sleep, now: clock.now,
      });

      const podPolls = calls.map((call, index) => ({ call, index }))
        .filter(({ call }) => call.command === 'kubectl' && call.args[0] === 'get' &&
          call.args[1] === 'pods' && call.args.includes('--selector') &&
          !call.args.some(a => a.includes('agentweaver.io/db-maintenance-run=')));
      const mapDelete = calls.findIndex(call => call.command === 'kubectl' &&
        call.args[0] === 'delete' && call.args[1] === 'configmap' &&
        call.args[2] === 'projects-config-runtime-config');
      const mapCreate = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create' &&
        parse(call.options.input).kind === 'ConfigMap' &&
        parse(call.options.input).metadata.name === 'projects-config-runtime-config');
      const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');

      assert.equal(podPolls.length, 2, `${phase} ${deletionTimestamp ?? 'active'} Pod must block one poll`);
      assert.ok(podPolls[0].index < podPolls[1].index &&
        podPolls[1].index < mapDelete && mapDelete < mapCreate && mapCreate < apply,
      JSON.stringify({ polls: podPolls.map(item => item.index), mapDelete, mapCreate, apply }));
      assert.deepEqual(clock.sleeps, [5_000, 70_000]);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
        call.args[1] === 'pod'), false);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('remaining Projects workload Pod preserves the stop timeout and recovery', async () => {
  const directory = scratch();
  const fixture = projectsActivationFixture(directory);
  const calls = [];
  const clock = boundedClock();
  const projectsDeployment = workloadSource(
    path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'), 'projects-config');
  const exec = fakeExec(calls, {
    liveRuntimeConfig: fixture.liveRuntimeConfig,
    state: fixture.state,
    podList: (_, recordedCalls) => [
      createdProjectsMigrationPod(recordedCalls),
      projectsWorkloadPod(projectsDeployment, 'Running'),
    ],
  });
  try {
    await assert.rejects(deployKustomize({ imageManifest: fixture.imageManifest }, {
      root: directory, exec, log() {}, sleep: clock.sleep, now: clock.now,
    }), /Timed out waiting for deployment\/projects-config Pods to terminate/);

    const podPolls = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'pods' && call.args.includes('--selector') &&
      !call.args.some(a => a.includes('agentweaver.io/db-maintenance-run=')));
    assert.equal(podPolls.length, 61);
    assert.deepEqual(clock.sleeps, Array(60).fill(5_000));
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
      call.args[1] === 'configmap' && call.args[2] === 'projects-config-runtime-config'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap' &&
      parse(call.options.input).metadata.name === 'projects-config-runtime-config'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), false);
    assert.equal(fixture.state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(fixture.state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], '3');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Projects migration Pod exemption requires its full identity and no runtime map reference', async () => {
  const directory = scratch();
  const targetMap = 'projects-config-runtime-config';
  const keyReference = {
    name: 'RUNTIME_SETTING',
    valueFrom: { configMapKeyRef: { name: targetMap, key: 'setting' } },
  };
  const cases = [
    ['missing owner', pod => { delete pod.metadata.ownerReferences; }],
    ['malformed owner references', pod => { pod.metadata.ownerReferences = {}; }],
    ['null owner reference', pod => { pod.metadata.ownerReferences = [null]; }],
    ['ReplicaSet owner', pod => {
      pod.metadata.ownerReferences[0].apiVersion = 'apps/v1';
      pod.metadata.ownerReferences[0].kind = 'ReplicaSet';
    }],
    ['non-controlling Job owner', pod => { pod.metadata.ownerReferences[0].controller = false; }],
    ['empty Job UID', pod => { pod.metadata.ownerReferences[0].uid = ''; }],
    ['invalid generated Job name', pod => { pod.metadata.ownerReferences[0].name = 'projects-config-migration-old'; }],
    ['wrong Job API version', pod => { pod.metadata.ownerReferences[0].apiVersion = 'batch/v1beta1'; }],
    ['wrong migration label', pod => { pod.metadata.labels['agentweaver.io/migration'] = 'other'; }],
    ['missing migration label', pod => { delete pod.metadata.labels['agentweaver.io/migration']; }],
    ['wrong ServiceAccount', pod => { pod.spec.serviceAccountName = 'projects-config'; }],
    ['active migration Pod', pod => { pod.status.phase = 'Running'; }],
    ['failed migration Pod', pod => { pod.status.phase = 'Failed'; }],
    ['container envFrom map reference', pod => {
      pod.spec.containers[0].envFrom = [{ configMapRef: { name: targetMap } }];
    }],
    ['container env valueFrom map reference', pod => {
      pod.spec.containers[0].env.push(structuredClone(keyReference));
    }],
    ['init container envFrom map reference', pod => {
      pod.spec.initContainers = [structuredClone(pod.spec.containers[0])];
      pod.spec.initContainers[0].envFrom = [{ configMapRef: { name: targetMap } }];
    }],
    ['init container env valueFrom map reference', pod => {
      pod.spec.initContainers = [structuredClone(pod.spec.containers[0])];
      pod.spec.initContainers[0].env.push(structuredClone(keyReference));
    }],
    ['ephemeral container envFrom map reference', pod => {
      pod.spec.ephemeralContainers = [structuredClone(pod.spec.containers[0])];
      pod.spec.ephemeralContainers[0].envFrom = [{ configMapRef: { name: targetMap } }];
    }],
    ['ephemeral container env valueFrom map reference', pod => {
      pod.spec.ephemeralContainers = [structuredClone(pod.spec.containers[0])];
      pod.spec.ephemeralContainers[0].env.push(structuredClone(keyReference));
    }],
    ['ConfigMap volume reference', pod => {
      pod.spec.volumes.push({ name: 'runtime-config', configMap: { name: targetMap } });
    }],
    ['projected ConfigMap volume reference', pod => {
      pod.spec.volumes.push({
        name: 'runtime-config',
        projected: { sources: [{ configMap: { name: targetMap } }] },
      });
    }],
  ];
  try {
    for (const [name, mutate] of cases) {
      const fixture = projectsActivationFixture(directory);
      const calls = [];
      const clock = boundedClock();
      const exec = fakeExec(calls, {
        liveRuntimeConfig: fixture.liveRuntimeConfig,
        state: fixture.state,
        podList: (index, recordedCalls) => {
          const migration = createdProjectsMigrationPod(recordedCalls);
          if (index !== 0) return [migration];
          const candidate = structuredClone(migration);
          mutate(candidate);
          return [migration, candidate];
        },
      });

      await deployKustomize({ imageManifest: fixture.imageManifest }, {
        root: directory, exec, log() {}, sleep: clock.sleep, now: clock.now,
      });

      const podPolls = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'get' &&
        call.args[1] === 'pods' && call.args.includes('--selector') &&
        !call.args.some(a => a.includes('agentweaver.io/db-maintenance-run=')));
      const mapDelete = calls.findIndex(call => call.command === 'kubectl' &&
        call.args[0] === 'delete' && call.args[1] === 'configmap' &&
        call.args[2] === 'projects-config-runtime-config');
      assert.equal(podPolls.length, 2, `${name} must remain a blocker until removed`);
      assert.ok(calls.indexOf(podPolls[1]) < mapDelete, `${name} must block ConfigMap replacement`);
      assert.deepEqual(clock.sleeps, [5_000, 70_000]);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('immutable runtime ConfigMap update checks delete permission before migration writes', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  const calls = [];
  const liveRuntimeConfig = structuredClone(runtimeConfig);
  liveRuntimeConfig.data.AgentHost__ModelBindingsRevision = 'old-revision';
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver.identity.broker': selectedTag } }));
  try {
    await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec: fakeExec(calls, { liveRuntimeConfig, denyRuntimeConfigDelete: true }),
      log() {},
    }), /cannot delete configmap\/projects-config-runtime-config/);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      call.args[1] === 'can-i' && call.args[2] === 'delete' &&
      call.args[3] === 'configmap/projects-config-runtime-config'), true);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['annotate', 'create', 'delete', 'apply', 'scale'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('runtime ConfigMap and Environment activation permission failures stop before every write', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'images.json');
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-agenthost': selectedTag } }));
  const changedEnvironmentState = () => {
    const environmentMap = structuredClone(environmentRuntimeConfig);
    environmentMap.data.Environment__RuntimeBootstrap__DefaultSourceProfileId = 'old-profile';
    environmentMap.metadata.uid = 'environment-map-uid';
    environmentMap.metadata.resourceVersion = '7';
    const agentHostMap = structuredClone(agentHostRuntimeConfig);
    agentHostMap.metadata.uid = 'agenthost-map-uid';
    agentHostMap.metadata.resourceVersion = '8';
    const deployment = workloadSource(path.join(directory, 'deploy', 'k8s', 'kustomization.yaml'),
      'environment');
    for (const container of deployment.spec.template.spec.containers) {
      if (container.image?.startsWith(`${REGISTRY}/`)) {
        container.image = container.image.replace(/:receipt-pinned$/, `:${existingTag}`);
      }
      for (const setting of container.env ?? []) {
        if (setting.value?.startsWith(`${REGISTRY}/`)) {
          setting.value = setting.value.replace(/:receipt-pinned$/, `:${existingTag}`);
        }
      }
    }
    deployment.spec.replicas = 2;
    return {
      runtimeConfig: structuredClone(runtimeConfig),
      deployments: { environment: deployment },
      configMaps: {
        'agenthost-runtime-v1': agentHostMap,
        'environment-runtime-config': environmentMap,
      },
    };
  };
  const denied = [
    { permission: 'create configmaps', settings: {}, expected: /cannot create configmaps/ },
    {
      permission: 'delete configmaps/agenthost-runtime-v1',
      settings: {},
      expected: /must allow delete configmaps\/agenthost-runtime-v1/,
    },
    {
      permission: 'update deployments.apps/environment --subresource=scale',
      settings: { state: changedEnvironmentState() },
      expected: /cannot update deployments\.apps\/environment --subresource=scale/,
    },
    {
      permission: 'patch deployments.apps/environment --subresource=scale',
      settings: { state: changedEnvironmentState() },
      expected: /cannot patch deployments\.apps\/environment --subresource=scale/,
    },
    {
      permission: 'patch deployment/environment',
      settings: {},
      expected: /cannot patch deployment\/environment/,
    },
    {
      permission: 'list pods',
      settings: { state: changedEnvironmentState() },
      expected: /cannot list pods/,
    },
  ];
  try {
    for (const { permission, settings, expected } of denied) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: fakeExec(calls, { ...settings, denyRuntimePermission: permission }),
        log() {},
      }), expected);
      assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
        ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false,
      `${permission} denial must precede writes`);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('required Secret metadata failures stop before workload effects', async () => {
  const directory = scratch();
  const failures = [
    [{ missingSecrets: ['agenthost-tls'] }, /Secret\/agenthost-tls is missing/],
    [{ secretTypes: { 'agenthost-tls': 'Opaque' } }, /Secret\/agenthost-tls must have type kubernetes\.io\/tls/],
    [{ missingSecrets: ['identity-broker-internal-tls'] }, /Secret\/identity-broker-internal-tls is missing/],
    [{ secretTypes: { 'identity-broker-internal-tls': 'Opaque' } },
      /Secret\/identity-broker-internal-tls must have type kubernetes\.io\/tls/],
    [{ missingSecrets: ['identity-broker-signing'], secretTypes: { 'gateway-tls': 'Opaque' } },
      /Secret\/identity-broker-signing is missing; Secret\/gateway-tls must have type kubernetes\.io\/tls/],
  ];
  try {
    for (const [settings, expected] of failures) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, settings), log() {},
      }), expected);
      assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
        ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false);
      const secretReads = calls.filter(call => call.command === 'kubectl' &&
        call.args[0] === 'get' && call.args[1] === 'secret');
      assert.equal(secretReads.length, 1);
      assert.deepEqual(secretReads[0].args.slice(2, secretReads[0].args.indexOf('--ignore-not-found=true')).sort(), [
        'agenthost-tls', 'environment-tls', 'events-and-sessions-tls', 'gateway-tls', 'identity-broker-internal-tls',
        'identity-broker-signing', 'identity-broker-tls', 'knowledge-tls', 'mcp-tls', 'orchestrator-tls',
        'projects-config-tls',
      ]);
      assert.equal(secretReads[0].args.some(arg => arg === '--output=json'), false);
      assert.equal(secretReads[0].args.includes('--output=wide'), true);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
        call.args.includes('secrets')), false);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a preserved provider Secret reference is checked before workload effects', async () => {
  const directory = scratch();
  const calls = [];
  try {
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root: directory,
      exec: fakeExec(calls, {
        liveProviderSetup: buildIdentityBrokerProviderPlan(providerSetupInput, root),
        missingSecrets: ['github-repo-app-oauth'],
      }),
      log() {},
    }), /Secret\/github-repo-app-oauth is missing/);
    assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('effective Environment RBAC failures stop before workload effects', async () => {
  const directory = scratch();
  const failures = [
    [{ denyEnvironmentPermission: 'get sandboxclaims.extensions.agents.x-k8s.io' },
      /lacks effective permission get sandboxclaims.extensions.agents.x-k8s.io/],
    [{ denyEnvironmentPermission: 'get configmaps/agenthost-runtime-v1' },
      /lacks effective permission get configmaps\/agenthost-runtime-v1/],
    [{ denyEnvironmentPermission: 'get runtimeclasses.node.k8s.io/kata-vm-isolation' },
      /lacks effective permission get runtimeclasses\.node\.k8s\.io\/kata-vm-isolation/],
    [{ denyEnvironmentPermission: 'get pods --subresource=log' },
      /lacks effective permission get pods --subresource=log/],
    [{ denyEnvironmentPermission: 'create pods --subresource=exec' },
      /lacks effective permission create pods --subresource=exec/],
    [{ denyEnvironmentPermission: 'create pods --subresource=attach' },
      /lacks effective permission create pods --subresource=attach/],
    [{
      denyEnvironmentPermission: 'patch ciliumnetworkpolicies.cilium.io/agenthost-platform-egress',
      denyEnvironmentPermissionExitCode: 1,
      denyEnvironmentPermissionOutput: 'no - Azure does not have opinion for this user\n',
    }, /lacks effective permission patch ciliumnetworkpolicies.cilium.io\/agenthost-platform-egress.*apply `?deploy\/k8s\/proposed\/environment-agent-sandbox-rbac\.yaml/],
  ];
  try {
    for (const [settings, expected] of failures) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, settings), log() {},
      }), expected);
      assert.equal(calls.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
        ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Environment RBAC check errors without a denial remain impersonation failures', async () => {
  const directory = scratch();
  const calls = [];
  try {
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root: directory,
      exec: fakeExec(calls, {
        environmentPermissionCheckError: 'patch ciliumnetworkpolicies.cilium.io/agenthost-platform-egress',
      }),
      log() {},
    }), /Could not check effective permission patch ciliumnetworkpolicies\.cilium\.io\/agenthost-platform-egress.*allowed to impersonate.*cannot impersonate/);
    assert.deepEqual(kubernetesWriteCalls(calls), []);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('cluster prerequisite failures stop before workload effects', async () => {
  const directory = scratch();
  const failures = [
    [{ liveStorageClass: { metadata: { name: 'x' }, provisioner: 'file.csi.azure.com',
      mountOptions: ['mfsymlinks', 'actimeo=30', 'nosharesock'] } },
    /must use file\.csi\.azure\.com with exactly these mount options.*cannot change after creation/],
    [{ liveStorageClass: null, denyRuntimePermission: 'create storageclasses' },
      /cannot create storageclasses at cluster scope/],
    [{ kataNodes: [] }, /No Ready, schedulable node matches RuntimeClass\/kata-vm-isolation \(kubernetes\.azure\.com\/kata-vm-isolation=true\); 0 matching.*npm run azure:deploy/],
    [{ kataNodes: [
      { spec: { unschedulable: true }, status: { conditions: [{ type: 'Ready', status: 'True' }] } },
      { spec: {}, status: { conditions: [{ type: 'Ready', status: 'False' }] } },
    ] }, /No Ready, schedulable node .*2 matching node\(s\)/],
    [{ denyRuntimePermission: 'list nodes' }, /cannot list nodes at cluster scope/],
    [{ denyRuntimePermission: 'delete persistentvolumeclaims' },
      /cannot delete persistentvolumeclaims .*install smoke check/],
    [{ denyRuntimePermission: 'get pods/log' }, /cannot get pods\/log/],
    [{ amaLogs: null }, /DaemonSet\/ama-logs is missing in namespace kube-system.*deploy\.mjs --execute/],
    [{ amaLogs: null, clusterNodes: [] }, /DaemonSet\/ama-logs is missing/],
    [{ amaLogs: null, clusterNodes: [canaryNode('eastus2euap'), canaryNode('eastus2')] }, /DaemonSet\/ama-logs is missing/],
    [{ amaLogs: null, clusterNodes: [canaryNode('eastus2euap'), { metadata: {} }] }, /DaemonSet\/ama-logs is missing/],
    [{ amaLogs: null, clusterNodes: 'not json' }, /invalid JSON for the node list/],
    [{ amaLogs: { metadata: { generation: 1 }, status: {} }, clusterNodes: [canaryNode('eastus2euap')] },
      /ama-logs is not ready \(0\/0 ready\)/],
    [{ amaLogs: { metadata: { generation: 1 }, status: { observedGeneration: 1, desiredNumberScheduled: 3,
      numberReady: 2, numberAvailable: 2, updatedNumberScheduled: 3 } } }, /ama-logs is not ready \(2\/3 ready\)/],
    [{ amaLogs: { metadata: { generation: 1 }, status: {} } }, /ama-logs is not ready \(0\/0 ready\)/],
    [{ denyRuntimePermission: 'get daemonsets.apps/ama-logs' },
      /cannot get daemonsets\.apps\/ama-logs in namespace kube-system/],
  ];
  try {
    for (const [settings, expected] of failures) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, settings), log() {},
      }), expected);
      assert.deepEqual(kubernetesWriteCalls(calls), []);
    }
    const calls = [];
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, { kataNodes: [] }), log() {},
    }));
    const storageClassRead = calls.find(call => call.args[0] === 'get' && call.args[1] === 'storageclass');
    assert.equal(storageClassRead.args[storageClassRead.args.indexOf('--as') + 1],
      'system:serviceaccount:agentweaver:environment');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('deploy continues without ama-logs only when every node is in a canary region', async () => {
  const directory = scratch();
  try {
    for (const region of ['eastus2euap', 'centraluseuap']) {
      const calls = [];
      const lines = [];
      await deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, { amaLogs: null, clusterNodes: [canaryNode(region), canaryNode(region)] }),
        log: line => lines.push(line),
      });
      assert.match(lines.join('\n'), /ama-logs is missing and every node is in a canary \(EUAP\) region.*not collected/);
      assert.ok(calls.some(call => call.args[0] === 'auth' && call.args[2] === 'list' && call.args[3] === 'nodes'));
      const auditCalls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag, agentHostEgressAudit: 'allowed' }, {
        root: directory, exec: fakeExec(auditCalls, {
          amaLogs: null, clusterNodes: [canaryNode(region), canaryNode(region)], networkLogApiAvailable: true,
        }), log() {},
      }), /AgentHost egress audit requires DaemonSet\/ama-logs/);
      assert.deepEqual(kubernetesWriteCalls(auditCalls), []);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('deploy skips unready ama-logs pods only on sandbox nodes', async () => {
  const directory = scratch();
  const ready = [{ type: 'Ready', status: 'True' }];
  const node = name => ({ metadata: { name }, spec: {}, status: { conditions: ready } });
  const pod = (nodeName, isReady, uid = 'ama-uid') => ({ metadata: { ownerReferences: [{ uid }] },
    spec: { nodeName }, status: { conditions: [{ type: 'Ready', status: isReady ? 'True' : 'False' }] } });
  const amaLogs = (status = {}) => ({ metadata: { generation: 1, uid: 'ama-uid' },
    spec: { selector: { matchLabels: { component: 'ama-logs-agent', tier: 'node' } } },
    status: { observedGeneration: 1, desiredNumberScheduled: 3, numberReady: 2, numberAvailable: 2,
      updatedNumberScheduled: 3, ...status } });
  const base = { kataNodes: [node('kata-0')], amaLogs: amaLogs(),
    amaLogsPods: [pod('system-0', true), pod('user-0', true), pod('kata-0', false)] };
  try {
    const calls = [];
    const lines = [];
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, base), log: line => lines.push(line),
    });
    assert.match(lines.join('\n'),
      /ama-logs is not ready on sandbox node\(s\) kata-0\..*ContainerNetworkLogs, and egress audit data .*not collected/);
    const podList = calls.find(call => call.args[0] === 'get' && call.args[1] === 'pods' &&
      call.args.includes('kube-system'));
    assert.equal(podList.args[podList.args.indexOf('--selector') + 1], 'component=ama-logs-agent,tier=node');

    const auditCalls = [];
    await assert.rejects(deployKustomize({ imageTag: selectedTag, agentHostEgressAudit: 'allowed' }, {
      root: directory, exec: fakeExec(auditCalls, { ...base, networkLogApiAvailable: true }), log() {},
    }), /AgentHost egress audit requires DaemonSet\/ama-logs to be ready on sandbox nodes/);
    assert.deepEqual(kubernetesWriteCalls(auditCalls), []);

    const failures = [
      { amaLogsPods: [pod('system-0', false), pod('user-0', true), pod('kata-0', false)] },
      { amaLogsPods: [pod('system-0', true), pod('kata-0', false)] },
      { amaLogsPods: [pod('system-0', true), pod('user-0', true, 'other-uid'), pod('kata-0', false)] },
      { amaLogs: amaLogs({ updatedNumberScheduled: 2 }) },
      { kataNodes: [{ spec: {}, status: { conditions: ready } }] },
    ];
    for (const settings of failures) {
      const failedCalls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(failedCalls, { ...base, ...settings }), log() {},
      }), /ama-logs is not ready \(2\/3 ready\).*deploy\.mjs --execute/);
      assert.deepEqual(kubernetesWriteCalls(failedCalls), []);
    }
    const deniedCalls = [];
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(deniedCalls, { ...base, denyKubeSystemPodList: true }), log() {},
    }), /ama-logs is not ready \(2\/3 ready\)\. The selected identity cannot list pods in namespace kube-system/);
    assert.deepEqual(kubernetesWriteCalls(deniedCalls), []);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('AgentHost egress audit requires a ready ama-logs pod on every sandbox node', async () => {
  const directory = scratch();
  const ready = [{ type: 'Ready', status: 'True' }];
  const node = name => ({ metadata: { name }, spec: {}, status: { conditions: ready } });
  const pod = nodeName => ({
    metadata: { ownerReferences: [{ uid: 'ama-uid' }] },
    spec: { nodeName },
    status: { conditions: ready },
  });
  const calls = [];
  try {
    await assert.rejects(deployKustomize({
      imageTag: selectedTag,
      agentHostEgressAudit: 'allowed',
    }, {
      root: directory,
      exec: fakeExec(calls, {
        kataNodes: [node('kata-0'), node('kata-1')],
        amaLogs: {
          metadata: { generation: 1, uid: 'ama-uid' },
          spec: { selector: { matchLabels: { component: 'ama-logs-agent', tier: 'node' } } },
          status: {
            observedGeneration: 1,
            desiredNumberScheduled: 3,
            numberReady: 3,
            numberAvailable: 3,
            updatedNumberScheduled: 3,
          },
        },
        amaLogsPods: [pod('system-0'), pod('user-0'), pod('kata-0')],
        networkLogApiAvailable: true,
      }),
      log() {},
    }), /ready, DaemonSet-owned ama-logs pod on every sandbox node.*kata-1/);
    assert.deepEqual(kubernetesWriteCalls(calls), []);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('deploy creates only a missing product StorageClass before migrations', async () => {
  const directory = scratch();
  try {
    const calls = [];
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, { liveStorageClass: null }), log() {},
    });
    const creates = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create');
    const policyCreate = creates.find(call =>
      parse(call.options.input).metadata?.name === 'agenthost-platform-egress');
    assert.ok(policyCreate, 'the missing AgentHost policy seed is created');
    const policySeed = parse(policyCreate.options.input);
    assert.equal(policySeed.kind, 'CiliumNetworkPolicy');
    assert.equal(policySeed.metadata.name, 'agenthost-platform-egress');
    assert.equal(policyCreate.args.includes('--namespace'), true);
    const storageClassCreate = creates.find(call =>
      parse(call.options.input).kind === 'StorageClass');
    assert.ok(storageClassCreate, 'the missing product StorageClass is created');
    const storageClass = parse(storageClassCreate.options.input);
    assert.equal(storageClass.kind, 'StorageClass');
    assert.equal(storageClass.metadata.name, 'agentweaver-workspace-azurefile');
    assert.equal(storageClassCreate.args.includes('--namespace'), false);
    assert.equal(creates.filter(call => parse(call.options.input).kind === 'StorageClass').length, 1);
    assert.ok(calls.indexOf(policyCreate) < calls.findIndex(call => call.args[0] === 'apply'));
    const applied = parse(calls.find(call => call.args[0] === 'apply').options.input);
    assert.equal(applied.items.some(resource =>
      resource.kind === 'CiliumNetworkPolicy' && resource.metadata.name === 'agenthost-platform-egress'), false);
    const firstMigration = creates.find(call => parse(call.options.input).kind === 'Job');
    assert.ok(calls.indexOf(storageClassCreate) < calls.indexOf(firstMigration));

    const existing = [];
    await deployKustomize({ imageTag: selectedTag }, { root: directory, exec: fakeExec(existing), log() {} });
    assert.equal(existing.some(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'StorageClass'), false);
    assert.equal(existing.some(call => call.args[0] === 'auth' && call.args.includes('storageclasses')), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('install smoke check runs an AgentHost-labelled sandbox pod and always cleans up', async () => {
  const directory = scratch();
  try {
    const calls = [];
    const messages = [];
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls), log: message => messages.push(message),
    });
    const smokeCreate = calls.findIndex(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'List');
    const lastRollout = calls.findLastIndex(call => call.args[0] === 'rollout');
    assert.ok(smokeCreate > lastRollout);
    const [claim, pod] = parse(calls[smokeCreate].options.input).items;
    assert.equal(claim.spec.storageClassName, 'agentweaver-workspace-azurefile');
    assert.deepEqual(claim.spec.accessModes, ['ReadWriteMany']);
    assert.match(pod.metadata.name, /^agentweaver-install-smoke-[0-9a-f]{8}$/);
    assert.deepEqual(pod.metadata.labels, {
      'app.kubernetes.io/name': 'agenthost',
      'agentweaver.io/service': 'agenthost',
      'agentweaver.io/workload': 'agenthost',
      'agentweaver.io/identity-client': 'true',
      'agentweaver.io/install-smoke-check': 'true',
    });
    assert.equal(pod.spec.runtimeClassName, 'kata-vm-isolation');
    assert.equal(pod.spec.automountServiceAccountToken, false);
    const [container] = pod.spec.containers;
    assert.equal(container.image, `${REGISTRY}/agentweaver-agenthost@${selectedAgentHostRegistry.childDigest}`);
    assert.equal(container.env, undefined);
    assert.equal(container.envFrom, undefined);
    assert.deepEqual(pod.spec.volumes, [{ name: 'workspace', persistentVolumeClaim: { claimName: pod.metadata.name } }]);
    assert.match(container.command[2], /getent hosts environment\.agentweaver\.svc\.cluster\.local/);
    assert.match(container.command[2], /\/dev\/tcp\/environment\.agentweaver\.svc\.cluster\.local\/8443/);
    assert.deepEqual(container.command.slice(3), ['smoke']);
    assert.doesNotMatch(container.command[2], /SMOKE_EGRESS_PROBE|192\.0\.2\.1/);
    assert.equal(calls.some(call => call.args[0] === 'apply' &&
      parse(call.options.input).kind === 'ContainerNetworkLog'), false);
    const staleCleanup = calls.findIndex(call => call.args[0] === 'delete' &&
      call.args.includes('agentweaver.io/install-smoke-check=true'));
    assert.ok(staleCleanup > lastRollout && staleCleanup < smokeCreate);
    assert.ok(calls.some(call => call.args[0] === 'delete' && call.args[1] === `pod/${pod.metadata.name}` &&
      call.args[2] === `persistentvolumeclaim/${pod.metadata.name}`));
    assert.ok(messages.some(message => /Install smoke check passed/.test(message)));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('AgentHost egress audit selects flows, owns collector cleanup, and tags the denied smoke probe', async () => {
  const directory = scratch();
  const ready = [{ type: 'Ready', status: 'True' }];
  const pod = nodeName => ({
    metadata: { ownerReferences: [{ uid: 'ama-uid' }] },
    spec: { nodeName },
    status: { conditions: ready },
  });
  const auditReadiness = {
    kataNodes: [{ metadata: { name: 'kata-0' }, spec: {}, status: { conditions: ready } }],
    amaLogs: {
      metadata: { generation: 1, uid: 'ama-uid' },
      spec: { selector: { matchLabels: { component: 'ama-logs-agent', tier: 'node' } } },
      status: {
        observedGeneration: 1,
        desiredNumberScheduled: 3,
        numberReady: 3,
        numberAvailable: 3,
        updatedNumberScheduled: 3,
      },
    },
    amaLogsPods: [pod('system-0'), pod('user-0'), pod('kata-0')],
  };
  const ownedCollector = {
    apiVersion: 'acn.azure.com/v1alpha1',
    kind: 'ContainerNetworkLog',
    metadata: {
      name: 'agentweaver-agenthost-egress',
      labels: { 'agentweaver.dev/managed-by': 'agentweaver' },
    },
  };
  const run = async (mode, options = {}) => {
    const calls = [];
    const exec = fakeExec(calls, {
      networkLogApiAvailable: true,
      ...(mode === 'off' ? {} : auditReadiness),
      ...options,
    });
    await deployKustomize({ imageTag: selectedTag, agentHostEgressAudit: mode }, {
      root: directory, exec, log() {},
      deleteCollector: async ({ context, root, uid, resourceVersion }) => {
        const result = exec('kubectl', ['delete', 'containernetworklogs.acn.azure.com',
          'agentweaver-agenthost-egress', `--preconditions=uid=${uid},resourceVersion=${resourceVersion}`,
          '--context', context], { check: false, cwd: root });
        if (result.status !== 0) throw new Error(result.stderr);
      },
    });
    return calls;
  };
  try {
    const allowedCalls = await run('allowed');
    const allowed = allowedCalls.find(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ContainerNetworkLog');
    const allowedCollector = parse(allowed.options.input);
    assert.equal(allowedCollector.metadata.name, 'agentweaver-agenthost-egress');
    assert.equal(allowedCollector.metadata.namespace, undefined);
    assert.deepEqual(allowedCollector.spec.includefilters[0].from.namespacedPod, ['agentweaver/']);
    assert.deepEqual(allowedCollector.spec.includefilters[0].from.labelSelector.matchLabels,
      { 'agentweaver.io/workload': 'agenthost' });
    assert.deepEqual(allowedCollector.spec.includefilters[0].verdict, ['dropped', 'forwarded']);
    assert.deepEqual(allowedCollector.spec.aggregation.additionalFields,
      ['source.labels', 'IP.destination', 'destination_names']);
    const smokeCall = allowedCalls.find(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'List');
    const smokePod = parse(smokeCall.options.input).items.find(item => item.kind === 'Pod');
    const ownerLabel = smokePod.metadata.labels['agentweaver.dev/sandbox-owner'];
    assert.match(ownerLabel, /^[0-9a-f]{40}$/);
    assert.match(smokePod.spec.containers[0].command[2], new RegExp(
      `SMOKE_EGRESS_PROBE ${ownerLabel} 192\\.0\\.2\\.1:443`));
    assert.match(smokePod.spec.containers[0].command[2], /fail egress-deny/);

    const deniedCalls = await run('denied');
    const denied = deniedCalls.find(call => call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ContainerNetworkLog');
    assert.deepEqual(parse(denied.options.input).spec.includefilters[0].verdict, ['dropped']);

    const reconciledCalls = await run('denied', { networkLogResource: ownedCollector });
    const reconciled = reconciledCalls.find(call => call.args[0] === 'patch' &&
      call.args[1] === 'containernetworklogs.acn.azure.com');
    const patch = JSON.parse(reconciled.args[reconciled.args.indexOf('--patch') + 1]);
    assert.deepEqual(patch.slice(0, 3), [
      { op: 'test', path: '/metadata/uid', value: 'uid-agentweaver-agenthost-egress' },
      { op: 'test', path: '/metadata/resourceVersion', value: '1' },
      { op: 'test', path: '/metadata/labels/agentweaver.dev~1managed-by', value: 'agentweaver' },
    ]);
    assert.deepEqual(patch.find(operation => operation.path === '/spec').value.includefilters[0].verdict,
      ['dropped']);

    const absentCalls = await run('off');
    assert.equal(absentCalls.some(call => call.args[0] === 'delete' &&
      call.args[1] === 'containernetworklogs.acn.azure.com'), false);

    const offCalls = await run('off', { networkLogResource: ownedCollector });
    assert.ok(offCalls.some(call => call.args[0] === 'delete' &&
      call.args[1] === 'containernetworklogs.acn.azure.com' &&
      call.args[2] === 'agentweaver-agenthost-egress'));

    const unownedOffCalls = await run('off', { networkLogResource: {
      ...ownedCollector, metadata: { name: ownedCollector.metadata.name, labels: {} },
    } });
    assert.equal(unownedOffCalls.some(call => call.args[0] === 'delete' &&
      call.args[1] === 'containernetworklogs.acn.azure.com'), false);

    for (const race of ['create', 'update', 'delete']) {
      const calls = [];
      const state = { runtimeConfig: structuredClone(runtimeConfig), deployments: {} };
      const initial = race === 'create' ? undefined : ownedCollector;
      const exec = fakeExec(calls, {
        ...auditReadiness, networkLogApiAvailable: true, networkLogResource: initial, networkLogRace: race, state,
      });
      await assert.rejects(deployKustomize({
        imageTag: selectedTag, agentHostEgressAudit: race === 'delete' ? 'off' : 'allowed',
      }, {
        root: directory,
        exec,
        deleteCollector: async ({ context, root, uid, resourceVersion }) => {
          const result = exec('kubectl', ['delete', 'containernetworklogs.acn.azure.com',
            'agentweaver-agenthost-egress', `--preconditions=uid=${uid},resourceVersion=${resourceVersion}`,
            '--context', context], { check: false, cwd: root });
          if (result.status !== 0) throw new Error(result.stderr);
        },
        log() {},
      }), /already exists|ownership or resourceVersion changed|precondition failed/);
      assert.equal(state.networkLogResource?.metadata.labels['agentweaver.dev/managed-by'], 'third-party');
    }

    const conflictCalls = [];
    await assert.rejects(deployKustomize({ imageTag: selectedTag, agentHostEgressAudit: 'allowed' }, {
      root: directory,
      exec: fakeExec(conflictCalls, {
        ...auditReadiness,
        networkLogApiAvailable: true,
        networkLogResource: { ...ownedCollector, metadata: { name: ownedCollector.metadata.name, labels: {} } },
      }),
      log() {},
    }), /is not managed by Agentweaver/);
    assert.deepEqual(kubernetesWriteCalls(conflictCalls), []);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('collector deletion sends UID and resourceVersion preconditions through kubectl proxy', async () => {
  const requests = [];
  let command;
  const proxy = new EventEmitter();
  proxy.exitCode = null;
  proxy.signalCode = null;
  proxy.kill = () => {
    proxy.signalCode = 'SIGTERM';
    queueMicrotask(() => proxy.emit('close'));
    return true;
  };
  await deleteContainerNetworkLog({
    context: 'selected-context', root: 'repo', uid: 'collector-uid', resourceVersion: '17',
  }, {
    availableLoopbackPort: async () => 32123,
    spawn(...args) {
      command = args;
      return proxy;
    },
    async fetch(url, options = {}) {
      requests.push({ url, options });
      return { ok: true, status: 200, arrayBuffer: async () => new ArrayBuffer(0) };
    },
  });
  assert.deepEqual(command, ['kubectl', [
    'proxy', '--port=32123', '--address=127.0.0.1', '--context', 'selected-context',
  ], { cwd: 'repo', stdio: 'ignore', windowsHide: true }]);
  assert.equal(requests[0].url, 'http://127.0.0.1:32123/version');
  assert.equal(requests[1].url,
    'http://127.0.0.1:32123/apis/acn.azure.com/v1alpha1/containernetworklogs/agentweaver-agenthost-egress');
  assert.equal(requests[1].options.method, 'DELETE');
  assert.deepEqual(JSON.parse(requests[1].options.body), {
    apiVersion: 'v1',
    kind: 'DeleteOptions',
    preconditions: { uid: 'collector-uid', resourceVersion: '17' },
  });
  assert.equal(proxy.signalCode, 'SIGTERM');
});

test('install smoke check failures name the cause and clean up', async () => {
  const directory = scratch();
  const failures = [
    [{ smokePod: { status: { phase: 'Failed' } }, smokeLogs: 'SMOKE_FAIL dns\n' },
      /could not resolve environment\.agentweaver\.svc\.cluster\.local/],
    [{ smokePod: { status: { phase: 'Failed' } }, smokeLogs: 'SMOKE_FAIL environment-connect\n' },
      /could not open TCP 8443 to Environment/],
    [{ smokePod: { status: { phase: 'Failed' } }, smokeLogs: 'SMOKE_FAIL storage-symlink\n' },
      /mfsymlinks mount option is missing/],
    [{ smokePod: { status: { phase: 'Failed' } }, smokeLogs: 'SMOKE_FAIL byok:demo.openai.azure.com\n' },
      /no HTTPS response from BYOK endpoint demo\.openai\.azure\.com \(DNS, proxy, connection, or TLS failure/],
    [{ smokePod: { status: { phase: 'Succeeded' } }, smokeLogs: '' }, /without a result/],
    [{ smokePod: { status: { phase: 'Pending', conditions: [{ type: 'PodScheduled', status: 'False',
      reason: 'Unschedulable', message: '0/3 nodes are available' }] } } },
    /did not finish in 10 minutes \(not scheduled: Unschedulable 0\/3 nodes are available\)/],
    [{ smokeCreateError: 'pods "x" is forbidden: exceeded quota: agentweaver-quota' },
      /ResourceQuota has no room for the install smoke check/],
  ];
  try {
    for (const [settings, expected] of failures) {
      const calls = [];
      const clock = boundedClock();
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, settings), log() {}, sleep: clock.sleep,
      }), expected);
      assert.ok(calls.some(call => call.args[0] === 'delete' &&
        call.args[1]?.startsWith('pod/agentweaver-install-smoke-')));
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('deploy:kustomize blocks environment-migration on existing Environment deployment or active pods before any cluster writes', async () => {
  const directory = scratch();
  const manifestPath = path.join(directory, 'env-manifest.json');
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver.environment': selectedTag } }));
  try {
    // Full release (includes identity-broker + environment) must reject BEFORE scaling identity-broker/projects-config or writing.
    const callsFullRelease = [];
    await assert.rejects(
      deployKustomize({ imageTag: selectedTag }, {
        root: directory,
        exec: fakeExec(callsFullRelease, { liveEnvironmentPresent: true }),
        log() {},
      }),
      /Existing Environment installations cannot run environment-migration through deploy:kustomize.*db:maintenance -- --execute --deploy-apps/);
    assert.equal(callsFullRelease.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['scale', 'annotate', 'create', 'patch', 'delete', 'apply'].includes(call.args[0])), false);

    const callsExisting = [];
    await assert.rejects(
      deployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: fakeExec(callsExisting, { liveEnvironmentPresent: true }),
        log() {},
      }),
      /Existing Environment installations cannot run environment-migration through deploy:kustomize.*db:maintenance -- --execute --deploy-apps/);
    assert.equal(callsExisting.some(call => call.command === 'kubectl' && !isMaintenanceLeaseCall(call) &&
      ['scale', 'annotate', 'create', 'patch', 'delete', 'apply'].includes(call.args[0])), false);

    const callsPods = [];
    const baseExec = fakeExec(callsPods);
    const podExec = (command, args, options) => {
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'environment' &&
          args.includes('--ignore-not-found=true')) {
        return { status: 0, stdout: '', stderr: '' };
      }
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'pods' &&
          args.includes('agentweaver.io/workload=environment')) {
        return { status: 0, stdout: JSON.stringify({ items: [{ metadata: { name: 'env-0' }, status: { phase: 'Running' } }] }), stderr: '' };
      }
      return baseExec(command, args, options);
    };
    await assert.rejects(
      deployKustomize({ imageManifest: manifestPath }, { root: directory, exec: podExec, log() {} }),
      /Cannot run environment-migration through deploy:kustomize while non-terminal environment pods exist.*db:maintenance -- --execute --deploy-apps/);
    assert.equal(callsPods.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      call.options?.input?.includes('environment-migration-')), false);

    // If an active Environment pod appears while the Broker migration job runs, abort right before environment-migration creation.
    const brokerAndEnvManifestPath = path.join(directory, 'broker-and-env-manifest.json');
    writeFileSync(brokerAndEnvManifestPath, JSON.stringify({
      images: {
        'agentweaver.identity.broker': selectedTag,
        'agentweaver.environment': selectedTag,
      },
    }));
    const callsRace = [];
    const baseRaceExec = fakeExec(callsRace);
    let brokerMigrationCompleted = false;
    const raceExec = (command, args, options) => {
      if (command === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'environment' &&
          args.includes('--ignore-not-found=true')) {
        return { status: 0, stdout: '', stderr: '' };
      }
      const result = baseRaceExec(command, args, options);
      if (command === 'kubectl' && args[0] === 'wait' &&
          args.some(arg => arg.startsWith('job/identity-broker-migration-'))) {
        brokerMigrationCompleted = true;
      }
      if (brokerMigrationCompleted && command === 'kubectl' && args[0] === 'get' && args[1] === 'pods' &&
          args.includes('agentweaver.io/workload=environment')) {
        return {
          status: 0,
          stdout: JSON.stringify({ items: [{ metadata: { name: 'env-race-0' }, status: { phase: 'Running' } }] }),
          stderr: '',
        };
      }
      return result;
    };
    await assert.rejects(
      deployKustomize({ imageManifest: brokerAndEnvManifestPath }, { root: directory, exec: raceExec, log() {} }),
      /Cannot run environment-migration through deploy:kustomize while non-terminal environment pods exist.*db:maintenance -- --execute --deploy-apps/);
    assert.equal(brokerMigrationCompleted, true);
    assert.equal(callsRace.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      call.options?.input?.includes('environment-migration-')), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize acquires Lease first, scales down incompatible projects-config, persists Pending then Required, and restores replicas', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    const calls = [];
    const baseExec = fakeExec(calls, {
      liveImageRefs: { 'agentweaver.projects.config': 'sha-0000000000000000000000000000000000000000' },
    });
    const exec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: { name: 'projects-config', namespace: 'agentweaver' },
            spec: {
              replicas: 2,
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      return baseExec(cmd, args, opts);
    };
    const verifyImageCapability = async ({ image, capability }) => {
      if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
        throw new Error('pre-20261128012056 image');
      }
    };
    const deletedLeases = [];
    await realDeployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec,
      sleep: async () => {},
      verifyImageCapability,
      deleteMaintenanceLease: async opts => {
        deletedLeases.push(opts);
        return baseExec.deleteMaintenanceLease(opts);
      },
      log() {},
    });

    const leaseCreateIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.trimStart().startsWith('{') && JSON.parse(c.options.input).kind === 'Lease');
    const stateReadIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'get' &&
      c.args[1] === 'configmap' && c.args[2] === 'p1-db-maintenance-state');
    const scaleDownIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'scale' &&
      c.args[1] === 'deployment/projects-config' && c.args.includes('--replicas=0'));
    const pendingCreateIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.includes('projectsConfig.consumeGuardPending'));
    const jobCreateIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.includes('projects-config-migration') && !c.options?.input?.includes('p1-db-maintenance-state'));
    const requiredCreateIdx = calls.findLastIndex(c => c.command === 'kubectl' &&
      (c.args[0] === 'create' || c.args[0] === 'patch') &&
      (c.options?.input?.includes('projectsConfig.consumeGuardRequired') ||
        c.args.join(' ').includes('projectsConfig.consumeGuardRequired')));
    const jobDeleteIdx = calls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'delete' &&
      c.args[1] === 'job' && c.args[2]?.startsWith('projects-config-migration-'));
    assert.ok(leaseCreateIdx >= 0 && leaseCreateIdx < stateReadIdx);
    assert.ok(leaseCreateIdx < scaleDownIdx);
    assert.ok(pendingCreateIdx >= 0 && pendingCreateIdx < jobCreateIdx);
    assert.ok(jobCreateIdx >= 0 && requiredCreateIdx > jobCreateIdx && jobDeleteIdx > requiredCreateIdx &&
      deletedLeases.length === 1,
    JSON.stringify({ leaseCreateIdx, stateReadIdx, scaleDownIdx, pendingCreateIdx, jobCreateIdx, requiredCreateIdx, jobDeleteIdx, deletedLeases }));

    const applied = parse(calls.find(c => c.command === 'kubectl' && c.args[0] === 'apply').options.input);
    const pcApplied = applied.items.find(r => r.kind === 'Deployment' && r.metadata.name === 'projects-config');
    assert.equal(pcApplied.spec.replicas, 2);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize reuses heldMaintenanceRunId without nested Lease and retains owned Lease if Pods are unsettled', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));

    // 1. Reuse heldMaintenanceRunId without creating or deleting Lease.
    const reuseCalls = [];
    const reuseExec = fakeExec(reuseCalls);
    const reuseWrappedExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
        return { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'outer-run-123' } }), stderr: '' };
      }
      return reuseExec(cmd, args, opts);
    };
    await realDeployKustomize({ imageManifest: manifestPath }, {
      root: directory,
      exec: reuseWrappedExec,
      sleep: async () => {},
      verifyImageCapability: async () => {},
      heldMaintenanceRunId: 'outer-run-123',
      log() {},
    });
    assert.equal(reuseCalls.some(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.trimStart().startsWith('{') && JSON.parse(c.options.input).kind === 'Lease'), false);
    assert.equal(reuseCalls.some(c => c.command === 'kubectl' && c.args[0] === 'delete' &&
      c.args[1] === '--raw' && c.args[2]?.includes('/leases/p1-db-maintenance-lock')), false);

    // 2. Retain owned Lease if active Pod remains for the run.
    const retainCalls = [];
    const baseRetainExec = fakeExec(retainCalls);
    const retainExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'pods' &&
          args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
        return {
          status: 0,
          stdout: JSON.stringify({ kind: 'List', items: [{ metadata: { name: 'stuck-pod' }, status: { phase: 'Running' } }] }),
          stderr: '',
        };
      }
      return baseRetainExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: retainExec,
        deleteMaintenanceLease: baseRetainExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /Could not clean up migration Job .* retain p1-db-maintenance-lock/);
    assert.equal(retainCalls.some(c => c.command === 'kubectl' && c.args[0] === 'delete' &&
      c.args[1] === '--raw' && c.args[2]?.includes('/leases/p1-db-maintenance-lock')), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: unrelated Gateway-only deploy neither acquires Lease nor runs Project Config compatibility check, while projects-config path retains both', async () => {
  const directory = scratch();
  try {
    const partialManifestPath = path.join(directory, 'gateway-only.manifest.json');
    writeFileSync(partialManifestPath, JSON.stringify({
      images: { 'agentweaver.gateway': selectedTag },
    }));
    const gwCalls = [];
    const baseGwExec = fakeExec(gwCalls);
    const gwExec = (cmd, args, opts) => {
      if (cmd === 'docker' && args[0] === 'buildx' && args[1] === 'imagetools' &&
          args.some(a => a.includes('2'.repeat(64)))) {
        return { status: 0, stdout: `sha256:${'2'.repeat(64)}\n`, stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state') {
        return {
          status: 0,
          stdout: JSON.stringify({ data: { 'projectsConfig.consumeGuardRequired': 'true' } }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: { name: 'projects-config', namespace: 'agentweaver' },
            spec: {
              replicas: 2,
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      return baseGwExec(cmd, args, opts);
    };
    let compatCheckedForGateway = false;
    await realDeployKustomize({ imageManifest: partialManifestPath }, {
      root: directory,
      exec: gwExec,
      sleep: async () => {},
      verifyImageCapability: async ({ capability }) => {
        if (capability === 'projects-config-consume-guard') {
          compatCheckedForGateway = true;
          throw new Error('pre-20261128012056 image');
        }
      },
      log() {},
    });
    assert.equal(compatCheckedForGateway, false, 'Gateway-only deploy must not run Project Config compatibility check');
    assert.equal(gwCalls.some(c => c.command === 'kubectl' && c.args[1] === 'lease'), false);
    assert.equal(gwCalls.some(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.trimStart().startsWith('{') && JSON.parse(c.options.input).kind === 'Lease'), false);

    // Projects & Config migration path acquires Lease before live reads and enforces compatibility.
    const pcManifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(pcManifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    const pcCalls = [];
    const basePcExec = fakeExec(pcCalls);
    const pcExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state') {
        pcCalls.push({ command: cmd, args, options: opts });
        return {
          status: 0,
          stdout: JSON.stringify({ data: { 'projectsConfig.consumeGuardRequired': 'true' } }),
          stderr: '',
        };
      }
      return basePcExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: pcManifestPath }, {
        root: directory,
        exec: pcExec,
        deleteMaintenanceLease: basePcExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async ({ capability }) => {
          if (capability === 'projects-config-consume-guard') throw new Error('pre-20261128012056 image');
        },
        log() {},
      }),
      /pre-20261128012056 image/);

    const leaseCreateIdx = pcCalls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.trimStart().startsWith('{') && JSON.parse(c.options.input).kind === 'Lease');
    const stateReadIdx = pcCalls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'get' &&
      c.args[1] === 'configmap' && c.args[2] === 'p1-db-maintenance-state');
    const leasedDeploymentReadIdx = pcCalls.findLastIndex(c => c.command === 'kubectl' &&
      c.args[0] === 'get' && c.args[1] === 'deployment' && c.args[2] === 'projects-config');
    assert.ok(leaseCreateIdx >= 0 && leaseCreateIdx < stateReadIdx);
    assert.ok(leaseCreateIdx < leasedDeploymentReadIdx);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: activateRuntimeConfig keeps projects-config at 0 during cutover even if apply fails, and migration Job carries run label', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    const calls = [];
    const baseExec = fakeExec(calls);
    const exec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: { name: 'projects-config', namespace: 'agentweaver' },
            spec: {
              replicas: 2,
              selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' &&
          args[2] === 'projects-config-runtime-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            ...runtimeConfig,
            data: { ...runtimeConfig.data, 'appsettings.json': '{"old":true}' },
          }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'apply') {
        return { status: 1, stdout: '', stderr: 'simulated apply failure' };
      }
      return baseExec(cmd, args, opts);
    };
    const verifyImageCapability = async ({ image, capability }) => {
      if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
        throw new Error('pre-20261128012056 image');
      }
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec,
        deleteMaintenanceLease: baseExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability,
        log() {},
      }),
      /Workload apply failed/);

    const scaleUpCalls = calls.filter(c => c.command === 'kubectl' && c.args[0] === 'scale' &&
      c.args[1] === 'deployment/projects-config' && !c.args.includes('--replicas=0'));
    assert.equal(scaleUpCalls.length, 0, 'old projects-config must stay at 0 when apply fails');

    const jobCreate = calls.find(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.includes('projects-config-migration-') && !c.options?.input?.includes('p1-db-maintenance-state'));
    const jobDoc = parse(jobCreate.options.input);
    const runLabel = jobDoc.metadata.labels['agentweaver.io/db-maintenance-run'];
    assert.match(runLabel, /^[a-f0-9]{12}$/);
    assert.equal(jobDoc.spec.template.metadata.labels['agentweaver.io/db-maintenance-run'], runLabel);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: wait failure settles Job, runs real Pair A verifier without override, and retains Lease when Pod stays active', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));

    // 1. Active Pod after wait failure -> deletes Job and retains Lease.
    const activeCalls = [];
    const baseActiveExec = fakeExec(activeCalls);
    const activeExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/projects-config-migration-'))) {
        return { status: 1, stdout: '', stderr: 'job wait failed' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'pods' &&
          args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
        return {
          status: 0,
          stdout: JSON.stringify({ kind: 'List', items: [{ metadata: { name: 'stuck-mig-pod' }, status: { phase: 'Running' } }] }),
          stderr: '',
        };
      }
      return baseActiveExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: activeExec,
        deleteMaintenanceLease: baseActiveExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /projects-config-migration-.* failed/);
    assert.ok(activeCalls.some(c => c.command === 'kubectl' && c.args[0] === 'delete' && c.args[1] === 'job'));
    assert.equal(activeCalls.some(c => c.command === 'kubectl' && c.args[0] === 'delete' && c.args[1] === 'lease'), false);

    // 2. Settled wait failure -> runs real --verify-projects-config-pair-a Job and clears Pending on PROJECTS_CONFIG_EXACT_PAIR_A.
    const settledCalls = [];
    const baseSettledExec = fakeExec(settledCalls);
    const settledExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/projects-config-migration-'))) {
        return { status: 1, stdout: '', stderr: 'job wait failed' };
      }
      if (cmd === 'kubectl' && args[0] === 'logs' && args.some(a => a.startsWith('job/projects-config-paira-'))) {
        return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
      }
      return baseSettledExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: settledExec,
        deleteMaintenanceLease: baseSettledExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /projects-config-migration-.* failed/);
    const pairAJobCreate = settledCalls.find(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.includes('--verify-projects-config-pair-a'));
    assert.ok(pairAJobCreate, 'real Pair A verifier Job must be created on settled migration failure');
    assert.ok(settledCalls.some(c => c.command === 'kubectl' &&
      (c.args[0] === 'create' || c.args[0] === 'patch') &&
      ((c.options?.input ?? '').includes('consumeGuardPending":null') ||
        c.args.join(' ').includes('consumeGuardPending":null'))));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deleteKubernetesResourceWithPreconditions sends DeleteOptions body through kubectl proxy and rejects HTTP 409', async () => {
  const requests = [];
  let proxyArgs;
  const makeFakeProxy = () => {
    const p = new EventEmitter();
    p.exitCode = null;
    p.signalCode = null;
    p.kill = () => {
      p.exitCode = 0;
      p.emit('close');
    };
    return p;
  };
  await deleteKubernetesResourceWithPreconditions({
    context: 'selected-context',
    root,
    apiPath: '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock',
    uid: 'lease-uid-1',
    resourceVersion: '42',
    resourceLabel: 'p1-db-maintenance-lock',
  }, {
    availableLoopbackPort: async () => 54321,
    spawn: (_cmd, args) => {
      proxyArgs = args;
      return makeFakeProxy();
    },
    fetch: async (url, init = {}) => {
      requests.push({ url, init });
      return new Response('{}', { status: 200 });
    },
  });
  assert.deepEqual(proxyArgs, ['proxy', '--port=54321', '--address=127.0.0.1', '--context', 'selected-context']);
  const deleteReq = requests.find(r => r.init.method === 'DELETE');
  assert.ok(deleteReq);
  assert.equal(deleteReq.url,
    'http://127.0.0.1:54321/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock');
  assert.deepEqual(JSON.parse(deleteReq.init.body), {
    apiVersion: 'v1',
    kind: 'DeleteOptions',
    preconditions: { uid: 'lease-uid-1', resourceVersion: '42' },
  });

  await assert.rejects(
    deleteKubernetesResourceWithPreconditions({
      baseArgs: ['--kubeconfig', 'p0-kubeconfig'],
      root,
      apiPath: '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock',
      uid: 'lease-uid-1',
      resourceVersion: '42',
      resourceLabel: 'p1-db-maintenance-lock',
    }, {
      availableLoopbackPort: async () => 54322,
      spawn: (_cmd, args) => {
        proxyArgs = args;
        return makeFakeProxy();
      },
      fetch: async (_url, init = {}) => new Response('{}', { status: init.method === 'DELETE' ? 409 : 200 }),
    }),
    /HTTP 409/);
  assert.deepEqual(proxyArgs, ['proxy', '--port=54322', '--address=127.0.0.1', '--kubeconfig', 'p0-kubeconfig']);
});

test('v9: deployKustomize Lease release proves UID and resourceVersion preconditions', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    for (const mismatch of ['uid', 'resourceVersion']) {
      const calls = [];
      const baseExec = fakeExec(calls);
      await assert.rejects(
        realDeployKustomize({ imageManifest: manifestPath }, {
          root: directory,
          exec: baseExec,
          deleteMaintenanceLease: async ({ apiPath, uid, resourceVersion }) => {
            assert.equal(apiPath, '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock');
            assert.ok(uid && resourceVersion);
            throw new Error(`Conflict: ${mismatch} mismatch`);
          },
          sleep: async () => {},
          verifyImageCapability: async () => {},
          log() {},
        }),
        /Could not release p1-db-maintenance-lock/);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize recovers scaled-down projects-config when consumeGuardPending write fails only if Pair A is proven', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    for (const pairAProven of [true, false]) {
      const calls = [];
      const baseExec = fakeExec(calls);
      const exec = (cmd, args, opts) => {
        if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
          return {
            status: 0,
            stdout: JSON.stringify({
              kind: 'Deployment',
              metadata: { name: 'projects-config', namespace: 'agentweaver' },
              spec: {
                replicas: 2,
                selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
                template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
              },
            }),
            stderr: '',
          };
        }
        if (cmd === 'kubectl' && args[0] === 'create' &&
            (opts?.input ?? '').includes('projectsConfig.consumeGuardPending')) {
          return { status: 1, stdout: '', stderr: 'simulated pending write failure' };
        }
        return baseExec(cmd, args, opts);
      };
      const verifyImageCapability = async ({ image, capability }) => {
        if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
          throw new Error('pre-20261128012056 image');
        }
      };
      await assert.rejects(
        realDeployKustomize({ imageManifest: manifestPath }, {
          root: directory,
          exec,
          deleteMaintenanceLease: baseExec.deleteMaintenanceLease,
          sleep: async () => {},
          verifyImageCapability,
          verifyExactPairA: async () => pairAProven,
          log() {},
        }),
        /simulated pending write failure/);
      const restored = calls.some(c =>
        c.command === 'kubectl' && c.args[0] === 'scale' &&
        c.args[1] === 'deployment/projects-config' && c.args.includes('--replicas=2'));
      assert.equal(restored, pairAProven);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize interrupted retry at zero replicas reads saved annotation and restores replicas', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));
    const calls = [];
    const baseExec = fakeExec(calls);
    const exec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: {
              name: 'projects-config',
              namespace: 'agentweaver',
              annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
            },
            spec: {
              replicas: 0,
              selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/projects-config-migration-'))) {
        return { status: 1, stdout: '', stderr: 'job wait failed' };
      }
      return baseExec(cmd, args, opts);
    };
    const verifyImageCapability = async ({ image, capability }) => {
      if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
        throw new Error('pre-20261128012056 image');
      }
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec,
        deleteMaintenanceLease: baseExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability,
        verifyExactPairA: async () => true,
        log() {},
      }),
      /projects-config-migration-.* failed/);
    assert.ok(calls.some(c =>
      c.command === 'kubectl' && c.args[0] === 'scale' &&
      c.args[1] === 'deployment/projects-config' && c.args.includes('--replicas=2')));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize propagates retain-Lease to heldMaintenanceRunId owner and clears inherited consumeGuardPending on Pair A recovery', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));

    // 1. Propagate onRetainLease when heldMaintenanceRunId is set and Job cleanup fails.
    const retainCalls = [];
    const baseRetainExec = fakeExec(retainCalls);
    const retainExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
        return { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'outer-run-123' } }), stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'delete' && args[1] === 'job') {
        return { status: 1, stdout: '', stderr: 'delete job failed' };
      }
      return baseRetainExec(cmd, args, opts);
    };
    let retainedNotified = false;
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: retainExec,
        heldMaintenanceRunId: 'outer-run-123',
        onRetainLease: () => { retainedNotified = true; },
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /Could not clean up migration Job/);
    assert.equal(retainedNotified, true);

    // 2. Clear inherited consumeGuardPending on Pair A recovery even when this run's Pending patch fails.
    const recoverCalls = [];
    const baseRecoverExec = fakeExec(recoverCalls);
    const recoverExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state') {
        return {
          status: 0,
          stdout: JSON.stringify({ data: { 'projectsConfig.consumeGuardPending': 'true' } }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: {
              name: 'projects-config',
              namespace: 'agentweaver',
              annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
            },
            spec: {
              replicas: 0,
              selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'patch' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state' &&
          args.join(' ').includes('"projectsConfig.consumeGuardPending":"true"')) {
        return { status: 1, stdout: '', stderr: 'simulated pending write failure' };
      }
      return baseRecoverExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: recoverExec,
        deleteMaintenanceLease: baseRecoverExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async ({ image, capability }) => {
          if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
            throw new Error('pre-20261128012056 image');
          }
        },
        verifyExactPairA: async () => true,
        log() {},
      }),
      /simulated pending write failure/);
    const clearIdx = recoverCalls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'patch' &&
      c.args[2] === 'p1-db-maintenance-state' && c.args.join(' ').includes('"projectsConfig.consumeGuardPending":null'));
    const restoreIdx = recoverCalls.findIndex(c => c.command === 'kubectl' && c.args[0] === 'scale' &&
      c.args[1] === 'deployment/projects-config' && c.args.includes('--replicas=2'));
    assert.ok(clearIdx >= 0 && clearIdx < restoreIdx, JSON.stringify({ clearIdx, restoreIdx }));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: deployKustomize cleans up Job on consumeGuardRequired failure and retains Lease on Pair A delete failure or restore timeout', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));

    // 1. consumeGuardRequired write failure still deletes completed migration Job.
    const reqFailCalls = [];
    const baseReqExec = fakeExec(reqFailCalls);
    const reqFailExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && (args[0] === 'create' || args[0] === 'patch') &&
          ((opts?.input ?? '').includes('consumeGuardRequired') || args.join(' ').includes('consumeGuardRequired'))) {
        return { status: 1, stdout: '', stderr: 'simulated required write failure' };
      }
      return baseReqExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: reqFailExec,
        deleteMaintenanceLease: baseReqExec.deleteMaintenanceLease,
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /simulated required write failure/);
    assert.ok(reqFailCalls.some(c => c.command === 'kubectl' && c.args[0] === 'delete' &&
      c.args[1] === 'job' && c.args[2]?.startsWith('projects-config-migration-')));

    // 2. Pair A verifier Job delete failure retains Lease and notifies outer owner.
    const pairAFailCalls = [];
    const basePairAExec = fakeExec(pairAFailCalls);
    const pairAFailExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
        return { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'outer-run-123' } }), stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/projects-config-migration-'))) {
        return { status: 1, stdout: '', stderr: 'job wait failed' };
      }
      if (cmd === 'kubectl' && args[0] === 'logs' && args.some(a => a.startsWith('job/projects-config-paira-'))) {
        return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'delete' && args[1] === 'job' && args[2]?.startsWith('projects-config-paira-')) {
        return { status: 1, stdout: '', stderr: 'delete paira job failed' };
      }
      return basePairAExec(cmd, args, opts);
    };
    let pairARetained = false;
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: pairAFailExec,
        heldMaintenanceRunId: 'outer-run-123',
        onRetainLease: () => { pairARetained = true; },
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /Could not clean up Pair A verification Job/);
    assert.equal(pairARetained, true);

    // 3. Replica restore readiness timeout retains Lease and notifies outer owner.
    const rolloutCalls = [];
    const baseRolloutExec = fakeExec(rolloutCalls);
    const rolloutExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
        return { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'outer-run-123' } }), stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'Deployment',
            metadata: {
              name: 'projects-config',
              namespace: 'agentweaver',
              annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
            },
            spec: {
              replicas: 0,
              selector: { matchLabels: { 'app.kubernetes.io/name': 'projects-config' } },
              template: { spec: { containers: [{ image: `${REGISTRY}/agentweaver.projects.config@sha256:${'2'.repeat(64)}` }] } },
            },
          }),
          stderr: '',
        };
      }
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/projects-config-migration-'))) {
        return { status: 1, stdout: '', stderr: 'job wait failed' };
      }
      if (cmd === 'kubectl' && args[0] === 'rollout' && args[1] === 'status') {
        return { status: 1, stdout: '', stderr: 'timed out waiting for rollout' };
      }
      return baseRolloutExec(cmd, args, opts);
    };
    let rolloutRetained = false;
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath }, {
        root: directory,
        exec: rolloutExec,
        heldMaintenanceRunId: 'outer-run-123',
        onRetainLease: () => { rolloutRetained = true; },
        sleep: async () => {},
        verifyImageCapability: async ({ image, capability }) => {
          if (capability === 'projects-config-consume-guard' && image.includes('2'.repeat(64))) {
            throw new Error('pre-20261128012056 image');
          }
        },
        verifyExactPairA: async () => true,
        log() {},
      }),
      /Deployment projects-config did not become ready after Pair A recovery/);
    assert.equal(rolloutRetained, true);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('v9: incompatible image rejects before collector reconciliation and lost wait on active Broker Job retains Lease', async () => {
  const directory = scratch();
  try {
    const manifestPath = path.join(directory, 'projects-config.manifest.json');
    writeFileSync(manifestPath, JSON.stringify({
      images: { 'agentweaver.projects.config': selectedTag },
    }));

    // 1. Rejected incompatible image leaves existing ContainerNetworkLog collector untouched.
    const collectorCalls = [];
    const baseCollectorExec = fakeExec(collectorCalls);
    const existingCollector = {
      apiVersion: 'acn.azure.com/v1alpha1',
      kind: 'ContainerNetworkLog',
      metadata: {
        name: 'agentweaver-agenthost-egress',
        uid: 'col-uid-1',
        resourceVersion: '7',
        labels: { 'agentweaver.dev/managed-by': 'agentweaver' },
      },
    };
    const collectorExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'api-resources') {
        return { status: 0, stdout: 'containernetworklogs.acn.azure.com\n', stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'containernetworklogs.acn.azure.com') {
        return { status: 0, stdout: JSON.stringify(existingCollector), stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state') {
        return {
          status: 0,
          stdout: JSON.stringify({ data: { 'projectsConfig.consumeGuardRequired': 'true' } }),
          stderr: '',
        };
      }
      return baseCollectorExec(cmd, args, opts);
    };
    let collectorDeleted = false;
    await assert.rejects(
      realDeployKustomize({ imageManifest: manifestPath, agentHostEgressAudit: 'off' }, {
        root: directory,
        exec: collectorExec,
        deleteMaintenanceLease: baseCollectorExec.deleteMaintenanceLease,
        deleteCollector: async () => { collectorDeleted = true; },
        sleep: async () => {},
        verifyImageCapability: async ({ capability }) => {
          if (capability === 'projects-config-consume-guard') throw new Error('incompatible image');
        },
        log() {},
      }),
      /incompatible image/);
    assert.equal(collectorDeleted, false, 'existing collector must remain untouched on rejected image');
    assert.equal(collectorCalls.some(c => c.command === 'kubectl' &&
      c.args[1] === 'containernetworklogs.acn.azure.com'), false);

    // 2. Identity Broker-only deploy does not acquire Project Policy Lease or invoke Project Config compatibility guard.
    const brokerManifestPath = path.join(directory, 'broker.manifest.json');
    writeFileSync(brokerManifestPath, JSON.stringify({
      images: { 'agentweaver.identity.broker': selectedTag },
    }));
    const brokerOnlyCalls = [];
    const baseBrokerOnlyExec = fakeExec(brokerOnlyCalls);
    let brokerOnlyCompatChecked = false;
    await realDeployKustomize({ imageManifest: brokerManifestPath }, {
      root: directory,
      exec: baseBrokerOnlyExec,
      sleep: async () => {},
      verifyImageCapability: async ({ capability }) => {
        if (capability === 'projects-config-consume-guard') {
          brokerOnlyCompatChecked = true;
          throw new Error('must not run on Identity Broker-only deploy');
        }
      },
      log() {},
    });
    assert.equal(brokerOnlyCompatChecked, false);
    assert.equal(brokerOnlyCalls.some(c => c.command === 'kubectl' && c.args[1] === 'lease'), false);
    assert.equal(brokerOnlyCalls.some(c => c.command === 'kubectl' && c.args[0] === 'create' &&
      c.options?.input?.trimStart().startsWith('{') && JSON.parse(c.options.input).kind === 'Lease'), false);

    // 3. Lost wait on an active Identity Broker migration Job under heldMaintenanceRunId retains Lease.
    const brokerCalls = [];
    const baseBrokerExec = fakeExec(brokerCalls);
    let brokerLeaseDeleted = false;
    let brokerRetained = false;
    const brokerExec = (cmd, args, opts) => {
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'lease' && args[2] === 'p1-db-maintenance-lock') {
        return { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'outer-run-123' } }), stderr: '' };
      }
      if (cmd === 'kubectl' && args[0] === 'wait' && args.some(a => a.startsWith('job/identity-broker-migration-'))) {
        return { status: 1, stdout: '', stderr: 'lost connection during wait' };
      }
      if (cmd === 'kubectl' && args[0] === 'get' && args[1] === 'jobs' &&
          args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'List',
            items: [{ metadata: { name: 'identity-broker-migration-active' }, status: { active: 1 } }],
          }),
          stderr: '',
        };
      }
      return baseBrokerExec(cmd, args, opts);
    };
    await assert.rejects(
      realDeployKustomize({ imageManifest: brokerManifestPath }, {
        root: directory,
        exec: brokerExec,
        heldMaintenanceRunId: 'outer-run-123',
        onRetainLease: () => { brokerRetained = true; },
        deleteMaintenanceLease: async () => { brokerLeaseDeleted = true; },
        sleep: async () => {},
        verifyImageCapability: async () => {},
        log() {},
      }),
      /Migration Job identity-broker-migration-.* failed/);
    assert.equal(brokerRetained, true);
    assert.equal(brokerLeaseDeleted, false, 'Lease must be retained while Identity Broker migration Job is still active');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
