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
  AGENT_SANDBOX_RELEASE,
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
import {
  parse as parseYaml, parseAllDocuments as parseAllYamlDocuments, stringify as stringifyYaml,
} from 'yaml';
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
const v103SourceSha = 'acfa2732dba0728adfeaf5754c8e9af9118f2482';
const v103Manifest = JSON.parse(execFileSync('git', [
  'show', `${v103SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
const v104SourceSha = '7bb3ceb282c9377e49da2bc7b6d336baa4eb8209';
const v104SourceTreeSha = '61adf823936284285f68df17b890a5b0da70c033';
const v104ProvenanceSha256 = 'b7a4ef1c71559fe5f8c96d8d7beee2c6c14ed8fad700aea7711a1cc3aa643ea8';
const v104Manifest = JSON.parse(execFileSync('git', [
  'show', `${v104SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
/** The provider-catalog review merge SHA is distinct from the release source SHA. */
const providerCatalogSourceSha = 'b002620bd8cb53a971b6d5b082d6ab43dbf11fb7';
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
/** Image digests from the immutable publication receipt bound to v103SourceSha. */
const v103ImageDigests = Object.freeze({
  'Agentweaver.AgentHost': 'sha256:1b551cbb274a820c65a991813e490583b21615b479cd1fc41effe09da34d5972',
  'Agentweaver.Identity.Broker': 'sha256:b462827eee091f6e4351681820041a516d0e2f2e7630bfef134c9e13d96fdea4',
  'Agentweaver.EventsAndSessions': 'sha256:d0c38a8519348a478a5c48d9d0a922677e4499a25314f5b72cf7dc1d3329dd0f',
  'Agentweaver.Gateway': 'sha256:2df9bc2cccbf762887e4a0ccbb1187ee8e65601e0d76c43f73eb39e068579085',
  'Agentweaver.Mcp': 'sha256:7529591ce6c71d4f1e1a0ec7889718b95217c166f93f88906880a4ea7397a779',
  'Agentweaver.Web': 'sha256:11a3a57e175a6c5361f1d84fe5be24ac8f7716f9666d25e4e2c19d990e5944e2',
  'Agentweaver.Orchestrator': 'sha256:fad8b3217ccaf680fb9fe5b750ca4e0dc67a8d2f4584b9b05795b35832eef771',
  'Agentweaver.Environment': 'sha256:460b98f9970fcd1cf871c0f969e407416457f074f7782cae2919fb881df2230b',
  'Agentweaver.Knowledge': 'sha256:f42829d3f93783f020f2893f439fcd25dfdfed5fa3f6973c783bcc8a752940d5',
  'Agentweaver.Projects.Config': 'sha256:0775465f17a0cf339694c8090756fce9063d119d0ceac5134f94d700a1f9dc45',
});
/** AgentHost's receipt-measured linux/amd64 compressed image size in bytes. */
const v103AgentHostCompressedPullBytes = 321483310;
/** Image digests from the immutable publication receipt bound to v104SourceSha. */
const v104ImageDigests = Object.freeze({
  'Agentweaver.AgentHost': 'sha256:f6d22c6170e3a0d5a3d2d071cd546220c4742771abc091c237f691df63c83025',
  'Agentweaver.Identity.Broker': 'sha256:9452b1bb82803ecf5dfa35fc2228ff9d68b3d0998ada58ec6a8e46269e399585',
  'Agentweaver.EventsAndSessions': 'sha256:d4c5af4ce384c1e1644dcd6d23e57afd4beba5f661c4fbbd82caeef6c123d458',
  'Agentweaver.Gateway': 'sha256:a10f24fd847409ffca6c328e8206898e46c81caeb9b19aed4386157ba7e7f4a6',
  'Agentweaver.Mcp': 'sha256:734eb02c991adbe81624ea07ae1c5e057292d6789dd64af76e61fddce2187026',
  'Agentweaver.Web': 'sha256:c48373e00f43c1c2052507ac1393ca09d5f818cf284f3ab28e7576f0fd6c4d2a',
  'Agentweaver.Orchestrator': 'sha256:9cf0fc02d816703ad52a2e70ac310b921385ac5c75230f12a2171c83ecd3a9d1',
  'Agentweaver.Environment': 'sha256:5371b561e8e262134aad50d8aa610a3c9b2308ca4d20576902967843c8b87f96',
  'Agentweaver.Knowledge': 'sha256:256dd83a1f66b5a3c9a69e3e7db32fb6bffd03dc0cf8c6258fafc50bc8b2f27e',
  'Agentweaver.Projects.Config': 'sha256:715eeb38463f8688fee41cdff7b2c81ce78aa14ff1acb04f8ff41a15fa69813b',
});
/** AgentHost's receipt-measured linux/amd64 compressed image size in bytes. */
const v104AgentHostCompressedPullBytes = 321483172;
const v105SourceSha = 'edae14b469548b8409b0eef651504691e0216a1e';
const v105SourceTreeSha = '265da7c601e033adc99501735b74ccdb2df94420';
const v105ProvenanceSha256 = '78c1fe655be9321727aa9882cb9bad2cf32d1e7faef86f6736151085adca9d21';
const v105AgentHostImageConfigDigest =
  'sha256:2bb01d7d5b90f225d98fde16904f11ef3d090b5f0c358f30f1fa35ec607fe618';
const v105Manifest = JSON.parse(execFileSync('git', [
  'show', `${v105SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
/** Image digests from the immutable publication receipt bound to v105SourceSha. */
const v105ImageDigests = Object.freeze({
  'Agentweaver.AgentHost': 'sha256:015a081bea2e0b6cd36fb75c7adf868995785594cf81b3a4dc988ba3810ffe99',
  'Agentweaver.Identity.Broker': 'sha256:d6e5efbc36622c123813e23a42de48192b70e9315c1093a41462d43660f141bc',
  'Agentweaver.EventsAndSessions': 'sha256:38cb0ffc8a48f2fdd768189cb99a1638f94a736d24a18b79150aeae30d20f8e9',
  'Agentweaver.Gateway': 'sha256:93de1e732a97ab885ea9a2b83671ce129b016e1a827de754b09f082870627c1c',
  'Agentweaver.Mcp': 'sha256:c0f85eb908337d5c70d3d32261bfeeb5a068ece93d34a57bfc063028b50d43dc',
  'Agentweaver.Web': 'sha256:4097186fc602065617f07dbde72d69f4cda16f4394bc1ab2c6747884b9c9259b',
  'Agentweaver.Orchestrator': 'sha256:2b131ad738b0c6841d3981d72369d5fca13dfd79237035e39273f2aec048f156',
  'Agentweaver.Environment': 'sha256:70eb93a709f801e2e27a931a92f40e2dd9f8ced960eec518b191ab844cf8ea0f',
  'Agentweaver.Knowledge': 'sha256:ea43f7c166927a00bc3ceb529ab94f688d03c4e1ab7c3dfb3e538dc721a9b45c',
  'Agentweaver.Projects.Config': 'sha256:5bb6e9b3a204924bb63f9ae27d41e591d36e56d5df261e2d715da53e321550b0',
});
/** AgentHost's receipt-measured linux/amd64 compressed image size in bytes. */
const v105AgentHostCompressedPullBytes = 321483958;
const v106SourceSha = '472601710cf1cf22d98d9cbe7492893768ce1218';
const v106SourceTreeSha = 'bf63850ebb07d325bb0d0b34892d6e6cad04a240';
const v106ProvenanceSha256 = 'b68f0cbfa8571136af1a13ca887d52c3761f57bf96634f802a183a9cb171b402';
const v106AgentHostImageConfigDigest =
  'sha256:167048aa1c859809f28fae913c5fc871d7dea4b87a932bde1c9fe9e46659a5a8';
const v106Manifest = JSON.parse(execFileSync('git', [
  'show', `${v106SourceSha}:releases/foundation.json`,
], { cwd: root, encoding: 'utf8' }));
/** Image digests from the immutable publication receipt bound to v106SourceSha. */
const v106ImageDigests = Object.freeze({
  'Agentweaver.AgentHost': 'sha256:e15fab2de04014b77d58a3ba5f08c729ad9f4f49880cbdbc5995c1093fe77fe6',
  'Agentweaver.Identity.Broker': 'sha256:8841568368ebdea5c3f7c1db481ca7a6f68db3d5e965632710f0cf31ff49e384',
  'Agentweaver.EventsAndSessions': 'sha256:9bdffac08b291455e3686e33f1e1d008392a7c7504fdcc096b39277429698814',
  'Agentweaver.Gateway': 'sha256:a1b30970ed3c1375450ca1ce8eb1e409c2b3acf7f53092fc83fbd4b0d62ee67c',
  'Agentweaver.Mcp': 'sha256:eb523a0505a9f5b61cb04c09fec0af3345ff50d4f256194709df8676a61a3b1d',
  'Agentweaver.Web': 'sha256:ac1c3f8cad01e18f9306375d3218d93f15960fa13b68acfeb40dab1031d5d4d8',
  'Agentweaver.Orchestrator': 'sha256:91e67a2ac41c31797ff63d137d1d8e0e42d59004964f8fb9ff0f715fb29c1075',
  'Agentweaver.Environment': 'sha256:d45fef1a20f1e0f138ecee43889505efd64c92eb93e719d3b2f591c8236fe7e2',
  'Agentweaver.Knowledge': 'sha256:939862dfde4d6931167131871b8ec7272a8ec3bbf1e42c14d909f645adc54b9f',
  'Agentweaver.Projects.Config': 'sha256:e011dba619857b4cffcc0fd14f64f31de01ef9c16e5ea1965ede5f211dd14160',
});
/** AgentHost's receipt-measured linux/amd64 compressed image size in bytes. */
const v106AgentHostCompressedPullBytes = 321486521;
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
const v103Root = sourceFixtureRoot(v103SourceSha, v103Manifest, 'agentweaver-v1-0-3-release-tests-');
const v104Root = sourceFixtureRoot(v104SourceSha, v104Manifest, 'agentweaver-v1-0-4-release-tests-');
const v105Root = sourceFixtureRoot(v105SourceSha, v105Manifest, 'agentweaver-v1-0-5-release-tests-');
const v106Root = sourceFixtureRoot(v106SourceSha, v106Manifest, 'agentweaver-v1-0-6-release-tests-');
after(() => {
  rmSync(p0Root, { recursive: true, force: true });
  rmSync(v101Root, { recursive: true, force: true });
  rmSync(v102Root, { recursive: true, force: true });
  rmSync(v103Root, { recursive: true, force: true });
  rmSync(v104Root, { recursive: true, force: true });
  rmSync(v105Root, { recursive: true, force: true });
  rmSync(v106Root, { recursive: true, force: true });
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
const agentSandboxTestManifest = Buffer.from([
  'apiVersion: v1',
  'kind: Namespace',
  'metadata:',
  '  name: agent-sandbox-system',
  '---',
  'apiVersion: apiextensions.k8s.io/v1',
  'kind: CustomResourceDefinition',
  'metadata:',
  '  name: sandboxes.agents.x-k8s.io',
  '---',
  'apiVersion: rbac.authorization.k8s.io/v1',
  'kind: ClusterRole',
  'metadata:',
  '  name: agent-sandbox-controller',
  '---',
  'apiVersion: rbac.authorization.k8s.io/v1',
  'kind: ClusterRoleBinding',
  'metadata:',
  '  name: agent-sandbox-controller',
  '---',
  'apiVersion: v1',
  'kind: ServiceAccount',
  'metadata:',
  '  name: agent-sandbox-controller',
  '  namespace: agent-sandbox-system',
  '---',
  'apiVersion: v1',
  'kind: Service',
  'metadata:',
  '  name: agent-sandbox-controller',
  '  namespace: agent-sandbox-system',
  '---',
  'apiVersion: apps/v1',
  'kind: Deployment',
  'metadata:',
  '  name: agent-sandbox-controller',
  '  namespace: agent-sandbox-system',
].join('\n'));
const agentSandboxTestRelease = Object.freeze({
  ...AGENT_SANDBOX_RELEASE,
  version: 'v1.0.7-test',
  sourceCommit: 'a'.repeat(40),
  manifestUrl: 'https://example.test/agent-sandbox.yaml',
  manifestSha256: createHash('sha256').update(agentSandboxTestManifest).digest('hex'),
  controllerImage: 'registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.7-test',
});
function agentSandboxTestDependencies() {
  return {
    agentSandboxRelease: agentSandboxTestRelease,
    fetchAgentSandbox: async (url) => {
      assert.equal(url, agentSandboxTestRelease.manifestUrl);
      return {
        ok: true,
        status: 200,
        arrayBuffer: async () => agentSandboxTestManifest.buffer.slice(
          agentSandboxTestManifest.byteOffset,
          agentSandboxTestManifest.byteOffset + agentSandboxTestManifest.byteLength),
      };
    },
  };
}
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
  agentHostImageConfigDigest = `sha256:${'d'.repeat(64)}`,
  agentHostCompressedPullBytes = 123456,
  agentHostSourceTree = 'f'.repeat(40),
  provenanceSha256 = 'b'.repeat(64),
} = {}) {
  const product = manifest.components.filter(({ id }) => id !== 'Agentweaver.FoundationProbe');
  return {
    schemaVersion: 1,
    sourceSha: receiptSourceSha,
    provenanceSha256,
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
      const configDigest = agentHostImageConfigDigest;
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
                sourceTree: agentHostSourceTree,
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
  agentHostImageConfigDigest,
  agentHostCompressedPullBytes,
  agentHostSourceTree,
  provenanceSha256,
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
      agentHostImageConfigDigest,
      agentHostCompressedPullBytes,
      agentHostSourceTree,
      provenanceSha256,
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
    ...agentSandboxTestDependencies(),
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
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') return '';
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') {
        return agentSandboxTestRelease.controllerImage;
      }
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') return 'yes';
      if (file === 'kubectl' && (args[0] === 'wait' ||
          (args[0] === 'rollout' && args[1] === 'status'))) return '';
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server') &&
          args.includes('--filename')) {
        commands.push({ file, args, input: options?.input });
        return '';
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=client')) {
        commands.push({ file, args, input: options?.input });
        return '';
      }
      if (file === 'kubectl' && args[0] === 'apply' && args.includes('--filename')) {
        commands.push({ file, args, input: options?.input });
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

function renderedRuntimeConfigMap(fixtureRoot, name) {
  const rendered = execFileSync('kubectl', [
    'kustomize', path.join(fixtureRoot, 'deploy', 'k8s'),
  ], { cwd: fixtureRoot, encoding: 'utf8' });
  return parseKustomizeResources(rendered).items.find((resource) =>
    resource.kind === 'ConfigMap' && resource.metadata.name === name);
}

function renderedAgentHostRuntimeConfigMap(fixtureRoot) {
  return renderedRuntimeConfigMap(fixtureRoot, 'agenthost-runtime-v1');
}

function renderedProjectsConfigRuntimeConfigMap(fixtureRoot) {
  return renderedRuntimeConfigMap(fixtureRoot, 'projects-config-runtime-config');
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
  wrongTag[wrongTag.indexOf('v1.0.0')] = 'v1.0.7';
  assert.throws(() => parseArgs(wrongTag),
    /limited to the exact v1\.0\.0 and v1\.0\.1 and v1\.0\.2 and v1\.0\.3 and v1\.0\.4 and v1\.0\.5 and v1\.0\.6/);
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

test('v1.0.5 pre-acceptance requires its normal publication and rejects completion proofs', () => {
  const parsed = parseArgs(normalBootstrapArgsFor({}, 'v1.0.5'));
  assert.equal(parsed.preAcceptance, true);
  assert.equal(parsed.releaseTag, 'v1.0.5');
  assert.equal(parsed.dryRun, true);
  assert.equal(parsed.completionPath, undefined);
  assert.throws(() => parseArgs([...normalBootstrapArgsFor({}, 'v1.0.5'), '--completion', 'continuation.json']),
    /not accepted for the v1.0.5 normal publication/);
});

test('v1.0.6 pre-acceptance requires its normal publication and rejects completion proofs', () => {
  const parsed = parseArgs(normalBootstrapArgsFor({}, 'v1.0.6'));
  assert.equal(parsed.preAcceptance, true);
  assert.equal(parsed.releaseTag, 'v1.0.6');
  assert.equal(parsed.dryRun, true);
  assert.equal(parsed.completionPath, undefined);
  assert.throws(() => parseArgs([...normalBootstrapArgsFor({}, 'v1.0.6'), '--completion', 'continuation.json']),
    /not accepted for the v1.0.6 normal publication/);
});

test('pre-acceptance dry-run accepts only the tracked draft and returns a non-release result', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const result = await deployRelease({ argv: bootstrapArgsFor() }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, PARTIAL_PUBLICATION.sourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.deepEqual(result.agentSandbox, {
    version: agentSandboxTestRelease.version,
    sourceCommit: agentSandboxTestRelease.sourceCommit,
    manifestSha256: agentSandboxTestRelease.manifestSha256,
    controllerImage: agentSandboxTestRelease.controllerImage,
  });
  assert.equal('acceptedRelease' in result, false);
  assert.doesNotMatch(JSON.stringify(result), /"stage":"release"/);
  assert.equal(dependencies.commands.length, 2);
  assert.deepEqual(dependencies.commands.map(({ args }) => args), [
    ['apply', '--dry-run=client', '--filename', '-'],
    ['apply', '--dry-run=client', '-k', path.join('deploy', 'k8s')],
  ]);
});

test('AgentSandbox release pin matches the reviewed upstream asset and image', () => {
  assert.deepEqual(AGENT_SANDBOX_RELEASE, {
    version: 'v1.0.7',
    sourceCommit: '807cc37107bd7c012115afac9fa9c3b77b8365c2',
    manifestUrl: 'https://github.com/kubernetes-sigs/agent-sandbox/releases/download/v1.0.7/sandbox-with-extensions.yaml',
    manifestSha256: '136c10c9347cb5a6740b351444cae82f404e883c3f520272257ba785f2af0da8',
    controllerImage: 'registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.7',
  });
});

test('AgentSandbox manifest checksum mismatch stops before cluster reads or writes', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  let targetVerified = false;
  dependencies.verifyTarget = async () => { targetVerified = true; };
  dependencies.fetchAgentSandbox = async () => ({
    ok: true,
    status: 200,
    arrayBuffer: async () => Buffer.from('unverified manifest'),
  });

  await assert.rejects(
    deployRelease({ argv: bootstrapArgsFor() }, dependencies),
    /AgentSandbox v1\.0\.7-test manifest SHA-256/,
  );
  assert.equal(targetVerified, false);
  assert.deepEqual(dependencies.commands, []);
});

test('fresh AgentSandbox install preflights without requiring the controller namespace', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const originalRun = dependencies.run;
  const permissions = [];
  const writes = [];
  let controllerPreflightKinds;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
      permissions.push(args.slice(2));
      return 'yes';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server') &&
        args.includes('--filename')) {
      controllerPreflightKinds = parseAllYamlDocuments(options.input)
        .map((document) => document.toJSON()?.kind);
      assert.equal(permissions.length, 9, 'check every controller namespace permission before server preflight');
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--filename') &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      assert.ok(controllerPreflightKinds, 'finish preflight before applying the controller manifest');
      writes.push('agent-sandbox');
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      assert.ok(controllerPreflightKinds, 'finish preflight before applying product workloads');
      writes.push('product');
      return '';
    }
    return originalRun(file, args, options);
  };
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'fresh-agent-sandbox-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: () => undefined,
    checkpointReadFile: () => { throw Object.assign(new Error('not found'), { code: 'ENOENT' }); },
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });

  const result = await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);

  assert.equal(result.dryRun, false);
  assert.deepEqual(controllerPreflightKinds, [
    'Namespace', 'CustomResourceDefinition', 'ClusterRole', 'ClusterRoleBinding',
  ]);
  assert.deepEqual(permissions, [
    ['get', 'serviceaccounts/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['create', 'serviceaccounts', '--namespace', 'agent-sandbox-system'],
    ['patch', 'serviceaccounts/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['get', 'services/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['create', 'services', '--namespace', 'agent-sandbox-system'],
    ['patch', 'services/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['get', 'deployments/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['create', 'deployments', '--namespace', 'agent-sandbox-system'],
    ['patch', 'deployments/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
  ]);
  assert.deepEqual(writes, ['agent-sandbox', 'product']);
});

test('AgentSandbox namespace permission denial stops before controller or product writes', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const writes = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
      return args[2] === 'create' && args[3] === 'deployments' ? 'no' : 'yes';
    }
    if (file === 'kubectl' && args[0] === 'apply' &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      writes.push(args);
    }
    return originalRun(file, args, options);
  };

  await assert.rejects(
    deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
    /authorization must allow create deployments in namespace agent-sandbox-system/,
  );
  assert.deepEqual(writes, []);
  assert.equal(dependencies.commands.some(({ args }) =>
    args[0] === 'apply' && args.includes('--dry-run=server')), false);
});

test('pre-acceptance dry-run reports a changed owned immutable runtime ConfigMap without writing', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const desired = renderedAgentHostRuntimeConfigMap(dependencies.root);
  const existing = structuredClone(desired);
  const settings = JSON.parse(existing.data['appsettings.json']);
  settings.AgentHost.Image.Digest = `sha256:${'0'.repeat(64)}`;
  existing.data['appsettings.json'] = JSON.stringify(settings, null, 2);
  let targetVerified = false;
  const originalVerifyTarget = dependencies.verifyTarget;
  dependencies.verifyTarget = async (...args) => {
    const result = await originalVerifyTarget(...args);
    targetVerified = true;
    return result;
  };
  const reads = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      if (args[2] !== 'agenthost-runtime-v1') return originalRun(file, args, options);
      assert.equal(targetVerified, true, 'verify the approved target before reading a live ConfigMap');
      reads.push({ args, options });
      return args.includes('--output=name')
        ? 'configmap/agenthost-runtime-v1'
        : JSON.stringify(options.projectJson(existing));
    }
    return originalRun(file, args, options);
  };

  const result = await deployRelease({ argv: bootstrapArgsFor() }, dependencies);

  assert.deepEqual(result.immutableRuntimeConfigMapReplacements, ['agenthost-runtime-v1']);
  assert.deepEqual(reads.map(({ args }) => args), [
    [
      'get', 'configmap', 'agenthost-runtime-v1', '--namespace', target.namespace,
      '--ignore-not-found', '--output=name',
    ],
    ['get', 'configmap', 'agenthost-runtime-v1', '--namespace', target.namespace, '--output=json'],
  ]);
  assert.equal(reads[1].options.preserveProjectedJson, true);
  assert.deepEqual(Object.keys(reads[1].options.projectJson(existing)).sort(), [
    'binaryDataDigest', 'dataDigest', 'immutable', 'kind', 'name', 'namespace',
  ]);
  assert.equal(dependencies.commands.length, 2);
  assert.ok(dependencies.commands.every(({ args }) =>
    args[0] !== 'replace' && args[0] !== 'delete'));
});

test('pre-acceptance dry-run detects changed Projects.Config data without exposing runtime values', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const agentHost = renderedAgentHostRuntimeConfigMap(dependencies.root);
  const desired = renderedProjectsConfigRuntimeConfigMap(dependencies.root);
  const existing = structuredClone(desired);
  existing.data.ProjectsConfig__PrivilegedSetup__ApprovedSubject = 'changed-subject';
  const currentByName = new Map([
    ['agenthost-runtime-v1', agentHost],
    ['projects-config-runtime-config', existing],
  ]);
  const reads = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      const name = args[2];
      const current = currentByName.get(name);
      reads.push({ args, options, current });
      return args.includes('--output=name')
        ? `configmap/${name}`
        : JSON.stringify(options.projectJson(current));
    }
    return originalRun(file, args, options);
  };

  const result = await deployRelease({ argv: bootstrapArgsFor() }, dependencies);

  assert.deepEqual(result.immutableRuntimeConfigMapReplacements, ['projects-config-runtime-config']);
  assert.deepEqual(reads.map(({ args }) => args[2]), [
    'agenthost-runtime-v1', 'agenthost-runtime-v1',
    'projects-config-runtime-config', 'projects-config-runtime-config',
  ]);
  assert.deepEqual(Object.keys(reads[3].options.projectJson(reads[3].current)).sort(), [
    'binaryDataDigest', 'dataDigest', 'immutable', 'kind', 'name', 'namespace',
  ]);
  assert.equal(
    JSON.stringify(result).includes(desired.data.ProjectsConfig__PrivilegedSetup__ApprovedIssuer),
    false,
  );
  assert.doesNotMatch(JSON.stringify(result), /c4279823-a7c7-477e-982e-f4145bb0a83d/);
  assert.doesNotMatch(JSON.stringify(result), /ProjectsConfig__PrivilegedSetup__ApprovedTenantId/);
  assert.equal(result.target.tenantId, target.tenantId);
  assert.equal(dependencies.commands.length, 2);
  assert.ok(dependencies.commands.every(({ args }) =>
    args[0] !== 'replace' && args[0] !== 'delete'));
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

test('pre-acceptance execute replaces only changed agenthost-runtime-v1 after server preflight', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const desired = renderedAgentHostRuntimeConfigMap(dependencies.root);
  const existing = structuredClone(desired);
  const settings = JSON.parse(existing.data['appsettings.json']);
  settings.AgentHost.Image.Digest = `sha256:${'0'.repeat(64)}`;
  existing.data['appsettings.json'] = JSON.stringify(settings, null, 2);
  const calls = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      if (args[2] !== 'agenthost-runtime-v1') return originalRun(file, args, options);
      calls.push({ operation: args.includes('--output=name') ? 'get-name' : 'get-data', args });
      return args.includes('--output=name')
        ? 'configmap/agenthost-runtime-v1'
        : JSON.stringify(options.projectJson(existing));
    }
    if (file === 'kubectl' && args[0] === 'wait') {
      calls.push({ operation: 'crds-ready', args });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'rollout' && args[1] === 'status') {
      calls.push({ operation: 'controller-ready', args });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') {
      calls.push({ operation: 'controller-image', args });
      return originalRun(file, args, options);
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server') &&
        args.includes('--filename')) {
      const isAgentSandbox = parseAllYamlDocuments(options.input).some((document) => {
        const resource = document.toJSON();
        return resource?.kind === 'Namespace' &&
          resource.metadata?.name === 'agent-sandbox-system';
      });
      calls.push({
        operation: isAgentSandbox
          ? 'agent-sandbox-server-preflight'
          : 'server-preflight',
        args,
        input: options.input,
      });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--filename') &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      calls.push({ operation: 'agent-sandbox-apply', args, input: options.input });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
      calls.push({ operation: `can-i-${args[2]}`, args });
      return 'yes';
    }
    if (file === 'kubectl' && args[0] === 'replace') {
      calls.push({ operation: 'replace', args, input: options.input });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) {
      calls.push({ operation: 'apply' });
      return '';
    }
    return originalRun(file, args, options);
  };
  let checkpoint;
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'configmap-replacement-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointReadFile: () => checkpoint,
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });

  const result = await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);

  assert.deepEqual(result.immutableRuntimeConfigMapReplacements, ['agenthost-runtime-v1']);
  assert.deepEqual(calls.map(({ operation }) => operation), [
    'get-name', 'get-data', 'can-i-create', 'can-i-delete',
    'can-i-get', 'can-i-create', 'can-i-patch',
    'can-i-get', 'can-i-create', 'can-i-patch',
    'can-i-get', 'can-i-create', 'can-i-patch',
    'agent-sandbox-server-preflight', 'server-preflight', 'agent-sandbox-apply',
    'crds-ready', 'controller-ready', 'controller-image', 'replace', 'apply',
  ]);
  const sandboxPermissionChecks = calls.filter(({ operation }) =>
    operation.startsWith('can-i-')).slice(2);
  assert.deepEqual(sandboxPermissionChecks.map(({ args }) => args), [
    ['auth', 'can-i', 'get', 'serviceaccounts/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'create', 'serviceaccounts', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'patch', 'serviceaccounts/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'get', 'services/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'create', 'services', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'patch', 'services/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'get', 'deployments/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'create', 'deployments', '--namespace', 'agent-sandbox-system'],
    ['auth', 'can-i', 'patch', 'deployments/agent-sandbox-controller', '--namespace', 'agent-sandbox-system'],
  ]);
  const serverPreflight = calls.find(({ operation }) => operation === 'server-preflight');
  assert.deepEqual(serverPreflight.args, [
    'apply', '--dry-run=server', '--request-timeout=30s', '--filename', '-',
  ]);
  const preflightResources = parseKustomizeResources(serverPreflight.input);
  assert.equal(preflightResources.items.some((resource) =>
    resource.kind === 'ConfigMap' && resource.metadata.name === 'agenthost-runtime-v1'), false);
  assert.equal(preflightResources.items.some((resource) =>
    resource.kind === 'ConfigMap' && resource.metadata.name === 'environment-cilium-config'), true);
  assert.deepEqual(calls.find(({ operation }) => operation === 'crds-ready').args, [
    'wait', '--for=condition=Established',
    'crd/sandboxclaims.extensions.agents.x-k8s.io',
    'crd/sandboxes.agents.x-k8s.io',
    'crd/sandboxtemplates.extensions.agents.x-k8s.io',
    'crd/sandboxwarmpools.extensions.agents.x-k8s.io',
    '--timeout=180s',
  ]);
  assert.deepEqual(calls.find(({ operation }) => operation === 'controller-ready').args, [
    'rollout', 'status', 'deployment/agent-sandbox-controller',
    '--namespace', 'agent-sandbox-system', '--timeout=5m',
  ]);
  const replacement = calls.find(({ operation }) => operation === 'replace');
  assert.deepEqual(replacement.args, [
    'replace', '--force', '--filename', '-', '--namespace', target.namespace,
  ]);
  assert.deepEqual(JSON.parse(replacement.input), desired);
  assert.equal(calls.filter(({ operation }) => operation === 'replace').length, 1);
});

test('pre-acceptance execute replaces only changed Projects.Config runtime data after preflight', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const desiredAgentHost = renderedAgentHostRuntimeConfigMap(dependencies.root);
  const desired = renderedProjectsConfigRuntimeConfigMap(dependencies.root);
  const existing = structuredClone(desired);
  existing.data.ProjectsConfig__PrivilegedSetup__ApprovedSubject = 'changed-subject';
  const currentByName = new Map([
    ['agenthost-runtime-v1', desiredAgentHost],
    ['projects-config-runtime-config', existing],
  ]);
  const calls = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      const name = args[2];
      const current = currentByName.get(name);
      calls.push({ operation: args.includes('--output=name') ? 'get-name' : 'get-data', name, args });
      return args.includes('--output=name')
        ? `configmap/${name}`
        : JSON.stringify(options.projectJson(current));
    }
    if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
      calls.push({ operation: `can-i-${args[2]}`, args });
    } else if (file === 'kubectl' && args[0] === 'replace') {
      calls.push({ operation: 'replace', args, input: options.input });
      return '';
    } else if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      calls.push({ operation: 'apply', args });
      return '';
    } else if (file === 'kubectl' && args[0] === 'apply' &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      calls.push({ operation: 'agent-sandbox-apply', args });
    } else if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
      const isAgentSandbox = parseAllYamlDocuments(options.input).some((document) => {
        const resource = document.toJSON();
        return resource?.kind === 'Namespace' &&
          resource.metadata?.name === 'agent-sandbox-system';
      });
      calls.push({
        operation: isAgentSandbox ? 'agent-sandbox-server-preflight' : 'server-preflight',
        args,
        input: options.input,
      });
    }
    return originalRun(file, args, options);
  };
  let checkpoint;
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'projects-config-replacement-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointReadFile: () => checkpoint,
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });

  const result = await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);

  assert.deepEqual(result.immutableRuntimeConfigMapReplacements, ['projects-config-runtime-config']);
  const permissionChecks = calls.filter(({ operation }) => operation === 'can-i-create' ||
    operation === 'can-i-delete');
  assert.deepEqual(permissionChecks.slice(0, 2).map(({ args }) => [args[2], args[3]]), [
    ['create', 'configmaps'],
    ['delete', 'configmaps/projects-config-runtime-config'],
  ]);
  const firstWrite = calls.findIndex(({ operation }) =>
    ['agent-sandbox-apply', 'replace', 'apply'].includes(operation));
  assert.ok(permissionChecks.slice(0, 2).every(({ args }) =>
    calls.findIndex(({ operation, args: checked }) =>
      operation === `can-i-${args[2]}` && checked === args) < firstWrite));

  const productPreflight = calls.find(({ operation }) => operation === 'server-preflight');
  const preflightResources = parseKustomizeResources(productPreflight.input);
  for (const name of [
    'agenthost-runtime-v1',
    'projects-config-runtime-config',
    'environment-cilium-config',
    'projects-config-provider-catalog',
  ]) {
    assert.equal(preflightResources.items.some((resource) =>
      resource.kind === 'ConfigMap' && resource.metadata.name === name), name !== 'projects-config-runtime-config');
  }
  const replacements = calls.filter(({ operation }) => operation === 'replace');
  assert.equal(replacements.length, 1);
  assert.deepEqual(replacements[0].args, [
    'replace', '--force', '--filename', '-', '--namespace', target.namespace,
  ]);
  assert.deepEqual(JSON.parse(replacements[0].input), desired);
  assert.ok(calls.findIndex(({ operation }) => operation === 'replace') <
    calls.findIndex(({ operation }) => operation === 'apply'));
});

test('pre-acceptance execute refuses replacement without create and delete permissions', async () => {
  for (const deniedVerb of ['create', 'delete']) {
    const fixture = bootstrapFixture();
    const dependencies = bootstrapDependencies(fixture);
    const desired = renderedAgentHostRuntimeConfigMap(dependencies.root);
    const existing = structuredClone(desired);
    const settings = JSON.parse(existing.data['appsettings.json']);
    settings.AgentHost.Image.Digest = `sha256:${'0'.repeat(64)}`;
    existing.data['appsettings.json'] = JSON.stringify(settings, null, 2);
    const permissions = [];
    const writes = [];
    const originalRun = dependencies.run;
    dependencies.run = (file, args, options = {}) => {
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
        if (args[2] !== 'agenthost-runtime-v1') return originalRun(file, args, options);
        return args.includes('--output=name')
          ? 'configmap/agenthost-runtime-v1'
          : JSON.stringify(options.projectJson(existing));
      }
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
        permissions.push([args[2], args[3]]);
        return args[2] === deniedVerb ? 'no' : 'yes';
      }
      if (file === 'kubectl' && (args[0] === 'replace' ||
          (args[0] === 'apply' && args.includes('-k') && !args.some((arg) => arg.startsWith('--dry-run='))))) {
        writes.push(args);
      }
      return originalRun(file, args, options);
    };

    await assert.rejects(
      deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
      new RegExp(`authorization must allow ${deniedVerb}`),
    );
    assert.deepEqual(permissions, deniedVerb === 'create'
      ? [['create', 'configmaps']]
      : [['create', 'configmaps'], ['delete', 'configmaps/agenthost-runtime-v1']]);
    assert.deepEqual(writes, [], 'permission denial must happen before replacement or bundle apply');
  }
});

test('pre-acceptance execute checks Projects.Config replacement permissions before writes', async () => {
  for (const deniedVerb of ['create', 'delete']) {
    const fixture = bootstrapFixture();
    const dependencies = bootstrapDependencies(fixture);
    const agentHost = renderedAgentHostRuntimeConfigMap(dependencies.root);
    const desired = renderedProjectsConfigRuntimeConfigMap(dependencies.root);
    const existing = structuredClone(desired);
    existing.data.ProjectsConfig__PrivilegedSetup__ApprovedSubject = 'changed-subject';
    const currentByName = new Map([
      ['agenthost-runtime-v1', agentHost],
      ['projects-config-runtime-config', existing],
    ]);
    const permissions = [];
    const writes = [];
    const originalRun = dependencies.run;
    dependencies.run = (file, args, options = {}) => {
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
        const name = args[2];
        const current = currentByName.get(name);
        return args.includes('--output=name')
          ? `configmap/${name}`
          : JSON.stringify(options.projectJson(current));
      }
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') {
        permissions.push([args[2], args[3]]);
        return args[2] === deniedVerb ? 'no' : 'yes';
      }
      if (file === 'kubectl' && (args[0] === 'replace' ||
          (args[0] === 'apply' && !args.some((arg) => arg.startsWith('--dry-run='))))) {
        writes.push(args);
      }
      return originalRun(file, args, options);
    };

    await assert.rejects(
      deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
      new RegExp(`authorization must allow ${deniedVerb}`),
    );
    assert.deepEqual(permissions, deniedVerb === 'create'
      ? [['create', 'configmaps']]
      : [['create', 'configmaps'], ['delete', 'configmaps/projects-config-runtime-config']]);
    assert.deepEqual(writes, [], 'permission denial must happen before replacement or bundle apply');
  }
});

test('pre-acceptance execute keeps ordinary apply when the owned immutable runtime data is unchanged', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const desired = renderedAgentHostRuntimeConfigMap(dependencies.root);
  const calls = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      if (args[2] !== 'agenthost-runtime-v1') return originalRun(file, args, options);
      calls.push({ operation: args.includes('--output=name') ? 'get-name' : 'get-data' });
      return args.includes('--output=name')
        ? 'configmap/agenthost-runtime-v1'
        : JSON.stringify(options.projectJson(desired));
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
      calls.push({
        operation: args.includes('-k') ? 'product-server-preflight' : 'agent-sandbox-server-preflight',
        args,
      });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'replace') {
      calls.push({ operation: 'replace' });
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) {
      calls.push({ operation: 'apply' });
      return '';
    }
    return originalRun(file, args, options);
  };
  let checkpoint;
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'unchanged-configmap-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointReadFile: () => checkpoint,
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });

  const result = await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);

  assert.deepEqual(result.immutableRuntimeConfigMapReplacements, []);
  assert.deepEqual(calls.map(({ operation }) => operation), [
    'get-name', 'get-data', 'agent-sandbox-server-preflight', 'product-server-preflight', 'apply',
  ]);
  assert.deepEqual(calls.find(({ operation }) => operation === 'product-server-preflight').args, [
    'apply', '--dry-run=server', '--request-timeout=30s', '-k', path.join('deploy', 'k8s'),
  ]);
});

test('pre-acceptance resume reapplies a completed bundle when the owned immutable runtime ConfigMap drifted', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const desired = renderedAgentHostRuntimeConfigMap(dependencies.root);
  let existing = structuredClone(desired);
  const calls = [];
  const originalRun = dependencies.run;
  dependencies.run = (file, args, options = {}) => {
    if (file === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      if (args[2] !== 'agenthost-runtime-v1') return originalRun(file, args, options);
      return args.includes('--output=name')
        ? 'configmap/agenthost-runtime-v1'
        : JSON.stringify(options.projectJson(existing));
    }
    if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') return 'yes';
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
      calls.push('server-preflight');
      return '';
    }
    if (file === 'kubectl' && args[0] === 'replace') {
      calls.push('replace');
      existing = JSON.parse(options.input);
      return '';
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.some((arg) => arg.startsWith('--dry-run='))) {
      calls.push('apply');
      return '';
    }
    return originalRun(file, args, options);
  };
  let checkpoint;
  Object.assign(dependencies, {
    checkpointDirectory: path.join(root, 'resume-configmap-drift-checkpoint-fixture'),
    checkpointMkdir: () => undefined,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointReadFile: () => checkpoint,
    checkpointRm: () => undefined,
    verifyLive: async () => undefined,
  });

  await deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies);
  assert.equal(JSON.parse(checkpoint).stages.apply.completed, true);
  const settings = JSON.parse(existing.data['appsettings.json']);
  settings.AgentHost.Image.Digest = `sha256:${'0'.repeat(64)}`;
  existing.data['appsettings.json'] = JSON.stringify(settings, null, 2);
  calls.length = 0;

  await deployRelease({ argv: [...bootstrapArgsFor(), '--execute', '--resume'] }, dependencies);

  assert.deepEqual(calls, ['server-preflight', 'server-preflight', 'replace', 'apply']);
  assert.deepEqual(existing, desired);
});

test('release execute stops with access guidance when server-side manifest preflight is denied', async () => {
  const fixture = bootstrapFixture();
  const dependencies = bootstrapDependencies(fixture);
  const originalRun = dependencies.run;
  let applies = 0;
  let serverPreflights = 0;
  const clusterWrites = [];
  dependencies.run = (file, args, options) => {
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('--dry-run=server')) {
      serverPreflights++;
      if (serverPreflights === 2) throw new Error('Kubernetes API returned Forbidden');
      return originalRun(file, args, options);
    }
    if (file === 'kubectl' && (args[0] === 'replace' ||
        (args[0] === 'apply' && !args.some((arg) => arg.startsWith('--dry-run='))))) {
      clusterWrites.push(args);
    }
    if (file === 'kubectl' && args[0] === 'apply' && args.includes('-k') &&
        !args.includes('--dry-run=client') && !args.includes('--dry-run=server')) applies++;
    return originalRun(file, args, options);
  };
  await assert.rejects(deployRelease({ argv: [...bootstrapArgsFor(), '--execute'] }, dependencies),
    /server-side preflight failed before any cluster write.*outside this tooling.*Kubernetes API returned Forbidden/);
  assert.equal(serverPreflights, 2);
  assert.deepEqual(clusterWrites, []);
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
  assert.equal(dependencies.commands.length, 2);
  assert.deepEqual(dependencies.commands.map(({ args }) => args), [
    ['apply', '--dry-run=client', '--filename', '-'],
    ['apply', '--dry-run=client', '-k', path.join('deploy', 'k8s')],
  ]);
});

test('v1.0.2 pre-acceptance rejects its receipt when checked-in pins target a newer publication', async () => {
  const fixture = normalBootstrapFixture(v102Manifest, v102SourceSha, {
    imageDigests: v102ImageDigests,
    agentHostCompressedPullBytes: v102AgentHostCompressedPullBytes,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v102Root,
    sourceSha: v102SourceSha,
    rewriteKustomizeFixture: false,
  });
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.2', providerCatalogSourceSha),
    }, dependencies),
    /checked-in P0 Kustomize images and runtime pins must match the exact publication receipt/,
  );
});

test('v1.0.3 pre-acceptance rejects checked-in pins for the newer v1.0.6 publication', async () => {
  const fixture = normalBootstrapFixture(v103Manifest, v103SourceSha, {
    imageDigests: v103ImageDigests,
    agentHostCompressedPullBytes: v103AgentHostCompressedPullBytes,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v103Root,
    sourceSha: v103SourceSha,
    rewriteKustomizeFixture: false,
  });
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.3', providerCatalogSourceSha),
    }, dependencies),
    /checked-in P0 Kustomize images and runtime pins must match the exact publication receipt/,
  );
});

test('v1.0.4 pre-acceptance dry-run uses its exact source, receipt pins, and image measurement', async () => {
  const fixture = normalBootstrapFixture(v104Manifest, v104SourceSha, {
    imageDigests: v104ImageDigests,
    agentHostCompressedPullBytes: v104AgentHostCompressedPullBytes,
    agentHostSourceTree: v104SourceTreeSha,
    provenanceSha256: v104ProvenanceSha256,
  });
  const releaseVersions = new Set(fixture.manifest.components
    .filter(({ id }) => id !== 'Agentweaver.FoundationProbe')
    .map(({ version }) => version));
  assert.ok(releaseVersions.size > 1);
  assert.equal(execFileSync('git', ['rev-parse', `${v104SourceSha}^{tree}`], {
    cwd: root,
    encoding: 'utf8',
  }).trim(), v104SourceTreeSha);
  assert.equal(fixture.publication.provenanceSha256, v104ProvenanceSha256);

  prepareKustomizeFixture(fixture, v104Root, v104SourceSha, providerCatalogSourceSha);
  const dependencies = bootstrapDependencies(fixture, {
    root: v104Root,
    sourceSha: v104SourceSha,
    rewriteKustomizeFixture: false,
  });
  const kubernetesRoot = path.join(v104Root, 'deploy', 'k8s');
  const kustomization = parseYaml(readFileSync(path.join(kubernetesRoot, 'kustomization.yaml'), 'utf8'));
  const pins = new Map(kustomization.images.map(({ name, digest }) => [name, digest]));
  assert.equal(pins.size, 10);
  for (const [componentId, digest] of Object.entries(v104ImageDigests)) {
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
    String(v104AgentHostCompressedPullBytes),
  );
  const agentHostConfig = parseYaml(readFileSync(path.join(
    kubernetesRoot, 'profiles', 'agenthost', 'runtime-config.example.yaml',
  ), 'utf8'));
  const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
  assert.deepEqual(agentHostImage, {
    Digest: v104ImageDigests['Agentweaver.AgentHost'],
    Platform: 'linux/amd64',
    CompressedPullBytes: v104AgentHostCompressedPullBytes,
  });
  const measured = fixture.publication.published.find(({ id }) => id === 'Agentweaver.AgentHost')
    .preparedOciMeasurement;
  assert.equal(measured.sourceSha, v104SourceSha);
  assert.equal(measured.sourceTree, v104SourceTreeSha);
  assert.equal(measured.image.digest, v104ImageDigests['Agentweaver.AgentHost']);
  assert.equal(measured.image.compressedPullBytes, v104AgentHostCompressedPullBytes);

  const result = await deployRelease({
    argv: normalBootstrapArgsFor({}, 'v1.0.4', providerCatalogSourceSha),
  }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v104SourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal('acceptedRelease' in result, false);
  assert.equal(
    result.perSandboxAgentHost,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${v104ImageDigests['Agentweaver.AgentHost']}`,
  );
  assert.deepEqual(
    result.services.map(({ id, image }) => [id, image]),
    fixture.publication.published
      .filter(({ kind, id }) => kind === 'image' && id !== 'Agentweaver.AgentHost')
      .map(({ id, image }) => [id, image]),
  );
});

test('v1.0.5 pre-acceptance dry-run uses its exact source, receipt pins, and AgentHost measurement', async () => {
  const fixture = normalBootstrapFixture(v105Manifest, v105SourceSha, {
    imageDigests: v105ImageDigests,
    agentHostImageConfigDigest: v105AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v105AgentHostCompressedPullBytes,
    agentHostSourceTree: v105SourceTreeSha,
    provenanceSha256: v105ProvenanceSha256,
  });
  const releaseVersions = new Set(fixture.manifest.components
    .filter(({ id }) => id !== 'Agentweaver.FoundationProbe')
    .map(({ version }) => version));
  assert.ok(releaseVersions.size > 1);
  assert.equal(execFileSync('git', ['rev-parse', `${v105SourceSha}^{tree}`], {
    cwd: root,
    encoding: 'utf8',
  }).trim(), v105SourceTreeSha);
  assert.equal(fixture.publication.provenanceSha256, v105ProvenanceSha256);

  prepareKustomizeFixture(fixture, v105Root, v105SourceSha, providerCatalogSourceSha);
  const kubernetesRoot = path.join(v105Root, 'deploy', 'k8s');
  const kustomization = parseYaml(readFileSync(path.join(kubernetesRoot, 'kustomization.yaml'), 'utf8'));
  const pins = new Map(kustomization.images.map(({ name, digest }) => [name, digest]));
  assert.equal(pins.size, 10);
  for (const [componentId, digest] of Object.entries(v105ImageDigests)) {
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
    String(v105AgentHostCompressedPullBytes),
  );
  const agentHostConfig = parseYaml(readFileSync(path.join(
    kubernetesRoot, 'profiles', 'agenthost', 'runtime-config.example.yaml',
  ), 'utf8'));
  const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
  assert.deepEqual(agentHostImage, {
    Digest: v105ImageDigests['Agentweaver.AgentHost'],
    Platform: 'linux/amd64',
    CompressedPullBytes: v105AgentHostCompressedPullBytes,
  });
  const measured = fixture.publication.published.find(({ id }) => id === 'Agentweaver.AgentHost')
    .preparedOciMeasurement;
  assert.equal(measured.sourceSha, v105SourceSha);
  assert.equal(measured.sourceTree, v105SourceTreeSha);
  assert.equal(measured.image.digest, v105ImageDigests['Agentweaver.AgentHost']);
  assert.equal(measured.image.configDigest, v105AgentHostImageConfigDigest);
  assert.equal(measured.image.platform, 'linux/amd64');
  assert.equal(measured.image.version, '1.0.2');
  assert.equal(measured.image.compressedPullBytes, v105AgentHostCompressedPullBytes);

  const dependencies = bootstrapDependencies(fixture, {
    root: v105Root,
    sourceSha: v105SourceSha,
    rewriteKustomizeFixture: false,
  });
  const result = await deployRelease({
    argv: normalBootstrapArgsFor({}, 'v1.0.5', providerCatalogSourceSha),
  }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v105SourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal('acceptedRelease' in result, false);
  assert.equal(
    result.perSandboxAgentHost,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${v105ImageDigests['Agentweaver.AgentHost']}`,
  );
  assert.deepEqual(
    result.services.map(({ id, image }) => [id, image]),
    fixture.publication.published
      .filter(({ kind, id }) => kind === 'image' && id !== 'Agentweaver.AgentHost')
      .map(({ id, image }) => [id, image]),
  );
});

test('v1.0.6 pre-acceptance dry-run uses its exact source, receipt pins, and AgentHost measurement', async () => {
  const fixture = normalBootstrapFixture(v106Manifest, v106SourceSha, {
    imageDigests: v106ImageDigests,
    agentHostImageConfigDigest: v106AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v106AgentHostCompressedPullBytes,
    agentHostSourceTree: v106SourceTreeSha,
    provenanceSha256: v106ProvenanceSha256,
  });
  const releaseVersions = new Set(fixture.manifest.components
    .filter(({ id }) => id !== 'Agentweaver.FoundationProbe')
    .map(({ version }) => version));
  assert.ok(releaseVersions.size > 1);
  assert.equal(execFileSync('git', ['rev-parse', `${v106SourceSha}^{tree}`], {
    cwd: root,
    encoding: 'utf8',
  }).trim(), v106SourceTreeSha);
  assert.equal(fixture.publication.provenanceSha256, v106ProvenanceSha256);

  const dependencies = bootstrapDependencies(fixture, {
    root: v106Root,
    sourceSha: v106SourceSha,
    rewriteKustomizeFixture: false,
  });
  const kubernetesRoot = path.join(v106Root, 'deploy', 'k8s');
  const kustomization = parseYaml(readFileSync(path.join(kubernetesRoot, 'kustomization.yaml'), 'utf8'));
  const pins = new Map(kustomization.images.map(({ name, digest }) => [name, digest]));
  assert.equal(pins.size, 10);
  for (const [componentId, digest] of Object.entries(v106ImageDigests)) {
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
    String(v106AgentHostCompressedPullBytes),
  );
  const agentHostConfig = parseYaml(readFileSync(path.join(
    kubernetesRoot, 'profiles', 'agenthost', 'runtime-config.example.yaml',
  ), 'utf8'));
  const agentHostImage = JSON.parse(agentHostConfig.data['appsettings.json']).AgentHost.Image;
  assert.deepEqual(agentHostImage, {
    Digest: v106ImageDigests['Agentweaver.AgentHost'],
    Platform: 'linux/amd64',
    CompressedPullBytes: v106AgentHostCompressedPullBytes,
  });
  const measured = fixture.publication.published.find(({ id }) => id === 'Agentweaver.AgentHost')
    .preparedOciMeasurement;
  assert.equal(measured.sourceSha, v106SourceSha);
  assert.equal(measured.sourceTree, v106SourceTreeSha);
  assert.equal(measured.image.digest, v106ImageDigests['Agentweaver.AgentHost']);
  assert.equal(measured.image.configDigest, v106AgentHostImageConfigDigest);
  assert.equal(measured.image.platform, 'linux/amd64');
  assert.equal(measured.image.version, '1.0.3');
  assert.equal(measured.image.compressedPullBytes, v106AgentHostCompressedPullBytes);

  const result = await deployRelease({
    argv: normalBootstrapArgsFor({}, 'v1.0.6', providerCatalogSourceSha),
  }, dependencies);
  assert.equal(result.resultType, 'pre-acceptance');
  assert.equal(result.sourceSha, v106SourceSha);
  assert.equal(result.dryRun, true);
  assert.equal(result.renderedResources, 61);
  assert.equal('acceptedRelease' in result, false);
  assert.equal(
    result.perSandboxAgentHost,
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}@${v106ImageDigests['Agentweaver.AgentHost']}`,
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
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.2', providerCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.2 published receipt/,
  );

  fixture.publication.sourceSha = v102SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.2', providerCatalogSourceSha),
    }, dependencies),
    /v1\.0\.2 pre-acceptance bootstrap rejects continuation publication receipts/,
  );
});

test('v1.0.3 pre-acceptance rejects changed-source and continuation receipts', async () => {
  const fixture = normalBootstrapFixture(v103Manifest, v103SourceSha, {
    imageDigests: v103ImageDigests,
    agentHostCompressedPullBytes: v103AgentHostCompressedPullBytes,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v103Root,
    sourceSha: v103SourceSha,
    rewriteKustomizeFixture: false,
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.3', providerCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.3 published receipt/,
  );

  fixture.publication.sourceSha = v103SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.3', providerCatalogSourceSha),
    }, dependencies),
    /v1\.0\.3 pre-acceptance bootstrap rejects continuation publication receipts/,
  );
});

