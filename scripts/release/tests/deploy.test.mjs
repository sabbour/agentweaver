import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  deployRelease,
  parseArgs,
  SERVICE_NETWORK_POLICY_NAMES,
  validatePublicationReceipt,
  validatePublishedManifest,
  validateRuntimeManifest,
  validateTarget,
  verifyRegistryImage,
} from '../deploy.mjs';
import { componentImageRepository } from '../pack.mjs';
import { createRuntimeList } from '../runtime-list.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const sourceSha = 'a'.repeat(40);
const target = {
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'aw-v1-p0',
  clusterName: 'aw-v1-p0-aks',
  namespace: 'agentweaver-v1-p0',
  nodeMin: 2,
  nodeMax: 3,
};
const productServices = [
  ['Agentweaver.AgentHost', 'agenthost'],
  ['Agentweaver.Identity.Broker', 'identity-broker'],
  ['Agentweaver.EventsAndSessions', 'events-and-sessions'],
  ['Agentweaver.Gateway', 'gateway'],
  ['Agentweaver.Mcp', 'mcp'],
  ['Agentweaver.Web', 'web'],
  ['Agentweaver.Orchestrator', 'orchestrator'],
  ['Agentweaver.Environment', 'environment'],
  ['Agentweaver.Knowledge', 'knowledge'],
  ['Agentweaver.Projects.Config', 'projects-config'],
];
const deployedServices = productServices.filter(([id]) => id !== 'Agentweaver.AgentHost');

function verifiedImageDigests(manifest = releaseManifest()) {
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  return new Map(images.map((image) => [image.name, new Set([
    image.image.slice(image.image.lastIndexOf('@') + 1),
    ...(image.sandboxImageDigest ? [image.sandboxImageDigest] : []),
  ])]));
}

function imagesExpectedHost(manifest = releaseManifest()) {
  return validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha)
    .find(({ id }) => id === 'Agentweaver.AgentHost').sandboxImage;
}

function releaseManifest() {
  const manifest = JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
  manifest.stage = 'release';
  for (const component of manifest.components) {
    if (component.kind === 'service' && component.id !== 'Agentweaver.FoundationProbe') {
      const nibble = (component.id.charCodeAt(0) % 15 + 1).toString(16);
      component.imageDigest = `sha256:${nibble.repeat(64)}`;
    }
  }
  manifest.evidence = {
    sourceSha,
    deploymentSha: sourceSha,
    personaRuns: ['api', 'ui', 'mcp'].map((surface) => ({
      surface, sourceSha, runUrl: `https://github.com/sabbour/agentweaver/actions/runs/${surface.length}`,
    })),
  };
  return manifest;
}

function publicationReceipt(manifest = releaseManifest()) {
  const product = manifest.components.filter(({ id }) => id !== 'Agentweaver.FoundationProbe');
  return {
    schemaVersion: 1,
    sourceSha,
    provenanceSha256: 'b'.repeat(64),
    status: 'published',
    planned: product.map((component) => {
      const isService = component.kind === 'service';
      return {
        id: component.id,
        version: component.version,
        kind: isService ? 'image' : 'package',
        sha256: 'c'.repeat(64),
        destination: isService
          ? `ghcr.io/sabbour/${componentImageRepository(component.id)}:${component.version}`
          : 'https://nuget.pkg.github.com/sabbour/index.json',
      };
    }),
    published: product.map((component) => {
      const isService = component.kind === 'service';
      const isAgentHost = component.id === 'Agentweaver.AgentHost';
      const configDigest = `sha256:${'d'.repeat(64)}`;
      return {
        id: component.id,
        version: component.version,
        kind: isService ? 'image' : 'package',
        ...(isService
          ? {
            image: `ghcr.io/sabbour/${componentImageRepository(component.id)}@${component.imageDigest}`,
            archiveSha256: 'c'.repeat(64),
            ...(isAgentHost ? {
              ociArchiveSha256: 'e'.repeat(64),
              imageConfigDigest: configDigest,
              preparedOciMeasurement: {
                kind: 'agenthost-image',
                schemaVersion: 1,
                sourceSha,
                sourceTree: 'f'.repeat(40),
                image: {
                  digest: `sha256:${'a'.repeat(64)}`,
                  configDigest,
                  platform: 'linux/amd64',
                  version: component.version,
                  compressedPullBytes: 123456,
                },
                measurement: 'platform-manifest plus unique config and gzip-layer blob bytes',
              },
            } : {}),
          }
          : { sha256: 'c'.repeat(64), feed: 'https://nuget.pkg.github.com/sabbour/index.json' }),
      };
    }),
  };
}

