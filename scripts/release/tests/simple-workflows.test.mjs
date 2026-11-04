import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse, stringify } from 'yaml';
import {
  deployKustomize,
  ENVIRONMENT_RBAC_REQUIREMENTS,
  parseDeployKustomizeArgs,
  parseImageManifest,
} from '../deploy-kustomize.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const REGISTRY = 'ghcr.io/sabbour';
const runtimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'projects-config-runtime-config.json'), 'utf8',
));
const readYaml = relativePath => parse(readFileSync(path.join(root, relativePath), 'utf8'));
const agentHostRuntimeConfig = readYaml('deploy/k8s/profiles/agenthost/runtime-config.example.yaml');
const environmentRuntimeConfig = JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', 'environment-runtime-config.json'), 'utf8',
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

function renderedRoot(kustomizationPath) {
  const source = parse(readFileSync(kustomizationPath, 'utf8'));
  assert.equal(source.images.length, 10);
  assert.equal(source.images.every(image => /^sha256:[a-f0-9]{64}$/.test(image.digest)), true);
  const resources = serviceNames.map(name => workloadSource(kustomizationPath, name));
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
    stringify(runtimeConfig),
    stringify(environmentRuntimeConfig),
    stringify(agentHostRuntimeConfig),
    ...resources.map(stringify),
  ].join('---\n');
}

