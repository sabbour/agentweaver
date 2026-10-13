import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  deployIntegration,
  parseIntegrationArgs,
  verifyIntegrationPublicationRemote,
} from '../deploy-integration.mjs';
import { componentImageRepository } from '../pack.mjs';
import { WEB_LOCK_PATH, WEB_PROJECT_PATH } from '../validate.mjs';
import { agentHostComponent } from './agenthost-fixture.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
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
  { id: 'Agentweaver.AgentHost', project: agentHostComponent.project },
  { id: 'Agentweaver.Identity.Broker', project: 'services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj' },
  { id: 'Agentweaver.EventsAndSessions', project: 'services/events-and-sessions/Agentweaver.EventsAndSessions/Agentweaver.EventsAndSessions.csproj' },
  { id: 'Agentweaver.Gateway', project: 'services/gateway/Agentweaver.Gateway/Agentweaver.Gateway.csproj' },
  { id: 'Agentweaver.Mcp', project: 'services/mcp/Agentweaver.Mcp/Agentweaver.Mcp.csproj' },
  { id: 'Agentweaver.Web', project: WEB_PROJECT_PATH },
  { id: 'Agentweaver.Orchestrator', project: 'services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj' },
  { id: 'Agentweaver.Environment', project: 'services/environment/Agentweaver.Environment/Agentweaver.Environment.csproj' },
  { id: 'Agentweaver.Knowledge', project: 'services/knowledge/Agentweaver.Knowledge/Agentweaver.Knowledge.csproj' },
  { id: 'Agentweaver.Projects.Config', project: 'services/projects-config/Agentweaver.Projects.Config/Agentweaver.Projects.Config.csproj' },
].map(component => ({ ...component, kind: 'service', version: '1.0.0' }));
const sha256 = value => createHash('sha256').update(value).digest('hex');
const digest = value => `sha256:${value.repeat(64)}`;