function runtimeManifest(manifest = releaseManifest()) {
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  return createRuntimeList(images, target.namespace, {
    configMapName: 'projects-config-provider-catalog',
    sourceCommit: sourceSha,
  });
}

const argsFor = (overrides = {}) => [
  'v1.0.0',
  '--manifest', 'release.json',
  '--publication', 'publication.json',
  '--runtime-manifest', 'runtime.json',
  '--subscription-id', target.subscriptionId,
  '--tenant-id', target.tenantId,
  '--resource-group', target.resourceGroup,
  '--cluster', target.clusterName,
  '--namespace', target.namespace,
  '--node-min', String(target.nodeMin),
  '--node-max', String(target.nodeMax),
  ...Object.entries(overrides).flatMap(([key, value]) => [`--${key}`, value]),
];

test('P0 target and every required deployment target input are explicit and exact', () => {
  assert.deepEqual(validateTarget(target), target);
  assert.throws(() => validateTarget({ ...target, subscriptionId: '0'.repeat(36) }), /exact approved v1 P0/);
  assert.throws(() => validateTarget({ ...target, nodeMax: 4 }), /node bounds/);
  assert.throws(() => parseArgs(['v1.0.0']), /--manifest is required/);
  assert.throws(() => parseArgs(argsFor().filter((value) => value !== '--tenant-id' && value !== target.tenantId)),
    /--tenant-id is required/);
  assert.throws(() => parseArgs(argsFor({ 'subscription-id': 'wrong' })), /target.subscriptionId/);
  assert.equal(parseArgs(argsFor()).dryRun, true);
  const generatedListArgs = argsFor().filter((value, index, values) =>
    value !== '--runtime-manifest' && values[index - 1] !== '--runtime-manifest');
  assert.throws(() => parseArgs(generatedListArgs), /--provider-catalog-configmap is required/);
  generatedListArgs.push('--provider-catalog-configmap', 'projects-catalog',
    '--provider-catalog-source-commit', sourceSha);
  assert.equal(parseArgs(generatedListArgs).runtimeManifestPath, undefined);
  assert.throws(() => parseArgs([...argsFor(), '--execute', '--dry-run']), /mutually exclusive/);
});

test('a draft or altered product manifest cannot pass published-release validation', () => {
  const manifest = releaseManifest();
  assert.equal(validatePublishedManifest(manifest, { root, sourceSha }).stage, 'release');
  const draft = structuredClone(manifest);
  draft.stage = 'draft';
  delete draft.evidence;
  assert.throws(() => validatePublishedManifest(draft, { root, sourceSha }), /still draft/);
  const wrongEvidence = structuredClone(manifest);
  wrongEvidence.evidence.sourceSha = 'd'.repeat(40);
  wrongEvidence.evidence.deploymentSha = 'd'.repeat(40);
  for (const run of wrongEvidence.evidence.personaRuns) run.sourceSha = 'd'.repeat(40);
  assert.throws(() => validatePublishedManifest(wrongEvidence, { root, sourceSha }), /must match/);
  const wrongProductVersion = structuredClone(manifest);
  wrongProductVersion.components.find(({ id }) => id === 'Agentweaver.Gateway').version = '1.0.1';
  assert.throws(() => validatePublishedManifest(wrongProductVersion, { root, sourceSha }), /checked-in .*declare/);
  const probe = structuredClone(manifest);
  probe.components.find(({ id }) => id === 'Agentweaver.FoundationProbe').imageDigest = `sha256:${'e'.repeat(64)}`;
  const publishedManifest = validatePublishedManifest(probe, { root, sourceSha });
  assert.equal(publishedManifest.components.filter(({ id, kind }) =>
    id !== 'Agentweaver.FoundationProbe' && kind === 'service').length, 10);
});

