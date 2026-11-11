import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse, stringify } from 'yaml';
import {
  assertTargetContext,
  deployKustomize,
  ENVIRONMENT_RBAC_REQUIREMENTS,
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
import { buildIdentityBrokerProviderPlan } from '../../azure/lib/identity-broker-provider-setup.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const REGISTRY = 'ghcr.io/sabbour';
const runtimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'projects-config-runtime-config.json'), 'utf8',
));
const brokerRuntimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'identity-broker-runtime-config.json'), 'utf8',
));
const brokerRuntimeConfigName = 'identity-broker-runtime-config-v3';
const readYaml = relativePath => parse(readFileSync(path.join(root, relativePath), 'utf8'));
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
  'events-and-sessions-migration', 'projects-config-migration',
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
  liveRuntimeConfig = runtimeConfig, denyRuntimeConfigDelete = false,
  liveBrokerRuntimeConfig = brokerRuntimeConfig, missingBrokerRuntimeConfig = false,
  brokerConfigMapReadFailure = false, brokerConfigMapReadOutput,
  brokerConfigMapCount = 1, brokerConfigMapReference = brokerRuntimeConfigName,
  liveAgentHostSettings = {}, failApplyCount = 0, remainingPodPolls = 0, podList, state,
  denyEnvironmentPermission, denyRuntimePermission, missingSecrets = [], secretTypes = {},
  liveStorageClass, kataNodes, clusterNodes,
  amaLogs = { metadata: { generation: 2 }, status: { observedGeneration: 2, desiredNumberScheduled: 3,
    numberReady: 3, numberAvailable: 3, updatedNumberScheduled: 3 } },
  amaLogsPods, denyKubeSystemPodList = false,
  smokePod = { status: { phase: 'Succeeded' } }, smokeLogs = 'SMOKE_OK\n', smokeCreateError,
  liveProviderSetup,
  activateProviderDuringMigration,
  selectedAgentHostImage = selectedAgentHostRegistry,
  existingAgentHostImage = existingAgentHostRegistry,
} = {}) {
  const live = state ?? { runtimeConfig: structuredClone(liveRuntimeConfig), deployments: {} };
  live.configMaps ??= {};
  const withIdentity = resource => {
    const value = structuredClone(resource);
    value.metadata.uid ??= `uid-${value.metadata.name}`;
    value.metadata.resourceVersion ??= '1';
    return value;
  };
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
    if (command === 'az') return { status: 0, stdout: `${serviceClientId}\n`, stderr: '' };
    if (command !== 'kubectl') return { status: 1, stdout: '', stderr: `unexpected command ${command}` };
    if (args[0] === 'config') return { status: 0, stdout: 'selected-context\n', stderr: '' };
    if (args[0] === 'auth') {
      if (args.some(arg => arg === '--resource-name' || arg.startsWith('--resource-name='))) {
        return { status: 1, stdout: '', stderr: 'error: unknown flag: --resource-name' };
      }
      const isEnvironmentCheck = args.includes('system:serviceaccount:agentweaver:environment');
      const subresource = args.find(arg => arg.startsWith('--subresource='));
      const permission = `${args[2]} ${args[3]}${subresource ? ` ${subresource}` : ''}`;
      const denied = denyRuntimeConfigDelete && args[2] === 'delete' &&
        args[3] === 'configmap/projects-config-runtime-config' ||
        denyKubeSystemPodList && permission === 'list pods' && args.includes('kube-system') ||
        isEnvironmentCheck && denyEnvironmentPermission === permission ||
        !isEnvironmentCheck && denyRuntimePermission === permission;
      return { status: 0, stdout: denied ? 'no\n' : 'yes\n', stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'configmap') {
      const name = args[2];
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
      return { status: 0, stdout: JSON.stringify(liveDeployment(args[2])), stderr: '' };
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
        return { status: 0, stdout: `---\n${JSON.stringify(job('identity-broker-migration'))}`, stderr: '' };
      }
      return { status: 1, stdout: '', stderr: `unexpected kustomize path ${relative}` };
    }
    if (args[0] === 'create') {
      const resource = parse(options.input);
      if (resource.kind === 'List' && smokeCreateError) {
        return { status: 1, stdout: '', stderr: smokeCreateError };
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
    if (args[0] === 'apply') {
      if (failedApplies > 0) {
        failedApplies -= 1;
        return { status: 1, stdout: '', stderr: 'simulated workload apply failure' };
      }
      const resources = parse(options.input).items;
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
  return exec;
}

function kubernetesWriteCalls(calls) {
  const writes = new Set(['create', 'delete', 'apply', 'scale', 'annotate', 'patch', 'replace']);
  return calls.filter(call => call.command === 'kubectl' && writes.has(call.args[0]));
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
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6').length, 4);
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
    "matrix.mode == 'web'",
  ]);
  for (const { with: options } of dockerSteps) {
    assert.match(options.tags, /:sha-\$\{\{ steps\.source\.outputs\.sha \}\}$/);
    for (const label of labels) assert.ok(options.labels.split('\n').includes(label), label);
  }
  const webBuild = dockerSteps.find(({ if: condition }) => condition === "matrix.mode == 'web'");
  assert.ok(webBuild.with['build-args'].includes('GIT_SHA=${{ steps.source.outputs.sha }}'));
  const publish = steps.find(({ run = '' }) => run.includes('PublishContainer'));
  assert.equal(publish.if, "matrix.mode == 'dotnet'");
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
    "matrix.id == 'Agentweaver.AgentHost' || (matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'docker')");
  const orchestratorDocker = steps.find(({ name }) => name === 'Build and push Orchestrator');
  assert.equal(orchestratorDocker.if,
    "matrix.id == 'Agentweaver.Orchestrator' && steps.orchestrator-mode.outputs.mode == 'docker'");
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
    const firstWrite = calls.findIndex(call => call.command === 'kubectl' &&
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
    ['missing live map', { missingBrokerRuntimeConfig: true }, /Live ConfigMap\/identity-broker-runtime-config-v3 is missing/],
    ['read failure', { brokerConfigMapReadFailure: true }, /Could not read ConfigMap\/identity-broker-runtime-config-v3/],
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
    assert.equal(createdJobs.every(value => value.spec.ttlSecondsAfterFinished === 3600), true);
    assert.equal(createdJobs.every(value => value.spec.template.spec.containers[0].image.startsWith(`${REGISTRY}/`)), true);
    assert.equal(createdJobs[1].spec.template.spec.containers[0].env[0].value, serviceClientId);
    const projectsConfigJob = createdJobs[4];
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
    const firstJob = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create');
    const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const brokerPatch = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'patch' && call.args[1] === 'deployment/identity-broker');
    const rollouts = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout');
    assert.ok(render >= 0 && render < firstJob && firstJob < apply && apply < brokerPatch);
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
      assert.equal(calls.some(call => call.command === 'kubectl' &&
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
      ['identity-broker-migration']);
    assert.equal(calls.some(call => call.command === 'az'), false);
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
  try {
    await assert.rejects(deployKustomize({ imageTag: selectedTag }, options),
      /Workload apply failed: simulated workload apply failure/);

    const scaleDown = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'scale' &&
      call.args[1] === 'deployment/environment' && call.args.includes('--replicas=0'));
    const podWait = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'get' &&
      call.args[1] === 'pods' && call.args.includes('--selector'));
    const deletions = calls.map((call, index) => ({ call, index }))
      .filter(({ call }) => call.command === 'kubectl' && call.args[0] === 'delete' &&
        call.args[1] === '--raw');
    const creations = calls.map((call, index) => ({ call, index }))
      .filter(({ call }) => call.command === 'kubectl' && call.args[0] === 'create' &&
        parse(call.options.input).kind === 'ConfigMap');
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
    await deployKustomize({ imageTag: selectedTag }, options);
    const retry = calls.slice(beforeRetry);
    assert.equal(retry.some(call => call.command === 'kubectl' && call.args[0] === 'delete' &&
      call.args[1] === '--raw'), false);
    assert.equal(retry.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap'), false);
    assert.deepEqual(retry.filter(call => call.command === 'kubectl' && call.args[0] === 'scale')
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
      !isSmokeCall(call)), false);
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
      ['job', 'pod'].includes(call.args[1]) && !isSmokeCall(call)), false);
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
          call.args[1] === 'pods' && call.args.includes('--selector'));
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
        ['job', 'pod'].includes(call.args[1])), false);
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
      call.args[1] === 'pods' && call.args.includes('--selector'));
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
        call.args[1] === 'pods' && call.args.includes('--selector'));
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
      assert.equal(calls.some(call => call.command === 'kubectl' &&
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
      assert.equal(calls.some(call => call.command === 'kubectl' &&
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
    assert.equal(calls.some(call => call.command === 'kubectl' &&
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
  ];
  try {
    for (const [settings, expected] of failures) {
      const calls = [];
      await assert.rejects(deployKustomize({ imageTag: selectedTag }, {
        root: directory, exec: fakeExec(calls, settings), log() {},
      }), expected);
      assert.equal(calls.some(call => call.command === 'kubectl' &&
        ['annotate', 'create', 'delete', 'apply', 'scale', 'patch'].includes(call.args[0])), false);
    }
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

test('deploy creates only a missing product StorageClass before migrations', async () => {
  const directory = scratch();
  try {
    const calls = [];
    await deployKustomize({ imageTag: selectedTag }, {
      root: directory, exec: fakeExec(calls, { liveStorageClass: null }), log() {},
    });
    const creates = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create');
    const storageClass = parse(creates[0].options.input);
    assert.equal(storageClass.kind, 'StorageClass');
    assert.equal(storageClass.metadata.name, 'agentweaver-workspace-azurefile');
    assert.equal(creates[0].args.includes('--namespace'), false);
    assert.equal(creates.filter(call => parse(call.options.input).kind === 'StorageClass').length, 1);

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
      /no HTTPS response from BYOK endpoint demo\.openai\.azure\.com \(DNS, connection, or TLS failure/],
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