function createFixture(t) {
  const fixtureRoot = path.join(root, 'artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const fixture = mkdtempSync(path.join(fixtureRoot, 'v1-integration-deploy-'));
  t.after(() => rmSync(fixture, { recursive: true, force: true }));

  const manifest = {
    schemaVersion: 1,
    stage: 'draft',
    components: productServices,
    compatibility: [],
  };
  mkdirSync(path.join(fixture, 'releases'), { recursive: true });
  writeFileSync(path.join(fixture, 'releases', 'foundation.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  for (const component of productServices) {
    if (component.id === 'Agentweaver.Web') continue;
    const project = path.join(fixture, component.project);
    mkdirSync(path.dirname(project), { recursive: true });
    writeFileSync(project, '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>\n');
  }
  mkdirSync(path.join(fixture, 'apps', 'web'), { recursive: true });
  writeFileSync(path.join(fixture, WEB_PROJECT_PATH), JSON.stringify({ name: 'web', version: '1.0.0' }));
  writeFileSync(path.join(fixture, WEB_LOCK_PATH), JSON.stringify({
    name: 'web',
    version: '1.0.0',
    lockfileVersion: 3,
    packages: { '': { name: 'web', version: '1.0.0' } },
  }));

  const components = productServices.map(component => {
    const imageConfigDigest = digest('e');
    const measurement = component.id === 'Agentweaver.AgentHost' ? {
      kind: 'agenthost-image',
      schemaVersion: 1,
      sourceSha,
      sourceTree,
      image: {
        configDigest: imageConfigDigest,
        version: component.version,
        platform: 'linux/amd64',
        compressedPullBytes: 123456,
      },
    } : undefined;
    return {
      ...component,
      record: {
        id: component.id,
        kind: 'service',
        version: component.version,
        project: component.project,
        ...(measurement ? { build: { measurement } } : {}),
      },
      artifact: {
        componentId: component.id,
        kind: 'image',
        path: `${component.id}.${component.version}.${component.id === 'Agentweaver.AgentHost' ||
          component.id === 'Agentweaver.Web' ? 'tar' : 'tar.gz'}`,
        tag: sourceSha,
        imageReference: `${componentImageRepository(component.id)}:${sourceSha}`,
        imageConfigDigest,
        integrationLabels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': component.version,
        },
        labels: {
          'org.opencontainers.image.revision': sourceSha,
          'io.agentweaver.source-tree': sourceTree,
          'org.opencontainers.image.version': component.version,
        },
        sha256: 'd'.repeat(64),
      },
    };
  });
  const provenance = {
    schemaVersion: 1,
    stage: 'draft',
    sourceSha,
    manifestSha256: sha256(JSON.stringify(manifest)),
    integration: { mode: 'images-only', sourceTree, imageTag: sourceSha },
    components: components.map(({ record }) => record),
    artifacts: components.map(({ artifact }) => artifact),
  };
  const provenanceBytes = Buffer.from(`${JSON.stringify(provenance, null, 2)}\n`);
  const publication = {
    schemaVersion: 1,
    mode: 'images-only',
    status: 'published',
    sourceSha,
    sourceTree,
    claimSha: 'c'.repeat(40),
    provenanceSha256: sha256(provenanceBytes),
    planned: components.map(({ id, version, artifact }) => ({
      id,
      version,
      kind: 'image',
      sha256: artifact.sha256,
      destination: `ghcr.io/sabbour/${componentImageRepository(id)}:${sourceSha}`,
    })),
    published: components.map(({ id, version, artifact }) => {
      const imageDigest = digest((productServices.findIndex(item => item.id === id) + 1).toString(16));
      const result = {
        id,
        version,
        kind: 'image',
        archiveSha256: artifact.sha256,
        sourceSha,
        sourceTree,
        tag: sourceSha,
        configDigest: artifact.imageConfigDigest,
        image: `ghcr.io/sabbour/${componentImageRepository(id)}@${imageDigest}`,
      };
      if (id === 'Agentweaver.AgentHost') {
        result.preparedOciMeasurement = components.find(item => item.id === id).record.build.measurement;
      }
      return result;
    }),
  };
  const argv = [
    '--source-sha', sourceSha,
    '--manifest', 'releases/foundation.json',
    '--provenance', 'provenance.json',
    '--publication', 'publication.json',
    '--subscription-id', target.subscriptionId,
    '--tenant-id', target.tenantId,
    '--resource-group', target.resourceGroup,
    '--cluster', target.clusterName,
    '--namespace', target.namespace,
    '--node-min', String(target.nodeMin),
    '--node-max', String(target.nodeMax),
    '--provider-catalog-configmap', 'projects-config-provider-catalog',
    '--provider-catalog-source-commit', 'f'.repeat(40),
    '--result', 'deployment-result.json',
  ];

  return {
    root: fixture,
    manifest,
    provenance,
    provenanceBytes,
    publication,
    argv,
    parsed: parseIntegrationArgs(argv),
  };
}

function deploymentDependencies(fixture, run = () => '') {
  const imageByRef = new Map(fixture.publication.published.map(result => {
    const component = fixture.manifest.components.find(item => item.id === result.id);
    return [result.image, { result, component }];
  }));
  return {
    root: fixture.root,
    git(...args) {
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') return sourceSha;
      if (args[0] === 'rev-parse' && args[1] === `${sourceSha}^{tree}`) return sourceTree;
      if (args[0] === 'status' && args[1] === '--porcelain') return '';
      if (args[0] === 'show' && args[1] === 'HEAD:releases/foundation.json') {
        return JSON.stringify(fixture.manifest);
      }
      assert.fail(`unexpected Git call: ${args.join(' ')}`);
    },
    verifyPublicationRemote: async () => true,
    verifyServiceImage: async image => {
      const { result, component } = imageByRef.get(image) ?? assert.fail(`unexpected image ${image}`);
      const imageDigest = image.slice(image.lastIndexOf('@') + 1);
      return {
        image,
        digest: imageDigest,
        acceptedDigests: new Set([imageDigest]),
        configDigest: result.configDigest,
        sourceSha,
        sourceTree,
        version: component.version,
      };
    },
    verifyAgentHostImage: async image => {
      const imageDigest = image.image.slice(image.image.lastIndexOf('@') + 1);
      return {
        image: image.image,
        digest: imageDigest,
        acceptedDigests: new Set([imageDigest]),
        compressedPullBytes: 123456,
      };
    },
    verifyTarget: async () => ({ exact: true }),
    verifyPrerequisites: () => {},
    verifyRouting: async () => {},
    verifyLive: async () => {},
    run,
  };
}

function deployOptions(fixture, updates = {}) {
  return {
    ...fixture.parsed,
    manifest: fixture.manifest,
    provenance: fixture.provenance,
    provenanceBytes: fixture.provenanceBytes,
    publication: fixture.publication,
    ...updates,
  };
}

test('integration deployment arguments default to read-only and require an exact source SHA', t => {
  const fixture = createFixture(t);
  assert.equal(fixture.parsed.dryRun, true);
  assert.equal(fixture.parsed.execute, false);
  assert.throws(() => parseIntegrationArgs(fixture.argv.slice(2)), /sourceSha is required/);
  const uppercaseSha = [...fixture.argv];
  uppercaseSha[1] = 'A'.repeat(40);
  assert.throws(() => parseIntegrationArgs(uppercaseSha), /lowercase 40-character Git commit SHA/);
});

test('manual workflow prepares by default and gates image-only publication on explicit selection', () => {
  const workflow = readFileSync(path.join(root, '.github', 'workflows', 'v1-integration-images.yml'), 'utf8');
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /expected_source:[\s\S]*required: true[\s\S]*type: string/);
  assert.match(workflow, /publish:[\s\S]*default: false[\s\S]*type: boolean/);
  assert.match(workflow, /test "\$GITHUB_REF" = "refs\/heads\/v1"/);
  assert.match(workflow, /test "\$EXPECTED_SOURCE" = "\$GITHUB_SHA"/);
  assert.match(workflow, /release:integration-pack -- --integration-source-sha/);
  assert.match(workflow, /if: \$\{\{ inputs\.publish \}\}/);
  assert.match(workflow, /environment: v1-publication/);
  assert.match(workflow, /--integration-images-only --confirm-integration-publication/);
  assert.doesNotMatch(workflow, /release:publish(?:\s|$)/);
});