test('publication provenance must prove exactly the 23 product components and all ten GHCR digests', () => {
  const manifest = releaseManifest();
  const receipt = publicationReceipt(manifest);
  const images = validatePublicationReceipt(receipt, manifest, sourceSha);
  assert.equal(images.length, 10);
  assert.ok(images.every(({ image }) => image.startsWith('ghcr.io/sabbour/agentweaver')));
  const agentHost = images.find(({ id }) => id === 'Agentweaver.AgentHost');
  assert.equal(agentHost.sandboxImage,
    `ghcr.io/sabbour/${componentImageRepository(agentHost.id)}@sha256:${'a'.repeat(64)}`);
  assert.equal(agentHost.compressedPullBytes, 123456);
  const missing = structuredClone(receipt);
  missing.published.pop();
  assert.throws(() => validatePublicationReceipt(missing, manifest, sourceSha), /exact 23-component/);
  const wrongDigest = structuredClone(receipt);
  wrongDigest.published.find(({ id }) => id === 'Agentweaver.Web').image =
    `ghcr.io/sabbour/agentweaver-web@sha256:${'f'.repeat(64)}`;
  assert.throws(() => validatePublicationReceipt(wrongDigest, manifest, sourceSha), /disagree on the published image/);
  const wrongArchive = structuredClone(receipt);
  wrongArchive.published.find(({ id }) => id === 'Agentweaver.Web').archiveSha256 = 'f'.repeat(64);
  assert.throws(() => validatePublicationReceipt(wrongArchive, manifest, sourceSha), /disagree on the published image/);
  const partial = structuredClone(receipt);
  partial.status = 'partial';
  assert.throws(() => validatePublicationReceipt(partial, manifest, sourceSha), /partial/);
  for (const [label, change] of [
    ['missing', (entry) => { delete entry.preparedOciMeasurement; }],
    ['wrong source', (entry) => { entry.preparedOciMeasurement.sourceSha = 'd'.repeat(40); }],
    ['wrong platform', (entry) => { entry.preparedOciMeasurement.image.platform = 'linux/arm64'; }],
    ['wrong config', (entry) => { entry.imageConfigDigest = `sha256:${'b'.repeat(64)}`; }],
    ['invalid byte count', (entry) => { entry.preparedOciMeasurement.image.compressedPullBytes = 0; }],
  ]) {
    const invalid = structuredClone(receipt);
    change(invalid.published.find(({ id }) => id === 'Agentweaver.AgentHost'));
    assert.throws(() => validatePublicationReceipt(invalid, manifest, sourceSha),
      /AgentHost publication receipt lacks/ , label);
  }
});