test('v1.0.4 pre-acceptance rejects changed-source and continuation receipts', async () => {
  const fixture = normalBootstrapFixture(v104Manifest, v104SourceSha, {
    imageDigests: v104ImageDigests,
    agentHostCompressedPullBytes: v104AgentHostCompressedPullBytes,
    agentHostSourceTree: v104SourceTreeSha,
    provenanceSha256: v104ProvenanceSha256,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v104Root,
    sourceSha: v104SourceSha,
    rewriteKustomizeFixture: false,
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.4', providerCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.4 published receipt/,
  );

  fixture.publication.sourceSha = v104SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.4', providerCatalogSourceSha),
    }, dependencies),
    /v1\.0\.4 pre-acceptance bootstrap rejects continuation publication receipts/,
  );
});

test('v1.0.5 pre-acceptance rejects changed-source and continuation receipts', async () => {
  const fixture = normalBootstrapFixture(v105Manifest, v105SourceSha, {
    imageDigests: v105ImageDigests,
    agentHostImageConfigDigest: v105AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v105AgentHostCompressedPullBytes,
    agentHostSourceTree: v105SourceTreeSha,
    provenanceSha256: v105ProvenanceSha256,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v105Root,
    sourceSha: v105SourceSha,
    rewriteKustomizeFixture: false,
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.5', providerCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.5 published receipt/,
  );

  fixture.publication.sourceSha = v105SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.5', providerCatalogSourceSha),
    }, dependencies),
    /v1\.0\.5 pre-acceptance bootstrap rejects continuation publication receipts/,
  );
});

