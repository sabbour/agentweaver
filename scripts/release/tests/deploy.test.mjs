import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  cpSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test, { after } from 'node:test';
import {
  deployRelease,
  parseArgs,
  SERVICE_NETWORK_POLICY_NAMES,
  validatePublicationReceipt,
  validatePublishedManifest,
  validateKustomizeInstall,
  validateRuntimeManifest,
  validateTarget,
  verifyAgentHostRegistryImage,
  verifyExistingP0BrokerRouting,
  verifyLiveDeployment,
  verifyRegistryImage,
  verifyAzureTarget,
  verifyBootstrapRelease,
  verifyRuntimePrerequisites,
} from '../deploy.mjs';
import { componentImageRepository, componentImageTag } from '../pack.mjs';
import {
  createKustomizeImageEntries, parseKustomizeResources, validateKustomizeImagePins,
} from '../kustomize-runtime.mjs';
import { parse as parseYaml, stringify as stringifyYaml } from 'yaml';
import {
  brokerRunBindingFingerprint, createBrokerRunBindingPolicies, createRuntimeList,
  validateBrokerRunBinding,
} from '../runtime-list.mjs';
import { PARTIAL_PUBLICATION } from '../partial-publication-contract.mjs';
import { run as runProcess } from '../../azure/lib/exec.mjs';
import {
  projectIdentityRoutingInventory, projectIdentityRoutingReadback,
} from '../../azure/lib/identity-broker-routing.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const p0SourceSha = PARTIAL_PUBLICATION.sourceSha;
const p0Manifest = JSON.parse(execFileSync('git', [
  'show', `${p0SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
const v101SourceSha = '56cf5a8f841dc22f69cd087d45f4e2882702dfb4';
const v101Manifest = JSON.parse(execFileSync('git', [
  'show', `${v101SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
const v102SourceSha = '3c834c12cf1e66bf385b3833a7f0a5cdd5782f8a';
const v102Manifest = JSON.parse(execFileSync('git', [
  'show', `${v102SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
/** The provider-catalog merge SHA is distinct from the v1.0.2 release source SHA. */
const v102ProviderCatalogSourceSha = 'b002620bd8cb53a971b6d5b082d6ab43dbf11fb7';
/** Image digests from the immutable publication receipt bound to v102SourceSha. */
const v102ImageDigests = Object.freeze({
  'Agentweaver.AgentHost': 'sha256:aa5baafb79ca1210188053834ededebe708cc27f167e5b24bab6633199e6245e',
  'Agentweaver.Identity.Broker': 'sha256:9c70e405494ae6c2eea7044dc48f97664a819658be387e17b026e436c3cba238',
  'Agentweaver.EventsAndSessions': 'sha256:0aff1a8944c037aab888dab3c5966420186ecaac6cbfd8ace09e3bf8c87986ae',
  'Agentweaver.Gateway': 'sha256:8f65db7f52ea0ed09bd84fe125da8c4ce523e93923fef48bc700a13fde731da7',
  'Agentweaver.Mcp': 'sha256:a722641fe29714797873acaf714778bab7fce76798cf2766b00955efbcbd62f4',
  'Agentweaver.Web': 'sha256:11b6fcc71c35de9a1d0fbe1ce8922493f7b401627c8b7cbddc31839a471b49d8',
  'Agentweaver.Orchestrator': 'sha256:f645ea05863bacecca730bda489cdc7ac884b08bb93001412b343adc9e420a34',
  'Agentweaver.Environment': 'sha256:7101827399e849753d4d8bc94dbbd15390fe545c5f5ce981ea5e7681d9acd0a2',
  'Agentweaver.Knowledge': 'sha256:1d6452bd908ee1f561d732d5ec38210a997b808cc753224b11ab75b6f113387f',
  'Agentweaver.Projects.Config': 'sha256:028bbfab0279db719d43379642c91b5044fdffc8dac4cb6eecf52a37acab7c67',
});
/** AgentHost's receipt-measured linux/amd64 compressed image size in bytes. */
const v102AgentHostCompressedPullBytes = 321483218;
function sourceFixtureRoot(sourceSha, manifest, prefix) {
  const fixtureRoot = mkdtempSync(path.join(tmpdir(), prefix));
  for (const file of [...manifest.components.map(component => component.project), 'apps/web/package-lock.json']) {
    const contents = execFileSync('git', ['show', `${sourceSha}:${file}`], {
      cwd: root, encoding: 'utf8',
    });
    const destination = path.join(fixtureRoot, file);
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, contents);
  }
  cpSync(path.join(root, 'deploy', 'k8s'), path.join(fixtureRoot, 'deploy', 'k8s'), { recursive: true });
  completePreprovisioningRuntimeInputs(path.join(fixtureRoot, 'deploy', 'k8s'));
  return fixtureRoot;
}
function completePreprovisioningRuntimeInputs(kubernetesRoot) {
  // Deployment-flow tests use synthetic finalized values only in their disposable source copy.
  const managedHost = 'agentweaver.0123456789abcdef.eastus2euap.aksapp.io';
  const replacements = new Map([
    ['CHANGEME-KEYVAULT-HOST', 'configured-kv-01.vault.azure.net'],
    ['CHANGEME-MANAGED-HOST', managedHost],
    ['CHANGEME-PORTAL-HOST', `portal.${managedHost}`],
    ['CHANGEME-identity-broker-client-id', '00000000-0000-4000-8000-000000000001'],
    ['CHANGEME-environment-client-id', '00000000-0000-4000-8000-000000000002'],
    ['CHANGEME-events-and-sessions-client-id', '00000000-0000-4000-8000-000000000003'],
    ['CHANGEME-knowledge-client-id', '00000000-0000-4000-8000-000000000004'],
    ['CHANGEME-orchestrator-client-id', '00000000-0000-4000-8000-000000000005'],
    ['CHANGEME-projects-config-client-id', '00000000-0000-4000-8000-000000000006'],
  ]);
  const visit = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        visit(file);
      } else if (/\.(?:json|yaml|yml)$/.test(entry.name)) {
        let contents = readFileSync(file, 'utf8');
        for (const [placeholder, value] of replacements) contents = contents.replaceAll(placeholder, value);
        if (file.includes(`${path.sep}runtime-configs${path.sep}`)) {
          const resource = JSON.parse(contents);
          if (resource.metadata?.annotations) {
            delete resource.metadata.annotations['agentweaver.io/runtime-config-status'];
            delete resource.metadata.annotations['agentweaver.io/runtime-config-incomplete-reason'];
          }
          contents = `${JSON.stringify(resource, null, 2)}\n`;
        }
        writeFileSync(file, contents, 'utf8');
      }
    }
  };
  visit(kubernetesRoot);
}
const p0Root = sourceFixtureRoot(p0SourceSha, p0Manifest, 'agentweaver-v1-0-0-release-tests-');
const v101Root = sourceFixtureRoot(v101SourceSha, v101Manifest, 'agentweaver-v1-0-1-release-tests-');
const v102Root = sourceFixtureRoot(v102SourceSha, v102Manifest, 'agentweaver-v1-0-2-release-tests-');
after(() => {
  rmSync(p0Root, { recursive: true, force: true });
  rmSync(v101Root, { recursive: true, force: true });
  rmSync(v102Root, { recursive: true, force: true });
});
const sourceSha = 'a'.repeat(40);
const target = {
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'agentweaver',
  clusterName: 'agentweaver-aks',
  namespace: 'agentweaver',
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
  const manifest = structuredClone(p0Manifest);
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

/** Builds a test receipt; AgentHost fields can model the measured published image. */
function publicationReceipt(manifest = releaseManifest(), receiptSourceSha = sourceSha, {
  agentHostImageDigest = `sha256:${'a'.repeat(64)}`,
  agentHostCompressedPullBytes = 123456,
} = {}) {
  const product = manifest.components.filter(({ id }) => id !== 'Agentweaver.FoundationProbe');
  return {
    schemaVersion: 1,
    sourceSha: receiptSourceSha,
    provenanceSha256: 'b'.repeat(64),
    status: 'published',
    requiredPackageIds: product.filter((component) => component.kind !== 'service')
      .map((component) => component.id),
    planned: product.map((component) => {
      const isService = component.kind === 'service';
      return {
        id: component.id,
        version: component.version,
        kind: isService ? 'image' : 'package',
        sha256: 'c'.repeat(64),
        destination: isService
          ? `ghcr.io/sabbour/${componentImageRepository(component.id)}:${componentImageTag(component.version, receiptSourceSha)}`
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
                sourceSha: receiptSourceSha,
                sourceTree: 'f'.repeat(40),
                image: {
                  digest: agentHostImageDigest,
                  configDigest,
                  platform: 'linux/amd64',
                  version: component.version,
                  compressedPullBytes: agentHostCompressedPullBytes,
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

function bootstrapFixture() {
  const manifest = structuredClone(p0Manifest);
  const publishedManifest = structuredClone(manifest);
  for (const component of publishedManifest.components) {
    if (component.kind === 'service' && component.id !== 'Agentweaver.FoundationProbe') {
      const nibble = (component.id.charCodeAt(0) % 15 + 1).toString(16);
      component.imageDigest = `sha256:${nibble.repeat(64)}`;
    }
  }
  const publication = publicationReceipt(publishedManifest, PARTIAL_PUBLICATION.sourceSha);
  publication.continuation = { namespace: PARTIAL_PUBLICATION.namespace };
  return { manifest, publication, completion: structuredClone(publication) };
}

/** Creates normal-publication fixtures with optional source-bound image receipt values. */
function normalBootstrapFixture(sourceManifest = v101Manifest, receiptSourceSha = v101SourceSha, {
  imageDigests,
  agentHostCompressedPullBytes,
} = {}) {
  const manifest = structuredClone(sourceManifest);
  const publishedManifest = structuredClone(manifest);
  for (const component of publishedManifest.components) {
    if (component.kind === 'service' && component.id !== 'Agentweaver.FoundationProbe') {
      const nibble = (component.id.charCodeAt(0) % 15 + 1).toString(16);
      component.imageDigest = imageDigests?.[component.id] ?? `sha256:${nibble.repeat(64)}`;
    }
  }
  return {
    manifest,
    publication: publicationReceipt(publishedManifest, receiptSourceSha, {
      agentHostImageDigest: imageDigests?.['Agentweaver.AgentHost'],
      agentHostCompressedPullBytes,
    }),
  };
}

function publishedAgentHostFixture(mutateConfig = () => {}) {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const component = manifest.components.find(({ id }) => id === 'Agentweaver.AgentHost');
  const published = publication.published.find(({ id }) => id === 'Agentweaver.AgentHost');
  const prepared = published.preparedOciMeasurement;
  const digest = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  const config = {
    os: 'linux',
    architecture: 'amd64',
    config: {
      User: '1654:1654',
      Entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
      Labels: {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': prepared.sourceTree,
        'org.opencontainers.image.version': component.version,
      },
    },
  };
  mutateConfig(config);
  const configText = JSON.stringify(config);
  const configBytes = Buffer.from(configText);
  const configDigest = digest(configBytes);
  published.imageConfigDigest = configDigest;
  prepared.image.configDigest = configDigest;
  const layerBytes = Buffer.from([0x1f, 0x8b, 0x08, 0x00]);
  const layer = {
    mediaType: 'application/vnd.oci.image.layer.v1.tar+gzip',
    digest: digest(layerBytes),
    size: layerBytes.length,
  };
  const childText = JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: {
      mediaType: 'application/vnd.oci.image.config.v1+json',
      digest: configDigest,
      size: configBytes.length,
    },
    layers: [layer, { ...layer }],
  });
  const childDigest = digest(Buffer.from(childText));
  const topText = JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.index.v1+json',
    manifests: [{
      mediaType: 'application/vnd.oci.image.manifest.v1+json',
      digest: childDigest,
      size: Buffer.byteLength(childText),
      platform: { os: 'linux', architecture: 'amd64' },
    }],
  });
  const topDigest = digest(Buffer.from(topText));
  component.imageDigest = topDigest;
  published.image = `ghcr.io/sabbour/${componentImageRepository(component.id)}@${topDigest}`;
  const images = validatePublicationReceipt(publication, manifest, sourceSha);
  const genericIndex = JSON.stringify({
    schemaVersion: 2,
    manifests: [{
      digest: `sha256:${'e'.repeat(64)}`,
      platform: { os: 'linux', architecture: 'amd64' },
    }],
  });
  const response = (text, responseDigest) => ({
    ok: true,
    status: 200,
    headers: { get: name => name.toLowerCase() === 'docker-content-digest' ? responseDigest : null },
    text: async () => text,
    json: async () => JSON.parse(text),
  });
  const fetch = async url => {
    const value = String(url);
    if (value.startsWith('https://ghcr.io/token?')) {
      return { ok: true, status: 200, json: async () => ({ token: 'test-pull-token' }) };
    }
    if (value.endsWith(`/manifests/${topDigest}`)) return response(topText, topDigest);
    if (value.endsWith(`/manifests/${childDigest}`)) return response(childText, childDigest);
    if (value.endsWith(`/blobs/${configDigest}`)) return response(configText, null);
    const requestedDigest = value.slice(value.lastIndexOf('/') + 1);
    return response(genericIndex, requestedDigest);
  };
  return {
    manifest,
    publication,
    images,
    topDigest,
    childDigest,
    configDigest,
    configBytes,
    childText,
    fetch,
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
  '--subscription-id', target.subscriptionId,
  '--tenant-id', target.tenantId,
  '--resource-group', target.resourceGroup,
  '--cluster', target.clusterName,
  '--namespace', target.namespace,
  '--node-min', String(target.nodeMin),
  '--node-max', String(target.nodeMax),
  '--provider-catalog-configmap', 'projects-config-provider-catalog',
  '--provider-catalog-source-commit', sourceSha,
  ...Object.entries(overrides).flatMap(([key, value]) => [`--${key}`, value]),
];

const bootstrapArgsFor = (overrides = {}) => [
  '--pre-acceptance',
  'v1.0.0',
  '--manifest', 'releases/foundation.json',
  '--publication', 'publication.json',
  '--completion', 'completion.json',
  '--subscription-id', target.subscriptionId,
  '--tenant-id', target.tenantId,
  '--resource-group', target.resourceGroup,
  '--cluster', target.clusterName,
  '--namespace', target.namespace,
  '--node-min', String(target.nodeMin),
  '--node-max', String(target.nodeMax),
  '--provider-catalog-configmap', 'projects-config-provider-catalog',
  '--provider-catalog-source-commit', 'd'.repeat(40),
  ...Object.entries(overrides).flatMap(([key, value]) => [`--${key}`, value]),
];

/** Builds bootstrap args; the provider-catalog commit may differ from the release source. */
const normalBootstrapArgsFor = (
  overrides = {},
  releaseTag = 'v1.0.1',
  providerCatalogSourceSha = 'd'.repeat(40),
) => [
  '--pre-acceptance',
  releaseTag,
  '--manifest', 'releases/foundation.json',
  '--publication', 'publication.json',
  '--subscription-id', target.subscriptionId,
  '--tenant-id', target.tenantId,
  '--resource-group', target.resourceGroup,
  '--cluster', target.clusterName,
  '--namespace', target.namespace,
  '--node-min', String(target.nodeMin),
  '--node-max', String(target.nodeMax),
  '--provider-catalog-configmap', 'projects-config-provider-catalog',
  '--provider-catalog-source-commit', providerCatalogSourceSha,
  ...Object.entries(overrides).flatMap(([key, value]) => [`--${key}`, value]),
];

/** Rewrites synthetic pins by default; source-bundle tests disable rewriting to catch drift. */
function bootstrapDependencies(fixture, {
  root: fixtureRoot = p0Root,
  sourceSha: fixtureSourceSha = p0SourceSha,
  rewriteKustomizeFixture = true,
  ...overrides
} = {}) {
  if (rewriteKustomizeFixture) {
    prepareKustomizeFixture(fixture, fixtureRoot, fixtureSourceSha, 'd'.repeat(40));
  }
  const commands = [];
  const dependencies = {
    root: fixtureRoot,
    readFile: (file, encoding) => {
      if (path.resolve(file) === path.join(fixtureRoot, 'releases', 'foundation.json')) {
        return JSON.stringify(fixture.manifest);
      }
      if (path.resolve(file) === path.join(fixtureRoot, 'publication.json')) {
        return JSON.stringify(fixture.publication);
      }
      if (path.resolve(file) === path.join(fixtureRoot, 'completion.json') && fixture.completion) {
        return JSON.stringify(fixture.completion);
      }
      return readFileSync(file, encoding);
    },
    verifyBootstrapRelease: async (_tag, publication) => {
      assert.equal(publication.sourceSha, fixtureSourceSha);
      return fixtureSourceSha;
    },
    verifyCompletion: (completion, publication, source) => {
      assert.ok(fixture.completion);
      assert.deepEqual(completion, publication);
      assert.equal(source, fixtureSourceSha);
    },
    verifyImages: async (images) => new Map(images.map((image) => [
      image.name,
      new Set([
        image.image.slice(image.image.lastIndexOf('@') + 1),
        ...(image.sandboxImageDigest ? [image.sandboxImageDigest] : []),
      ]),
    ])),
    verifyTarget: async () => ({ exact: true }),
    verifyPrerequisites: () => undefined,
    run: (file, args, options) => {
      if (file === 'git' && args[0] === 'ls-tree') return 'releases/foundation.json';
      if (file === 'git' && args[0] === 'show') {
        const sourcePath = args[1].slice(fixtureSourceSha.length + 1);
        if (args[1] === `${fixtureSourceSha}:releases/foundation.json`) {
          return JSON.stringify(fixture.manifest);
        }
        return readFileSync(path.join(fixtureRoot, sourcePath.replaceAll('/', path.sep)), 'utf8');
      }
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync('kubectl', ['kustomize', args[1]], {
          cwd: fixtureRoot,
          encoding: 'utf8',
        });
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=client')) {
        commands.push({ file, args });
        return '';
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k')) return '';
      assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
    },
  };
  return { ...dependencies, ...overrides, commands };
}

// Isolate fixture image and catalog pins so release-helper tests exercise real Kustomize rendering.
function prepareKustomizeFixture(fixture, fixtureRoot, receiptSourceSha, catalogSourceCommit) {
  if (fixture.publication.sourceSha !== receiptSourceSha || fixture.publication.status !== 'published') return;
  const receiptManifest = structuredClone(fixture.manifest);
  for (const component of receiptManifest.components) {
    const image = fixture.publication.published?.find((entry) => entry.id === component.id)?.image;
    const separator = typeof image === 'string' ? image.lastIndexOf('@') : -1;
    if (component.kind === 'service' && separator > 0) {
      component.imageDigest = image.slice(separator + 1);
    }
  }
  const images = validatePublicationReceipt(fixture.publication, receiptManifest, receiptSourceSha);
  const sourceDirectory = path.join(fixtureRoot, 'deploy', 'k8s');
  const kustomizationPath = path.join(sourceDirectory, 'kustomization.yaml');
  const kustomization = parseYaml(readFileSync(kustomizationPath, 'utf8'));
  kustomization.images = createKustomizeImageEntries(images);
  writeFileSync(kustomizationPath, stringifyYaml(kustomization), 'utf8');
  const host = images.find(({ id }) => id === 'Agentweaver.AgentHost');
  const environmentPath = path.join(sourceDirectory, 'base', 'workloads', 'environment-deployment.json');
  const environment = JSON.parse(readFileSync(environmentPath, 'utf8'));
  const environmentValues = new Map(environment.spec.template.spec.containers[0].env
    .map(entry => [entry.name, entry]));
  assert.match(environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImage').value,
    /:receipt-pinned$/, 'AgentHost image source stays a Kustomize transformer target');
  environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform').value = 'linux/amd64';
  environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes').value =
    String(host.compressedPullBytes);
  writeFileSync(environmentPath, `${JSON.stringify(environment, null, 2)}\n`, 'utf8');
  const agentHostConfigPath = path.join(sourceDirectory, 'profiles', 'agenthost', 'runtime-config.example.yaml');
  const agentHostConfig = parseYaml(readFileSync(agentHostConfigPath, 'utf8'));
  const agentHostSettings = JSON.parse(agentHostConfig.data['appsettings.json']);
  agentHostSettings.AgentHost.Image.Digest = host.sandboxImageDigest;
  agentHostSettings.AgentHost.Image.Platform = 'linux/amd64';
  agentHostSettings.AgentHost.Image.CompressedPullBytes = host.compressedPullBytes;
  agentHostConfig.data['appsettings.json'] = JSON.stringify(agentHostSettings, null, 2);
  writeFileSync(agentHostConfigPath, stringifyYaml(agentHostConfig), 'utf8');
  for (const name of ['knowledge', 'orchestrator', 'projects-config']) {
    const file = path.join(sourceDirectory, 'base', 'workloads', `${name}-deployment.json`);
    const workload = JSON.parse(readFileSync(file, 'utf8'));
    workload.spec.template.metadata.annotations['agentweaver.io/provider-catalog-source'] =
      catalogSourceCommit;
    writeFileSync(file, `${JSON.stringify(workload, null, 2)}\n`, 'utf8');
  }
}

test('P0 target and every required deployment target input are explicit and exact', () => {
  assert.deepEqual(validateTarget(target), target);
  assert.throws(() => validateTarget({ ...target, subscriptionId: '0'.repeat(36) }), /exact approved v1 P0/);
  assert.throws(() => validateTarget({ ...target, nodeMax: 4 }), /node bounds/);
  assert.throws(() => parseArgs(['v1.0.0']), /--manifest is required/);
  assert.throws(() => parseArgs(argsFor().filter((value) => value !== '--tenant-id' && value !== target.tenantId)),
    /--tenant-id is required/);
  assert.throws(() => parseArgs(argsFor({ 'subscription-id': 'wrong' })), /target.subscriptionId/);
  assert.equal(parseArgs(argsFor()).dryRun, true);
  const missingCatalogArgs = argsFor().filter((value, index, values) =>
    !['--provider-catalog-configmap', '--provider-catalog-source-commit'].includes(value) &&
    !['--provider-catalog-configmap', '--provider-catalog-source-commit'].includes(values[index - 1]));
  assert.throws(() => parseArgs(missingCatalogArgs), /--provider-catalog-configmap is required/);
  assert.equal(parseArgs(argsFor()).providerCatalogConfigMapName, 'projects-config-provider-catalog');
  assert.throws(() => parseArgs([...argsFor(), '--execute', '--dry-run']), /mutually exclusive/);
});

test('pre-acceptance mode is explicit and requires its exact v1.0.0 inputs', () => {
  const parsed = parseArgs(bootstrapArgsFor());
  assert.equal(parsed.preAcceptance, true);
  assert.equal(parsed.releaseTag, 'v1.0.0');
  assert.equal(parsed.dryRun, true);
  assert.throws(() => parseArgs(bootstrapArgsFor({ 'cluster': 'other-cluster' })), /exact approved v1 P0/);
  assert.throws(() => parseArgs(bootstrapArgsFor().filter((value, index, values) =>
    value !== '--completion' && values[index - 1] !== '--completion')), /--completion is required/);
  assert.throws(() => parseArgs([...bootstrapArgsFor(), '--runtime-manifest', 'runtime.json']),
    /unknown argument "--runtime-manifest"/);
  const wrongTag = bootstrapArgsFor();
  wrongTag[wrongTag.indexOf('v1.0.0')] = 'v1.0.3';
  assert.throws(() => parseArgs(wrongTag),
    /limited to the exact v1\.0\.0 and v1\.0\.1 and v1\.0\.2/);
});

test('v1.0.1 pre-acceptance requires the normal publication inputs and rejects completion proofs', () => {
  const parsed = parseArgs(normalBootstrapArgsFor());
  assert.equal(parsed.preAcceptance, true);
  assert.equal(parsed.releaseTag, 'v1.0.1');
  assert.equal(parsed.dryRun, true);
  assert.equal(parsed.completionPath, undefined);
  assert.throws(() => parseArgs([...normalBootstrapArgsFor(), '--completion', 'continuation.json']),
    /not accepted for the v1.0.1 normal publication/);
  assert.throws(() => parseArgs(normalBootstrapArgsFor({ 'runtime-manifest': 'runtime.json' })),
    /unknown argument "--runtime-manifest"/);
});

test('v1.0.2 pre-acceptance requires normal publication inputs and rejects completion proofs', () => {
  const parsed = parseArgs(normalBootstrapArgsFor({}, 'v1.0.2'));
  assert.equal(parsed.preAcceptance, true);
  assert.equal(parsed.releaseTag, 'v1.0.2');
  assert.equal(parsed.dryRun, true);
  assert.equal(parsed.completionPath, undefined);
  assert.throws(() => parseArgs(normalBootstrapArgsFor({}, 'v1.0.2').concat([
    '--completion', 'continuation.json',
  ])), /not accepted for the v1.0.2 normal publication/);
});

test('pre-acceptance dry-run accepts only the tracked draft and returns a non-release result', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const result = await deployRelease({ argv: bootstrapArgsFor() }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, PARTIAL_PUBLICATION.sourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal('acceptedRelease' in result, false);
  assert.doesNotMatch(JSON.stringify(result), /"stage":"release"/);
  assert.equal(dependencies.commands.length, 1);
  assert.deepEqual(dependencies.commands[0].args,
    ['apply', '--dry-run=client', '-k', path.join('deploy', 'k8s')]);
});

test('pre-acceptance execute and resume retain a separately labeled checkpoint', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const originalRun = dependencies.run;
  let checkpoint;
  let applies = 0;
  let liveChecks = 0;
  let clears = 0;
  dependencies.run = (file, args, options) => {
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) {
      applies++;
      return '';
    }
    return originalRun(file, args, options);
  };
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'bootstrap-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointReadFile: () => checkpoint,
    checkpointRm: () => { clears++; },
    verifyLive: async () => { liveChecks++; },
  });
  const first = await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);
  assert.equal(first.resultType, 'pre-acceptance');
  const saved = JSON.parse(checkpoint);
  assert.equal(saved.resultType, 'pre-acceptance');
  assert.equal(saved.sourceSha, PARTIAL_PUBLICATION.sourceSha);

  const resumed = await deployRelease({
    argv: [...bootstrapArgsFor(), '--execute', '--resume'],
  }, dependencies);
  assert.equal(resumed.resultType, 'pre-acceptance');
  assert.equal(resumed.resumed, true);
  assert.equal(applies, 1);
  assert.equal(liveChecks, 2);
  assert.equal(clears, 2);
});

test('release execute stops with access guidance when server-side manifest preflight is denied', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const originalRun = dependencies.run;
  let applies = 0;
  dependencies.run = (file, args, options) => {
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
      throw new Error('Kubernetes API returned Forbidden');
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) applies++;
    return originalRun(file, args, options);
  };
  await assert.rejects(deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
    /server-side preflight failed before any workload write.*outside this tooling.*Kubernetes API returned Forbidden/);
  assert.equal(applies, 0);
});

test('explicit workload deployment stops on incomplete runtime inputs before target checks or apply', async () => {
  const fixture = bootstrapFixture();
  const fixtureRoot = sourceFixtureRoot(p0SourceSha, p0Manifest, 'agentweaver-incomplete-runtime-test-');
  try {
    const runtimePath = path.join(fixtureRoot, 'deploy', 'k8s', 'runtime-configs',
      'environment-runtime-config.json');
    const runtime = JSON.parse(readFileSync(runtimePath, 'utf8'));
    runtime.metadata.annotations = {
      ...runtime.metadata.annotations,
      'agentweaver.io/runtime-config-status': 'incomplete',
      'agentweaver.io/runtime-config-incomplete-reason':
        'P1 initialization is blocked because the initializer requires an already-ready 127.0.0.1:15432 connection, but the documented bootstrap removes its temporary tunnel before returning and no documented continuation keeps or shares it. Guarded PostgreSQL bootstrap did not run because the preflight resource-inventory guard rejected an unexpected type/name before Azure what-if; the Agentweaver database and roles remain uninitialized.',
    };
    runtime.data.Environment__Authentication__Authority = 'https://CHANGEME-MANAGED-HOST/';
    writeFileSync(runtimePath, `${JSON.stringify(runtime, null, 2)}\n`, 'utf8');

    const dependencies = bootstrapDependencies(fixture, { root: fixtureRoot });
    let targetChecks = 0;
    dependencies.verifyTarget = async () => { targetChecks++; return { exact: true }; };
    await assert.rejects(
      deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
      /workload deployment is blocked until the incomplete runtime ConfigMaps are sourced: environment-runtime-config/,
    );
    assert.equal(targetChecks, 0);
    assert.equal(dependencies.commands.length, 0);
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('pre-acceptance rejects a receipt bound to another source before verification', async () => {
  const fixture = bootstrapFixture();
  fixture.publication.sourceSha = 'b'.repeat(40);
  const dependencies = bootstrapDependencies(fixture);
  await assert.rejects(deployRelease({ argv: bootstrapArgsFor() }, dependencies),
    /exact source-bound v1.0.0 published continuation receipt/);
});

test('v1.0.1 pre-acceptance dry-run uses the mixed source-bound draft and normal publication digests', async () => {
  const fixture = normalBootstrapFixture();
  const dependencies = bootstrapDependencies(fixture, {
    root: v101Root,
    sourceSha: v101SourceSha,
  });
  let completionChecks = 0;
  dependencies.verifyCompletion = () => { completionChecks++; };
  const result = await deployRelease({ argv: normalBootstrapArgsFor() }, dependencies);
  const versions = new Set(fixture.manifest.components
    .filter(({ id }) => id !== 'Agentweaver.FoundationProbe')
    .map(({ version }) => version));
  assert.equal(fixture.publication.published.filter(({ kind }) => kind === 'package').length, 13);
  assert.equal(fixture.publication.published.filter(({ kind }) => kind === 'image').length, 10);
  assert.ok(versions.size > 1);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v101SourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal(completionChecks, 0);
  assert.deepEqual(
    result.services.map(({ id, image }) => [id, image]),
    fixture.publication.published
      .filter(({ kind, id }) => kind === 'image' && id !== 'Agentweaver.AgentHost')
      .map(({ id, image }) => [id, image]),
  );
  const agentHost = fixture.publication.published.find(({ id }) => id === 'Agentweaver.AgentHost');
  assert.equal(
    result.perSandboxAgentHost,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${agentHost.preparedOciMeasurement.image.digest}`,
  );
  assert.equal('acceptedRelease' in result, false);
  assert.equal(dependencies.commands.length, 1);
  assert.deepEqual(dependencies.commands[0].args,
    ['apply', '--dry-run=client', '-k', path.join('deploy', 'k8s')]);
});

test('v1.0.2 pre-acceptance dry-run uses its exact mixed-version source and publication digests', async () => {
  const fixture = normalBootstrapFixture(v102Manifest, v102SourceSha, {
    imageDigests: v102ImageDigests,
    agentHostCompressedPullBytes: v102AgentHostCompressedPullBytes,
  });
  const kubernetesRoot = path.join(v102Root, 'deploy', 'k8s');
  const kustomization = parseYaml(readFileSync(path.join(kubernetesRoot, 'kustomization.yaml'), 'utf8'));
  const pins = new Map(kustomization.images.map(({ name, digest }) => [name, digest]));
  assert.equal(pins.size, 10);
  for (const [componentId, digest] of Object.entries(v102ImageDigests)) {
    assert.equal(
      pins.get(`ghcr.io/sabbour/${componentImageRepository(componentId)}`),
      digest,
      `${componentId} pin in the checked-in helper bundle`,
    );
  }
  const environment = JSON.parse(readFileSync(path.join(
    kubernetesRoot, 'base', 'workloads', 'environment-deployment.json',
  ), 'utf8'));
  const environmentValues = new Map(environment.spec.template.spec.containers[0].env
    .map(({ name, value }) => [name, value]));
  assert.equal(
    environmentValues.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes'),
    String(v102AgentHostCompressedPullBytes),
  );
  const agentHostConfig = parseYaml(readFileSync(path.join(
    kubernetesRoot, 'profiles', 'agenthost', 'runtime-config.example.yaml',
  ), 'utf8'));
  const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
  assert.deepEqual(agentHostImage, {
    Digest: v102ImageDigests['Agentweaver.AgentHost'],
    Platform: 'linux/amd64',
    CompressedPullBytes: v102AgentHostCompressedPullBytes,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v102Root,
    sourceSha: v102SourceSha,
    // Exercise the checked-in v1.0.2 receipt pins; synthetic rewriting would mask drift.
    rewriteKustomizeFixture: false,
  });
  const result = await deployRelease({
    argv: normalBootstrapArgsFor({}, 'v1.0.2', v102ProviderCatalogSourceSha),
  }, dependencies);
  const versions = new Set(fixture.manifest.components
    .filter(({ id }) => id !== 'Agentweaver.FoundationProbe')
    .map(({ version }) => version));
  assert.ok(versions.size > 1);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v102SourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal('acceptedRelease' in result, false);
  assert.equal(
    result.perSandboxAgentHost,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${v102ImageDigests['Agentweaver.AgentHost']}`,
  );
  assert.deepEqual(
    result.services.map(({ id, image }) => [id, image]),
    fixture.publication.published
      .filter(({ kind, id }) => kind === 'image' && id !== 'Agentweaver.AgentHost')
      .map(({ id, image }) => [id, image]),
  );
});

test('v1.0.2 pre-acceptance rejects changed-source and continuation receipts', async () => {
  const fixture = normalBootstrapFixture(v102Manifest, v102SourceSha, {
    imageDigests: v102ImageDigests,
    agentHostCompressedPullBytes: v102AgentHostCompressedPullBytes,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v102Root,
    sourceSha: v102SourceSha,
    rewriteKustomizeFixture: false,
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.2', v102ProviderCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.2 published receipt/,
  );

  fixture.publication.sourceSha = v102SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.2', v102ProviderCatalogSourceSha),
    }, dependencies),
    /v1\.0\.2 pre-acceptance bootstrap rejects continuation publication receipts/,
  );
});

test('release helper parses Kustomize output containing the managed Broker hostname', () => {
  const hostname = 'agentweaver.0123456789abcdef.eastus2euap.aksapp.io';
  const output = [
    'apiVersion: v1',
    'kind: Ingress',
    'metadata:',
    '  name: identity-broker',
    'spec:',
    '  rules:',
    `    - host: ${hostname}`,
    '',
  ].join('\n');
  const result = runProcess(process.execPath, [
    '-e', `process.stdout.write(${JSON.stringify(output)})`,
  ]);
  const resources = parseKustomizeResources(result.stdout);

  assert.equal(resources.items[0].spec.rules[0].host, hostname);
});

test('v1.0.1 pre-acceptance executes only after verifying the normal publication and keeps its result labeled', async () => {
  const fixture = normalBootstrapFixture();
  const dependencies = bootstrapDependencies(fixture, {
    root: v101Root,
    sourceSha: v101SourceSha,
  });
  const originalRun = dependencies.run;
  let applies = 0;
  dependencies.run = (file, args, options) => {
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) {
      applies++;
      return '';
    }
    return originalRun(file, args, options);
  };
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'v101-bootstrap-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: () => undefined,
    checkpointReadFile: () => { throw Object.assign(new Error('not found'), { code: 'ENOENT' }); },
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });
  const result = await deployRelease({
    argv: [...normalBootstrapArgsFor(), '--execute'],
  }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v101SourceSha);
  assert.equal(result.dryRun, false);
  assert.equal(applies, 1);
});

test('v1.0.1 rejects continuation receipts, changed source receipts, and altered tracked drafts', async () => {
  const fixture = normalBootstrapFixture();
  const dependencies = bootstrapDependencies(fixture, {
    root: v101Root,
    sourceSha: v101SourceSha,
  });
  fixture.publication.continuation = { namespace: 'unexpected' };
  await assert.rejects(deployRelease({ argv: normalBootstrapArgsFor() }, dependencies),
    /rejects continuation publication receipts/);

  delete fixture.publication.continuation;
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(deployRelease({ argv: normalBootstrapArgsFor() }, dependencies),
    /exact source-bound v1.0.1 published receipt/);

  fixture.publication.sourceSha = v101SourceSha;
  const alteredManifest = structuredClone(fixture.manifest);
  alteredManifest.components.find(({ id }) => id === 'Agentweaver.Gateway').version = '9.9.9';
  await assert.rejects(deployRelease({
    argv: normalBootstrapArgsFor(),
    manifest: alteredManifest,
  }, dependencies), /differs from releases\/foundation\.json at the published source commit/);
});

test('v1.0.1 rejects a completion proof supplied through the deployment API', async () => {
  const fixture = normalBootstrapFixture();
  const dependencies = bootstrapDependencies(fixture, {
    root: v101Root,
    sourceSha: v101SourceSha,
  });
  await assert.rejects(deployRelease({
    argv: normalBootstrapArgsFor(),
    completion: {},
  }, dependencies), /--completion is not accepted for the v1.0.1 normal publication/);
});

test('pre-acceptance rejects a missing publication receipt file', async () => {
  const fixture = bootstrapFixture();
  const args = bootstrapArgsFor();
  args[args.indexOf('publication.json')] = 'missing-publication.json';
  await assert.rejects(deployRelease({ argv: args }, bootstrapDependencies(fixture)), /ENOENT/);
});

test('pre-acceptance rejects a receipt that differs from the immutable result', async () => {
  const fixture = bootstrapFixture();
  const tagObjectSha = '1'.repeat(40);
  const resultObjectSha = '2'.repeat(40);
  const changedPublication = {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  };
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      if (endpoint.endsWith('/releases/tags/v1.0.0')) {
        return JSON.stringify({ tag_name: 'v1.0.0', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.0')}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: tagObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${tagObjectSha}`)) {
        return JSON.stringify({
          tag: 'v1.0.0',
          object: { type: 'commit', sha: PARTIAL_PUBLICATION.sourceSha },
        });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(`${PARTIAL_PUBLICATION.namespace}/result`)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: `${PARTIAL_PUBLICATION.namespace}/result`,
          object: { type: 'commit', sha: PARTIAL_PUBLICATION.sourceSha },
          message: JSON.stringify(changedPublication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  await assert.rejects(verifyBootstrapRelease('v1.0.0', fixture.publication, { root, run }),
    /differs from the immutable GitHub publication result/);
});

test('v1.0.1 verifies the exact normal publication result ref and receipt', async () => {
  const fixture = normalBootstrapFixture();
  const resultObjectSha = '2'.repeat(40);
  const resultRefName = `agentweaver-publication/${v101SourceSha}/result`;
  let tagCommitSha = v101SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.1')) {
        return JSON.stringify({ tag_name: 'v1.0.1', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.1')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v101SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.1', fixture.publication, { root, run }), v101SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.1', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v101SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.1', {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  }, { root, run }), /differs from the immutable GitHub publication result/);
});

test('v1.0.2 verifies the exact normal publication result ref and source commit', async () => {
  const fixture = normalBootstrapFixture(v102Manifest, v102SourceSha);
  const resultObjectSha = '4'.repeat(40);
  const resultRefName = `agentweaver-publication/${v102SourceSha}/result`;
  let tagCommitSha = v102SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.2')) {
        return JSON.stringify({ tag_name: 'v1.0.2', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.2')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v102SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.2', fixture.publication, { root, run }), v102SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.2', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v102SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.2', {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  }, { root, run }), /differs from the immutable GitHub publication result/);
});

test('strict release deployment still rejects a lightweight release tag', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return sourceSha;
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      if (args[1].endsWith('/releases/tags/v1.0.0')) {
        return JSON.stringify({ tag_name: 'v1.0.0', draft: false, prerelease: false });
      }
      if (args[1].endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.0')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: sourceSha } });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  await assert.rejects(deployRelease({
    argv: argsFor(),
    manifest,
    publication,
  }, {
    root: p0Root,
    run,
  }), /must be an annotated Git tag/);
});