test('runtime manifest requires nine shared workloads and pins AgentHost through Environment options', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  const rendered = validateRuntimeManifest(runtimeManifest(manifest), images, target.namespace);
  assert.equal(rendered.items.length, 18);
  assert.equal(rendered.items.filter(({ kind }) => kind === 'Deployment').length, deployedServices.length);
  assert.equal(rendered.items.some(({ metadata }) => metadata.name === 'agenthost'), false);
  assert.equal(rendered.items.some(({ metadata }) => metadata.name.includes('foundation-probe')), false);
  const environment = rendered.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'environment');
  const environmentValues = new Map(environment.spec.template.spec.containers[0].env
    .map(({ name, value }) => [name, value]));
  const host = images.find(({ id }) => id === 'Agentweaver.AgentHost');
  assert.equal(environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImage'), host.sandboxImage);
  assert.equal(environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform'), 'linux/amd64');
  assert.equal(environmentValues.get(
    'Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes'), '123456');
  const item = (kind, name) => rendered.items.find((resource) =>
    resource.kind === kind && resource.metadata.name === name);
  const identity = item('Deployment', 'identity-broker').spec.template.spec.containers[0];
  const identityDeployment = item('Deployment', 'identity-broker');
  const acceptedBroker = readFileSync(path.join(root, 'deploy', 'k8s', 'base', 'identity-broker',
    'deployment.yaml'), 'utf8');
  const acceptedRunAsUser = Number(acceptedBroker.match(/runAsUser:\s*(\d+)/)?.[1]);
  const acceptedIdentityInjection = acceptedBroker.match(/azure\.workload\.identity\/use:\s*"([^"]+)"/)?.[1];
  assert.equal(identityDeployment.spec.template.spec.securityContext.runAsUser, acceptedRunAsUser);
  assert.equal(identityDeployment.spec.template.metadata.labels['azure.workload.identity/use'],
    acceptedIdentityInjection);
  assert.equal(acceptedIdentityInjection, 'true');
  assert.ok(identity.envFrom.some(({ configMapRef }) =>
    configMapRef?.name === 'identity-broker-runtime-config' && configMapRef.optional === false));
  assert.ok(item('Deployment', 'identity-broker').spec.template.spec.volumes
    .some(({ persistentVolumeClaim }) =>
    persistentVolumeClaim?.claimName === 'identity-broker-key-ring'));
  const projects = item('Deployment', 'projects-config').spec.template;
  assert.equal(projects.metadata.annotations['agentweaver.io/provider-catalog-source'], sourceSha);
  assert.ok(projects.spec.containers[0].envFrom.some(({ prefix, configMapRef }) =>
    prefix === 'ProjectsConfig__ProviderCatalog__' &&
    configMapRef?.name === 'projects-config-provider-catalog' && configMapRef.optional === false));
  const webContainer = item('Deployment', 'web').spec.template.spec.containers[0];
  assert.equal(webContainer.ports[0].containerPort, 8080);
  assert.ok(webContainer.envFrom.some(({ configMapRef }) =>
    configMapRef?.name === 'web-runtime-config' && configMapRef.optional === false));
  const mutable = structuredClone(rendered);
  mutable.items.find(({ kind, metadata }) => kind === 'Deployment' && metadata.name === 'web')
    .spec.template.spec.containers[0].image = 'ghcr.io/sabbour/agentweaver-web:1.0.0';
  assert.throws(() => validateRuntimeManifest(mutable, images, target.namespace), /exact immutable GHCR image/);
  const sharedAgentHost = structuredClone(rendered);
  const environmentDeployment = sharedAgentHost.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'environment');
  const duplicateHost = structuredClone(environmentDeployment);
  duplicateHost.metadata.name = 'agenthost';
  duplicateHost.spec.template.metadata.labels['app.kubernetes.io/name'] = 'agenthost';
  duplicateHost.spec.template.spec.serviceAccountName = 'agenthost';
  duplicateHost.spec.template.spec.containers[0].name = 'agenthost';
  duplicateHost.spec.template.spec.containers[0].image = host.image;
  sharedAgentHost.items.push(duplicateHost);
  assert.throws(() => validateRuntimeManifest(sharedAgentHost, images, target.namespace), /unsupported workload "agenthost"/);
  const wrongHostMeasurement = structuredClone(rendered);
  wrongHostMeasurement.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'environment').spec.template.spec.containers[0].env
    .find(({ name }) => name === 'Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes').value = '1';
  assert.throws(() => validateRuntimeManifest(wrongHostMeasurement, images, target.namespace), /exact measured AgentHost/);
  const missingTls = structuredClone(rendered);
  const gateway = missingTls.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'gateway');
  gateway.spec.template.spec.volumes = [];
  assert.throws(() => validateRuntimeManifest(missingTls, images, target.namespace), /TLS files must come from an existing/);
  const wrongTlsSecret = structuredClone(rendered);
  wrongTlsSecret.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'gateway')
    .spec.template.spec.volumes.find(({ name }) => name === 'tls').secret.secretName = 'web-tls';
  assert.throws(() => validateRuntimeManifest(wrongTlsSecret, images, target.namespace), /exact service TLS Secret/);
  const webHttps = structuredClone(rendered);
  const web = webHttps.items.find(({ kind, metadata }) => kind === 'Deployment' && metadata.name === 'web');
  web.spec.template.spec.containers[0].ports[0] = { name: 'https', containerPort: 8443, protocol: 'TCP' };
  assert.throws(() => validateRuntimeManifest(webHttps, images, target.namespace), /source-declared HTTP application port/);
  const route = structuredClone(rendered);
  route.items.push({ apiVersion: 'networking.k8s.io/v1', kind: 'Ingress',
    metadata: { name: 'outside', namespace: target.namespace } });
  assert.throws(() => validateRuntimeManifest(route, images, target.namespace), /only Deployment and ClusterIP Service/);
});