test('immutable publication claim must match the exact source, provenance, and image plan', async t => {
  const fixture = createFixture(t);
  const namespace = `agentweaver-integration/${sourceSha}`;
  const resultTagSha = 'f'.repeat(40);
  const claim = {
    schemaVersion: 1,
    sourceSha,
    mode: 'images-only',
    sourceTree,
    provenanceSha256: fixture.publication.provenanceSha256,
    planned: fixture.publication.planned,
    status: 'claimed',
    published: [],
  };
  const claimTag = {
    tag: `${namespace}/claim`,
    message: JSON.stringify(claim),
    object: { type: 'commit', sha: sourceSha },
  };
  const resultTag = {
    tag: `${namespace}/result`,
    message: JSON.stringify(fixture.publication),
    object: { type: 'commit', sha: sourceSha },
  };
  const run = (file, args) => {
    assert.equal(file, 'gh');
    const endpoint = args[1];
    if (endpoint.includes('/git/ref/tags/')) {
      const isClaim = endpoint.endsWith('%2Fclaim');
      return JSON.stringify({ object: {
        type: 'tag',
        sha: isClaim ? fixture.publication.claimSha : resultTagSha,
      } });
    }
    if (endpoint === `repos/sabbour/agentweaver/git/tags/${fixture.publication.claimSha}`) {
      return JSON.stringify(claimTag);
    }
    if (endpoint === `repos/sabbour/agentweaver/git/tags/${resultTagSha}`) {
      return JSON.stringify(resultTag);
    }
    assert.fail(`unexpected GitHub call: ${endpoint}`);
  };
  assert.equal(await verifyIntegrationPublicationRemote(fixture.publication, sourceSha, {
    root: fixture.root,
    run,
  }), true);

  const tamperedClaim = structuredClone(claim);
  tamperedClaim.planned[0].destination = 'ghcr.io/sabbour/other:wrong';
  claimTag.message = JSON.stringify(tamperedClaim);
  await assert.rejects(verifyIntegrationPublicationRemote(fixture.publication, sourceSha, {
    root: fixture.root,
    run,
  }), /immutable integration claim is not bound to the exact source and publication plan/);
});