test('a draft or altered product manifest cannot pass published-release validation', () => {
  const manifest = releaseManifest();
  assert.equal(validatePublishedManifest(manifest, { root: p0Root, sourceSha }).stage, 'release');
  const draft = structuredClone(manifest);
  draft.stage = 'draft';
  delete draft.evidence;
  assert.throws(() => validatePublishedManifest(draft, { root: p0Root, sourceSha }), /still draft/);
  const wrongEvidence = structuredClone(manifest);
  wrongEvidence.evidence.sourceSha = 'd'.repeat(40);
  wrongEvidence.evidence.deploymentSha = 'd'.repeat(40);
  for (const run of wrongEvidence.evidence.personaRuns) run.sourceSha = 'd'.repeat(40);
  assert.throws(() => validatePublishedManifest(wrongEvidence, { root: p0Root, sourceSha }), /must match/);
  const wrongProductVersion = structuredClone(manifest);
  wrongProductVersion.components.find(({ id }) => id === 'Agentweaver.Gateway').version = '1.0.1';
  assert.throws(() => validatePublishedManifest(wrongProductVersion, { root: p0Root, sourceSha }), /checked-in .*declare/);
  const probe = structuredClone(manifest);
  probe.components.find(({ id }) => id === 'Agentweaver.FoundationProbe').imageDigest = `sha256:${'e'.repeat(64)}`;
  const publishedManifest = validatePublishedManifest(probe, { root: p0Root, sourceSha });
  assert.equal(publishedManifest.components.filter(({ id, kind }) =>
    id !== 'Agentweaver.FoundationProbe' && kind === 'service').length, 10);
});