test('runtime manifest preserves only the existing Azure Workload Identity pod labels', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  for (const name of [
    'identity-broker', 'events-and-sessions', 'environment', 'knowledge', 'orchestrator', 'projects-config',
  ]) {
    const runtime = runtimeManifest(manifest);
    const deployment = runtime.items.find(value => value.kind === 'Deployment' && value.metadata.name === name);
    delete deployment.spec.template.metadata.labels['azure.workload.identity/use'];
    assert.throws(() => validateRuntimeManifest(runtime, images, target.namespace),
      /existing Azure Workload Identity injection setting/, name);
  }
  const unexpected = runtimeManifest(manifest);
  unexpected.items.find(value => value.kind === 'Deployment' && value.metadata.name === 'gateway')
    .spec.template.metadata.labels['azure.workload.identity/use'] = 'true';
  assert.throws(() => validateRuntimeManifest(unexpected, images, target.namespace),
    /existing Azure Workload Identity injection setting/);
});

test('runtime manifest labels only Gateway and Orchestrator as Identity Broker clients', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  const runtime = runtimeManifest(manifest);
  for (const name of ['gateway', 'orchestrator']) {
    const deployment = runtime.items.find(value => value.kind === 'Deployment' && value.metadata.name === name);
    assert.equal(deployment.spec.template.metadata.labels['agentweaver.io/identity-client'], 'true');
  }
  for (const name of deployedServices.filter(([, service]) => !['gateway', 'orchestrator'].includes(service))
    .map(([, service]) => service)) {
    const deployment = runtime.items.find(value => value.kind === 'Deployment' && value.metadata.name === name);
    assert.equal(deployment.spec.template.metadata.labels['agentweaver.io/identity-client'], undefined);
  }
  const missing = structuredClone(runtime);
  delete missing.items.find(value => value.kind === 'Deployment' && value.metadata.name === 'gateway')
    .spec.template.metadata.labels['agentweaver.io/identity-client'];
  assert.throws(() => validateRuntimeManifest(missing, images, target.namespace), /exact Identity Broker client selector/);
  const unexpected = structuredClone(runtime);
  unexpected.items.find(value => value.kind === 'Deployment' && value.metadata.name === 'web')
    .spec.template.metadata.labels['agentweaver.io/identity-client'] = 'true';
  assert.throws(() => validateRuntimeManifest(unexpected, images, target.namespace), /exact Identity Broker client selector/);
});

test('preflight requires source-owned Cilium policy names for all product workloads', () => {
  const expectedServices = productServices.map(([, name]) => name).sort();
  assert.deepEqual(Object.keys(SERVICE_NETWORK_POLICY_NAMES).sort(), expectedServices);
  assert.deepEqual(SERVICE_NETWORK_POLICY_NAMES, {
    'identity-broker': 'identity-broker-egress',
    'events-and-sessions': 'events-and-sessions-network',
    agenthost: 'agenthost-environment-ingress',
    environment: ['environment-agenthost-egress', 'environment-agenthost-ingress'],
    gateway: ['gateway-owner-network', 'gateway-api-ingress'],
    mcp: ['mcp-gateway-network', 'mcp-gateway-ingress'],
    orchestrator: 'orchestrator-owner-network',
    knowledge: 'knowledge-owner-network',
    'projects-config': 'projects-config-network',
    web: 'web-gateway-ingress',
  });
  const policy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'web-gateway-ingress.yaml'), 'utf8');
  assert.match(policy, /name: web-gateway-ingress/);
  assert.match(policy, /agentweaver\.io\/workload: web/);
  assert.match(policy, /k8s:io\.kubernetes\.pod\.namespace: CHANGEME-GATEWAY-NAMESPACE/);
  assert.match(policy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(policy, /port: "8080"/);
  assert.doesNotMatch(policy, /fromEntities:\s*-\s*world|0\.0\.0\.0\/0/);
  const gatewayPolicy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'gateway-api-ingress.yaml'), 'utf8');
  assert.match(gatewayPolicy, /name: gateway-api-ingress/);
  assert.match(gatewayPolicy, /agentweaver\.io\/workload: gateway/);
  assert.match(gatewayPolicy, /k8s:io\.kubernetes\.pod\.namespace: CHANGEME-GATEWAY-NAMESPACE/);
  assert.match(gatewayPolicy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(gatewayPolicy, /port: "8443"/);
  const mcpPolicy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'mcp-gateway-ingress.yaml'), 'utf8');
  assert.match(mcpPolicy, /name: mcp-gateway-ingress/);
  assert.match(mcpPolicy, /agentweaver\.io\/workload: mcp/);
  assert.match(mcpPolicy, /k8s:io\.kubernetes\.pod\.namespace: CHANGEME-GATEWAY-NAMESPACE/);
  assert.match(mcpPolicy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(mcpPolicy, /port: "8443"/);
});