function fakeExec(calls, {
  failedJob, failedRollout, missingProjectsConfigClientId, liveImageRefs, distinctDigests = false,
  liveRuntimeConfig = runtimeConfig, denyRuntimeConfigDelete = false,
  liveAgentHostSettings = {}, failApplyCount = 0, remainingPodPolls = 0, state,
  denyEnvironmentPermission, denyRuntimePermission, missingAgentHostTls = false,
  wrongAgentHostTlsType = false,
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
  let podPolls = remainingPodPolls;
  let failedApplies = failApplyCount;
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
            : existingTag;
          if (reference === null) {
            if (container.image?.startsWith(prefix)) container.image = undefined;
          } else if (container.image?.startsWith(prefix)) container.image = `${prefix}${reference}`;
          for (const setting of container.env ?? []) {
            if (reference === null) {
              if (setting.value?.startsWith(prefix)) setting.value = undefined;
            } else if (setting.value?.startsWith(prefix)) setting.value = `${prefix}${reference}`;
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
      const isEnvironmentCheck = args.includes('system:serviceaccount:agentweaver:environment');
      const permission = `${args[2]} ${args[3]}`;
      const denied = denyRuntimeConfigDelete && args[2] === 'delete' &&
        args[3] === 'configmap/projects-config-runtime-config' ||
        isEnvironmentCheck && denyEnvironmentPermission === permission ||
        !isEnvironmentCheck && denyRuntimePermission === permission;
      return { status: 0, stdout: denied ? 'no\n' : 'yes\n', stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'configmap') {
      const name = args[2];
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
      return {
        status: 0,
        stdout: missingAgentHostTls
          ? 'No resources found in agentweaver namespace.\n'
          : `NAME           TYPE\nagenthost-tls   ${wrongAgentHostTlsType ? 'Opaque' : 'kubernetes.io/tls'}\n`,
        stderr: '',
      };
    }
    if (args[0] === 'get' && args[1] === 'pods') {
      const items = podPolls > 0 ? [{ metadata: { name: 'projects-config-0' } }] : [];
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
    if (args[0] === 'get') {
      const name = args[2];
      return { status: 0, stdout: JSON.stringify({ kind: args[1], metadata: { name } }), stderr: '' };
    }
    if (args[0] === 'kustomize') {
      const relative = path.relative(calls[0].options.cwd, args[1]).replaceAll('\\', '/');
      if (relative === 'deploy/k8s') {
        calls.at(-1).renderedImages = parse(readFileSync(path.join(args[1], 'kustomization.yaml'), 'utf8')).images;
        return { status: 0, stdout: renderedRoot(path.join(args[1], 'kustomization.yaml')), stderr: '' };
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
      return name.startsWith(failedJob ?? '\u0000')
        ? { status: 1, stdout: '', stderr: 'job failed' }
        : { status: 0, stdout: 'condition met\n', stderr: '' };
    }
    if (args[0] === 'logs') return { status: 0, stdout: 'migration failed safely\n', stderr: '' };
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

test('v1 image workflow pushes the ten existing Kustomize images directly', () => {
  const workflow = readYaml('.github/workflows/v1-integration-images.yml');
  const entries = workflow.jobs['build-and-push'].strategy.matrix.include;
  const overlay = readYaml('deploy/k8s/kustomization.yaml');

  assert.deepEqual(workflow.on.push.branches, ['v1']);
  for (const input of ['.github/workflows/v1-integration-images.yml', 'Directory.Build.*', 'releases/foundation.json'])
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

  const steps = workflow.jobs['build-and-push'].steps;
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6').length, 3);
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6')
    .every(({ with: options }) => options.provenance === false), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('PublishContainer')), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('imagetools inspect')), true);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);
  assert.equal(JSON.stringify(workflow).includes('source_sha'), false);

  // Every image must carry the exact source commit, Git tree, and manifest component version.
  const identity = steps.findIndex(({ id }) => id === 'source');
  assert.ok(identity >= 0 && identity < steps.findIndex(({ uses = '' }) => uses === 'docker/build-push-action@v6'));
  assert.equal(steps[identity].env.COMPONENT_ID, '${{ matrix.id }}');
  assert.match(steps[identity].run, /git rev-parse --verify "\$\{GITHUB_SHA\}\^\{tree\}"/);
  assert.match(steps[identity].run, /select\(\.id == \$id\) \| \.version\] \| if length == 1/);
  assert.match(steps[identity].run, /releases\/foundation\.json/);
  for (const output of [
    'echo "sha=${GITHUB_SHA}"',
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
    "matrix.id == 'Agentweaver.AgentHost'", "matrix.id == 'Agentweaver.Identity.Broker'", "matrix.mode == 'web'",
  ]);
  for (const { with: options } of dockerSteps)
    for (const label of labels) assert.ok(options.labels.split('\n').includes(label), label);
  const publish = steps.find(({ run = '' }) => run.includes('PublishContainer'));
  assert.equal(publish.if, "matrix.mode == 'dotnet'");
  for (const property of [
    '-p:AgentweaverImageSourceSha=${{ steps.source.outputs.sha }}',
    '-p:AgentweaverImageSourceTree=${{ steps.source.outputs.tree }}',
    '-p:AgentweaverImageVersion=${{ steps.source.outputs.version }}',
  ]) assert.ok(publish.run.includes(property), property);
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
    assert.deepEqual(
      calls.filter(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
        call.args[1] === 'can-i' && call.args.includes('system:serviceaccount:agentweaver:environment'))
        .map(({ args }) => {
          const resourceName = args.find(arg => arg.startsWith('--resource-name='))?.split('=')[1];
          return `${args[2]} ${args[3]}${resourceName ? `/${resourceName}` : ''}` +
            `${args.includes('--namespace') ? '' : ' cluster'}`;
        }).sort(),
      ENVIRONMENT_RBAC_REQUIREMENTS.map(({ verb, resource, resourceName, cluster }) =>
        `${verb} ${resource}${resourceName ? `/${resourceName}` : ''}${cluster ? ' cluster' : ''}`).sort(),
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
    const rollouts = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout');
    assert.ok(render >= 0 && render < firstJob && firstJob < apply);
    assert.equal(apply, calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply'));
    assert.deepEqual(calls[apply].args.slice(1, 5), [
      '--filename', '-', '--context', 'selected-context',
    ]);
    const applied = parse(calls[apply].options.input);
    assert.equal(applied.kind, 'List');
    assert.equal(applied.items.some(resource => resource.kind === 'Namespace'), false);
    assert.equal(applied.items.filter(resource => resource.kind === 'Deployment').length, 9);
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
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-manifest', 'images.json', '--image-tag', selectedTag, '--execute',
  ]), /exactly one/);
  assert.throws(() => parseDeployKustomizeArgs(['--image-manifest', '--execute']), /requires a file path/);
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
        ? existingAgentHostRegistry.topDigest
        : digestForTag(existingTag), `${repository} keeps its deployed image`);
    }
    assert.equal(calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create')
      .map(call => parse(call.options.input)).some(resource => resource.kind === 'Job'), false);
    assert.equal(calls.some(call => call.command === 'az'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      ['jobs.batch', 'pods', 'pods/log'].includes(call.args[3]) &&
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
      `${REGISTRY}/agentweaver-agenthost@${existingAgentHostRegistry.topDigest}`);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__OptionsRevision,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__OptionsRevision);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__ContainerImagePlatform,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__ContainerImagePlatform);
    assert.equal(environmentSettings.Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes,
      liveAgentHostSettings.Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes);
    const agentHostConfigMap = applied.items.find(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === 'agenthost-runtime-v1');
    assert.deepEqual(JSON.parse(agentHostConfigMap.data['appsettings.json']).AgentHost.Image, {
      Digest: existingAgentHostRegistry.topDigest,
      Platform: 'linux/amd64',
      CompressedPullBytes: 987654321,
    });
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
      '2f8d3d969428d8ca3b33d1a083a2fc80da34ed54111d65eaf7a87c3a0240f053',
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
  const sleeps = [];
  writeFileSync(manifestPath, JSON.stringify({ images: { 'agentweaver-web': selectedTag } }));
  try {
    const exec = fakeExec(calls, {
      liveRuntimeConfig,
      state,
      failApplyCount: 1,
      remainingPodPolls: 1,
      distinctDigests: true,
    });
    const options = { root: directory, exec, sleep: async milliseconds => sleeps.push(milliseconds), log() {} };
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
    assert.ok(firstScaleDown < firstDelete && firstDelete < firstCreate &&
      firstCreate < firstScaleUp && firstScaleUp < firstApply);
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
      '2f8d3d969428d8ca3b33d1a083a2fc80da34ed54111d65eaf7a87c3a0240f053');
    assert.equal(state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], '3');
    const appliedResources = parse(calls[firstApply].options.input).items;
    const appliedRuntimeConfig = appliedResources.find(resource =>
      resource.kind === 'ConfigMap' && resource.metadata.name === 'projects-config-runtime-config');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Authentication__BrokerRunBindingAudience,
      'https://projects.agentweaver.io/run-binding');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__CertificatePath,
      '/var/run/projects-config-tls/tls.crt');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__KeyPath,
      '/var/run/projects-config-tls/tls.key');
    assert.equal(appliedRuntimeConfig.data.ProjectsConfig__Tls__ServerCertificateSha256,
      '2f8d3d969428d8ca3b33d1a083a2fc80da34ed54111d65eaf7a87c3a0240f053');
    const appliedProjectsDeployment = appliedResources.find(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === 'projects-config');
    assert.equal(appliedProjectsDeployment.spec.replicas, 3);
    assert.equal(appliedProjectsDeployment.spec.template.spec.containers[0].image,
      `${REGISTRY}/agentweaver.projects.config@${digestForTag(existingTag)}`);

    const callsBeforeRetry = calls.length;
    await deployKustomize({ imageManifest: manifestPath }, options);
    const retryCalls = calls.slice(callsBeforeRetry);
    assert.equal(retryCalls.some(call => call.command === 'kubectl' && call.args[0] === 'delete'), false);
    assert.equal(retryCalls.some(call => call.command === 'kubectl' && call.args[0] === 'create' &&
      parse(call.options.input).kind === 'ConfigMap'), false);
    assert.deepEqual(retryCalls.filter(call => call.command === 'kubectl' && call.args[0] === 'scale')
      .map(call => call.args[2]), ['--replicas=0', '--replicas=3']);
    assert.equal(state.deployments['projects-config'].spec.replicas, 3);
    assert.equal(state.deployments['projects-config'].metadata.annotations[restoreReplicasAnnotation], undefined);
    assert.deepEqual(sleeps, [5_000, 70_000, 70_000]);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      call.args[1] === 'can-i' && call.args[2] === 'delete' &&
      call.args[3] === 'configmap/projects-config-runtime-config'), true);
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
      permission: 'update deployments/scale',
      settings: { state: changedEnvironmentState() },
      expected: /cannot update deployments\/scale/,
    },
    {
      permission: 'patch deployments/scale',
      settings: { state: changedEnvironmentState() },
      expected: /cannot patch deployments\/scale/,
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

test('TLS metadata and effective Environment RBAC failures stop before workload effects', async () => {
  const directory = scratch();
  const failures = [
    [{ missingAgentHostTls: true }, /Secret\/agenthost-tls is missing/],
    [{ wrongAgentHostTlsType: true }, /must exist in namespace agentweaver with type kubernetes.io\/tls/],
    [{ denyEnvironmentPermission: 'get sandboxclaims.extensions.agents.x-k8s.io' },
      /lacks effective permission get sandboxclaims.extensions.agents.x-k8s.io/],
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
      assert.equal(secretReads[0].args.some(arg => arg === '--output=json'), false);
      assert.equal(secretReads[0].args.includes('--output=wide'), true);
      assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
        call.args.includes('secrets')), false);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