test('publication provenance proves every image and every package selected for this source', () => {
  const manifest = releaseManifest();
  const receipt = publicationReceipt(manifest);
  const completeReceipt = structuredClone(receipt);
  const images = validatePublicationReceipt(receipt, manifest, sourceSha);
  assert.equal(images.length, 10);
  assert.ok(images.every(({ image }) => image.startsWith('ghcr.io/sabbour/agentweaver')));
  const agentHost = images.find(({ id }) => id === 'Agentweaver.AgentHost');
  assert.equal(agentHost.sandboxImage,
    `ghcr.io/sabbour/${componentImageRepository(agentHost.id)}@sha256:${'a'.repeat(64)}`);
  assert.equal(agentHost.compressedPullBytes, 123456);
  const unchangedPackageIds = receipt.requiredPackageIds.slice(1);
  receipt.requiredPackageIds = receipt.requiredPackageIds.slice(0, 1);
  const packagesOmitted = structuredClone(receipt);
  packagesOmitted.planned = packagesOmitted.planned.filter(({ id }) => !unchangedPackageIds.includes(id));
  packagesOmitted.published = packagesOmitted.published.filter(({ id }) => !unchangedPackageIds.includes(id));
  assert.equal(validatePublicationReceipt(packagesOmitted, manifest, sourceSha).length, 10);
  const missingRequiredPackage = structuredClone(packagesOmitted);
  missingRequiredPackage.planned = missingRequiredPackage.planned.filter(({ kind }) => kind === 'image');
  missingRequiredPackage.published = missingRequiredPackage.published.filter(({ kind }) => kind === 'image');
  assert.throws(() => validatePublicationReceipt(missingRequiredPackage, manifest, sourceSha),
    /does not prove required package/);
  const legacyReceipt = publicationReceipt(manifest);
  delete legacyReceipt.requiredPackageIds;
  for (const entry of legacyReceipt.planned) {
    if (entry.kind === 'image') {
      const component = manifest.components.find(({ id }) => id === entry.id);
      entry.destination = `ghcr.io/sabbour/${componentImageRepository(component.id)}:${component.version}`;
    }
  }
  assert.equal(validatePublicationReceipt(legacyReceipt, manifest, sourceSha).length, 10);
  const legacyPackageMissing = structuredClone(legacyReceipt);
  legacyPackageMissing.planned = legacyPackageMissing.planned.filter(({ kind }) => kind === 'image');
  legacyPackageMissing.published = legacyPackageMissing.published.filter(({ kind }) => kind === 'image');
  assert.throws(() => validatePublicationReceipt(legacyPackageMissing, manifest, sourceSha),
    /does not prove required package/);
  const missingImage = structuredClone(packagesOmitted);
  const lastImageId = missingImage.planned.filter(({ kind }) => kind === 'image').at(-1).id;
  missingImage.planned = missingImage.planned.filter(({ id }) => id !== lastImageId);
  missingImage.published = missingImage.published.filter(({ id }) => id !== lastImageId);
  assert.throws(() => validatePublicationReceipt(missingImage, manifest, sourceSha), /every product service image/);
  const wrongDigest = structuredClone(completeReceipt);
  wrongDigest.published.find(({ id }) => id === 'Agentweaver.Web').image =
    `ghcr.io/sabbour/agentweaver-web@sha256:${'f'.repeat(64)}`;
  assert.throws(() => validatePublicationReceipt(wrongDigest, manifest, sourceSha), /disagree on the published image/);
  const wrongArchive = structuredClone(completeReceipt);
  wrongArchive.published.find(({ id }) => id === 'Agentweaver.Web').archiveSha256 = 'f'.repeat(64);
  assert.throws(() => validatePublicationReceipt(wrongArchive, manifest, sourceSha), /disagree on the published image/);
  const partial = structuredClone(completeReceipt);
  partial.status = 'partial';
  assert.throws(() => validatePublicationReceipt(partial, manifest, sourceSha), /partial/);
  for (const [label, change] of [
    ['missing', (entry) => { delete entry.preparedOciMeasurement; }],
    ['wrong source', (entry) => { entry.preparedOciMeasurement.sourceSha = 'd'.repeat(40); }],
    ['wrong platform', (entry) => { entry.preparedOciMeasurement.image.platform = 'linux/arm64'; }],
    ['wrong config', (entry) => { entry.imageConfigDigest = `sha256:${'b'.repeat(64)}`; }],
    ['invalid byte count', (entry) => { entry.preparedOciMeasurement.image.compressedPullBytes = 0; }],
  ]) {
    const invalid = structuredClone(completeReceipt);
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
    configMapRef?.name === 'identity-broker-runtime-config-v2' && configMapRef.optional === false));
  assert.ok(item('Deployment', 'identity-broker').spec.template.spec.volumes
    .some(({ persistentVolumeClaim }) =>
    persistentVolumeClaim?.claimName === 'identity-broker-key-ring'));
  const projects = item('Deployment', 'projects-config').spec.template;
  assert.equal(projects.metadata.annotations['agentweaver.io/provider-catalog-source'], sourceSha);
  assert.ok(projects.spec.containers[0].envFrom.some(({ prefix, configMapRef }) =>
    prefix === 'ProjectsConfig__ProviderCatalog__' &&
    configMapRef?.name === 'projects-config-provider-catalog' && configMapRef.optional === false));
  // Every catalog consumer shares the source commit and immutable ConfigMap.
  const knowledge = item('Deployment', 'knowledge').spec.template;
  assert.equal(knowledge.metadata.annotations['agentweaver.io/provider-catalog-source'], sourceSha);
  assert.ok(knowledge.spec.containers[0].envFrom.some(({ prefix, configMapRef }) =>
    prefix === 'ProjectsConfig__ProviderCatalog__' &&
    configMapRef?.name === 'projects-config-provider-catalog' && configMapRef.optional === false));
  const orchestrator = item('Deployment', 'orchestrator').spec.template;
  assert.equal(orchestrator.metadata.annotations['agentweaver.io/provider-catalog-source'], sourceSha);
  assert.ok(orchestrator.spec.containers[0].envFrom.some(({ prefix, configMapRef }) =>
    prefix === 'ProjectsConfig__ProviderCatalog__' &&
    configMapRef?.name === 'projects-config-provider-catalog' && configMapRef.optional === false));
  const ciliumVolume = item('Deployment', 'environment').spec.template.spec.volumes
    .find(({ name }) => name === 'cilium-options');
  assert.deepEqual(ciliumVolume.configMap, {
    name: 'environment-cilium-config',
    optional: false,
    items: [{ key: 'appsettings.Production.json', path: 'appsettings.Production.json' }],
  });
  assert.ok(item('Deployment', 'environment').spec.template.spec.containers[0].volumeMounts.some((mount) =>
    mount.name === 'cilium-options' && mount.mountPath === '/app/appsettings.Production.json' &&
    mount.subPath === 'appsettings.Production.json' && mount.readOnly === true));
  const webContainer = item('Deployment', 'web').spec.template.spec.containers[0];
  assert.equal(webContainer.ports[0].containerPort, 8080);
  assert.ok(webContainer.envFrom.some(({ configMapRef }) =>
    configMapRef?.name === 'web-runtime-config' && configMapRef.optional === false));
  const mcp = item('Deployment', 'mcp').spec.template.spec;
  assert.ok(mcp.volumes.some(volume => volume.configMap?.name === 'service-backend-ca' &&
    volume.configMap.optional === false && volume.configMap.items?.[0]?.key === 'ca.crt'));
  assert.ok(mcp.containers[0].volumeMounts.some(mount => mount.name === 'backend-ca' &&
    mount.mountPath === '/var/run/service-backend-ca' && mount.readOnly === true));
  const missingOrchestratorCatalog = structuredClone(rendered);
  const orchestratorContainer = missingOrchestratorCatalog.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'orchestrator').spec.template.spec.containers[0];
  orchestratorContainer.envFrom = orchestratorContainer.envFrom
    .filter(({ prefix }) => prefix !== 'ProjectsConfig__ProviderCatalog__');
  assert.throws(() => validateRuntimeManifest(missingOrchestratorCatalog, images, target.namespace),
    /orchestrator must load one immutable-source Projects provider catalog ConfigMap/);
  const missingKnowledgeCatalog = structuredClone(rendered);
  const knowledgeContainer = missingKnowledgeCatalog.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'knowledge').spec.template.spec.containers[0];
  knowledgeContainer.envFrom = knowledgeContainer.envFrom
    .filter(({ prefix }) => prefix !== 'ProjectsConfig__ProviderCatalog__');
  assert.throws(() => validateRuntimeManifest(missingKnowledgeCatalog, images, target.namespace),
    /knowledge must load one immutable-source Projects provider catalog ConfigMap/);
  const mismatchedOrchestratorCatalog = structuredClone(rendered);
  const mismatchedOrchestrator = mismatchedOrchestratorCatalog.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'orchestrator').spec.template;
  mismatchedOrchestrator.spec.containers[0].envFrom
    .find(({ prefix }) => prefix === 'ProjectsConfig__ProviderCatalog__').configMapRef.name = 'other-catalog';
  assert.throws(() => validateRuntimeManifest(mismatchedOrchestratorCatalog, images, target.namespace),
    /orchestrator must consume the exact same provider catalog/);
  const wrongCatalogSource = structuredClone(rendered);
  wrongCatalogSource.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'orchestrator')
    .spec.template.metadata.annotations['agentweaver.io/provider-catalog-source'] = 'e'.repeat(40);
  assert.throws(() => validateRuntimeManifest(wrongCatalogSource, images, target.namespace, undefined, {
    configMapName: 'projects-config-provider-catalog',
    sourceCommit: sourceSha,
  }), /orchestrator must consume the exact same provider catalog/);
  const missingCiliumConfig = structuredClone(rendered);
  const environmentPod = missingCiliumConfig.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'environment').spec.template.spec;
  environmentPod.volumes = environmentPod.volumes.filter(({ name }) => name !== 'cilium-options');
  assert.throws(() => validateRuntimeManifest(missingCiliumConfig, images, target.namespace),
    /Environment must mount the exact read-only Cilium options file/);
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
  const wrongMcpCa = structuredClone(rendered);
  wrongMcpCa.items.find(({ kind, metadata }) => kind === 'Deployment' && metadata.name === 'mcp')
    .spec.template.spec.volumes.find(({ name }) => name === 'backend-ca').configMap.name = 'untrusted-ca';
  assert.throws(() => validateRuntimeManifest(wrongMcpCa, images, target.namespace),
    /exact public backend CA ConfigMap/);
  const missingMcpCaMount = structuredClone(rendered);
  const missingMcpCaContainer = missingMcpCaMount.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'mcp').spec.template.spec.containers[0];
  missingMcpCaContainer.volumeMounts =
    missingMcpCaContainer.volumeMounts.filter(({ name }) => name !== 'backend-ca');
  assert.throws(() => validateRuntimeManifest(missingMcpCaMount, images, target.namespace),
    /exact public backend CA ConfigMap/);
  const nonMcpCaMount = structuredClone(rendered);
  const mcpBackendCaVolume = nonMcpCaMount.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'mcp').spec.template.spec.volumes
    .find(({ name }) => name === 'backend-ca');
  const webPodSpec = nonMcpCaMount.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'web').spec.template.spec;
  webPodSpec.volumes.push(structuredClone(mcpBackendCaVolume));
  assert.throws(() => validateRuntimeManifest(nonMcpCaMount, images, target.namespace),
    /only MCP may mount the public backend CA ConfigMap/);
  const aliasedNonMcpCa = structuredClone(rendered);
  const aliasedCaVolume = structuredClone(aliasedNonMcpCa.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'mcp').spec.template.spec.volumes
    .find(({ name }) => name === 'backend-ca'));
  aliasedCaVolume.name = 'public-backend-ca';
  const aliasedWeb = aliasedNonMcpCa.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'web').spec.template.spec;
  aliasedWeb.volumes.push(aliasedCaVolume);
  aliasedWeb.containers[0].volumeMounts.push({
    name: 'public-backend-ca',
    mountPath: '/var/run/service-backend-ca',
    readOnly: true,
  });
  assert.throws(() => validateRuntimeManifest(aliasedNonMcpCa, images, target.namespace),
    /only MCP may mount the public backend CA ConfigMap/);
  const aliasedMcpCa = structuredClone(rendered);
  const aliasedMcpPod = aliasedMcpCa.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'mcp').spec.template.spec;
  const extraCaVolume = structuredClone(aliasedMcpPod.volumes.find(({ name }) => name === 'backend-ca'));
  extraCaVolume.name = 'extra-backend-ca';
  aliasedMcpPod.volumes.push(extraCaVolume);
  aliasedMcpPod.containers[0].volumeMounts.push({
    name: 'extra-backend-ca',
    mountPath: '/var/run/extra-backend-ca',
    readOnly: false,
  });
  assert.throws(() => validateRuntimeManifest(aliasedMcpCa, images, target.namespace),
    /exact public backend CA ConfigMap/);
  const webHttps = structuredClone(rendered);
  const web = webHttps.items.find(({ kind, metadata }) => kind === 'Deployment' && metadata.name === 'web');
  web.spec.template.spec.containers[0].ports[0] = { name: 'https', containerPort: 8443, protocol: 'TCP' };
  assert.throws(() => validateRuntimeManifest(webHttps, images, target.namespace), /source-declared HTTP application port/);
  const route = structuredClone(rendered);
  route.items.push({ apiVersion: 'networking.k8s.io/v1', kind: 'Ingress',
    metadata: { name: 'outside', namespace: target.namespace } });
  assert.throws(() => validateRuntimeManifest(route, images, target.namespace), /only Deployment and ClusterIP Service/);
});