test('runtime preflight checks shared backend certificates once after verifying required policy names', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const names = new Set(Object.values(SERVICE_NETWORK_POLICY_NAMES).flatMap(value =>
    Array.isArray(value) ? value : [value]));
  const calls = [];
  await assert.rejects(deployRelease({
    releaseTag: 'v1.0.0', target, manifest, publication,
    runtimeManifest: runtimeManifest(manifest), dryRun: true,
  }, {
    root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => verifiedImageDigests(manifest),
    verifyTarget: async () => undefined,
    run: (file, args) => {
      calls.push(args);
      if (args[0] !== 'get') return '';
      if (args[1] === 'networkpolicies.networking.k8s.io') {
        return JSON.stringify({ items: [{ metadata: { name: 'default-deny-all' } },
          { metadata: { name: 'allow-dns-egress' } }] });
      }
      if (args[1] === 'ciliumnetworkpolicies.cilium.io') {
        return JSON.stringify({ items: [...names].map(name => ({ metadata: { name } })) });
      }
      if (args[1] === 'secret' && args[2] === 'service-backend-ca') return 'Opaque:';
      return JSON.stringify({ metadata: { name: args[2], namespace: target.namespace } });
    },
  }), /exact service-backend-ca Secret must contain its public ca\.crt/);
  assert.equal(calls.filter(args => args[1] === 'secret' && args[2] === 'service-backend-ca').length, 1);
});

test('default dry-run validates and renders without issuing any external write', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const commands = [];
  let imagesVerified = 0;
  let targetVerified = 0;
  let prerequisitesVerified = 0;
  const result = await deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: sourceSha,
    dryRun: true,
  }, {
    root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => { imagesVerified++; return verifiedImageDigests(manifest); },
    verifyTarget: async () => { targetVerified++; return { exact: true }; },
    verifyPrerequisites: () => { prerequisitesVerified++; },
    run: (file, args) => {
      commands.push([file, args]);
      assert.deepEqual([file, ...args], ['kubectl', 'apply', '--dry-run=client', '--filename', '-']);
      return '';
    },
  });
  assert.equal(result.dryRun, true);
  assert.equal(result.services.length, deployedServices.length);
  assert.equal(result.perSandboxAgentHost, imagesExpectedHost(manifest));
  assert.equal(result.renderedResources, 18);
  assert.equal(imagesVerified, 1);
  assert.equal(targetVerified, 1);
  assert.equal(prerequisitesVerified, 1);
  assert.equal(commands.length, 1);
});

test('dry-run refuses a GHCR receipt that omits the measured AgentHost platform manifest', async () => {
  const manifest = releaseManifest();
  await assert.rejects(deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication: publicationReceipt(manifest),
    runtimeManifest: runtimeManifest(manifest),
    dryRun: true,
  }, {
    root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => new Map(),
    verifyTarget: async () => assert.fail('target verification must not run after a GHCR mismatch'),
    verifyPrerequisites: () => assert.fail('prerequisite verification must not run after a GHCR mismatch'),
    run: () => assert.fail('no Kubernetes command may run after a GHCR mismatch'),
  }), /does not attest the exact measured linux\/amd64 AgentHost image/);
});