test('v1.0.6 pre-acceptance rejects changed-source and continuation receipts', async () => {
  const fixture = normalBootstrapFixture(v106Manifest, v106SourceSha, {
    imageDigests: v106ImageDigests,
    agentHostImageConfigDigest: v106AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v106AgentHostCompressedPullBytes,
    agentHostSourceTree: v106SourceTreeSha,
    provenanceSha256: v106ProvenanceSha256,
  });
  const dependencies = bootstrapDependencies(fixture, {
    root: v106Root,
    sourceSha: v106SourceSha,
    rewriteKustomizeFixture: false,
  });
  fixture.publication.sourceSha = 'b'.repeat(40);
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.6', providerCatalogSourceSha),
    }, dependencies),
    /exact source-bound v1\.0\.6 published receipt/,
  );

  fixture.publication.sourceSha = v106SourceSha;
  fixture.publication.continuation = { unexpected: true };
  await assert.rejects(
    deployRelease({
      argv: normalBootstrapArgsFor({}, 'v1.0.6', providerCatalogSourceSha),
    }, dependencies),
    /v1\.0\.6 pre-acceptance bootstrap rejects continuation publication receipts/,
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

test('v1.0.3 verifies the exact normal publication result ref and source commit', async () => {
  const fixture = normalBootstrapFixture(v103Manifest, v103SourceSha, {
    imageDigests: v103ImageDigests,
    agentHostCompressedPullBytes: v103AgentHostCompressedPullBytes,
  });
  const resultObjectSha = '5'.repeat(40);
  const resultRefName = `agentweaver-publication/${v103SourceSha}/result`;
  let tagCommitSha = v103SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.3')) {
        return JSON.stringify({ tag_name: 'v1.0.3', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.3')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v103SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.3', fixture.publication, { root, run }), v103SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.3', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v103SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.3', {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  }, { root, run }), /differs from the immutable GitHub publication result/);
});