test('runtime List provider inputs match the checked-in catalog and reject unapproved values', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  const rendered = validateRuntimeManifest(runtimeManifest(manifest), images, target.namespace);
  const catalog = JSON.parse(readFileSync(path.join(root, 'deploy', 'k8s', 'provider-catalogs',
    'projects-config-provider-catalog.snapshot.json'), 'utf8')).ProjectsConfig.ProviderCatalog;
  const runtimeInputs = [
    {
      workload: 'environment',
      providerId: 'agent-sandbox',
      name: 'Environment__Sandbox__AgentSandbox__OptionsRevision',
    },
    {
      workload: 'environment',
      providerId: 'azure-files-csi',
      name: 'Environment__Storage__AzureFiles__OptionsRevision',
    },
    {
      workload: 'environment',
      providerId: 'cilium',
      name: 'Environment__NetworkPolicy__Cilium__AdapterVersion',
      catalogProperty: 'AdapterVersion',
    },
    {
      workload: 'environment',
      providerId: 'cilium',
      name: 'Environment__NetworkPolicy__Cilium__OptionsRevision',
    },
    {
      workload: 'orchestrator',
      providerId: 'agt.dotnet-yaml',
      name: 'AgentGovernance__Policy__OptionsRevision',
    },
    {
      workload: 'events-and-sessions',
      providerId: 'copilot.usage-cost',
      name: 'EventsAndSessions__Cost__Copilot__OptionsRevision',
    },
  ];
  const deployment = (resources, name) => resources.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === name);
  for (const input of runtimeInputs) {
    const container = deployment(rendered, input.workload).spec.template.spec.containers[0];
    const matches = container.env.filter(({ name }) => name === input.name);
    const registration = catalog.Registrations.find(({ Id }) => Id === input.providerId);
    assert.ok(registration, `snapshot registration ${input.providerId} exists`);
    assert.equal(matches.length, 1, `${input.name} is emitted exactly once`);
    assert.equal(matches[0].value, registration[input.catalogProperty ?? 'OptionsRevision'],
      `${input.name} matches the catalog`);
    assert.ok(container.envFrom.some(({ configMapRef }) =>
      configMapRef?.name === `${input.workload}-runtime-config` && configMapRef.optional === false),
    `${input.workload} retains its runtime ConfigMap envFrom`);
  }

  const invalidCases = [
    ['wrong value', (resources) => {
      deployment(resources, 'environment').spec.template.spec.containers[0].env
        .find(({ name }) => name === 'Environment__Sandbox__AgentSandbox__OptionsRevision')
        .value = 'invented-revision';
    }],
    ['missing value', (resources) => {
      const container = deployment(resources, 'events-and-sessions').spec.template.spec.containers[0];
      container.env = container.env.filter(({ name }) =>
        name !== 'EventsAndSessions__Cost__Copilot__OptionsRevision');
    }],
    ['unknown revision', (resources) => {
      deployment(resources, 'environment').spec.template.spec.containers[0].env
        .push({ name: 'Environment__Cilium__OptionsRevision', value: 'p1-v1.0.0' });
    }],
    ['wrong workload', (resources) => {
      deployment(resources, 'events-and-sessions').spec.template.spec.containers[0].env
        .push({ name: 'AgentGovernance__Policy__OptionsRevision', value: 'p1-v1.0.0' });
    }],
  ];
  for (const [label, mutate] of invalidCases) {
    const invalid = structuredClone(runtimeManifest(manifest));
    mutate(invalid);
    assert.throws(
      () => validateRuntimeManifest(invalid, images, target.namespace),
      /provider OptionsRevision/,
      label,
    );
  }
  const wrongAdapterVersion = structuredClone(runtimeManifest(manifest));
  deployment(wrongAdapterVersion, 'environment').spec.template.spec.containers[0].env
    .find(({ name }) => name === 'Environment__NetworkPolicy__Cilium__AdapterVersion')
    .value = '2.0.0';
  assert.throws(
    () => validateRuntimeManifest(wrongAdapterVersion, images, target.namespace),
    /provider AdapterVersion/,
  );
  const missingAdapterVersion = structuredClone(runtimeManifest(manifest));
  const environmentContainer = deployment(missingAdapterVersion, 'environment')
    .spec.template.spec.containers[0];
  environmentContainer.env = environmentContainer.env.filter(({ name }) =>
    name !== 'Environment__NetworkPolicy__Cilium__AdapterVersion');
  assert.throws(
    () => validateRuntimeManifest(missingAdapterVersion, images, target.namespace),
    /provider AdapterVersion/,
  );
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