test('resume rechecks publication, GHCR, target and live deployment health before success', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const runtime = runtimeManifest(manifest);
  const hash = (value) => createHash('sha256').update(JSON.stringify(value)).digest('hex');
  const verification = { release: 0, images: 0, target: 0, prerequisites: 0, live: 0, clear: 0, apply: 0 };
  const directory = path.join(root, 'scripts', 'release', 'tests', 'owned-checkpoint-fixture');
  const result = await deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    runtimeManifest: runtime,
    execute: true,
    dryRun: false,
    resume: true,
  }, {
    root,
    checkpointDirectory: directory,
    checkpointReadFile: (file) => JSON.stringify({
      version: 1,
      key: path.basename(file, '.json'),
      releaseTag: 'v1.0.0',
      sourceSha,
      target,
      manifestSha256: hash(manifest),
      runtimeManifestSha256: hash(runtime),
      publicationSha256: hash(publication),
      stages: { apply: { completed: true } },
    }),
    checkpointRm: () => { verification.clear++; },
    verifyRelease: async () => { verification.release++; return sourceSha; },
    verifyImages: async () => { verification.images++; return verifiedImageDigests(manifest); },
    verifyTarget: async () => { verification.target++; },
    verifyPrerequisites: () => { verification.prerequisites++; },
    verifyLive: async (images) => {
      verification.live++;
      assert.equal(images.length, deployedServices.length);
    },
    run: (file, args) => {
      if (args[0] === 'apply' && args[1] === '--filename') verification.apply++;
      return '';
    },
  });
  assert.equal(result.resumed, true);
  assert.deepEqual(verification, {
    release: 2,
    images: 2,
    target: 2,
    prerequisites: 1,
    live: 1,
    clear: 1,
    apply: 0,
  });
});

test('checkpoint read and write failures are surfaced as deployment failures', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const common = {
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    runtimeManifest: runtimeManifest(manifest),
    execute: true,
    dryRun: false,
  };
  const baseDependencies = {
    root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => verifiedImageDigests(manifest),
    verifyTarget: async () => undefined,
    verifyPrerequisites: () => undefined,
    run: () => '',
  };
  const readError = new Error('access denied');
  readError.code = 'EACCES';
  await assert.rejects(deployRelease({ ...common, resume: true }, {
    ...baseDependencies,
    checkpointDirectory: path.join(root, 'scripts', 'release', 'tests', 'owned-checkpoint-fixture'),
    checkpointReadFile: () => { throw readError; },
  }), /cannot read or parse deployment checkpoint.*access denied/);

  let applyCount = 0;
  await assert.rejects(deployRelease(common, {
    ...baseDependencies,
    checkpointDirectory: path.join(root, 'scripts', 'release', 'tests', 'owned-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: () => { throw new Error('disk full'); },
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'apply' && args[1] === '--filename') applyCount++;
      return '';
    },
  }), /cannot persist deployment checkpoint: disk full/);
  assert.equal(applyCount, 1);
});

test('GHCR digest verification uses registry HTTP only and accepts the verified amd64 child digest', async () => {
  const calls = [];
  const amd64Digest = `sha256:${'d'.repeat(64)}`;
  const topDigest = `sha256:${'c'.repeat(64)}`;
  const accepted = await verifyRegistryImage(
    `ghcr.io/sabbour/agentweaver-web@${topDigest}`,
    async (url, options = {}) => {
      calls.push([String(url), options]);
      if (String(url).startsWith('https://ghcr.io/token?')) {
        return { ok: true, status: 200, json: async () => ({ token: 'opaque-test-token' }) };
      }
      return {
        ok: true,
        status: 200,
        headers: { get: (name) => name.toLowerCase() === 'docker-content-digest' ? topDigest : null },
        json: async () => ({ schemaVersion: 2, manifests: [
          { digest: amd64Digest, platform: { os: 'linux', architecture: 'amd64' } },
          { digest: `sha256:${'e'.repeat(64)}`, platform: { os: 'linux', architecture: 'arm64' } },
        ] }),
      };
    },
  );
  assert.ok(accepted.has(topDigest));
  assert.ok(accepted.has(amd64Digest));
  assert.equal(calls.length, 2);
  assert.match(calls[1][0], /\/v2\/sabbour\/agentweaver-web\/manifests\/sha256:/);
  assert.equal(calls[1][1].headers.authorization, 'Bearer opaque-test-token');
  assert.ok(calls.every(([url]) => url.startsWith('https://ghcr.io/')));
});