test('integration deployment rejects draft, provenance, and receipt mismatches before remote checks', async t => {
  const fixture = createFixture(t);
  const dependencies = deploymentDependencies(fixture, () =>
    assert.fail('deployment command must not run for invalid source evidence'));
  dependencies.verifyPublicationRemote = async () => assert.fail('remote receipt check must not run');
  dependencies.verifyServiceImage = async () => assert.fail('registry access must not run');

  const manifestWithEvidence = { ...fixture.manifest, evidence: {} };
  fixture.manifest.evidence = {};
  await assert.rejects(deployIntegration(deployOptions(fixture, { manifest: manifestWithEvidence }), dependencies),
    /draft must not claim deployment or persona evidence/);
  delete fixture.manifest.evidence;

  const provenanceWithWrongTree = structuredClone(fixture.provenance);
  provenanceWithWrongTree.integration.sourceTree = 'c'.repeat(40);
  await assert.rejects(deployIntegration(deployOptions(fixture, { provenance: provenanceWithWrongTree }), dependencies),
    /preparation provenance, draft manifest, and images-only receipt do not match/);

  const receiptWithWrongTree = { ...fixture.publication, sourceTree: 'c'.repeat(40) };
  await assert.rejects(deployIntegration(deployOptions(fixture, { publication: receiptWithWrongTree }), dependencies),
    /images-only publication receipt is partial or not bound to the exact source and tree/);
});

test('default integration dry-run verifies images and renders without applying resources', async t => {
  const fixture = createFixture(t);
  const commands = [];
  const dependencies = deploymentDependencies(fixture, (file, args) => {
    commands.push([file, args]);
    assert.deepEqual([file, ...args], ['kubectl', 'apply', '--dry-run=client', '--filename', '-']);
    return '';
  });
  const result = await deployIntegration(deployOptions(fixture), dependencies);
  assert.equal(result.dryRun, true);
  assert.equal(result.services.length, 9);
  assert.equal(result.sourceSha, sourceSha);
  assert.equal(result.sourceTree, sourceTree);
  assert.equal(result.renderedResources, 18);
  assert.deepEqual(result.targetInfo, { exact: true });
  assert.equal(commands.length, 1);
});

test('execute mode records exact registry and Kubernetes readbacks without external commands', async t => {
  const fixture = createFixture(t);
  const resultPath = path.join(fixture.root, 'deployment-result.json');
  const options = deployOptions(fixture, {
    execute: true,
    dryRun: false,
    resultPath: 'deployment-result.json',
  });
  let runtimeManifest;
  const commands = [];
  const dependencies = deploymentDependencies(fixture, (file, args, options = {}) => {
    commands.push(args);
    assert.equal(file, 'kubectl');
    if (args[0] === 'apply') {
      assert.ok(options.input);
      runtimeManifest = JSON.parse(options.input);
      return '';
    }
    assert.equal(args[0], 'get');
    const name = args[1] === 'pods'
      ? args[args.indexOf('--selector') + 1].split('=').at(-1)
      : args[2];
    const resource = runtimeManifest.items.find(item =>
      item.kind === (args[1] === 'deployment' ? 'Deployment' : 'Service') && item.metadata.name === name);
    if (args[1] === 'deployment' || args[1] === 'service') return JSON.stringify(resource);
    assert.equal(args[1], 'pods');
    const image = runtimeManifest.items.find(item =>
      item.kind === 'Deployment' && item.metadata.name === name).spec.template.spec.containers[0].image;
    return JSON.stringify({
      items: [{
        metadata: { name: `${name}-ready`, namespace: target.namespace },
        spec: { containers: [{ image }] },
        status: {
          conditions: [{ type: 'Ready', status: 'True' }],
          containerStatuses: [{ ready: true, imageID: image }],
        },
      }],
    });
  });

  const result = await deployIntegration(options, dependencies);
  assert.equal(result.dryRun, false);
  assert.equal(result.services.length, 9);
  assert.equal(result.resultPath, resultPath);
  assert.match(result.readbackSha256, /^[a-f0-9]{64}$/);
  assert.equal(commands.filter(args => args[0] === 'apply' && args.includes('--dry-run=client')).length, 1);
  assert.equal(commands.filter(args => args[0] === 'apply' && !args.includes('--dry-run=client')).length, 1);
  assert.equal(commands.filter(args => args[0] === 'get').length, 27);

  const saved = JSON.parse(readFileSync(resultPath, 'utf8'));
  assert.equal(saved.status, 'deployed');
  assert.equal(saved.sourceSha, sourceSha);
  assert.equal(saved.sourceTree, sourceTree);
  assert.equal(saved.registryImages.length, 10);
  assert.equal(saved.readbacks.resources.length, 27);
  assert.equal(saved.provenanceSha256, fixture.publication.provenanceSha256);
  assert.notEqual(saved.provenanceSha256, sha256(JSON.stringify(fixture.provenance)));
});