test('Broker run binding is explicit, pins both Pod templates, and adds two narrow network policies', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  const binding = {
    projectsOwnerAddress: 'https://projects-config.agentweaver.svc.cluster.local:8443/',
    projectsAudience: 'https://projects.agentweaver.io/run-binding',
    projectsServerCertificateSha256: 'a'.repeat(64),
    projectsTrustBundleSha256: 'b'.repeat(64),
  };
  const defaultManifest = runtimeManifest(manifest);
  assert.equal(defaultManifest.items.length, 18);

  const rendered = validateRuntimeManifest(
    createRuntimeList(images, target.namespace, {
      configMapName: 'projects-config-provider-catalog',
      sourceCommit: sourceSha,
    }, binding),
    images,
    target.namespace,
    binding,
  );
  assert.equal(rendered.items.length, 20);
  const item = (kind, name) => rendered.items.find(resource =>
    resource.kind === kind && resource.metadata.name === name);
  const fingerprint = brokerRunBindingFingerprint(binding);
  for (const name of ['identity-broker', 'projects-config']) {
    assert.equal(item('Deployment', name).spec.template.metadata.annotations[
      'agentweaver.io/broker-run-binding-sha256'], fingerprint);
  }
  const broker = item('Deployment', 'identity-broker').spec.template.spec;
  assert.ok(broker.volumes.some(volume => volume.name === 'projects-ca' &&
    volume.configMap?.name === 'service-backend-ca' && volume.configMap.defaultMode === 0o440));
  assert.ok(broker.containers[0].volumeMounts.some(mount => mount.name === 'projects-ca' &&
    mount.mountPath === '/var/run/service-backend-ca' && mount.readOnly === true));
  const expectedPolicies = createBrokerRunBindingPolicies(target.namespace);
  assert.equal(rendered.items.filter(resource => resource.kind === 'CiliumNetworkPolicy').length, 2);
  for (const policy of expectedPolicies) {
    assert.deepEqual(item('CiliumNetworkPolicy', policy.metadata.name), policy);
  }
  assert.throws(() => validateBrokerRunBinding({ ...binding, unused: true }), /must contain exactly/);
  for (const projectsAudience of [
    'agentweaver-projects-config',
    'http://projects.agentweaver.io/run-binding',
    'https://user@projects.agentweaver.io/run-binding',
    'https://projects.agentweaver.io/run-binding?mode=1',
    'https://projects.agentweaver.io/run-binding#section',
  ]) {
    assert.throws(() => validateBrokerRunBinding({ ...binding, projectsAudience }),
      /absolute HTTPS URI without user info, query, or fragment/);
  }

  const removedPolicy = structuredClone(rendered);
  removedPolicy.items = removedPolicy.items.filter(resource => resource.kind !== 'CiliumNetworkPolicy');
  assert.throws(() => validateRuntimeManifest(removedPolicy, images, target.namespace, binding),
    /exactly two Broker-to-Projects/);
  const extraPolicy = structuredClone(rendered);
  extraPolicy.items.push({
    ...structuredClone(expectedPolicies[0]),
    metadata: { ...expectedPolicies[0].metadata, name: 'extra-broker-run-binding-policy' },
  });
  assert.throws(() => validateRuntimeManifest(extraPolicy, images, target.namespace, binding),
    /only the exact opt-in Broker-to-Projects Cilium policies/);
  const alteredPolicy = structuredClone(rendered);
  alteredPolicy.items.find(resource => resource.metadata?.name ===
    'identity-broker-projects-egress')
    .spec.egress[0].toPorts[0].ports[0].port = '443';
  assert.throws(() => validateRuntimeManifest(alteredPolicy, images, target.namespace, binding),
    /exact opt-in Broker-to-Projects/);
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
    agenthost: ['agenthost-environment-ingress', 'agenthost-egress', 'broker-issuer-egress'],
    environment: [
      'environment-agenthost-egress', 'environment-agenthost-ingress',
      'database-workload-identity-egress', 'broker-issuer-egress',
    ],
    gateway: ['gateway-owner-network', 'gateway-api-ingress', 'broker-issuer-egress'],
    mcp: ['mcp-gateway-network', 'mcp-gateway-ingress', 'broker-issuer-egress'],
    orchestrator: [
      'orchestrator-owner-network', 'database-workload-identity-egress', 'broker-issuer-egress',
    ],
    knowledge: ['knowledge-owner-network', 'database-workload-identity-egress', 'broker-issuer-egress'],
    'projects-config': 'projects-config-network',
    web: 'web-gateway-ingress',
  });
  const policy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'web-gateway-ingress.yaml'), 'utf8');
  assert.match(policy, /name: web-gateway-ingress/);
  assert.match(policy, /agentweaver\.io\/workload: web/);
  assert.match(policy, /k8s:io\.kubernetes\.pod\.namespace: agentweaver-gateway/);
  assert.match(policy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(policy, /port: "8080"/);
  assert.doesNotMatch(policy, /fromEntities:\s*-\s*world|0\.0\.0\.0\/0/);
  const gatewayPolicy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'gateway-api-ingress.yaml'), 'utf8');
  assert.match(gatewayPolicy, /name: gateway-api-ingress/);
  assert.match(gatewayPolicy, /agentweaver\.io\/workload: gateway/);
  assert.match(gatewayPolicy, /k8s:io\.kubernetes\.pod\.namespace: agentweaver-gateway/);
  assert.match(gatewayPolicy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(gatewayPolicy, /port: "8443"/);
  const mcpPolicy = readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'mcp-gateway-ingress.yaml'), 'utf8');
  assert.match(mcpPolicy, /name: mcp-gateway-ingress/);
  assert.match(mcpPolicy, /agentweaver\.io\/workload: mcp/);
  assert.match(mcpPolicy, /k8s:io\.kubernetes\.pod\.namespace: agentweaver-gateway/);
  assert.match(mcpPolicy, /k8s:gateway\.networking\.k8s\.io\/gateway-name: identity-broker/);
  assert.match(mcpPolicy, /port: "8443"/);
  const environmentPolicy = readFileSync(path.join(root, 'deploy', 'k8s', 'base',
    'environment-agenthost-network.yaml'), 'utf8');
  assert.match(environmentPolicy, /name: environment-agenthost-egress[\s\S]*?agentweaver\.io\/workload: projects-config[\s\S]*?port: "8443"/);
});