test('v1.0.4 verifies the exact normal publication result ref and source commit', async () => {
  const fixture = normalBootstrapFixture(v104Manifest, v104SourceSha, {
    imageDigests: v104ImageDigests,
    agentHostCompressedPullBytes: v104AgentHostCompressedPullBytes,
    agentHostSourceTree: v104SourceTreeSha,
    provenanceSha256: v104ProvenanceSha256,
  });
  const resultObjectSha = '6'.repeat(40);
  const resultRefName = `agentweaver-publication/${v104SourceSha}/result`;
  let tagCommitSha = v104SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.4')) {
        return JSON.stringify({ tag_name: 'v1.0.4', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.4')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v104SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.4', fixture.publication, { root, run }), v104SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.4', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v104SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.4', {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  }, { root, run }), /differs from the immutable GitHub publication result/);
});

test('v1.0.5 verifies the exact normal publication result ref and source commit', async () => {
  const fixture = normalBootstrapFixture(v105Manifest, v105SourceSha, {
    imageDigests: v105ImageDigests,
    agentHostImageConfigDigest: v105AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v105AgentHostCompressedPullBytes,
    agentHostSourceTree: v105SourceTreeSha,
    provenanceSha256: v105ProvenanceSha256,
  });
  const resultObjectSha = '6'.repeat(40);
  const resultRefName = `agentweaver-publication/${v105SourceSha}/result`;
  let tagCommitSha = v105SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.5')) {
        return JSON.stringify({ tag_name: 'v1.0.5', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.5')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v105SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.5', fixture.publication, { root, run }), v105SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.5', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v105SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.5', {
    ...fixture.publication,
    provenanceSha256: 'f'.repeat(64),
  }, { root, run }), /differs from the immutable GitHub publication result/);
});