test('deployment preflight inventories every Gateway, HTTPRoute, and BackendTLSPolicy', () => {
  const calls = [];
  assert.throws(() => verifyExistingP0BrokerRouting(target, {
    root,
    run: (_file, args) => {
      calls.push(args);
      return JSON.stringify({ items: [] });
    },
  }));
  assert.deepEqual(calls.map(args => [
    args[1], args[args.indexOf('--namespace') + 1], args.includes('--field-selector'), args.at(-1),
  ]), [
    ['gateways', 'agentweaver-gateway', false, 'json'],
    ['httproutes', target.namespace, false, 'json'],
    ['backendtlspolicies', target.namespace, false, 'json'],
    ['defaultdomaincertificates', target.namespace, true, 'json'],
    ['defaultdomaincertificates', 'agentweaver-gateway', true, 'json'],
  ]);
  assert.ok(calls.every(args => args[0] === 'get' && args.includes('--output') &&
    args.at(-1) === 'json'));
});

test('routing readbacks project actual portal hostnames before output redaction', () => {
  const gatewayNamespace = 'agentweaver-gateway';
  const hostnameDomain = '*.0123456789abcdef.eastus2euap.aksapp.io';
  const brokerHostname = `agentweaver.${hostnameDomain.slice(2)}`;
  const portalHostname = `portal.${hostnameDomain.slice(2)}`;
  const controller = 'istio.aks.azure.com/gateway-controller';
  const conditions = (...types) => types.map(type => ({ type, status: 'True', observedGeneration: 1 }));
  const owned = (kind, name, namespace, service, spec, status) => ({
    apiVersion: kind === 'DefaultDomainCertificate'
      ? 'approuting.kubernetes.azure.com/v1alpha1' : 'gateway.networking.k8s.io/v1',
    kind,
    metadata: {
      name, namespace, uid: `${kind}-${name}-uid`, generation: 1, resourceVersion: '1',
      labels: { 'agentweaver.io/service': service },
    },
    spec,
    status,
  });
  const route = (name, service, hostname, sectionName, paths, backend, port) => owned(
    'HTTPRoute', name, target.namespace, service, {
      hostnames: [hostname],
      parentRefs: [{ name: 'identity-broker', namespace: gatewayNamespace, sectionName }],
      rules: [{ matches: paths.map(({ type, value }) => ({ path: { type, value } })),
        backendRefs: [{ name: backend, port }] }],
    }, {
      parents: [{ controllerName: controller, parentRef: {
        name: 'identity-broker', namespace: gatewayNamespace, sectionName,
      }, conditions: conditions('Accepted', 'ResolvedRefs') }],
    });
  const backendPolicy = (name, service, hostname) => owned(
    'BackendTLSPolicy', name, target.namespace, service, {
      targetRefs: [{ group: '', kind: 'Service', name: service, sectionName: 'https' }],
      validation: hostname === brokerHostname
        ? { hostname, wellKnownCACertificates: 'System' }
        : { hostname, caCertificateRefs: [{ group: '', kind: 'ConfigMap', name: 'service-backend-ca' }] },
    }, {
      ancestors: [{ controllerName: controller, ancestorRef: { name: 'identity-broker', namespace: gatewayNamespace },
        conditions: conditions('Accepted', 'ResolvedRefs') }],
    });
  const certificate = owned('DefaultDomainCertificate', 'identity-broker', target.namespace, 'identity-broker',
    { target: { secret: 'identity-broker-tls' } },
    { domain: hostnameDomain, conditions: conditions('Available') });
  const gateway = owned('Gateway', 'identity-broker', gatewayNamespace, 'identity-broker', {
    gatewayClassName: 'approuting-istio',
    listeners: ['https', 'portal'].map((name, index) => ({
      name, hostname: index === 0 ? brokerHostname : portalHostname, port: 443, protocol: 'HTTPS',
      tls: { mode: 'Terminate', certificateRefs: [{ kind: 'Secret', name: 'identity-broker-tls' }] },
      allowedRoutes: { namespaces: { from: 'Selector',
        selector: { matchLabels: { 'kubernetes.io/metadata.name': target.namespace } } } },
    })),
  }, {
    conditions: conditions('Accepted', 'Programmed'),
    addresses: [{ type: 'IPAddress', value: '203.0.113.10' }],
    listeners: ['https', 'portal'].map(name => ({
      name, conditions: conditions('Accepted', 'Programmed', 'ResolvedRefs'),
    })),
  });
  const parentPaths = [{ type: 'PathPrefix', value: '/' }];
  const resources = {
    defaultdomaincertificates: { 'identity-broker': certificate },
    gateways: { 'identity-broker': gateway },
    httproutes: {
      'identity-broker': route('identity-broker', 'identity-broker', brokerHostname, 'https',
        parentPaths, 'identity-broker', 443),
      'web-application': route('web-application', 'web', portalHostname, 'portal',
        parentPaths, 'web', 8080),
      'gateway-api': route('gateway-api', 'gateway', portalHostname, 'portal', [
        { type: 'PathPrefix', value: '/api/v1' },
        { type: 'Exact', value: '/openapi/v1.json' },
      ], 'gateway', 8443),
      mcp: route('mcp', 'mcp', portalHostname, 'portal', [
        { type: 'Exact', value: '/mcp' },
        { type: 'Exact', value: '/.well-known/oauth-protected-resource/mcp' },
      ], 'mcp', 8443),
    },
    backendtlspolicies: {
      'identity-broker': backendPolicy('identity-broker', 'identity-broker', brokerHostname),
      'gateway-api': backendPolicy('gateway-api', 'gateway', 'gateway.agentweaver.svc.cluster.local'),
      mcp: backendPolicy('mcp', 'mcp', 'mcp.agentweaver.svc.cluster.local'),
    },
  };
  const calls = [];
  const run = (file, args, options) => {
    const resource = args[1];
    const namespace = args[args.indexOf('--namespace') + 1];
    const fieldSelector = args.indexOf('--field-selector');
    const items = fieldSelector >= 0
      ? [resources[resource]?.[args[fieldSelector + 1].split('=')[1]]].filter(Boolean)
      : Object.values(resources[resource] ?? {}).filter(object => object.metadata.namespace === namespace);
    const result = runProcess(process.execPath, ['-e',
      `process.stdout.write(${JSON.stringify(JSON.stringify({ items }))})`], options);
    const output = result.stdout.trim();
    calls.push({ file, args, options, output });
    return output;
  };

  const observed = verifyExistingP0BrokerRouting(target, { root, run });

  assert.equal(observed.hostname, brokerHostname);
  assert.equal(observed.portalHostname, portalHostname);
  assert.equal(observed.hostnameAligned, true);
  assert.equal(observed.gatewayProgrammed, true);
  assert.equal(calls.length, 5);
  assert.ok(calls.every(({ file, options }) => file === 'kubectl' &&
    [projectIdentityRoutingInventory, projectIdentityRoutingReadback].includes(options.projectJson) &&
    options.preserveProjectedJson === true));
  const routeInventory = calls.find(({ args }) => args[1] === 'httproutes');
  assert.ok(routeInventory, 'missing namespace-wide HTTPRoute inventory');
  const routeHostname = name => {
    const route = JSON.parse(routeInventory.output).items.find(item => item.metadata.name === name);
    assert.ok(route, `missing HTTPRoute readback for ${name}`);
    return route.spec.hostnames[0];
  };
  assert.equal(routeHostname('web-application'), portalHostname);
  assert.equal(routeHostname('gateway-api'), portalHostname);
});

test('release preflight accepts only the certificate-only state before a fresh product apply', () => {
  const domain = '*.0123456789abcdef.eastus2euap.aksapp.io';
  const certificate = namespace => ({
    apiVersion: 'approuting.kubernetes.azure.com/v1alpha1',
    kind: 'DefaultDomainCertificate',
    metadata: { name: 'identity-broker', namespace, uid: `${namespace}-certificate`, generation: 1 },
    spec: { target: { secret: 'identity-broker-tls' } },
    status: { domain, conditions: [{ type: 'Available', status: 'True', observedGeneration: 1 }] },
  });
  const resources = {
    'defaultdomaincertificates|agentweaver:identity-broker': certificate(target.namespace),
    'defaultdomaincertificates|agentweaver-gateway:identity-broker': certificate('agentweaver-gateway'),
  };
  const calls = [];
  const run = (file, args, options) => {
    const resource = args[1];
    const namespace = args[args.indexOf('--namespace') + 1];
    const fieldSelector = args.indexOf('--field-selector');
    const items = fieldSelector >= 0
      ? [resources[`${resource}|${namespace}:${args[fieldSelector + 1].split('=')[1]}`]].filter(Boolean)
      : Object.entries(resources)
        .filter(([key]) => key.startsWith(`${resource}|${namespace}:`))
        .map(([, object]) => object);
    calls.push(args);
    return JSON.stringify(options.projectJson({ items }));
  };

  const result = verifyExistingP0BrokerRouting(target, { root, run });

  assert.equal(result.certificatesConfigured, true);
  assert.equal(result.routingConfigured, false);
  assert.equal(result.runtimeVerified, false);
  assert.equal(result.hostname, `agentweaver.${domain.slice(2)}`);
  assert.equal(calls.length, 5);
  assert.ok(calls.every(args => args[0] === 'get'));

  resources['gateways|agentweaver-gateway:identity-broker'] = {
    kind: 'Gateway', apiVersion: 'gateway.networking.k8s.io/v1',
    metadata: { name: 'identity-broker', namespace: 'agentweaver-gateway', uid: 'gateway', generation: 1 },
  };
  assert.throws(() => verifyExistingP0BrokerRouting(target, { root, run }));
});

// The bundle Web route may precede Gateway creation, but its parent and backend stay exact.
test('release preflight resumes with only the exact root-bundle Web route', () => {
  const domain = '*.0123456789abcdef.eastus2euap.aksapp.io';
  const portalHostname = `portal.${domain.slice(2)}`;
  const certificate = namespace => ({
    apiVersion: 'approuting.kubernetes.azure.com/v1alpha1',
    kind: 'DefaultDomainCertificate',
    metadata: { name: 'identity-broker', namespace, uid: `${namespace}-certificate`, generation: 1 },
    spec: { target: { secret: 'identity-broker-tls' } },
    status: { domain, conditions: [{ type: 'Available', status: 'True', observedGeneration: 1 }] },
  });
  const webRoute = {
    apiVersion: 'gateway.networking.k8s.io/v1',
    kind: 'HTTPRoute',
    metadata: {
      name: 'web-application', namespace: target.namespace, uid: 'web-route',
      generation: 1, resourceVersion: '7', labels: { 'agentweaver.io/service': 'web' },
    },
    spec: {
      parentRefs: [{ name: 'identity-broker', namespace: 'agentweaver-gateway', sectionName: 'portal' }],
      hostnames: [portalHostname],
      rules: [{ matches: [{ path: { type: 'PathPrefix', value: '/' } }],
        backendRefs: [{ name: 'web', port: 8080 }] }],
    },
  };
  const resources = {
    'defaultdomaincertificates|agentweaver:identity-broker': certificate(target.namespace),
    'defaultdomaincertificates|agentweaver-gateway:identity-broker': certificate('agentweaver-gateway'),
    'httproutes|agentweaver:web-application': webRoute,
  };
  // Namespace-wide reads return every fixture in that namespace; certificate reads stay exact-name.
  const run = (file, args, options) => {
    const resource = args[1];
    const namespace = args[args.indexOf('--namespace') + 1];
    const fieldSelector = args.indexOf('--field-selector');
    const items = fieldSelector >= 0
      ? [resources[`${resource}|${namespace}:${args[fieldSelector + 1].split('=')[1]}`]].filter(Boolean)
      : Object.entries(resources)
        .filter(([key]) => key.startsWith(`${resource}|${namespace}:`))
        .map(([, object]) => object);
    return JSON.stringify(options.projectJson({ items }));
  };

  const result = verifyExistingP0BrokerRouting(target, { root, run });
  assert.equal(result.certificatesConfigured, true);
  assert.equal(result.routingConfigured, false);
  assert.equal(result.bundleWebRouteInstalled, true);
  assert.equal(result.hostname, `agentweaver.${domain.slice(2)}`);

  for (const mutate of [
    route => { route.spec.parentRefs[0].namespace = target.namespace; },
    route => { delete route.spec.parentRefs[0].namespace; },
    route => { route.spec.parentRefs[0].name = 'unexpected-gateway'; },
    route => { route.spec.parentRefs[0].port = 80; },
  ]) {
    const invalidRoute = structuredClone(webRoute);
    mutate(invalidRoute);
    resources['httproutes|agentweaver:web-application'] = invalidRoute;
    assert.throws(() => verifyExistingP0BrokerRouting(target, { root, run }),
      /does not match its exact source-owned host, path, listener, and backend/);
  }
  resources['httproutes|agentweaver:web-application'] = webRoute;
  resources['httproutes|agentweaver:unexpected'] = {
    ...structuredClone(webRoute), metadata: { ...webRoute.metadata, name: 'unexpected' },
  };
  assert.throws(() => verifyExistingP0BrokerRouting(target, { root, run }),
    /exact approved Gateway, HTTPRoute, and BackendTLSPolicy sets/);
  delete resources['httproutes|agentweaver:unexpected'];
  resources['gateways|agentweaver-gateway:unexpected'] = {
    apiVersion: 'gateway.networking.k8s.io/v1', kind: 'Gateway',
    metadata: { name: 'unexpected', namespace: 'agentweaver-gateway', uid: 'unexpected-gateway', generation: 1 },
  };
  assert.throws(() => verifyExistingP0BrokerRouting(target, { root, run }),
    /exact approved Gateway, HTTPRoute, and BackendTLSPolicy sets/);
  delete resources['gateways|agentweaver-gateway:unexpected'];
  resources['backendtlspolicies|agentweaver:unexpected'] = {
    apiVersion: 'gateway.networking.k8s.io/v1', kind: 'BackendTLSPolicy',
    metadata: { name: 'unexpected', namespace: target.namespace, uid: 'unexpected-policy', generation: 1 },
  };
  assert.throws(() => verifyExistingP0BrokerRouting(target, { root, run }),
    /exact approved Gateway, HTTPRoute, and BackendTLSPolicy sets/);
});

test('Azure target readbacks preserve exact hostnames without exposing kubeconfig credentials', () => {
  const fqdn = 'agentweaver.0123456789abcdef.eastus2euap.aksapp.io';
  const privateFqdn = 'agentweaver.0123456789abcdef.eastus2euap.privatelink.azmk8s.io';
  const apiPrivateFqdn = 'agentweaver.0123456789abcdef.eastus2euap.azmk8s.io';
  const expectedResourceId = `/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${target.clusterName}`;
  const fixtures = {
    account: { id: target.subscriptionId, tenantId: target.tenantId },
    aks: {
      id: expectedResourceId,
      fqdn,
      privateFqdn,
      apiserverAccessProfile: { privateFqdn: apiPrivateFqdn },
      agentPoolProfiles: [{ enableAutoScaling: true, minCount: 2, maxCount: 3, count: 2 }],
      properties: { adminUsername: 'sensitive-aks-property' },
    },
    kubeconfig: {
      'current-context': 'agentweaver',
      contexts: [{ name: 'agentweaver', context: { cluster: 'agentweaver', user: 'admin' } }],
      clusters: [{ name: 'agentweaver', cluster: { server: `https://${apiPrivateFqdn}:443` } }],
      users: [{ name: 'admin', user: { token: 'sensitive-kubeconfig-token' } }],
    },
    namespace: {
      metadata: { name: target.namespace, labels: { 'agentweaver.io/environment': 'v1-p0' } },
    },
  };
  const calls = [];
  const run = (file, args, options) => {
    if (file === 'kubectl' && args[0] === 'auth') {
      calls.push({ file, args, options, output: 'yes' });
      return 'yes';
    }
    const fixture = file === 'az'
      ? args[0] === 'account' ? fixtures.account : fixtures.aks
      : args[0] === 'config' ? fixtures.kubeconfig : fixtures.namespace;
    const result = runProcess(process.execPath, ['-e',
      `process.stdout.write(${JSON.stringify(JSON.stringify(fixture))})`], options);
    const output = result.stdout.trim();
    calls.push({ file, args, options, output });
    return output;
  };

  const result = verifyAzureTarget(target, { root, run });

  assert.equal(result.cluster.fqdn, fqdn);
  assert.equal(result.cluster.privateFqdn, privateFqdn);
  assert.equal(result.cluster.apiserverAccessProfile.privateFqdn, apiPrivateFqdn);
  assert.equal(result.contextName, 'agentweaver');
  assert.equal(result.server, `https://${apiPrivateFqdn}:443`);
  assert.equal(calls.length, 5);
  assert.deepEqual(calls[3].args, [
    'auth', 'can-i', 'get', `namespace/${target.namespace}`, '--request-timeout=15s',
  ]);
  assert.equal(calls[3].output, 'yes');
  assert.deepEqual(JSON.parse(calls[1].output), {
    id: expectedResourceId,
    fqdn,
    privateFqdn,
    apiserverAccessProfile: { privateFqdn: apiPrivateFqdn },
    agentPoolProfiles: [{ enableAutoScaling: true, minCount: 2, maxCount: 3, count: 2 }],
  });
  assert.deepEqual(JSON.parse(calls[2].output), {
    'current-context': 'agentweaver',
    contexts: [{ name: 'agentweaver', context: { cluster: 'agentweaver' } }],
    clusters: [{ name: 'agentweaver', cluster: { server: `https://${apiPrivateFqdn}:443` } }],
  });
  assert.ok(calls[1].options.preserveProjectedJson);
  assert.ok(calls[2].options.preserveProjectedJson);
  assert.equal(calls[0].options.projectJson, undefined);
  assert.equal(calls[4].options.projectJson, undefined);
  assert.ok(!calls[1].output.includes('sensitive-aks-property'));
  assert.ok(!calls[2].output.includes('sensitive-kubeconfig-token'));
});