test('v1.0.6 verifies the exact normal publication result ref and source commit', async () => {
  const fixture = normalBootstrapFixture(v106Manifest, v106SourceSha, {
    imageDigests: v106ImageDigests,
    agentHostImageConfigDigest: v106AgentHostImageConfigDigest,
    agentHostCompressedPullBytes: v106AgentHostCompressedPullBytes,
    agentHostSourceTree: v106SourceTreeSha,
    provenanceSha256: v106ProvenanceSha256,
  });
  const resultObjectSha = '7'.repeat(40);
  const resultRefName = `agentweaver-publication/${v106SourceSha}/result`;
  let tagCommitSha = v106SourceSha;
  const calls = [];
  const run = (file, args) => {
    if (file === 'git' && args[0] === 'rev-parse') return 'e'.repeat(40);
    if (file === 'git' && args[0] === 'status') return '';
    if (file === 'gh' && args[0] === 'api') {
      const endpoint = args[1];
      calls.push(endpoint);
      if (endpoint.endsWith('/releases/tags/v1.0.6')) {
        return JSON.stringify({ tag_name: 'v1.0.6', draft: false, prerelease: false });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent('v1.0.6')}`)) {
        return JSON.stringify({ object: { type: 'commit', sha: tagCommitSha } });
      }
      if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)) {
        return JSON.stringify({ object: { type: 'tag', sha: resultObjectSha } });
      }
      if (endpoint.endsWith(`/git/tags/${resultObjectSha}`)) {
        return JSON.stringify({
          tag: resultRefName,
          object: { type: 'commit', sha: v106SourceSha },
          message: JSON.stringify(fixture.publication),
        });
      }
    }
    assert.fail(`unexpected command: ${file} ${args.join(' ')}`);
  };
  assert.equal(await verifyBootstrapRelease('v1.0.6', fixture.publication, { root, run }), v106SourceSha);
  assert.ok(calls.some((endpoint) =>
    endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultRefName)}`)));
  tagCommitSha = 'f'.repeat(40);
  await assert.rejects(verifyBootstrapRelease('v1.0.6', fixture.publication, { root, run }),
    /must resolve to the exact published source commit/);
  tagCommitSha = v106SourceSha;
  await assert.rejects(verifyBootstrapRelease('v1.0.6', {
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
  // Generated runtime manifests mount the CA for every static owner caller before deployment validation.
  for (const name of [
    'environment', 'events-and-sessions', 'gateway', 'identity-broker', 'knowledge', 'mcp', 'orchestrator',
  ]) {
    const pod = item('Deployment', name).spec.template.spec;
    const volumeName = name === 'identity-broker' ? 'projects-ca' : 'backend-ca';
    const caVolume = pod.volumes.find(({ name: candidate }) => candidate === volumeName);
    assert.equal(caVolume?.configMap?.name, 'service-backend-ca', `${name} backend CA ConfigMap`);
    assert.equal(caVolume?.configMap?.optional, false, `${name} backend CA is required`);
    assert.deepEqual(caVolume?.configMap?.items, [{ key: 'ca.crt', path: 'ca.crt' }]);
    assert.ok(pod.containers[0].volumeMounts.some(mount =>
      mount.name === volumeName && mount.mountPath === '/var/run/service-backend-ca' &&
      mount.readOnly === true), `${name} backend CA mount`);
  }
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
  const missingGatewayCa = structuredClone(rendered);
  const gatewayPod = missingGatewayCa.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'gateway').spec.template.spec;
  gatewayPod.volumes = gatewayPod.volumes.filter(({ name }) => name !== 'backend-ca');
  assert.throws(() => validateRuntimeManifest(missingGatewayCa, images, target.namespace),
    /exact public backend CA ConfigMap/);
  const nonMcpCaMount = structuredClone(rendered);
  const mcpBackendCaVolume = nonMcpCaMount.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'mcp').spec.template.spec.volumes
    .find(({ name }) => name === 'backend-ca');
  const webPodSpec = nonMcpCaMount.items.find(({ kind, metadata }) =>
    kind === 'Deployment' && metadata.name === 'web').spec.template.spec;
  webPodSpec.volumes.push(structuredClone(mcpBackendCaVolume));
  assert.throws(() => validateRuntimeManifest(nonMcpCaMount, images, target.namespace),
    /only owner-calling workloads may mount the public backend CA ConfigMap/);
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
    /only owner-calling workloads may mount the public backend CA ConfigMap/);
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
    listeners: ['https'].map(name => ({
      name, hostname: `*.${hostnameDomain.slice(2)}`, port: 443, protocol: 'HTTPS',
      tls: { mode: 'Terminate', certificateRefs: [{ kind: 'Secret', name: 'identity-broker-tls' }] },
      allowedRoutes: { namespaces: { from: 'Selector',
        selector: { matchLabels: { 'kubernetes.io/metadata.name': target.namespace } } } },
    })),
  }, {
    conditions: conditions('Accepted', 'Programmed'),
    addresses: [{ type: 'IPAddress', value: '203.0.113.10' }],
    listeners: ['https'].map(name => ({
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
      'web-application': route('web-application', 'web', portalHostname, 'https',
        parentPaths, 'web', 8080),
      'gateway-api': route('gateway-api', 'gateway', portalHostname, 'https', [
        { type: 'PathPrefix', value: '/api/v1' },
        { type: 'Exact', value: '/openapi/v1.json' },
      ], 'gateway', 8443),
      mcp: route('mcp', 'mcp', portalHostname, 'https', [
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
      parentRefs: [{ name: 'identity-broker', namespace: 'agentweaver-gateway', sectionName: 'https' }],
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
    ...agentSandboxTestDependencies(),
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
      assert.ok(args.includes('-k') || args.includes('--filename'));
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
  assert.equal(commands.length, 2);
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

test('resume after exhausted post-apply registry reads retries and completes all live checks without reapplying', async () => {
  const manifest = releaseManifest();
  const publication = publicationReceipt(manifest);
  const verification = {
    release: 0, images: 0, target: 0, prerequisites: 0, live: 0, clear: 0, apply: 0, applyPreflight: 0,
  };
  const retryDelays = [];
  let resumedManifestAttempts = 0;
  const directory = path.join(root, 'scripts', 'release', 'tests', 'owned-checkpoint-fixture');
  let checkpoint;
  const dependencies = {
    ...agentSandboxTestDependencies(),
    root: p0Root,
    checkpointDirectory: directory,
    checkpointMkdir: () => undefined,
    checkpointReadFile: () => checkpoint,
    checkpointWriteFile: (_file, text) => { checkpoint = text; },
    checkpointRm: () => { verification.clear++; },
    verifyRelease: async () => { verification.release++; return sourceSha; },
    verifyImages: async refs => {
      verification.images++;
      const service = refs.find(image => image.id !== 'Agentweaver.AgentHost');
      await verifyRegistryImage(service.image, async url => {
        if (verification.images === 2 ||
            (verification.images === 4 && String(url).includes('/manifests/') && ++resumedManifestAttempts === 1)) {
          throw new TypeError('fetch failed', {
            cause: Object.assign(new Error('connection reset'), { code: 'ECONNRESET' }),
          });
        }
        if (String(url).includes('/token?')) {
          return { ok: true, json: async () => ({ token: 'test-token' }) };
        }
        return {
          ok: true,
          headers: { get: () => service.image.slice(service.image.lastIndexOf('@') + 1) },
          json: async () => ({ schemaVersion: 2 }),
        };
      }, async milliseconds => retryDelays.push(milliseconds));
      return verifiedImageDigests(manifest);
    },
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
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') return 'yes';
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') {
        return agentSandboxTestRelease.controllerImage;
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
  await assert.rejects(deployRelease({
    releaseTag: 'v1.0.0',
    target,
    manifest,
    publication,
    providerCatalogConfigMapName: 'projects-config-provider-catalog',
    providerCatalogSourceCommit: 'd'.repeat(40),
    execute: true,
    dryRun: false,
  }, dependencies), /https:\/\/ghcr.io\/token.*after 4 attempt.*ECONNRESET connection reset/);
  assert.ok(checkpoint);
  assert.equal(JSON.parse(checkpoint).stages.apply.completed, true);
  assert.equal(verification.live, 0);
  assert.equal(verification.clear, 0);
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
    target: 3,
    prerequisites: 2,
    live: 1,
    clear: 1,
    apply: 1,
    applyPreflight: 4,
  });
  assert.deepEqual(retryDelays, [1000, 2000, 4000, 1000]);
  assert.equal(resumedManifestAttempts, 2);
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
    ...agentSandboxTestDependencies(),
    root: p0Root,
    verifyRelease: async () => sourceSha,
    verifyImages: async () => verifiedImageDigests(manifest),
    verifyTarget: async () => undefined,
    verifyPrerequisites: () => undefined,
    run: (file, args) => {
      if (file === 'kubectl' && args[0] === 'kustomize') {
        return execFileSync(file, args, { cwd: p0Root, encoding: 'utf8' });
      }
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') return 'yes';
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') {
        return agentSandboxTestRelease.controllerImage;
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
      if (file === 'kubectl' && args[0] === 'auth' && args[1] === 'can-i') return 'yes';
      if (file === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') {
        return agentSandboxTestRelease.controllerImage;
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

test('AgentHost verification retries token, index, platform manifest and config independently', async () => {
  const fixture = publishedAgentHostFixture();
  const host = fixture.images.find(image => image.id === 'Agentweaver.AgentHost');
  const attempts = new Map();
  const delays = [];
  const result = await verifyAgentHostRegistryImage(host, async (url, options) => {
    const count = (attempts.get(String(url)) ?? 0) + 1;
    attempts.set(String(url), count);
    if (count === 1) return { ok: false, status: 429 };
    return fixture.fetch(url, options);
  }, async milliseconds => delays.push(milliseconds));
  assert.equal(result.digest, fixture.childDigest);
  assert.deepEqual([...attempts.values()], [2, 2, 2, 2]);
  assert.deepEqual(delays, [1000, 1000, 1000, 1000]);
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
    ...agentSandboxTestDependencies(),
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
      assert.equal(args[0], 'apply');
      assert.equal(args[1], '--dry-run=client');
      assert.ok(args.includes('-k') || args.includes('--filename'));
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