test('release target check fails before namespace reads when Kubernetes API access is missing', () => {
  const expectedResourceId = `/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${target.clusterName}`;
  for (const apiAccess of ['no', new Error('Unable to connect to the server: network is unreachable')]) {
    const calls = [];
    const run = (file, args) => {
      calls.push({ file, args });
      if (file === 'az' && args[0] === 'account') {
        return JSON.stringify({ id: target.subscriptionId, tenantId: target.tenantId });
      }
      if (file === 'az') {
        return JSON.stringify({
          id: expectedResourceId,
          fqdn: 'api.example.invalid',
          agentPoolProfiles: [{ enableAutoScaling: true, minCount: 2, maxCount: 3, count: 2 }],
        });
      }
      if (args[0] === 'config') {
        return JSON.stringify({
          'current-context': 'agentweaver',
          contexts: [{ name: 'agentweaver', context: { cluster: 'agentweaver' } }],
          clusters: [{ name: 'agentweaver', cluster: { server: 'https://api.example.invalid' } }],
        });
      }
      if (args[0] === 'auth') {
        if (apiAccess instanceof Error) throw apiAccess;
        return apiAccess;
      }
      assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
    };

    assert.throws(() => verifyAzureTarget(target, { root, run }),
      /Kubernetes API access is (?:unavailable or insufficient|insufficient).*outside this tooling/);
    assert.equal(calls.some(({ args }) => args[0] === 'get' && args[1] === 'namespace'), false);
  }
});

test('runtime preflight validates the shared backend CA before Broker routing readback', () => {
  const calls = [];
  assert.throws(() => verifyRuntimePrerequisites({ items: [] }, target, {
    root,
    managedResources: { items: [] },
    run: (_file, args) => {
      calls.push(args);
      return '';
    },
  }), /exact service-backend-ca ConfigMap must contain its public ca\.crt/);
  assert.equal(calls.filter(args => args[1] === 'configmap' && args[2] === 'service-backend-ca' &&
    args.at(-1).startsWith('jsonpath=')).length, 1);
});

test('runtime preflight rejects an incomplete service ConfigMap before other cluster checks', () => {
  // An incomplete marker blocks deployment until an operator resolves every required input.
  const manifest = {
    items: [{
      kind: 'Deployment',
      metadata: { name: 'gateway' },
      spec: {
        template: {
          spec: {
            containers: [{
              envFrom: [{ configMapRef: { name: 'gateway-runtime-config', optional: false } }],
            }],
          },
        },
      },
    }],
  };
  const calls = [];

  assert.throws(() => verifyRuntimePrerequisites(manifest, target, {
    root,
    managedResources: { items: [{
      kind: 'ConfigMap',
      metadata: {
        name: 'gateway-runtime-config',
        namespace: target.namespace,
        annotations: { 'agentweaver.io/runtime-config-status': 'incomplete' },
      },
    }] },
    run: (_file, args) => {
      calls.push(args);
      return '';
    },
  }), /gateway-runtime-config is marked incomplete/);
  assert.deepEqual(calls, []);
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
    root: p0Root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => { imagesVerified++; return verifiedImageDigests(manifest); },
    verifyTarget: async () => { targetVerified++; return { exact: true }; },
    verifyPrerequisites: () => { prerequisitesVerified++; },
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      commands.push([file, args]);
      assert.equal(file, 'kubectl');
      assert.equal(args[0], 'apply');
      assert.ok(args.includes('--dry-run=client'));
      assert.ok(args.includes('-k'));
      return '';
    },
  });
  assert.equal(result.dryRun, true);
  assert.equal(result.services.length, deployedServices.length);
  assert.equal(result.perSandboxAgentHost, imagesExpectedHost(manifest));
  assert.equal(result.renderedResources, 61);
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
    dryRun: true,
  }, {
    root: p0Root,
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
  const verification = {
    release: 0, images: 0, target: 0, prerequisites: 0, live: 0, clear: 0, apply: 0, applyPreflight: 0,
  };
  const directory = path.join(root, 'scripts', 'release', 'tests', 'owned-checkpoint-fixture');
  let checkpoint;
  const dependencies = {
    root: p0Root,
    checkpointDirectory: directory,
    checkpointMkdir: () => undefined,
    checkpointReadFile: () => checkpoint,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointRm: () => { verification.clear++; },
    verifyRelease: async () => { verification.release++; return sourceSha; },
    verifyImages: async () => { verification.images++; return verifiedImageDigests(manifest); },
    verifyTarget: async () => { verification.target++; },
    verifyPrerequisites: () => { verification.prerequisites++; },
    verifyLive: async (images) => {
      verification.live++;
      assert.equal(images.length, productServices.length);
      assert.ok(images.some(image => image.id === 'Agentweaver.AgentHost'));
    },
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
        verification.applyPreflight++;
        assert.ok(args.includes('--request-timeout=30s'));
        return '';
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
          !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) verification.apply++;
      return '';
    },
  };
  const first = await deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: 'd'.repeat(40),
    execute: true,
    dryRun: false,
  }, dependencies);
  assert.equal(first.dryRun, false);
  assert.ok(checkpoint);
  const result = await deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: 'd'.repeat(40),
    execute: true,
    dryRun: false,
    resume: true,
  }, dependencies);
  assert.equal(result.resumed, true);
  assert.deepEqual(verification, {
    release: 4,
    images: 4,
    target: 4,
    prerequisites: 2,
    live: 2,
    clear: 2,
    apply: 1,
    applyPreflight: 2,
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
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: 'd'.repeat(40),
    execute: true,
    dryRun: false,
  };
  const baseDependencies = {
    root: p0Root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => verifiedImageDigests(manifest),
    verifyTarget: async () => undefined,
    verifyPrerequisites: () => undefined,
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k')) return '';
      return '';
    },
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
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
          !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) applyCount++;
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

test('AgentHost registry verification pins the published child digest and measures its exact bytes', async () => {
  const fixture = publishedAgentHostFixture();
  const host = fixture.images.find(image => image.id === 'Agentweaver.AgentHost');
  const result = await verifyAgentHostRegistryImage(host, fixture.fetch);
  assert.equal(result.image,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${fixture.childDigest}`);
  assert.equal(result.digest, fixture.childDigest);
  assert.notEqual(result.digest, host.preparedOciMeasurement.image.digest);
  assert.equal(result.compressedPullBytes,
    Buffer.byteLength(fixture.childText) + fixture.configBytes.length + 4);
  assert.ok(result.acceptedDigests.has(fixture.topDigest));
  assert.ok(result.acceptedDigests.has(fixture.childDigest));
});

test('AgentHost registry verification rejects source-label drift in the published config', async () => {
  const fixture = publishedAgentHostFixture(config => {
    config.config.Labels['org.opencontainers.image.revision'] = 'f'.repeat(40);
  });
  const host = fixture.images.find(image => image.id === 'Agentweaver.AgentHost');
  await assert.rejects(verifyAgentHostRegistryImage(host, fixture.fetch),
    /published AgentHost config does not match its prepared source/);
});

test('default dry-run uses the verified published AgentHost platform child and its measured bytes', async () => {
  const fixture = publishedAgentHostFixture();
  let runtimeInput;
  const result = await deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest: fixture.manifest,
    publication: fixture.publication,
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: sourceSha,
    dryRun: true,
  }, {
    root: p0Root,
    verifyRelease: async () => sourceSha,
    verifyTarget: async () => undefined,
    verifyPrerequisites: () => undefined,
    fetch: fixture.fetch,
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'kustomize') {
        const output = execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
        runtimeInput = parseKustomizeResources(output);
        return output;
      }
      assert.equal(file, 'kubectl');
      assert.deepEqual(args.slice(0, 3), ['apply', '--dry-run=client', '-k']);
      return '';
    },
  });
  const environment = runtimeInput.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'environment');
  const env = new Map(environment.spec.template.spec.containers[0].env.map(({ name, value }) => [name, value]));
  assert.equal(env.get('Environment__Sandbox__AgentSandbox__ContainerImage'),
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${fixture.childDigest}`);
  assert.equal(env.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform'), 'linux/amd64');
  assert.equal(env.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes'),
    String(Buffer.byteLength(fixture.childText) + fixture.configBytes.length + 4));
  const agentHostConfig = runtimeInput.items.find(({ kind, metadata }) =>
    kind === 'ConfigMap' && metadata.name === 'agenthost-runtime-v1');
  const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
  assert.equal(agentHostImage.Digest, fixture.childDigest);
  assert.equal(agentHostImage.Platform, env.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform'));
  assert.equal(agentHostImage.CompressedPullBytes,
    Number(env.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes')));
  assert.equal(runtimeInput.items.some(({ metadata }) => metadata.name === 'agenthost'), false);
  assert.equal(result.renderedResources, runtimeInput.items.length);
  assert.equal(result.dryRun, true);
});

test('live deployment verification checks source probes and the published Environment AgentHost pin', () => {
  const manifest = releaseManifest();
  const images = validatePublicationReceipt(publicationReceipt(manifest), manifest, sourceSha);
  const runtime = runtimeManifest(manifest);
  const makeRun = value => (_file, args) => {
    if (args[0] === 'rollout') return '';
    if (args[1] === 'deployment') {
      return JSON.stringify(value.items.find(item =>
        item.kind === 'Deployment' && item.metadata.name === args[2]));
    }
    if (args[1] === 'pods') {
      const name = args[args.indexOf('--selector') + 1].split('=')[1];
      const deployment = value.items.find(item => item.kind === 'Deployment' && item.metadata.name === name);
      const image = deployment.spec.template.spec.containers[0].image;
      return JSON.stringify({ items: [{
        spec: { containers: [{ image }] },
        status: {
          conditions: [{ type: 'Ready', status: 'True' }],
          containerStatuses: [{ ready: true, imageID: image }],
        },
      }] });
    }
    assert.fail(`unexpected command: ${args.join(' ')}`);
  };
  const accepted = verifiedImageDigests(manifest);
  verifyLiveDeployment(images, target, accepted, { root, run: makeRun(runtime) });

  const wrongAgentHostPin = structuredClone(runtime);
  wrongAgentHostPin.items.find(item => item.kind === 'Deployment' && item.metadata.name === 'environment')
    .spec.template.spec.containers[0].env
    .find(entry => entry.name === 'Environment__Sandbox__AgentSandbox__ContainerImage').value =
      'ghcr.io/sabbour/agentweaver-agenthost@sha256:' + 'f'.repeat(64);
  assert.throws(() => verifyLiveDeployment(images, target, accepted,
    { root, run: makeRun(wrongAgentHostPin) }), /exact published AgentHost/);

  const wrongProbe = structuredClone(runtime);
  wrongProbe.items.find(item => item.kind === 'Deployment' && item.metadata.name === 'environment')
    .spec.template.spec.containers[0].readinessProbe.httpGet.path = '/health/live';
  assert.throws(() => verifyLiveDeployment(images, target, accepted,
    { root, run: makeRun(wrongProbe) }), /does not preserve its exact HTTPS \/health\/ready probe/);
});
