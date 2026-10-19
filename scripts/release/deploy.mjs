/**
 * Verifies published release evidence and deploys its images to the explicit Azure P0 target.
 * The flow checks runtime contracts and live resources before rollout, then verifies the resulting state.
 */
import { createHash, X509Certificate } from 'node:crypto';
import {
  cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  parse as parseYaml, parseAllDocuments as parseAllYamlDocuments, stringify as stringifyYaml,
} from 'yaml';
import { componentImageRepository, componentImageTag } from './pack.mjs';
import {
  BROKER_RUN_BINDING_ANNOTATION,
  PROJECTS_CONFIG_TLS_CERTIFICATE_PATH,
  PROJECTS_CONFIG_TLS_KEY_PATH, brokerRunBindingFingerprint,
  createBrokerRunBindingPolicies,
  PROVIDER_ADAPTER_VERSION_RUNTIME_INPUTS, PROVIDER_OPTIONS_REVISION_RUNTIME_INPUTS,
  validateBrokerRunBinding,
} from './runtime-list.mjs';
import {
  createKustomizeImageEntries, parseKustomizeResources, validateKustomizeImagePins,
} from './kustomize-runtime.mjs';
import { validateManifest } from './validate.mjs';
import { verifyRegistryImage } from './registry.mjs';
import { verifyPartialPublicationCompletion } from './partial-publication.mjs';
import { PARTIAL_PUBLICATION } from './partial-publication-contract.mjs';
import { run as runProcess } from '../azure/lib/exec.mjs';
import {
  assertExistingP0BundleWebRoute, managedBrokerHostname, observeExistingP0BrokerRouting,
  projectIdentityRoutingInventory, projectIdentityRoutingReadback,
} from '../azure/lib/identity-broker-routing.mjs';

export { verifyRegistryImage };

const REPOSITORY = 'sabbour/agentweaver';
const REGISTRY = 'ghcr.io/sabbour';
const PRODUCT_SERVICES = Object.freeze([
  'Agentweaver.AgentHost',
  'Agentweaver.Identity.Broker',
  'Agentweaver.EventsAndSessions',
  'Agentweaver.Gateway',
  'Agentweaver.Mcp',
  'Agentweaver.Web',
  'Agentweaver.Orchestrator',
  'Agentweaver.Environment',
  'Agentweaver.Knowledge',
  'Agentweaver.Projects.Config',
]);
const DEPLOYED_SERVICES = Object.freeze(PRODUCT_SERVICES.filter((id) => id !== 'Agentweaver.AgentHost'));
const PRODUCT_COMPONENTS = Object.freeze([
  'Agentweaver.Abstractions',
  'Agentweaver.Providers',
  'Agentweaver.SourceControl',
  'Agentweaver.Orchestrator.Core',
  'Agentweaver.Persistence.Postgres',
  'Agentweaver.Secrets.AzureKeyVault',
  'Agentweaver.Identity',
  'Agentweaver.AgentRuntime',
  'Agentweaver.AgentHost',
  'Agentweaver.Telemetry',
  'Agentweaver.Telemetry.AzureMonitor',
  'Agentweaver.ObjectStore.AzureBlob',
  'Agentweaver.Identity.Broker',
  'Agentweaver.EventsAndSessions',
  'Agentweaver.Gateway',
  'Agentweaver.Mcp',
  'Agentweaver.Web',
  'Agentweaver.Orchestrator',
  'Agentweaver.Environment',
  'Agentweaver.Providers.Storage.AzureFiles',
  'Agentweaver.Providers.Sandbox.AgentSandbox',
  'Agentweaver.Knowledge',
  'Agentweaver.Projects.Config',
]);
const TARGET = Object.freeze({
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'agentweaver',
  clusterName: 'agentweaver-aks',
  namespace: 'agentweaver',
  nodeMin: 2,
  nodeMax: 3,
});
const GATEWAY_NAMESPACE = 'agentweaver-gateway';
/** Maps manifest component IDs to Kubernetes deployment names used by release verification. */
export const SERVICE_NAMES = Object.freeze({
  'Agentweaver.AgentHost': 'agenthost',
  'Agentweaver.Identity.Broker': 'identity-broker',
  'Agentweaver.EventsAndSessions': 'events-and-sessions',
  'Agentweaver.Gateway': 'gateway',
  'Agentweaver.Mcp': 'mcp',
  'Agentweaver.Web': 'web',
  'Agentweaver.Orchestrator': 'orchestrator',
  'Agentweaver.Environment': 'environment',
  'Agentweaver.Knowledge': 'knowledge',
  'Agentweaver.Projects.Config': 'projects-config',
});
const WORKLOAD_IDENTITY_SERVICES = new Set([
  'identity-broker', 'events-and-sessions', 'environment', 'knowledge', 'orchestrator', 'projects-config',
]);
const IDENTITY_CLIENT_SERVICES = new Set(['gateway', 'orchestrator']);
const SERVICE_RUNTIME_CONFIG_MAP_NAMES = Object.freeze(DEPLOYED_SERVICES
  .map((id) => id === 'Agentweaver.Identity.Broker'
    ? 'identity-broker-runtime-config-v2'
    : `${SERVICE_NAMES[id]}-runtime-config`));
// Only marked pre-provisioning maps may carry approved placeholders; execution rejects them until Azure values are sourced.
const INCOMPLETE_RUNTIME_CONFIG_MAPS = new Set([
  'environment-runtime-config',
  'events-and-sessions-runtime-config',
  'gateway-runtime-config',
  'identity-broker-runtime-config-v2',
  'knowledge-runtime-config',
  'mcp-runtime-config',
  'orchestrator-runtime-config',
  'projects-config-runtime-config',
  'web-runtime-config',
]);
const INCOMPLETE_CONFIG_MAPS = new Set([...INCOMPLETE_RUNTIME_CONFIG_MAPS, 'agenthost-runtime-v1']);
// Only these source-owned immutable runtime maps are eligible for pre-acceptance replacement.
const REPLACEABLE_IMMUTABLE_RUNTIME_CONFIG_MAPS = new Set([
  'agenthost-runtime-v1',
  'projects-config-runtime-config',
]);
/** Upstream AgentSandbox release asset pinned to its source commit and content digest. */
export const AGENT_SANDBOX_RELEASE = Object.freeze({
  version: 'v1.0.7',
  sourceCommit: '807cc37107bd7c012115afac9fa9c3b77b8365c2',
  manifestUrl: 'https://github.com/kubernetes-sigs/agent-sandbox/releases/download/v1.0.7/sandbox-with-extensions.yaml',
  manifestSha256: '136c10c9347cb5a6740b351444cae82f404e883c3f520272257ba785f2af0da8',
  controllerImage: 'registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.7',
});
const AGENT_SANDBOX_CRD_NAMES = Object.freeze([
  'sandboxclaims.extensions.agents.x-k8s.io',
  'sandboxes.agents.x-k8s.io',
  'sandboxtemplates.extensions.agents.x-k8s.io',
  'sandboxwarmpools.extensions.agents.x-k8s.io',
]);
const AGENT_SANDBOX_CONTROLLER = Object.freeze({
  name: 'agent-sandbox-controller',
  namespace: 'agent-sandbox-system',
});
const AGENT_SANDBOX_NAMESPACED_RESOURCES = new Map([
  ['ServiceAccount', 'serviceaccounts'],
  ['Service', 'services'],
  ['Deployment', 'deployments'],
]);
const AGENT_SANDBOX_CLUSTER_SCOPED_KINDS = new Set([
  'Namespace',
  'CustomResourceDefinition',
  'ClusterRole',
  'ClusterRoleBinding',
]);
const PREPROVISIONING_RUNTIME_MARKERS = new Set([
  'CHANGEME-KEYVAULT-HOST',
  'CHANGEME-MANAGED-HOST',
  'CHANGEME-PORTAL-HOST',
  'CHANGEME-identity-broker-client-id',
  'CHANGEME-environment-client-id',
  'CHANGEME-events-and-sessions-client-id',
  'CHANGEME-knowledge-client-id',
  'CHANGEME-orchestrator-client-id',
  'CHANGEME-projects-config-client-id',
]);
const REQUIRED_INSTALL_CONFIG_MAPS = new Set([
  ...SERVICE_RUNTIME_CONFIG_MAP_NAMES,
  'environment-cilium-config',
  'agenthost-runtime-v1',
  'projects-config-provider-catalog',
]);
const WORKLOAD_IDENTITY_CONFIG = Object.freeze({
  'identity-broker': {
    configMap: 'identity-broker-runtime-config-v2',
    clientId: 'IdentityBroker__SecretRedemption__WorkloadIdentityClientId',
    tenantId: 'IdentityBroker__SecretRedemption__WorkloadIdentityTenantId',
  },
  'events-and-sessions': {
    configMap: 'events-and-sessions-runtime-config',
    clientId: 'EventsAndSessions__Database__WorkloadIdentity__ClientId',
    tenantId: 'EventsAndSessions__Database__WorkloadIdentity__TenantId',
  },
  environment: {
    configMap: 'environment-runtime-config',
    clientId: 'Environment__Database__WorkloadIdentity__ClientId',
    tenantId: 'Environment__Database__WorkloadIdentity__TenantId',
  },
  knowledge: {
    configMap: 'knowledge-runtime-config',
    clientId: 'Knowledge__Database__WorkloadIdentity__ClientId',
    tenantId: 'Knowledge__Database__WorkloadIdentity__TenantId',
  },
  orchestrator: {
    configMap: 'orchestrator-runtime-config',
    clientId: 'Orchestrator__Database__WorkloadIdentity__ClientId',
    tenantId: 'Orchestrator__Database__WorkloadIdentity__TenantId',
  },
  'projects-config': {
    configMap: 'projects-config-runtime-config',
    clientId: 'ProjectsConfig__Database__WorkloadIdentity__ClientId',
    tenantId: 'ProjectsConfig__Database__WorkloadIdentity__TenantId',
  },
});
const P0_PROVIDER_CATALOG_CONFIG_MAP = 'projects-config-provider-catalog';
const KUSTOMIZE_INSTALL_PATH = path.join('deploy', 'k8s');
const BACKEND_TLS_HOSTNAMES = Object.freeze({
  gateway: 'gateway.agentweaver.svc.cluster.local',
  mcp: 'mcp.agentweaver.svc.cluster.local',
});
const PROJECTS_CONFIG_HOSTNAME = 'projects-config.agentweaver.svc.cluster.local';
const SERVER_AUTH_EKU = '1.3.6.1.5.5.7.3.1';
const BACKEND_TLS_CA_CONFIGMAP = 'service-backend-ca';
const BOOTSTRAP_RELEASE_TAG = 'v1.0.0';
const BOOTSTRAP_RELEASES = Object.freeze({
  [BOOTSTRAP_RELEASE_TAG]: Object.freeze({
    sourceSha: PARTIAL_PUBLICATION.sourceSha,
    continuation: true,
  }),
  'v1.0.1': Object.freeze({
    sourceSha: '56cf5a8f841dc22f69cd087d45f4e2882702dfb4',
    continuation: false,
    allowLightweightTag: true,
  }),
  // Pin v1.0.2 to this source SHA and its immutable normal publication; reject continuation receipts.
  'v1.0.2': Object.freeze({
    sourceSha: '3c834c12cf1e66bf385b3833a7f0a5cdd5782f8a',
    continuation: false,
    allowLightweightTag: true,
  }),
  // Pin v1.0.3 to this source SHA and its immutable normal publication; reject continuation receipts.
  'v1.0.3': Object.freeze({
    sourceSha: 'acfa2732dba0728adfeaf5754c8e9af9118f2482',
    continuation: false,
    allowLightweightTag: true,
  }),
  // Pin v1.0.4 to this source SHA and its immutable normal publication; reject continuation receipts.
  'v1.0.4': Object.freeze({
    sourceSha: '7bb3ceb282c9377e49da2bc7b6d336baa4eb8209',
    continuation: false,
    allowLightweightTag: true,
  }),
});
/** Maps each service to all required Cilium policy names, including shared egress policies. */
export const SERVICE_NETWORK_POLICY_NAMES = Object.freeze({
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
const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const RELEASE_TAG = /^v1\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const CHECKPOINT_VERSION = 1;
const checkpointDirectory = path.join(os.homedir(), '.agentweaver', 'v1-release-deploy');
const registryManifestAccept = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');

function fail(message) {
  throw new Error(`release deployment: ${message}`);
}

function requiredString(value, name) {
  if (typeof value !== 'string' || value.trim() !== value || value.length === 0) {
    fail(`${name} is required and must be an exact nonempty string`);
  }
}

function safeKubernetesName(value, location) {
  if (typeof value !== 'string' || value.length > 253 ||
      !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/.test(value)) {
    fail(`${location} must be an exact Kubernetes DNS name`);
  }
}

function parseJson(text, location) {
  try {
    return JSON.parse(text);
  } catch (error) {
    fail(`${location} is not valid JSON: ${error.message}`);
  }
}

function configMapDataDigest(data) {
  const entries = Object.entries(data ?? {}).sort(([left], [right]) => left.localeCompare(right));
  return createHash('sha256').update(JSON.stringify(entries)).digest('hex');
}

function projectRuntimeConfigMap(resource) {
  return {
    kind: resource.kind,
    name: resource.metadata?.name,
    namespace: resource.metadata?.namespace,
    immutable: resource.immutable,
    dataDigest: configMapDataDigest(resource.data),
    binaryDataDigest: configMapDataDigest(resource.binaryData),
  };
}

function findImmutableRuntimeConfigMapReplacements(installationManifest, namespace, { root, run }) {
  const replacements = [];
  for (const name of REPLACEABLE_IMMUTABLE_RUNTIME_CONFIG_MAPS) {
    const desired = installationManifest.items.find((resource) =>
      resource.kind === 'ConfigMap' && resource.metadata?.name === name);
    if (!desired || desired.immutable !== true || desired.metadata?.namespace !== namespace) {
      fail(`source bundle must include immutable ConfigMap/${name} in namespace ${namespace}`);
    }

    const currentName = run('kubectl', [
      'get', 'configmap', name, '--namespace', namespace, '--ignore-not-found', '--output=name',
    ], { cwd: root });
    if (!currentName) continue;
    if (currentName !== `configmap/${name}`) {
      fail(`live ConfigMap lookup returned an unexpected resource for ${name}`);
    }
    const currentText = run('kubectl', [
      'get', 'configmap', name, '--namespace', namespace, '--output=json',
    ], {
      cwd: root,
      // Project only identity and hashes so runtime settings are never surfaced by command output.
      projectJson: projectRuntimeConfigMap,
      preserveProjectedJson: true,
    });
    const current = parseJson(currentText, `ConfigMap/${name}`);
    if (current.kind !== 'ConfigMap' || current.name !== name || current.namespace !== namespace ||
        typeof current.dataDigest !== 'string' || typeof current.binaryDataDigest !== 'string') {
      fail(`live ConfigMap/${name} does not match its explicitly managed namespace and name`);
    }
    const desiredProjection = projectRuntimeConfigMap(desired);
    if (current.immutable === true &&
        (current.dataDigest !== desiredProjection.dataDigest ||
         current.binaryDataDigest !== desiredProjection.binaryDataDigest)) {
      replacements.push(desired);
    }
  }
  return replacements;
}

function verifyImmutableRuntimeConfigMapReplacementPermissions(replacements, namespace, { root, run }) {
  for (const { metadata } of replacements) {
    const name = metadata.name;
    // Check create before delete because force replacement removes the old object before creating the new one.
    for (const [verb, resource] of [
      ['create', 'configmaps'],
      ['delete', `configmaps/${name}`],
    ]) {
      const allowed = run('kubectl', [
        'auth', 'can-i', verb, resource, '--namespace', namespace,
      ], { cwd: root }).trim().toLowerCase();
      if (allowed !== 'yes') {
        fail(`Kubernetes authorization must allow ${verb} ${resource} in namespace ${namespace} before replacing ConfigMap/${name}`);
      }
    }
  }
}

function serializeKustomizeResources(resources) {
  return resources.items.map((resource) => stringifyYaml(resource)).join('\n---\n');
}

/** Splits the pinned manifest because fresh clusters cannot dry-run into a namespace that does not exist. */
function prepareAgentSandboxServerPreflight(manifest) {
  const clusterResources = [];
  const namespacedResources = [];
  for (const document of parseAllYamlDocuments(manifest)) {
    if (document.errors.length > 0) fail('AgentSandbox manifest contains invalid YAML');
    const resource = document.toJSON();
    if (!resource) continue;
    const plural = AGENT_SANDBOX_NAMESPACED_RESOURCES.get(resource.kind);
    if (plural) {
      const namespace = resource.metadata?.namespace;
      const name = resource.metadata?.name;
      if (namespace !== AGENT_SANDBOX_CONTROLLER.namespace || typeof name !== 'string') {
        fail(`AgentSandbox ${resource.kind} must declare its controller namespace and resource name`);
      }
      namespacedResources.push({ name, namespace, plural });
    } else if (AGENT_SANDBOX_CLUSTER_SCOPED_KINDS.has(resource.kind)) {
      clusterResources.push(resource);
    } else {
      fail(`AgentSandbox manifest contains unsupported resource kind ${resource.kind ?? '(unknown)'}`);
    }
  }
  if (clusterResources.length === 0 || namespacedResources.length === 0) {
    fail('AgentSandbox manifest must include cluster-scoped and controller namespace resources');
  }
  return {
    clusterManifest: clusterResources.map((resource) => stringifyYaml(resource)).join('\n---\n'),
    namespacedResources,
  };
}

/** Checks apply permissions for namespace resources before any cluster write, including fresh installs. */
function verifyAgentSandboxNamespacePermissions(resources, { root, run }) {
  for (const { name, namespace, plural } of resources) {
    for (const verb of ['get', 'create', 'patch']) {
      const resource = verb === 'create' ? plural : `${plural}/${name}`;
      const allowed = run('kubectl', [
        'auth', 'can-i', verb, resource, '--namespace', namespace,
      ], { cwd: root }).trim().toLowerCase();
      if (allowed !== 'yes') {
        fail(`Kubernetes authorization must allow ${verb} ${resource} in namespace ${namespace} before installing AgentSandbox`);
      }
    }
  }
}

/** Fetches the upstream manifest only when its bytes match the reviewed release digest. */
async function fetchPinnedAgentSandboxManifest(fetchImpl, release) {
  const response = await fetchImpl(release.manifestUrl);
  if (!response.ok) {
    fail(`AgentSandbox ${release.version} manifest download failed with HTTP ${response.status}`);
  }
  const manifest = Buffer.from(await response.arrayBuffer());
  const digest = createHash('sha256').update(manifest).digest('hex');
  if (digest !== release.manifestSha256) {
    fail(`AgentSandbox ${release.version} manifest SHA-256 does not match its pinned release digest`);
  }
  return manifest.toString('utf8');
}

/** Waits for all pinned APIs and confirms the live controller still uses the approved image. */
function verifyAgentSandboxInstallation(release, { root, run }) {
  run('kubectl', [
    'wait', '--for=condition=Established',
    ...AGENT_SANDBOX_CRD_NAMES.map((name) => `crd/${name}`),
    '--timeout=180s',
  ], { cwd: root, timeout: 190_000 });
  run('kubectl', [
    'rollout', 'status', `deployment/${AGENT_SANDBOX_CONTROLLER.name}`,
    '--namespace', AGENT_SANDBOX_CONTROLLER.namespace, '--timeout=5m',
  ], { cwd: root, timeout: 310_000 });
  const image = run('kubectl', [
    'get', 'deployment', AGENT_SANDBOX_CONTROLLER.name,
    '--namespace', AGENT_SANDBOX_CONTROLLER.namespace,
    '--output=jsonpath={.spec.template.spec.containers[?(@.name=="agent-sandbox-controller")].image}',
  ], { cwd: root }).trim();
  if (image !== release.controllerImage) {
    fail(`live AgentSandbox controller image must be ${release.controllerImage}`);
  }
}

function runCommand(file, args, options = {}) {
  return runProcess(file, args, options).stdout.trim();
}

/** Parses deployment flags; bootstrap is a separate source-bound mode and writes still require --execute. */
export function parseArgs(argv = []) {
  const values = {};
  let releaseTag;
  let execute = false;
  let dryRun = false;
  let preAcceptance = false;
  let resume = false;
  let restart = false;
  let help = false;
  const options = new Map([
    ['--manifest', 'manifestPath'],
    ['--publication', 'publicationPath'],
    ['--completion', 'completionPath'],
    ['--provider-catalog-configmap', 'providerCatalogConfigMapName'],
    ['--provider-catalog-source-commit', 'providerCatalogSourceCommit'],
    ['--subscription-id', 'subscriptionId'],
    ['--tenant-id', 'tenantId'],
    ['--resource-group', 'resourceGroup'],
    ['--cluster', 'clusterName'],
    ['--namespace', 'namespace'],
    ['--node-min', 'nodeMin'],
    ['--node-max', 'nodeMax'],
  ]);

  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (['--help', '-h'].includes(arg)) {
      help = true;
    } else if (arg === '--execute') {
      execute = true;
    } else if (arg === '--dry-run') {
      dryRun = true;
    } else if (arg === '--pre-acceptance') {
      preAcceptance = true;
    } else if (arg === '--resume') {
      resume = true;
    } else if (arg === '--restart') {
      restart = true;
    } else if ([...options.keys()].some((name) => arg === name || arg.startsWith(`${name}=`))) {
      const name = [...options.keys()].find((option) => arg === option || arg.startsWith(`${option}=`));
      const key = options.get(name);
      let value;
      if (arg.startsWith(`${name}=`)) {
        value = arg.slice(name.length + 1);
      } else {
        value = argv[++index];
      }
      if (value === undefined || value === '' || value.startsWith('--')) {
        fail(`${name} requires an explicit value`);
      }
      values[key] = value;
    } else if (!arg.startsWith('-') && releaseTag === undefined) {
      releaseTag = arg;
    } else {
      fail(`unknown argument "${arg}"`);
    }
  }

  if (help) return { help: true, preAcceptance };
  if (execute && dryRun) fail('--execute and --dry-run are mutually exclusive');
  if (!RELEASE_TAG.test(releaseTag ?? '')) fail('an exact v1.x.y GitHub release tag is required');
  const bootstrapRelease = BOOTSTRAP_RELEASES[releaseTag];
  if (preAcceptance && !bootstrapRelease) {
    fail(`--pre-acceptance is limited to the exact ${Object.keys(BOOTSTRAP_RELEASES).join(' and ')} publications`);
  }
  if (resume && restart) fail('--resume and --restart are mutually exclusive');
  if ((resume || restart) && !execute) fail('--resume and --restart require explicit --execute');
  const required = [
    'manifestPath', 'publicationPath',
    'subscriptionId', 'tenantId', 'resourceGroup', 'clusterName', 'namespace', 'nodeMin', 'nodeMax',
  ];
  const optionNames = new Map([...options].map(([option, key]) => [key, option]));
  for (const key of required) requiredString(values[key], optionNames.get(key));
  if (preAcceptance) {
    if (bootstrapRelease.continuation) {
      requiredString(values.completionPath, '--completion');
    } else if (values.completionPath !== undefined) {
      fail(`--completion is not accepted for the ${releaseTag} normal publication`);
    }
    requiredString(values.providerCatalogConfigMapName, '--provider-catalog-configmap');
    if (!SHA.test(values.providerCatalogSourceCommit ?? '') ||
        /^0+$/.test(values.providerCatalogSourceCommit)) {
      fail('--provider-catalog-source-commit must be a nonzero full Git commit SHA');
    }
  }
  requiredString(values.providerCatalogConfigMapName, '--provider-catalog-configmap');
  if (!SHA.test(values.providerCatalogSourceCommit ?? '') ||
      /^0+$/.test(values.providerCatalogSourceCommit)) {
    fail('--provider-catalog-source-commit must be a nonzero full Git commit SHA');
  }
  const target = {
    subscriptionId: values.subscriptionId,
    tenantId: values.tenantId,
    resourceGroup: values.resourceGroup,
    clusterName: values.clusterName,
    namespace: values.namespace,
    nodeMin: Number(values.nodeMin),
    nodeMax: Number(values.nodeMax),
  };
  validateTarget(target);
  return {
    ...values,
    releaseTag,
    target,
    execute,
    dryRun: dryRun || !execute,
    resume,
    restart,
    preAcceptance,
    help: false,
  };
}

/** Validates the exact Azure subscription, tenant, resource group, cluster, and namespace contract. */
export function validateTarget(target) {
  if (!target || typeof target !== 'object' || Array.isArray(target)) fail('explicit P0 target is required');
  for (const key of ['subscriptionId', 'tenantId', 'resourceGroup', 'clusterName', 'namespace']) {
    requiredString(target[key], `target.${key}`);
    if (target[key] !== TARGET[key]) {
      fail(`target.${key} must be the exact approved v1 P0 value (${TARGET[key]})`);
    }
  }
  if (target.nodeMin !== TARGET.nodeMin || target.nodeMax !== TARGET.nodeMax) {
    fail(`target node bounds must be exactly ${TARGET.nodeMin}-${TARGET.nodeMax}`);
  }
  if (Object.keys(target).some((key) => !Object.hasOwn(TARGET, key))) {
    fail('target contains an unsupported field');
  }
  return TARGET;
}

/** Validates that the release manifest is published, source-bound, and consistent with repository files. */
export function validatePublishedManifest(manifest, { root, sourceSha }) {
  if (!SHA.test(sourceSha ?? '')) fail('release source SHA must be a lowercase full Git commit SHA');
  validateManifest(manifest, { root });
  if (manifest.stage !== 'release') {
    fail('the supplied manifest is still draft; an already-published release composition is required');
  }
  if (manifest.evidence.sourceSha !== sourceSha || manifest.evidence.deploymentSha !== sourceSha) {
    fail('release manifest source and deployment evidence must match the exact tagged source commit');
  }
  const product = manifest.components.filter((component) => component.id !== 'Agentweaver.FoundationProbe');
  const ids = product.map((component) => component.id).sort();
  const expected = [...PRODUCT_COMPONENTS].sort();
  if (ids.length !== expected.length || ids.some((id, index) => id !== expected[index])) {
    fail('release manifest must contain exactly the 23 v1 product components and exclude acceptance Probe from product inventory');
  }
  for (const service of PRODUCT_SERVICES) {
    const component = product.find((entry) => entry.id === service);
    if (!component || component.kind !== 'service' || component.version !== '1.0.0' ||
        !DIGEST.test(component.imageDigest ?? '') || /^0+$/.test(component.imageDigest.slice(7))) {
      fail(`${service} must be the v1.0.0 product service with a nonzero published image digest`);
    }
  }
  for (const component of product) {
    if (component.kind !== 'service' && component.version !== '1.0.0') {
      fail(`${component.id} must remain pinned at the approved product version 1.0.0`);
    }
  }
  const probe = manifest.components.find((component) => component.id === 'Agentweaver.FoundationProbe');
  if (probe && (probe.kind !== 'service' || probe.version !== '0.0.1')) {
    fail('FoundationProbe is acceptance-only at 0.0.1 and must remain outside product deployment');
  }
  return manifest;
}

/**
 * Validates the tracked bootstrap draft and adds only source-bound published image digests.
 * The draft itself remains unchanged and carries no deployment or persona acceptance claim.
 */
export function validateBootstrapManifest(manifest, publication, {
  root, sourceSha, releaseTag, manifestPath, run,
}) {
  const bootstrapRelease = BOOTSTRAP_RELEASES[releaseTag];
  if (!bootstrapRelease || sourceSha !== bootstrapRelease.sourceSha) {
    fail(`pre-acceptance bootstrap is bound to the exact ${releaseTag} source commit`);
  }
  const canonicalManifestPath = path.resolve(root, 'releases', 'foundation.json');
  if (path.resolve(root, manifestPath ?? '') !== canonicalManifestPath) {
    fail('pre-acceptance bootstrap requires the tracked releases/foundation.json manifest');
  }
  const trackedPath = run('git', [
    'ls-tree', '-r', '--full-tree', '--name-only', sourceSha, '--', 'releases/foundation.json',
  ], { cwd: root });
  if (trackedPath !== 'releases/foundation.json') {
    fail(`the ${releaseTag} draft manifest is not tracked at the exact published source commit`);
  }
  const sourceManifest = parseJson(run('git', [
    'show', `${sourceSha}:releases/foundation.json`,
  ], { cwd: root }), `source-bound ${releaseTag} draft manifest`);
  if (!isDeepStrictEqual(manifest, sourceManifest)) {
    fail('the supplied draft manifest differs from releases/foundation.json at the published source commit');
  }
  const readSourceProject = (file) => {
    const relativePath = path.relative(root, file);
    if (relativePath === '..' || relativePath.startsWith(`..${path.sep}`) || path.isAbsolute(relativePath)) {
      fail('a bootstrap manifest project path escapes the published source tree');
    }
    const sourcePath = relativePath.split(path.sep).join('/');
    return run('git', ['show', `${sourceSha}:${sourcePath}`], { cwd: root });
  };
  validateManifest(manifest, { root, readProject: readSourceProject });
  if (manifest.stage !== 'draft' || manifest.evidence !== undefined) {
    fail('pre-acceptance bootstrap requires the tracked draft without release evidence');
  }

  const product = manifest.components.filter((component) =>
    component.id !== 'Agentweaver.FoundationProbe');
  const ids = product.map((component) => component.id).sort();
  const expectedIds = [...PRODUCT_COMPONENTS].sort();
  if (ids.length !== expectedIds.length ||
      ids.some((id, index) => id !== expectedIds[index])) {
    fail(`the tracked draft must contain exactly the 23 ${releaseTag} product components`);
  }
  if (releaseTag === BOOTSTRAP_RELEASE_TAG) {
    for (const component of product) {
      if (component.version !== '1.0.0') {
        fail(`${component.id} must remain pinned at the approved product version 1.0.0`);
      }
    }
  }

  const deploymentManifest = structuredClone(manifest);
  for (const id of PRODUCT_SERVICES) {
    const component = deploymentManifest.components.find((entry) => entry.id === id);
    const repository = expectedRepository(id);
    const image = publication?.published?.find((entry) => entry?.id === id)?.image;
    const digest = typeof image === 'string' && image.startsWith(`${repository}@`)
      ? image.slice(repository.length + 1)
      : '';
    if (!DIGEST.test(digest) || /^sha256:0+$/.test(digest)) {
      fail(`the source-bound publication receipt has no valid immutable image digest for ${id}`);
    }
    component.imageDigest = digest;
  }
  return deploymentManifest;
}

/** Rejects bootstrap receipts that do not match the exact release's publication mode and source. */
function validateBootstrapPublication(tag, publication) {
  const bootstrapRelease = BOOTSTRAP_RELEASES[tag];
  const hasContinuation = publication !== null && typeof publication === 'object' &&
    Object.hasOwn(publication, 'continuation');
  if (!bootstrapRelease) {
    fail(`pre-acceptance bootstrap requires the exact source-bound ${tag} published receipt`);
  }
  if (bootstrapRelease.continuation) {
    if (publication?.sourceSha !== bootstrapRelease.sourceSha || publication.status !== 'published' ||
        !publication.continuation || typeof publication.continuation !== 'object' ||
        Array.isArray(publication.continuation)) {
      fail('pre-acceptance bootstrap requires the exact source-bound v1.0.0 published continuation receipt');
    }
  } else {
    if (publication?.sourceSha !== bootstrapRelease.sourceSha || publication.status !== 'published') {
      fail(`pre-acceptance bootstrap requires the exact source-bound ${tag} published receipt`);
    }
    if (hasContinuation) {
      fail(`${tag} pre-acceptance bootstrap rejects continuation publication receipts`);
    }
  }
}

function kindFor(component) {
  return component.kind === 'service' ? 'image' : 'package';
}

function expectedRepository(componentId) {
  return `${REGISTRY}/${componentImageRepository(componentId)}`;
}

/** Checks publication receipt schema, source, provenance hash, and component artifact coverage. */
export function validatePublicationReceipt(receipt, manifest, sourceSha) {
  if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt) ||
      receipt.schemaVersion !== 1 || receipt.sourceSha !== sourceSha ||
      receipt.status !== 'published' || !/^[a-f0-9]{64}$/.test(receipt.provenanceSha256 ?? '') ||
      !Array.isArray(receipt.planned) || !Array.isArray(receipt.published)) {
    fail('publication receipt is missing, partial, or not bound to the tagged source');
  }
  const product = manifest.components.filter((component) => component.id !== 'Agentweaver.FoundationProbe');
  const productById = new Map(product.map((component) => [component.id, component]));
  // New receipts identify changed packages; legacy receipts must still prove every package version.
  const services = product.filter((component) => component.kind === 'service');
  const packages = product.filter((component) => component.kind !== 'service');
  const packageIds = new Set(packages.map((component) => component.id));
  const requiredPackageIds = receipt.requiredPackageIds ?? [...packageIds];
  if (!Array.isArray(requiredPackageIds) ||
      new Set(requiredPackageIds).size !== requiredPackageIds.length ||
      requiredPackageIds.some((id) => !packageIds.has(id))) {
    fail('publication receipt has an invalid required package selection');
  }
  if (receipt.planned.filter((entry) => entry?.kind === 'image').length < services.length ||
      receipt.published.filter((entry) => entry?.kind === 'image').length < services.length) {
    fail('publication receipt must contain every product service image');
  }
  const planned = new Map();
  for (const entry of receipt.planned) {
    if (!entry || typeof entry !== 'object' || planned.has(entry.id)) fail('publication plan has malformed or duplicate entries');
    planned.set(entry.id, entry);
  }
  const published = new Map();
  for (const entry of receipt.published) {
    if (!entry || typeof entry !== 'object' || published.has(entry.id)) fail('published receipt has malformed or duplicate entries');
    published.set(entry.id, entry);
  }
  if (planned.size !== published.size || [...planned.keys()].some((id) => !published.has(id))) {
    fail('publication plan and result selections differ');
  }
  for (const id of requiredPackageIds) {
    if (!planned.has(id) || !published.has(id)) {
      fail(`publication receipt does not prove required package ${id}`);
    }
  }
  const plannedPackageIds = [...planned.values()]
    .filter((entry) => entry.kind === 'package').map((entry) => entry.id).sort();
  if (JSON.stringify([...requiredPackageIds].sort()) !== JSON.stringify(plannedPackageIds)) {
    fail('publication plan does not match its required package selection');
  }
  for (const id of planned.keys()) {
    if (!productById.has(id)) fail(`publication receipt contains a non-product component ${id}`);
  }

  let agentHostMeasurement;
  for (const component of product) {
    const expectedKind = kindFor(component);
    const plan = planned.get(component.id);
    const result = published.get(component.id);
    if (!plan && !result && component.kind !== 'service') continue;
    if (!plan || !result || plan.version !== component.version ||
        plan.kind !== expectedKind || result.version !== component.version ||
        result.kind !== expectedKind || !/^[a-f0-9]{64}$/.test(plan.sha256 ?? '')) {
      fail(`publication receipt does not prove the exact ${component.id} ${component.version} artifact`);
    }
    if (component.kind === 'service') {
      const expectedImage = `${expectedRepository(component.id)}@${component.imageDigest}`;
      // The original v1.0.0 receipt used version-only tags; current receipts use source-bound tags.
      const imageTag = receipt.requiredPackageIds === undefined
        ? component.version : componentImageTag(component.version, sourceSha);
      if (plan.destination !== `${expectedRepository(component.id)}:${imageTag}` ||
          result.image !== expectedImage || result.archiveSha256 !== plan.sha256 ||
          !DIGEST.test(component.imageDigest)) {
        fail(`publication receipt and release manifest disagree on the published image for ${component.id}`);
      }
      if (component.id === 'Agentweaver.AgentHost') {
        const measurement = result.preparedOciMeasurement;
        if (measurement?.kind !== 'agenthost-image' || measurement.schemaVersion !== 1 ||
            measurement.sourceSha !== sourceSha || !SHA.test(measurement.sourceTree ?? '') ||
            measurement.image?.version !== component.version ||
            measurement.image.platform !== 'linux/amd64' ||
            !DIGEST.test(measurement.image.digest ?? '') ||
            !DIGEST.test(measurement.image.configDigest ?? '') ||
            !Number.isSafeInteger(measurement.image.compressedPullBytes) ||
            measurement.image.compressedPullBytes <= 0 ||
            result.imageConfigDigest !== measurement.image.configDigest ||
            !/^[a-f0-9]{64}$/.test(result.ociArchiveSha256 ?? '')) {
          fail('AgentHost publication receipt lacks source-bound linux/amd64 image and compressed-byte evidence');
        }
        agentHostMeasurement = {
          image: `${expectedRepository(component.id)}@${measurement.image.digest}`,
          digest: measurement.image.digest,
          compressedPullBytes: measurement.image.compressedPullBytes,
          preparedOciMeasurement: measurement,
        };
      } else if (result.preparedOciMeasurement !== undefined ||
          result.imageConfigDigest !== undefined || result.ociArchiveSha256 !== undefined) {
        fail(`${component.id} publication receipt contains unsupported AgentHost image evidence`);
      }
    } else if (result.sha256 !== plan.sha256 || result.feed !== plan.destination ||
        !/^[a-f0-9]{64}$/.test(result.sha256 ?? '')) {
      fail(`publication receipt package hash does not match its plan for ${component.id}`);
    }
  }
  if (planned.has('Agentweaver.FoundationProbe') || published.has('Agentweaver.FoundationProbe')) {
    fail('acceptance-only FoundationProbe must not be part of product publication or deployment');
  }
  if (!agentHostMeasurement) fail('AgentHost publication receipt has no verified per-Sandbox image measurement');
  return PRODUCT_SERVICES.map((id) => {
    const component = manifest.components.find((entry) => entry.id === id);
    const result = { id, name: SERVICE_NAMES[id], image: `${expectedRepository(id)}@${component.imageDigest}` };
    if (id === 'Agentweaver.AgentHost') {
      result.sandboxImage = agentHostMeasurement.image;
      result.compressedPullBytes = agentHostMeasurement.compressedPullBytes;
      result.sandboxImageDigest = agentHostMeasurement.digest;
      result.preparedOciMeasurement = agentHostMeasurement.preparedOciMeasurement;
    }
    return result;
  });
}

/** Validates generated Kubernetes resources against the selected immutable images and namespace. */
export function validateRuntimeManifest(document, images, namespace, brokerRunBinding, providerCatalog) {
  const binding = brokerRunBinding === undefined
    ? undefined
    : validateBrokerRunBinding(brokerRunBinding);
  const bindingFingerprint = binding ? brokerRunBindingFingerprint(binding) : undefined;
  const expectedBindingPolicies = new Map((binding ? createBrokerRunBindingPolicies(namespace) : [])
    .map((policy) => [policy.metadata.name, policy]));
  const items = document?.kind === 'List' ? document.items
    : Array.isArray(document) ? document : null;
  if (!Array.isArray(items) || items.length === 0) fail('runtime manifest must be a rendered Kubernetes List');
  const hostImage = images.find((image) => image.id === 'Agentweaver.AgentHost');
  if (!hostImage?.sandboxImage || !Number.isSafeInteger(hostImage.compressedPullBytes) ||
      hostImage.compressedPullBytes <= 0) {
    fail('runtime validation requires the exact measured per-Sandbox AgentHost image');
  }
  const expectedNames = new Map(images
    .filter((image) => image.id !== 'Agentweaver.AgentHost')
    .map((image) => [image.name, image]));
  const deployments = new Map();
  const services = new Map();
  const bindingPolicies = new Map();
  for (const item of items) {
    if (!item || typeof item !== 'object' || Array.isArray(item)) {
      fail('runtime manifest contains an invalid resource');
    }
    if (item.kind === 'CiliumNetworkPolicy') {
      const expectedPolicy = expectedBindingPolicies.get(item.metadata?.name);
      if (!expectedPolicy || !isDeepStrictEqual(item, expectedPolicy)) {
        fail('runtime manifest may contain only the exact opt-in Broker-to-Projects Cilium policies');
      }
      if (bindingPolicies.has(item.metadata.name)) {
        fail(`runtime manifest contains duplicate CiliumNetworkPolicy "${item.metadata.name}"`);
      }
      bindingPolicies.set(item.metadata.name, item);
      continue;
    }
    if (!['Deployment', 'Service'].includes(item.kind)) {
      fail('runtime manifest may contain only Deployment and ClusterIP Service resources; held prerequisites are not managed here');
    }
    if (item.apiVersion !== (item.kind === 'Deployment' ? 'apps/v1' : 'v1') ||
        item.metadata?.namespace !== namespace) {
      fail(`${item.kind} must use the exact v1 P0 namespace and API version`);
    }
    const name = item.metadata?.name;
    if (!expectedNames.has(name)) fail(`runtime manifest contains unsupported workload "${name}"`);
    const resourceMap = item.kind === 'Deployment' ? deployments : services;
    if (resourceMap.has(name)) fail(`runtime manifest contains duplicate ${item.kind} "${name}"`);
    resourceMap.set(name, item);
  }
  const expectedBindingPolicyCount = binding ? 2 : 0;
  if (bindingPolicies.size !== expectedBindingPolicyCount) {
    fail('runtime manifest must contain exactly two Broker-to-Projects Cilium policies when run binding is enabled');
  }
  if (deployments.size !== DEPLOYED_SERVICES.length || services.size !== DEPLOYED_SERVICES.length) {
    fail('runtime manifest must include exactly one Service and Deployment for each of the nine shared product services');
  }

  // Knowledge, Projects & Config, and Orchestrator must consume the same reviewed catalog snapshot.
  const catalogReference = (name) => {
    const pod = deployments.get(name)?.spec?.template;
    const container = pod?.spec?.containers?.[0];
    const entries = (container?.envFrom ?? [])
      .filter(({ prefix }) => prefix === 'ProjectsConfig__ProviderCatalog__');
    const sourceCommit = pod?.metadata?.annotations?.['agentweaver.io/provider-catalog-source'];
    if (entries.length !== 1 || typeof entries[0].configMapRef?.name !== 'string' ||
        entries[0].configMapRef.optional === true ||
        !SHA.test(sourceCommit ?? '') || /^0+$/.test(sourceCommit)) {
      fail(`${name} must load one immutable-source Projects provider catalog ConfigMap`);
    }
    return { configMapName: entries[0].configMapRef.name, sourceCommit };
  };
  const projectsCatalog = catalogReference('projects-config');
  const expectedCatalog = providerCatalog ?? projectsCatalog;
  if (typeof expectedCatalog.configMapName !== 'string' ||
      !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?(?:\.[a-z0-9](?:[-a-z0-9]*[a-z0-9])?)*$/.test(expectedCatalog.configMapName) ||
      expectedCatalog.configMapName === 'projects-config-runtime-config' ||
      !SHA.test(expectedCatalog.sourceCommit ?? '') || /^0+$/.test(expectedCatalog.sourceCommit)) {
    fail('runtime validation requires the exact provider catalog ConfigMap name and source commit');
  }
  for (const name of ['knowledge', 'projects-config', 'orchestrator']) {
    const reference = catalogReference(name);
    if (reference.configMapName !== expectedCatalog.configMapName ||
        reference.sourceCommit !== expectedCatalog.sourceCommit) {
      fail(`${name} must consume the exact same provider catalog ConfigMap and source commit`);
    }
  }

  for (const [name, expected] of expectedNames) {
    const deployment = deployments.get(name);
    const service = services.get(name);
    if (!deployment || !service) fail(`runtime manifest is missing the ${name} Deployment or Service`);
    const podSpec = deployment.spec?.template?.spec;
    const containers = podSpec?.containers;
    if (!Array.isArray(containers) || containers.length !== 1 ||
        containers[0].name !== name || containers[0].image !== expected.image) {
      fail(`${name} must run only its exact immutable GHCR image from the published release`);
    }
    const labels = deployment.spec?.template?.metadata?.labels;
    if (labels?.['app.kubernetes.io/name'] !== name ||
        labels?.['agentweaver.io/service'] !== name ||
        labels?.['agentweaver.io/workload'] !== name) {
      fail(`${name} Pod must expose its exact service-owner selectors`);
    }
    if (IDENTITY_CLIENT_SERVICES.has(name)
      ? labels['agentweaver.io/identity-client'] !== 'true'
      : labels['agentweaver.io/identity-client'] !== undefined) {
      fail(`${name} Pod must preserve its exact Identity Broker client selector`);
    }
    const usesWorkloadIdentity = WORKLOAD_IDENTITY_SERVICES.has(name);
    if (usesWorkloadIdentity
      ? labels['azure.workload.identity/use'] !== 'true'
      : labels['azure.workload.identity/use'] !== undefined) {
      fail(`${name} Pod must preserve only its existing Azure Workload Identity injection setting`);
    }
    const bindingAnnotation = deployment.spec?.template?.metadata?.annotations?.[BROKER_RUN_BINDING_ANNOTATION];
    if (binding && ['identity-broker', 'projects-config'].includes(name)
      ? bindingAnnotation !== bindingFingerprint
      : bindingAnnotation !== undefined) {
      fail(`${name} Pod must preserve only its exact opt-in Broker run-binding fingerprint`);
    }
    if (podSpec.serviceAccountName !== name ||
        deployment.spec?.replicas !== 1 ||
        deployment.spec?.strategy?.type !== 'Recreate' ||
        podSpec.automountServiceAccountToken !== true ||
        podSpec.securityContext?.runAsNonRoot !== true ||
        !Number.isSafeInteger(podSpec.securityContext?.runAsUser) || podSpec.securityContext.runAsUser < 1 ||
        !Number.isSafeInteger(podSpec.securityContext?.runAsGroup) || podSpec.securityContext.runAsGroup < 1 ||
        podSpec.securityContext?.seccompProfile?.type !== 'RuntimeDefault' ||
        podSpec.initContainers?.length || podSpec.ephemeralContainers?.length ||
        podSpec.hostNetwork || podSpec.hostPID || podSpec.hostIPC) {
      fail(`${name} must use its existing service identity and a single restricted runtime pod`);
    }
    const container = containers[0];
    const providerAdapterVersionInputs = PROVIDER_ADAPTER_VERSION_RUNTIME_INPUTS
      .filter(({ workload }) => workload === name);
    const providerAdapterVersionNames = new Set(
      providerAdapterVersionInputs.map(({ name: envName }) => envName));
    const providerRevisionInputs = PROVIDER_OPTIONS_REVISION_RUNTIME_INPUTS
      .filter(({ workload }) => workload === name);
    const providerRevisionNames = new Set(providerRevisionInputs.map(({ name: envName }) => envName));
    // Runtime adapter versions are pinned to the reviewed catalog descriptor.
    for (const expectedVersion of providerAdapterVersionInputs) {
      const matches = (container.env ?? []).filter(({ name: envName }) => envName === expectedVersion.name);
      if (matches.length !== 1 || matches[0].value !== expectedVersion.value ||
          matches[0].valueFrom !== undefined) {
        fail(`${name} must set each exact approved provider AdapterVersion input once`);
      }
    }
    if (container.command !== undefined || container.args !== undefined ||
        container.securityContext?.allowPrivilegeEscalation !== false ||
        container.securityContext?.readOnlyRootFilesystem !== true ||
        !container.securityContext?.capabilities?.drop?.includes('ALL')) {
      fail(`${name} must not override its image entrypoint and must retain the restricted container profile`);
    }
    for (const env of container.env ?? []) {
      if (typeof env.name === 'string' && env.name.endsWith('OptionsRevision')) {
        const expectedRevision = providerRevisionInputs.find(({ name: envName }) => envName === env.name);
        if (!expectedRevision || env.value !== expectedRevision.value || env.valueFrom !== undefined) {
          fail(`${name} may set only its exact approved provider OptionsRevision inputs and values`);
        }
      }
      if (typeof env.name === 'string' && env.name.endsWith('AdapterVersion')) {
        const expectedVersion = providerAdapterVersionInputs.find(({ name: envName }) => envName === env.name);
        if (!expectedVersion || env.value !== expectedVersion.value || env.valueFrom !== undefined) {
          fail(`${name} may set only its exact approved provider AdapterVersion inputs and values`);
        }
      }
      if (env.value !== undefined && ![
        'ASPNETCORE_URLS',
        'ASPNETCORE_Kestrel__Certificates__Default__Path',
        'ASPNETCORE_Kestrel__Certificates__Default__KeyPath',
        'IdentityBroker__Signing__PfxPath',
        'Environment__Sandbox__AgentSandbox__ContainerImage',
        'Environment__Sandbox__AgentSandbox__ContainerImagePlatform',
        'Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes',
        ...providerAdapterVersionNames,
        ...providerRevisionNames,
      ].includes(env.name)) {
        fail(`${name} may not embed runtime credentials or operator configuration as literal environment values`);
      }
    }
    for (const volume of podSpec.volumes ?? []) {
      if (volume.hostPath !== undefined || volume.csi !== undefined || volume.nfs !== undefined) {
        fail(`${name} cannot mount host paths, CSI devices, or unreviewed network filesystems`);
      }
    }
    const web = name === 'web';
    const portName = web ? 'http' : 'https';
    const portNumber = web ? 8080 : 8443;
    const scheme = web ? 'HTTP' : 'HTTPS';
    if (!Array.isArray(container.ports) || container.ports.length !== 1 ||
        container.ports[0].name !== portName || container.ports[0].containerPort !== portNumber ||
        container.ports[0].protocol !== 'TCP') {
      fail(`${name} must expose only its source-declared ${scheme} application port`);
    }
    const environment = container.env ?? [];
    const getEnvValue = (key) => {
      const matches = environment.filter((entry) => entry.name === key);
      if (matches.length !== 1 || typeof matches[0].value !== 'string') return undefined;
      return matches[0].value;
    };
    for (const expectedRevision of providerRevisionInputs) {
      if (getEnvValue(expectedRevision.name) !== expectedRevision.value) {
        fail(`${name} must set its exact approved provider OptionsRevision input ${expectedRevision.name}`);
      }
    }
    if (getEnvValue('ASPNETCORE_URLS') !== `${web ? 'http' : 'https'}://+:${portNumber}`) {
      fail(`${name} must explicitly configure its ${scheme} listener`);
    }
    if (web) {
      if (environment.some((entry) => entry.name === 'ASPNETCORE_Kestrel__Certificates__Default__Path' ||
          entry.name === 'ASPNETCORE_Kestrel__Certificates__Default__KeyPath')) {
        fail('Web is source-owned HTTP on port 8080 and cannot claim an unconfigured HTTPS listener');
      }
    } else {
      const certificatePath = getEnvValue('ASPNETCORE_Kestrel__Certificates__Default__Path');
      const keyPath = getEnvValue('ASPNETCORE_Kestrel__Certificates__Default__KeyPath');
      if (typeof certificatePath !== 'string' || typeof keyPath !== 'string' ||
          !certificatePath.startsWith('/') || !keyPath.startsWith('/') ||
          path.posix.dirname(certificatePath) !== path.posix.dirname(keyPath)) {
        fail(`${name} must explicitly mount a private TLS certificate and key`);
      }
      const tlsDirectory = path.posix.dirname(certificatePath);
      const tlsMount = (podSpec.containers[0].volumeMounts ?? []).find((mount) =>
        mount.mountPath === tlsDirectory && mount.readOnly === true);
      const tlsVolume = (podSpec.volumes ?? []).find((volume) =>
        volume.name === tlsMount?.name && typeof volume.secret?.secretName === 'string' &&
        volume.secret.optional !== true);
      if (!tlsVolume) fail(`${name} TLS files must come from an existing read-only Secret volume`);
      if (tlsVolume.secret.secretName !== `${name}-tls`) {
        fail(`${name} must use its exact service TLS Secret ${name}-tls`);
      }
    }
    const backendCaVolumes = (podSpec.volumes ?? []).filter((volume) =>
      volume.name === 'backend-ca' || volume.configMap?.name === BACKEND_TLS_CA_CONFIGMAP);
    const backendCaVolumeNames = new Set(backendCaVolumes.map((volume) => volume.name));
    const backendCaMounts = (container.volumeMounts ?? []).filter((mount) =>
      mount.name === 'backend-ca' || backendCaVolumeNames.has(mount.name));
    const mountsBackendCa = name === 'mcp' || (name === 'identity-broker' && binding);
    if (mountsBackendCa) {
      const backendCaVolume = backendCaVolumes[0];
      const backendCaMount = backendCaMounts[0];
      const expectedVolumeName = name === 'mcp' ? 'backend-ca' : 'projects-ca';
      if (backendCaVolumes.length !== 1 ||
          backendCaVolume.name !== expectedVolumeName ||
          backendCaVolume.configMap?.name !== BACKEND_TLS_CA_CONFIGMAP ||
          backendCaVolume.configMap.optional === true ||
          backendCaVolume.configMap.items?.length !== 1 ||
          backendCaVolume.configMap.items[0].key !== 'ca.crt' ||
          backendCaVolume.configMap.items[0].path !== 'ca.crt' ||
          backendCaMounts.length !== 1 ||
          backendCaMount.name !== expectedVolumeName ||
          backendCaMount.mountPath !== '/var/run/service-backend-ca' ||
          backendCaMount.readOnly !== true ||
          (name === 'identity-broker' &&
            (backendCaVolume.configMap.optional !== false ||
             backendCaVolume.configMap.defaultMode !== 0o440))) {
        fail(`${name} must mount the exact public backend CA ConfigMap read-only`);
      }
    } else if (backendCaVolumes.length || backendCaMounts.length) {
      fail(binding
        ? 'only MCP and an opted-in Identity Broker may mount the public backend CA ConfigMap'
        : 'only MCP may mount the public backend CA ConfigMap');
    }
    // Endpoint-selector keys are file configuration because their '/' and ':' are not valid env-var names.
    const ciliumConfigVolumes = (podSpec.volumes ?? []).filter((volume) =>
      volume.name === 'cilium-options' || volume.configMap?.name === 'environment-cilium-config');
    const ciliumConfigMounts = (container.volumeMounts ?? []).filter((mount) =>
      mount.name === 'cilium-options' || mount.mountPath === '/app/appsettings.Production.json');
    if (name === 'environment') {
      const ciliumVolume = ciliumConfigVolumes[0];
      const ciliumMount = ciliumConfigMounts[0];
      if (ciliumConfigVolumes.length !== 1 ||
          ciliumVolume.configMap?.name !== 'environment-cilium-config' ||
          ciliumVolume.configMap.optional === true ||
          ciliumVolume.configMap.items?.length !== 1 ||
          ciliumVolume.configMap.items[0].key !== 'appsettings.Production.json' ||
          ciliumVolume.configMap.items[0].path !== 'appsettings.Production.json' ||
          ciliumConfigMounts.length !== 1 ||
          ciliumMount.name !== 'cilium-options' ||
          ciliumMount.mountPath !== '/app/appsettings.Production.json' ||
          ciliumMount.subPath !== 'appsettings.Production.json' ||
          ciliumMount.readOnly !== true) {
        fail('Environment must mount the exact read-only Cilium options file');
      }
      if (getEnvValue('Environment__Sandbox__AgentSandbox__ContainerImage') !== hostImage.sandboxImage ||
          getEnvValue('Environment__Sandbox__AgentSandbox__ContainerImagePlatform') !== 'linux/amd64' ||
          getEnvValue('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes') !==
            String(hostImage.compressedPullBytes)) {
        fail('Environment must pin the exact measured AgentHost image, platform, and compressed pull bytes');
      }
    } else if (environment.some((entry) =>
      entry.name.startsWith('Environment__Sandbox__AgentSandbox__ContainerImage'))) {
      fail('only Environment may configure the per-Sandbox AgentHost image');
    } else if (ciliumConfigVolumes.length || ciliumConfigMounts.length) {
      fail('only Environment may mount the Cilium options file');
    }
    for (const [probeName, expectedPath] of [['livenessProbe', '/health/live'], ['readinessProbe', '/health/ready']]) {
      const probe = container[probeName];
      if (probe?.httpGet?.path !== expectedPath || probe.httpGet.scheme !== scheme ||
          probe.httpGet.port !== portName) {
        fail(`${name} requires its source-owned ${scheme} ${expectedPath} ${probeName}`);
      }
    }
    if (service.spec?.type !== 'ClusterIP' ||
        service.metadata?.labels?.['app.kubernetes.io/name'] !== name ||
        service.metadata?.labels?.['agentweaver.io/service'] !== name ||
        service.spec?.selector?.['app.kubernetes.io/name'] !== name ||
        deployment.spec?.selector?.matchLabels?.['app.kubernetes.io/name'] !== name ||
        deployment.spec?.template?.metadata?.labels?.['app.kubernetes.io/name'] !== name ||
        !Array.isArray(service.spec?.ports) ||
        service.spec.ports.length !== 1 ||
        service.spec.ports[0].port !== (name === 'identity-broker' ? 443 : portNumber) ||
        service.spec.ports[0].targetPort !== portName ||
        service.spec.ports[0].protocol !== 'TCP') {
      fail(`${name} Service must remain private ClusterIP ${scheme}`);
    }
    for (const forbidden of ['hostNetwork', 'hostPID', 'hostIPC', 'hostAliases']) {
      if (podSpec[forbidden] !== undefined && podSpec[forbidden] !== false) {
        fail(`${name} cannot use ${forbidden}`);
      }
    }
    if (podSpec.imagePullSecrets?.length) fail(`${name} cannot add or invent image-pull credentials`);
  }
  if (deployments.has('agenthost') || services.has('agenthost')) {
    fail('AgentHost must remain per-Sandbox through Environment provider options, not a shared Deployment or Service');
  }
  return { apiVersion: 'v1', kind: 'List', items };
}

/** Validates the complete P0 Kustomize bundle and its rendered shared-service workload contract. */
export function validateKustomizeInstall(document, images, namespace, providerCatalog) {
  const items = document?.kind === 'List' ? document.items
    : Array.isArray(document) ? document : null;
  if (!Array.isArray(items) || items.length === 0) fail('Kustomize install must render Kubernetes resources');
  const allowedKinds = new Set([
    'Namespace', 'ResourceQuota', 'LimitRange', 'ServiceAccount', 'ConfigMap',
    'Service', 'Deployment', 'NetworkPolicy', 'CiliumNetworkPolicy', 'HTTPRoute',
  ]);
  const keyedResources = new Map();
  const namesByKind = new Map();
  const configMaps = new Map(items.filter(({ kind }) => kind === 'ConfigMap')
    .map((resource) => [resource.metadata.name, resource]));
  const incompleteRuntimeConfigMaps = new Set([...configMaps]
    .filter(([, resource]) =>
      resource.metadata.annotations?.['agentweaver.io/runtime-config-status'] === 'incomplete')
    .map(([name]) => name));
  for (const item of items) {
    if (!item || typeof item !== 'object' || Array.isArray(item) ||
        !allowedKinds.has(item.kind) || typeof item.metadata?.name !== 'string') {
      fail('Kustomize install contains an unsupported or invalid resource');
    }
    const name = item.metadata.name;
    const key = `${item.kind}/${item.kind === 'Namespace' ? '' : item.metadata.namespace ?? ''}/${name}`;
    if (keyedResources.has(key)) fail(`Kustomize install contains duplicate ${key}`);
    keyedResources.set(key, item);
    if (!namesByKind.has(item.kind)) namesByKind.set(item.kind, new Set());
    namesByKind.get(item.kind).add(name);
    if (item.kind === 'Namespace') {
      if (name !== namespace || item.metadata.namespace !== undefined ||
          item.metadata.labels?.['agentweaver.io/environment'] !== 'v1-p0') {
        fail('Kustomize install must include only the exact v1 P0 Namespace');
      }
    } else if (item.metadata.namespace !== namespace) {
      fail(`${item.kind}/${name} must use the exact v1 P0 namespace`);
    }
    if (item.kind === 'ConfigMap') {
      if (!item.data || typeof item.data !== 'object' || Array.isArray(item.data) ||
          Object.values(item.data).some((value) => typeof value !== 'string')) {
        fail(`ConfigMap/${name} must contain only string data`);
      }
      const status = item.metadata.annotations?.['agentweaver.io/runtime-config-status'];
      if (status === 'incomplete' &&
          !INCOMPLETE_CONFIG_MAPS.has(name)) {
        fail(`ConfigMap/${name} is unexpectedly marked incomplete`);
      }
      if (status === 'incomplete' &&
          (typeof item.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'] !== 'string' ||
            INCOMPLETE_RUNTIME_CONFIG_MAPS.has(name) &&
              !/P1 initialization is blocked because the initializer requires an already-ready 127\.0\.0\.1:15432 connection.*Guarded PostgreSQL bootstrap did not run because the preflight resource-inventory guard rejected an unexpected type\/name before Azure what-if/i
                .test(item.metadata.annotations['agentweaver.io/runtime-config-incomplete-reason']))) {
        fail(`ConfigMap/${name} must explain its incomplete pre-provisioning inputs`);
      }
      if (status === 'incomplete' && name === 'identity-broker-runtime-config-v2' &&
          !/signing state was not provisioned.*Key Vault PFX\/password persistence is unavailable/i
            .test(item.metadata.annotations['agentweaver.io/runtime-config-incomplete-reason'])) {
        fail(`ConfigMap/${name} must explain its unprovisioned signing and Key Vault state`);
      }
      if (status === 'incomplete') incompleteRuntimeConfigMaps.add(name);
      for (const [key, value] of Object.entries(item.data)) {
        const markers = [...value.matchAll(/CHANGEME-[A-Z0-9-]+/gi)].map(([marker]) => marker);
        if (markers.some((marker) => !PREPROVISIONING_RUNTIME_MARKERS.has(marker) ||
            status !== 'incomplete' || !INCOMPLETE_CONFIG_MAPS.has(name))) {
          fail(`ConfigMap/${name} contains an unresolved runtime input at ${key}`);
        }
      }
    }
    const unresolved = [...JSON.stringify(item).matchAll(/CHANGEME-[A-Z0-9-]+/gi)]
      .map(([marker]) => marker);
    const unresolvedAllowed = unresolved.every((marker) => {
      if (item.kind === 'ConfigMap') {
        return PREPROVISIONING_RUNTIME_MARKERS.has(marker) &&
          incompleteRuntimeConfigMaps.has(name) && INCOMPLETE_CONFIG_MAPS.has(name);
      }
      if (item.kind === 'ServiceAccount') {
        return /^CHANGEME-[a-z0-9-]+-client-id$/i.test(marker) &&
          Object.entries(WORKLOAD_IDENTITY_CONFIG).some(([service, identity]) =>
            service === name && incompleteRuntimeConfigMaps.has(identity.configMap) &&
            configMaps.get(identity.configMap)?.data?.[identity.clientId] === marker) ||
          marker === 'CHANGEME-identity-broker-client-id' && name === 'identity-broker' &&
            incompleteRuntimeConfigMaps.has('identity-broker-runtime-config-v2') &&
            configMaps.get('identity-broker-runtime-config-v2')?.data
              ?.IdentityBroker__SecretRedemption__WorkloadIdentityClientId === marker;
      }
      if (item.kind === 'CiliumNetworkPolicy') {
        // The Broker egress host may mirror only the marker in its incomplete runtime ConfigMap.
        return (marker === 'CHANGEME-MANAGED-HOST' &&
          ['broker-issuer-egress', 'events-and-sessions-network', 'projects-config-network'].includes(name) &&
          incompleteRuntimeConfigMaps.size > 0) ||
          (marker === 'CHANGEME-KEYVAULT-HOST' && name === 'identity-broker-egress' &&
            incompleteRuntimeConfigMaps.has('identity-broker-runtime-config-v2') &&
            configMaps.get('identity-broker-runtime-config-v2')?.data
              ?.IdentityBroker__SecretRedemption__VaultHost === marker);
      }
      if (item.kind === 'HTTPRoute') {
        return marker === 'CHANGEME-PORTAL-HOST' && name === 'web-application' &&
          (incompleteRuntimeConfigMaps.has('gateway-runtime-config') ||
            incompleteRuntimeConfigMaps.has('web-runtime-config'));
      }
      return false;
    });
    if (!unresolvedAllowed) {
      fail(`${item.kind}/${name} contains an unresolved non-approved runtime input`);
    }
  }

  const expectNames = (kind, expected, description) => {
    const actual = namesByKind.get(kind) ?? new Set();
    if (actual.size !== expected.size || [...expected].some((name) => !actual.has(name))) {
      fail(`Kustomize install must include the exact ${description}`);
    }
  };
  expectNames('Namespace', new Set([namespace]), 'P0 Namespace');
  expectNames('ResourceQuota', new Set([`${namespace}-quota`]), 'P0 ResourceQuota');
  expectNames('LimitRange', new Set([`${namespace}-limits`]), 'P0 LimitRange');
  expectNames('ServiceAccount', new Set(DEPLOYED_SERVICES.map((id) => SERVICE_NAMES[id])),
    'nine product ServiceAccounts');
  expectNames('ConfigMap', REQUIRED_INSTALL_CONFIG_MAPS, 'runtime ConfigMaps and provider catalog');
  expectNames('HTTPRoute', new Set(['web-application']), 'managed-domain Web HTTPRoute');
  expectNames('NetworkPolicy', new Set(['allow-dns-egress', 'default-deny-all']),
    'default-deny and DNS NetworkPolicies');
  const requiredCiliumPolicies = new Set(Object.values(SERVICE_NETWORK_POLICY_NAMES)
    .flatMap((names) => Array.isArray(names) ? names : [names]));
  expectNames('CiliumNetworkPolicy', requiredCiliumPolicies, 'service Cilium policies');

  // Keep the installed Portal route bound to the same origin configured for Gateway.
  const webOrigin = configMaps.get('gateway-runtime-config')?.data?.Gateway__WebOrigin;
  const webRoute = keyedResources.get(`HTTPRoute/${namespace}/web-application`);
  let webOriginUrl;
  try {
    webOriginUrl = new URL(webOrigin);
  } catch {
    fail('gateway-runtime-config must contain the absolute Portal Web origin');
  }
  if (webOriginUrl.protocol !== 'https:' || webOriginUrl.pathname !== '/' ||
      webOriginUrl.search || webOriginUrl.hash || webOriginUrl.username || webOriginUrl.password ||
      webRoute?.spec?.hostnames?.length !== 1 ||
      webRoute.spec.hostnames[0]?.toLowerCase() !== webOriginUrl.hostname.toLowerCase() ||
      webRoute.spec.parentRefs?.length !== 1 ||
      webRoute.spec.parentRefs[0]?.name !== 'identity-broker' ||
      webRoute.spec.parentRefs[0]?.namespace !== GATEWAY_NAMESPACE ||
      webRoute.spec.parentRefs[0]?.sectionName !== 'https' ||
      webRoute.spec.rules?.length !== 1 ||
      webRoute.spec.rules[0]?.matches?.length !== 1 ||
      webRoute.spec.rules[0].matches[0]?.path?.type !== 'PathPrefix' ||
      webRoute.spec.rules[0].matches[0].path.value !== '/' ||
      webRoute.spec.rules[0].backendRefs?.length !== 1 ||
      webRoute.spec.rules[0].backendRefs[0]?.name !== 'web' ||
      webRoute.spec.rules[0].backendRefs[0]?.port !== 8080) {
    fail('web-application HTTPRoute must serve the configured Portal origin through the managed Gateway listener');
  }
  for (const name of SERVICE_RUNTIME_CONFIG_MAP_NAMES) {
    if (!configMaps.has(name)) fail(`Kustomize install is missing ${name}`);
  }
  const agentHostConfig = configMaps.get('agenthost-runtime-v1');
  if (!agentHostConfig?.immutable) {
    fail('Kustomize install must include immutable agenthost-runtime-v1 source configuration');
  }
  let agentHostSettings;
  try {
    agentHostSettings = JSON.parse(agentHostConfig.data?.['appsettings.json'] ?? '');
  } catch {
    fail('agenthost-runtime-v1 must contain valid appsettings.json');
  }
  const agentHost = images.find((image) => image.id === 'Agentweaver.AgentHost');
  const imageSettings = agentHostSettings?.AgentHost?.Image;
  if (!agentHost?.sandboxImageDigest ||
      imageSettings?.Digest !== agentHost.sandboxImageDigest ||
      imageSettings?.Platform !== 'linux/amd64' ||
      imageSettings?.CompressedPullBytes !== agentHost.compressedPullBytes) {
    fail('agenthost-runtime-v1 image digest, platform, and compressed bytes must match the verified publication receipt');
  }
  const catalog = configMaps.get(providerCatalog?.configMapName);
  if (providerCatalog?.configMapName !== P0_PROVIDER_CATALOG_CONFIG_MAP ||
      !catalog?.immutable ||
      catalog.metadata.annotations?.['agentweaver.io/provider-catalog-product-source'] === undefined) {
    fail('Kustomize install must include the checked-in immutable Projects provider catalog');
  }

  const serviceAccounts = new Map(items.filter(({ kind }) => kind === 'ServiceAccount')
    .map((resource) => [resource.metadata.name, resource]));
  for (const [name, account] of serviceAccounts) {
    if (account.metadata.labels?.['agentweaver.io/service'] !== name ||
        account.automountServiceAccountToken !== true) {
      fail(`ServiceAccount/${name} must preserve its exact service identity contract`);
    }
  }
  for (const [name, identity] of Object.entries(WORKLOAD_IDENTITY_CONFIG)) {
    const account = serviceAccounts.get(name);
    const configMap = configMaps.get(identity.configMap);
    const clientId = configMap?.data?.[identity.clientId];
    const tenantId = configMap?.data?.[identity.tenantId];
    const annotations = account?.metadata.annotations ?? {};
    const incompleteClientId = incompleteRuntimeConfigMaps.has(identity.configMap) &&
      clientId === configMaps.get(identity.configMap)?.data?.[identity.clientId] &&
      /^CHANGEME-[a-z0-9-]+-client-id$/i.test(clientId ?? '');
    if ((!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(clientId ?? '') &&
        !incompleteClientId) ||
        !/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(tenantId ?? '') ||
        annotations['azure.workload.identity/client-id'] !== clientId ||
        annotations['azure.workload.identity/tenant-id'] !== tenantId ||
        account.metadata.labels?.['azure.workload.identity/use'] !== 'true') {
      fail(`ServiceAccount/${name} must use the exact client and tenant IDs from ${identity.configMap}`);
    }
  }
  for (const name of ['gateway', 'mcp', 'web']) {
    const account = serviceAccounts.get(name);
    if (account.metadata.labels?.['azure.workload.identity/use'] !== undefined ||
        account.metadata.annotations?.['azure.workload.identity/client-id'] !== undefined ||
        account.metadata.annotations?.['azure.workload.identity/tenant-id'] !== undefined) {
      fail(`ServiceAccount/${name} must not receive an unapproved workload identity`);
    }
  }

  const runtimeManifest = validateRuntimeManifest({
    apiVersion: 'v1',
    kind: 'List',
    items: items.filter(({ kind }) => kind === 'Deployment' || kind === 'Service'),
  }, images, namespace, undefined, providerCatalog);
  return {
    installationManifest: { apiVersion: 'v1', kind: 'List', items },
    runtimeManifest,
    incompleteRuntimeConfigMaps: [...incompleteRuntimeConfigMaps]
      .filter((name) => INCOMPLETE_RUNTIME_CONFIG_MAPS.has(name))
      .sort(),
  };
}

function resolvePodReferences(runtimeManifest) {
  const references = new Map();
  for (const item of runtimeManifest.items) {
    if (item.kind !== 'Deployment') continue;
    const pod = item.spec.template.spec;
    const add = (kind, name) => {
      if (!name) return;
      const key = `${kind}/${name}`;
      references.set(key, { kind, name });
    };
    add('serviceaccount', pod.serviceAccountName);
    for (const container of [...(pod.initContainers ?? []), ...pod.containers]) {
      for (const source of container.envFrom ?? []) {
        if (source.configMapRef?.optional !== true) add('configmap', source.configMapRef?.name);
        if (source.secretRef?.optional !== true) add('secret', source.secretRef?.name);
      }
      for (const env of container.env ?? []) {
        add('configmap', env.valueFrom?.configMapKeyRef?.optional === true ? undefined : env.valueFrom?.configMapKeyRef?.name);
        add('secret', env.valueFrom?.secretKeyRef?.optional === true ? undefined : env.valueFrom?.secretKeyRef?.name);
      }
    }
    for (const volume of pod.volumes ?? []) {
      add('configmap', volume.configMap?.optional === true ? undefined : volume.configMap?.name);
      add('secret', volume.secret?.optional === true ? undefined : volume.secret?.secretName);
      add('persistentvolumeclaim', volume.persistentVolumeClaim?.claimName);
    }
  }
  return [...references.values()];
}

function verifyBackendCertificates(target, { root, run }) {
  const caOutput = run('kubectl', [
    'get', 'configmap', BACKEND_TLS_CA_CONFIGMAP, '--namespace', target.namespace,
    '--output', 'jsonpath={.data.ca\\.crt}',
  ], { cwd: root });
  if (typeof caOutput !== 'string' || !caOutput.trim()) {
    fail(`the exact ${BACKEND_TLS_CA_CONFIGMAP} ConfigMap must contain its public ca.crt`);
  }
  let authority;
  try {
    authority = new X509Certificate(caOutput);
  } catch (error) {
    fail(`the ${BACKEND_TLS_CA_CONFIGMAP} ConfigMap has an invalid public CA certificate: ${error.message}`);
  }
  const now = Date.now();
  if (!authority.ca || Date.parse(authority.validFrom) > now || Date.parse(authority.validTo) <= now ||
      !authority.verify(authority.publicKey)) {
    fail(`${BACKEND_TLS_CA_CONFIGMAP} must contain a currently valid self-signed CA certificate`);
  }
  for (const [name, hostname] of Object.entries(BACKEND_TLS_HOSTNAMES)) {
    const output = run('kubectl', [
      'get', 'secret', `${name}-tls`, '--namespace', target.namespace,
      '--output', 'jsonpath={.type}:{.data.tls\\.crt}',
    ], { cwd: root });
    const separator = output.indexOf(':');
    if (separator < 0 || output.slice(0, separator) !== 'kubernetes.io/tls' || !output.slice(separator + 1)) {
      fail(`${name}-tls must contain its public tls.crt as a kubernetes.io/tls Secret`);
    }
    let leaf;
    try {
      leaf = new X509Certificate(Buffer.from(output.slice(separator + 1), 'base64'));
    } catch (error) {
      fail(`${name}-tls has an invalid public certificate: ${error.message}`);
    }
    if (leaf.ca || leaf.checkHost(hostname, { subject: 'never' }) !== hostname ||
        Date.parse(leaf.validFrom) > now || Date.parse(leaf.validTo) <= now ||
        leaf.issuer !== authority.subject || !leaf.verify(authority.publicKey)) {
      fail(`${name}-tls must be currently valid for ${hostname} and directly signed by ${BACKEND_TLS_CA_CONFIGMAP}`);
    }
  }
}

/**
 * Accepts only certificate-only, exact bundle-Web-only, or complete P0 routing states.
 * The Web-only retry state has no Gateway or BackendTLSPolicy and contains no extra route.
 */
export function verifyExistingP0BrokerRouting(target, { root, run }) {
  function read(resource, name, namespace = target.namespace) {
    return parseJson(run('kubectl', [
      'get', resource, '--namespace', namespace, '--field-selector', `metadata.name=${name}`, '--output', 'json',
    ], {
      cwd: root, projectJson: projectIdentityRoutingReadback, preserveProjectedJson: true,
    }), `${resource} ${name} routing`);
  }
    // Namespace-wide inventories make unexpected routes and policies fail closed.
    function readInventory(resource, namespace = target.namespace) {
    const inventory = parseJson(run('kubectl', [
      'get', resource, '--namespace', namespace, '--output', 'json',
    ], {
      cwd: root, projectJson: projectIdentityRoutingInventory, preserveProjectedJson: true,
    }), `${resource} routing inventory`);
    if (!Array.isArray(inventory.items)) fail(`${resource} routing inventory is not a Kubernetes list`);
    const resources = new Map();
    for (const object of inventory.items) {
      const name = object.metadata?.name;
      if (typeof name !== 'string' || resources.has(name)) {
        fail(`${resource} routing inventory contains a missing or duplicate name`);
      }
      resources.set(name, object);
    }
    return resources;
  }
  const gatewayInventory = readInventory('gateways', GATEWAY_NAMESPACE);
  const routeInventory = readInventory('httproutes');
  const backendPolicyInventory = readInventory('backendtlspolicies');
  const routing = {
    certificate: read('defaultdomaincertificates', 'identity-broker'),
    gatewayCertificate: read('defaultdomaincertificates', 'identity-broker', GATEWAY_NAMESPACE),
    gateway: gatewayInventory.get('identity-broker') ?? null,
    route: routeInventory.get('identity-broker') ?? null,
    backendPolicy: backendPolicyInventory.get('identity-broker') ?? null,
    webRoute: routeInventory.get('web-application') ?? null,
    apiRoute: routeInventory.get('gateway-api') ?? null,
    apiBackendPolicy: backendPolicyInventory.get('gateway-api') ?? null,
    mcpRoute: routeInventory.get('mcp') ?? null,
    mcpBackendPolicy: backendPolicyInventory.get('mcp') ?? null,
  };
  const noGatewayOrBackendPolicies = gatewayInventory.size === 0 && backendPolicyInventory.size === 0;
  const bundleWebOnly = routeInventory.size === 1 && routeInventory.has('web-application');
  const certificatesOnly = routeInventory.size === 0 && noGatewayOrBackendPolicies;
  if (certificatesOnly || (bundleWebOnly && noGatewayOrBackendPolicies)) {
    const hostname = managedBrokerHostname(routing.certificate, target.namespace);
    const gatewayHostname = managedBrokerHostname(routing.gatewayCertificate, GATEWAY_NAMESPACE);
    if (gatewayHostname !== hostname) fail('Managed Gateway and Broker certificate domains differ.');
    const portalHostname = `portal.${hostname.slice('agentweaver.'.length)}`;
    if (bundleWebOnly) assertExistingP0BundleWebRoute(routing.webRoute, portalHostname);
    return {
      hostname, portalHostname, certificatesConfigured: true, routingConfigured: false,
      bundleWebRouteInstalled: bundleWebOnly, runtimeVerified: false,
    };
  }
  const expectedNames = (inventory, names) =>
    inventory.size === names.length && names.every(name => inventory.has(name));
  // Full routing must contain the exact source-owned Gateway, route, and backend-policy names.
  if (!expectedNames(gatewayInventory, ['identity-broker']) ||
      !expectedNames(routeInventory, ['identity-broker', 'web-application', 'gateway-api', 'mcp']) ||
      !expectedNames(backendPolicyInventory, ['identity-broker', 'gateway-api', 'mcp'])) {
    fail('Existing P0 routing inventory does not match the exact approved Gateway, HTTPRoute, and BackendTLSPolicy sets.');
  }
  return observeExistingP0BrokerRouting(routing);
}

function checkpointKey(tag, sourceSha, target, identity) {
  const identityFields = [
    tag, sourceSha, target.subscriptionId, target.tenantId,
    target.resourceGroup, target.clusterName, target.namespace, target.nodeMin, target.nodeMax,
    identity.manifestSha256, identity.runtimeManifestSha256, identity.publicationSha256,
  ];
  // Keep bootstrap checkpoints separate without changing existing release checkpoint keys.
  if (identity.resultType === 'pre-acceptance') identityFields.unshift(identity.resultType);
  return createHash('sha256')
    .update(JSON.stringify(identityFields))
    .digest('hex');
}

function loadCheckpoint(key, identity, io) {
  const file = path.join(io.directory, `${key}.json`);
  let value;
  try {
    value = JSON.parse(io.readFile(file, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    fail(`cannot read or parse deployment checkpoint ${file}: ${error.message}`);
  }
  if (value?.version !== CHECKPOINT_VERSION || value.key !== key ||
      value.releaseTag !== identity.releaseTag || value.sourceSha !== identity.sourceSha ||
      JSON.stringify(value.target) !== JSON.stringify(identity.target) ||
      value.manifestSha256 !== identity.manifestSha256 ||
      value.runtimeManifestSha256 !== identity.runtimeManifestSha256 ||
      value.publicationSha256 !== identity.publicationSha256 ||
      identity.resultType !== undefined && value.resultType !== identity.resultType ||
      value.stages?.apply?.completed !== true) {
    fail(`deployment checkpoint ${file} is invalid; use --restart after inspecting it`);
  }
  return value;
}

function saveCheckpoint(key, state, io) {
  try {
    io.mkdir(io.directory, { recursive: true });
    io.writeFile(path.join(io.directory, `${key}.json`), `${JSON.stringify(state, null, 2)}\n`, 'utf8');
  } catch (error) {
    fail(`cannot persist deployment checkpoint: ${error.message}`);
  }
}

function clearCheckpoint(key, io) {
  try {
    io.rm(path.join(io.directory, `${key}.json`), { force: true });
  } catch (error) {
    fail(`cannot clear successful deployment checkpoint: ${error.message}`);
  }
}

/** Reads registry manifests and verifies that the AgentHost image digest and platform are consistent. */
export async function verifyAgentHostRegistryImage(image, fetchImpl = globalThis.fetch) {
  const referenceSeparator = image.image.lastIndexOf('@');
  const repository = image.image.slice('ghcr.io/'.length, referenceSeparator);
  const topDigest = image.image.slice(referenceSeparator + 1);
  const prepared = image.preparedOciMeasurement;
  if (!image.image.startsWith('ghcr.io/') || referenceSeparator < 'ghcr.io/'.length ||
      !repository || !DIGEST.test(topDigest) ||
      prepared?.kind !== 'agenthost-image' || prepared.schemaVersion !== 1 ||
      !SHA.test(prepared.sourceSha ?? '') || !SHA.test(prepared.sourceTree ?? '') ||
      prepared.image?.platform !== 'linux/amd64' || typeof prepared.image.version !== 'string' ||
      !DIGEST.test(prepared.image.configDigest ?? '')) {
    fail('AgentHost registry verification requires the published GHCR image and exact prepared image evidence');
  }

  const tokenUrl = new URL('https://ghcr.io/token');
  tokenUrl.searchParams.set('service', 'ghcr.io');
  tokenUrl.searchParams.set('scope', `repository:${repository}:pull`);
  const tokenResponse = await fetchImpl(tokenUrl, { headers: { accept: 'application/json' } });
  if (!tokenResponse.ok) fail(`GHCR does not expose ${repository} for anonymous pull verification (HTTP ${tokenResponse.status})`);
  const tokenJson = await tokenResponse.json();
  const token = tokenJson.token ?? tokenJson.access_token;
  if (typeof token !== 'string' || !token) fail(`GHCR returned no anonymous pull token for ${repository}`);

  async function getManifest(digest) {
    const response = await fetchImpl(`https://ghcr.io/v2/${repository}/manifests/${digest}`, {
      headers: { accept: registryManifestAccept, authorization: `Bearer ${token}` },
    });
    if (!response.ok) fail(`published image ${image.image} is unavailable from GHCR (HTTP ${response.status})`);
    const text = await response.text();
    const bytes = Buffer.from(text, 'utf8');
    const actualDigest = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
    if (response.headers.get('docker-content-digest') !== digest || actualDigest !== digest) {
      fail(`GHCR manifest bytes do not match the exact published digest for ${image.image}`);
    }
    return { text, bytes, value: parseJson(text, `GHCR manifest ${digest}`) };
  }

  const top = await getManifest(topDigest);
  let childDigest = topDigest;
  let child = top;
  if (Array.isArray(top.value.manifests)) {
    const platforms = top.value.manifests.filter(entry =>
      entry.platform?.os === 'linux' && entry.platform?.architecture === 'amd64');
    if (platforms.length !== 1 || !DIGEST.test(platforms[0]?.digest ?? '') ||
        !Number.isSafeInteger(platforms[0]?.size) || platforms[0].size <= 0) {
      fail('published AgentHost image must have exactly one valid linux/amd64 platform manifest');
    }
    childDigest = platforms[0].digest;
    child = await getManifest(childDigest);
    if (child.bytes.length !== platforms[0].size) {
      fail('published AgentHost platform manifest size does not match its index descriptor');
    }
  }

  const manifest = child.value;
  const supportedManifestTypes = new Set([
    'application/vnd.oci.image.manifest.v1+json',
    'application/vnd.docker.distribution.manifest.v2+json',
  ]);
  const supportedConfigTypes = new Set([
    'application/vnd.oci.image.config.v1+json',
    'application/vnd.docker.container.image.v1+json',
  ]);
  const supportedLayerTypes = new Set([
    'application/vnd.oci.image.layer.v1.tar+gzip',
    'application/vnd.docker.image.rootfs.diff.tar.gzip',
  ]);
  if (manifest.schemaVersion !== 2 || !supportedManifestTypes.has(manifest.mediaType) ||
      !supportedConfigTypes.has(manifest.config?.mediaType) ||
      manifest.config.digest !== prepared.image.configDigest ||
      !Number.isSafeInteger(manifest.config.size) || manifest.config.size <= 0 ||
      !Array.isArray(manifest.layers) || manifest.layers.length === 0) {
    fail('published AgentHost manifest does not match its exact prepared OCI config and compressed layers');
  }

  const configResponse = await fetchImpl(`https://ghcr.io/v2/${repository}/blobs/${manifest.config.digest}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  if (!configResponse.ok) fail(`published AgentHost config is unavailable from GHCR (HTTP ${configResponse.status})`);
  const configText = await configResponse.text();
  const configBytes = Buffer.from(configText, 'utf8');
  if (configBytes.length !== manifest.config.size ||
      `sha256:${createHash('sha256').update(configBytes).digest('hex')}` !== manifest.config.digest) {
    fail('published AgentHost config bytes do not match their manifest descriptor');
  }
  const config = parseJson(configText, 'published AgentHost config');
  const labels = config.config?.Labels;
  if (config.os !== 'linux' || config.architecture !== 'amd64' ||
      config.config?.User !== '1654:1654' ||
      JSON.stringify(config.config?.Entrypoint) !== JSON.stringify(['dotnet', 'Agentweaver.AgentHost.dll']) ||
      labels?.['org.opencontainers.image.revision'] !== prepared.sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== prepared.sourceTree ||
      labels?.['org.opencontainers.image.version'] !== prepared.image.version) {
    fail('published AgentHost config does not match its prepared source, tree, version, and runtime evidence');
  }

  const layers = new Map();
  let compressedPullBytes = child.bytes.length + configBytes.length;
  for (const layer of manifest.layers) {
    if (!DIGEST.test(layer.digest ?? '') || !Number.isSafeInteger(layer.size) || layer.size <= 0 ||
        !supportedLayerTypes.has(layer.mediaType) ||
        layers.has(layer.digest) && layers.get(layer.digest) !== layer.size) {
      fail('published AgentHost layer descriptors are invalid or conflicting');
    }
    if (layers.has(layer.digest)) continue;
    layers.set(layer.digest, layer.size);
    compressedPullBytes += layer.size;
    if (!Number.isSafeInteger(compressedPullBytes)) fail('published AgentHost pull-byte total exceeds the exact integer range');
  }

  return {
    image: `ghcr.io/${repository}@${childDigest}`,
    digest: childDigest,
    compressedPullBytes,
    acceptedDigests: new Set([topDigest, childDigest]),
  };
}

/** Requires a clean, full-SHA checkout before consulting release evidence. */
function verifyCleanCheckout(root, run, checkoutName = 'release source') {
  const current = run('git', ['rev-parse', 'HEAD'], { cwd: root });
  const dirty = run('git', ['status', '--porcelain'], { cwd: root });
  if (dirty) fail(`the ${checkoutName} checkout must be clean`);
  if (!SHA.test(current)) fail(`the local ${checkoutName} HEAD is not a full commit SHA`);
  return current;
}

/** Confirms the public Release, exact source tag, and immutable receipt for one source SHA. */
/**
 * Verify that a published release tag and its immutable result receipt bind the supplied publication to one source commit.
 * Callers that accept historical deployment evidence must provide the exact source SHA independently.
 */
export async function verifyPublishedSource(tag, publication, sourceSha, {
  root, run, sourceCommitDescription, allowLightweightTag = false,
}) {
  const release = parseJson(run('gh', ['api', `repos/${REPOSITORY}/releases/tags/${tag}`], { cwd: root }), 'GitHub Release');
  if (release.tag_name !== tag || release.draft !== false || release.prerelease !== false) {
    fail(`${tag} must identify an existing published, non-prerelease GitHub Release`);
  }
  const tagRefPath = `repos/${REPOSITORY}/git/ref/tags/${encodeURIComponent(tag)}`;
  const tagRef = parseJson(run('gh', ['api', tagRefPath], { cwd: root }), 'GitHub release tag ref');
  if (allowLightweightTag && tagRef.object?.type === 'commit' &&
      SHA.test(tagRef.object.sha ?? '')) {
    if (tagRef.object.sha !== sourceSha) {
      fail(`${tag} must resolve to the exact ${sourceCommitDescription} commit ${sourceSha}`);
    }
  } else if (tagRef.object?.type === 'tag' && SHA.test(tagRef.object.sha ?? '')) {
    const tagObject = parseJson(run('gh', ['api', `repos/${REPOSITORY}/git/tags/${tagRef.object.sha}`], { cwd: root }), 'GitHub annotated tag');
    if (tagObject.tag !== tag || tagObject.object?.type !== 'commit' ||
        !SHA.test(tagObject.object.sha ?? '') || tagObject.object.sha !== sourceSha) {
      fail(`${tag} must resolve to the exact ${sourceCommitDescription} commit ${sourceSha}`);
    }
  } else {
    fail(`${tag} must be an annotated Git tag`);
  }

  const resultRefName = publication?.continuation !== undefined
    ? `${PARTIAL_PUBLICATION.namespace}/result`
    : `agentweaver-publication/${sourceSha}/result`;
  if (publication?.continuation !== undefined && sourceSha !== PARTIAL_PUBLICATION.sourceSha) {
    fail('a continuation publication receipt is only valid for the one approved partial release source');
  }
  const resultRef = parseJson(run('gh', ['api', `repos/${REPOSITORY}/git/ref/tags/${encodeURIComponent(resultRefName)}`], { cwd: root }), 'publication result ref');
  if (resultRef.object?.type !== 'tag' || !SHA.test(resultRef.object.sha ?? '')) {
    fail('the immutable publication result for this exact source SHA is missing');
  }
  const resultTag = parseJson(run('gh', ['api', `repos/${REPOSITORY}/git/tags/${resultRef.object.sha}`], { cwd: root }), 'publication result tag');
  if (resultTag.tag !== resultRefName || resultTag.object?.type !== 'commit' || resultTag.object.sha !== sourceSha) {
    fail('the publication result tag is not bound to the exact release source commit');
  }
  const remotePublication = parseJson(resultTag.message, 'publication result message');
  if (JSON.stringify(remotePublication) !== JSON.stringify(publication)) {
    fail('the supplied publication receipt differs from the immutable GitHub publication result');
  }
  return sourceSha;
}

async function verifyRelease(tag, publication, { root, run }) {
  const sourceSha = verifyCleanCheckout(root, run);
  return verifyPublishedSource(tag, publication, sourceSha, {
    root, run, sourceCommitDescription: 'clean local source',
  });
}

/**
 * Verifies a supported bootstrap publication while allowing a clean, newer helper checkout.
 * The image source remains bound to the published release tag, not the helper commit.
 */
export async function verifyBootstrapRelease(tag, publication, { root, run }) {
  verifyCleanCheckout(root, run, 'deployment helper');
  validateBootstrapPublication(tag, publication);
  return verifyPublishedSource(tag, publication, BOOTSTRAP_RELEASES[tag].sourceSha, {
    root,
    run,
    sourceCommitDescription: 'published source',
    allowLightweightTag: BOOTSTRAP_RELEASES[tag].allowLightweightTag,
  });
}

function projectAksTargetReadback(value) {
  return {
    id: value?.id,
    fqdn: value?.fqdn,
    privateFqdn: value?.privateFqdn,
    apiserverAccessProfile: { privateFqdn: value?.apiserverAccessProfile?.privateFqdn },
    agentPoolProfiles: Array.isArray(value?.agentPoolProfiles)
      ? value.agentPoolProfiles.map((pool) => ({
        enableAutoScaling: pool?.enableAutoScaling,
        minCount: pool?.minCount,
        maxCount: pool?.maxCount,
        count: pool?.count,
      }))
      : null,
  };
}

function projectKubeconfigReadback(value) {
  return {
    'current-context': value?.['current-context'],
    contexts: Array.isArray(value?.contexts)
      ? value.contexts.map((entry) => ({
        name: entry?.name,
        context: { cluster: entry?.context?.cluster },
      }))
      : null,
    clusters: Array.isArray(value?.clusters)
      ? value.clusters.map((entry) => ({
        name: entry?.name,
        cluster: { server: entry?.cluster?.server },
      }))
      : null,
  };
}

/**
 * Confirms the explicit Azure target and checks Kubernetes API access before release preflight.
 * Missing access stops before the target namespace or workload resources are read.
 */
export function verifyAzureTarget(target, { root, run }) {
  const account = parseJson(run('az', ['account', 'show', '--subscription', target.subscriptionId, '--output', 'json'], { cwd: root }), 'Azure account');
  if (account.id?.toLowerCase() !== target.subscriptionId.toLowerCase() ||
      account.tenantId?.toLowerCase() !== target.tenantId.toLowerCase()) {
    fail('the existing Azure CLI context does not match the explicit v1 P0 subscription and tenant');
  }
  const cluster = parseJson(run('az', ['aks', 'show', '--name', target.clusterName,
    '--resource-group', target.resourceGroup, '--subscription', target.subscriptionId, '--output', 'json'], {
    cwd: root, projectJson: projectAksTargetReadback, preserveProjectedJson: true,
  }), 'AKS target');
  const expectedResourceId = `/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${target.clusterName}`;
  if (cluster.id?.toLowerCase() !== expectedResourceId.toLowerCase()) fail('Azure returned a different AKS resource than the explicit v1 P0 target');
  const pools = cluster.agentPoolProfiles;
  if (!Array.isArray(pools) || pools.length !== 1 ||
      pools[0].enableAutoScaling !== true || pools[0].minCount !== target.nodeMin ||
      pools[0].maxCount !== target.nodeMax || pools[0].count < target.nodeMin ||
      pools[0].count > target.nodeMax) {
    fail('the exact AKS target must have one autoscaling pool bounded to 2-3 nodes');
  }
  const kubeconfig = parseJson(run('kubectl', ['config', 'view', '--minify', '--output', 'json'], {
    cwd: root, projectJson: projectKubeconfigReadback, preserveProjectedJson: true,
  }), 'current Kubernetes context');
  const contextName = kubeconfig['current-context'];
  const context = (kubeconfig.contexts ?? []).find((entry) => entry.name === contextName)?.context;
  const clusterName = context?.cluster;
  const server = (kubeconfig.clusters ?? []).find((entry) => entry.name === clusterName)?.cluster?.server;
  let serverHost;
  try { serverHost = new URL(server).hostname.toLowerCase(); } catch { /* rejected below */ }
  const expectedHosts = [cluster.fqdn, cluster.privateFqdn,
    cluster.apiserverAccessProfile?.privateFqdn].filter(Boolean).map((host) => host.toLowerCase());
  if (!serverHost || !expectedHosts.includes(serverHost)) {
    fail('the current kubectl context does not point to the exact Azure-verified AKS API endpoint; no context fallback is allowed');
  }
  let namespaceReadAccess;
  try {
    namespaceReadAccess = run('kubectl', [
      'auth', 'can-i', 'get', `namespace/${target.namespace}`, '--request-timeout=15s',
    ], { cwd: root, timeout: 20_000 });
  } catch (error) {
    fail(
      `Kubernetes API access is unavailable or insufficient to read namespace ${target.namespace}. ` +
        `Provision the required API connectivity and permissions outside this tooling, then retry. ` +
        `The release tooling never grants a person AKS Cluster Admin. ${error.message}`,
    );
  }
  if (namespaceReadAccess.trim().toLowerCase() !== 'yes') {
    fail(
      `Kubernetes API access is insufficient to read namespace ${target.namespace}. ` +
        `Provision the required API connectivity and permissions outside this tooling, then retry. ` +
        'The release tooling never grants a person AKS Cluster Admin.',
    );
  }
  const namespace = parseJson(run('kubectl', ['get', 'namespace', target.namespace, '--output', 'json'], { cwd: root }), 'target namespace');
  if (namespace.metadata?.name !== target.namespace ||
      namespace.metadata?.labels?.['agentweaver.io/environment'] !== 'v1-p0') {
    fail('the exact pre-existing v1 P0 namespace and environment label are required');
  }
  return { cluster, contextName, server };
}

/** Checks source-managed bundle contents and operator-owned cluster prerequisites before deployment. */
export function verifyRuntimePrerequisites(
  runtimeManifest, target, { root, run, brokerRunBinding, managedResources } = {},
) {
  const managedByKey = new Map((managedResources?.items ?? []).map((resource) => [
    `${resource.kind}/${resource.metadata?.name}`,
    resource,
  ]));
  for (const reference of resolvePodReferences(runtimeManifest)) {
    const plural = reference.kind === 'serviceaccount' ? 'serviceaccount'
      : reference.kind === 'configmap' ? 'configmap'
        : reference.kind === 'secret' ? 'secret'
          : 'persistentvolumeclaim';
    const managedKind = reference.kind === 'serviceaccount' ? 'ServiceAccount'
      : reference.kind === 'configmap' ? 'ConfigMap' : undefined;
    const managed = managedKind
      ? managedByKey.get(`${managedKind}/${reference.name}`)
      : undefined;
    if (managed) {
      if (managed.metadata?.namespace !== target.namespace) {
        fail(`required source-managed ${reference.kind} ${reference.name} is not in the exact v1 P0 namespace`);
      }
      if (reference.kind === 'configmap' &&
          managed.metadata?.annotations?.['agentweaver.io/runtime-config-status'] === 'incomplete') {
        fail(`${reference.name} is marked incomplete; resolve its runtime inputs before deployment`);
      }
      continue;
    }
    if (reference.kind === 'secret' && brokerRunBinding) {
      const output = run('kubectl', [
        'get', plural, reference.name, '--namespace', target.namespace,
        '--output=jsonpath={.metadata.name}:{.metadata.namespace}',
      ], { cwd: root });
      if (output !== `${reference.name}:${target.namespace}`) {
        fail(`required existing ${reference.kind} ${reference.name} is not in the exact v1 P0 namespace`);
      }
      continue;
    }
    const result = run('kubectl', ['get', plural, reference.name, '--namespace', target.namespace, '--output', 'json'], { cwd: root });
    const resource = parseJson(result, `${reference.kind} ${reference.name}`);
    if (resource.metadata?.name !== reference.name || resource.metadata?.namespace !== target.namespace) {
      fail(`required existing ${reference.kind} ${reference.name} is not in the exact v1 P0 namespace`);
    }
    // External runtime references must also be complete before a workload can consume them.
    if (reference.kind === 'configmap' &&
        resource.metadata?.annotations?.['agentweaver.io/runtime-config-status'] === 'incomplete') {
      fail(`${reference.name} is marked incomplete; resolve its runtime inputs before deployment`);
    }
  }
  if (!managedResources) {
    const defaultPolicies = parseJson(run('kubectl', ['get', 'networkpolicies.networking.k8s.io',
      '--namespace', target.namespace, '--output', 'json'], { cwd: root }), 'namespace network policies');
    const defaultPolicyNames = new Set((defaultPolicies.items ?? []).map((entry) => entry.metadata?.name));
    if (!defaultPolicyNames.has('default-deny-all') || !defaultPolicyNames.has('allow-dns-egress')) {
      fail('the existing namespace default-deny and DNS-only network policies are required');
    }
    const policies = parseJson(run('kubectl', ['get', 'ciliumnetworkpolicies.cilium.io',
      '--namespace', target.namespace, '--output', 'json'], { cwd: root }), 'existing Cilium network policies');
    const policyNames = new Set((policies.items ?? []).map((entry) => entry.metadata?.name));
    for (const name of PRODUCT_SERVICES.map((id) => SERVICE_NAMES[id])) {
      const configuredPolicies = SERVICE_NETWORK_POLICY_NAMES[name];
      if (!configuredPolicies) {
        fail(`no v1 Cilium policy contract is defined for ${name}; deployment is blocked until its service-specific network policy is reviewed and checked in`);
      }
      for (const policyName of Array.isArray(configuredPolicies) ? configuredPolicies : [configuredPolicies]) {
        if (!policyNames.has(policyName)) {
          fail(`operator-reviewed ${policyName} Cilium policy must already exist; deployment does not create or broaden network access`);
        }
      }
    }
  }
  verifyBackendCertificates(target, { root, run });
  verifyExistingP0BrokerRouting(target, { root, run });
}

function readRuntimeConfigMap(name, target, { root, run }) {
  const resource = parseJson(run('kubectl', [
    'get', 'configmap', name, '--namespace', target.namespace, '--output', 'json',
  ], { cwd: root }), `${name} run-binding configuration`);
  if (resource.metadata?.name !== name || resource.metadata?.namespace !== target.namespace ||
      !resource.data || typeof resource.data !== 'object' || Array.isArray(resource.data)) {
    fail(`${name} must be the exact existing run-binding ConfigMap in the P0 namespace`);
  }
  return resource.data;
}

function canonicalConfigKey(value) {
  return value.replaceAll('__', ':').toLowerCase();
}

function audienceUrisMatch(left, right) {
  if (left === right) return true;
  try {
    const leftUri = new URL(left);
    const rightUri = new URL(right);
    return leftUri.protocol === 'https:' && rightUri.protocol === 'https:' &&
      leftUri.href === rightUri.href;
  } catch {
    return false;
  }
}

function requireRuntimeConfigValue(data, key, expected, resourceName) {
  const canonical = canonicalConfigKey(key);
  const matches = Object.entries(data).filter(([candidate]) =>
    canonicalConfigKey(candidate) === canonical);
  if (matches.length !== 1 || matches[0][1] !== expected) {
    fail(`${resourceName} must contain exactly the required ${key} setting`);
  }
}

function hashRuntimeSettings(data, keys) {
  const selected = Object.fromEntries(keys.map((key) => {
    const canonical = canonicalConfigKey(key);
    const [, value] = Object.entries(data).find(([candidate]) =>
      canonicalConfigKey(candidate) === canonical);
    return [canonical, value];
  }).sort(([left], [right]) => left.localeCompare(right)));
  return createHash('sha256').update(JSON.stringify(selected)).digest('hex');
}

function assertNoUnexpectedRunBindingSettings(data, prefix, allowedKeys, resourceName) {
  const allowed = new Set(allowedKeys.map(canonicalConfigKey));
  for (const key of Object.keys(data)) {
    const canonical = canonicalConfigKey(key);
    if (canonical.startsWith(prefix) && !allowed.has(canonical)) {
      fail(`${resourceName} contains an unsupported run-binding setting`);
    }
  }
}

function certificateIsCurrent(certificate, now) {
  const validFrom = Date.parse(certificate.validFrom);
  const validTo = Date.parse(certificate.validTo);
  return Number.isFinite(validFrom) && Number.isFinite(validTo) && validFrom <= now && validTo > now;
}

function parsePublicCertificateBundle(bytes, resourceName, fieldName) {
  const text = Buffer.isBuffer(bytes) ? bytes.toString('utf8') : String(bytes);
  const blocks = [...text.matchAll(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g)]
    .map(([block]) => block);
  if (blocks.length === 0 || text.replace(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g, '').trim()) {
    fail(`${resourceName} ${fieldName} must contain only PEM certificates`);
  }
  return blocks.map((block) => {
    try {
      return new X509Certificate(block);
    } catch {
      fail(`${resourceName} ${fieldName} contains an invalid public certificate`);
    }
  });
}

function parseCaBundle(bytes, configMapName) {
  const authorities = parsePublicCertificateBundle(bytes, configMapName, 'ca.crt');
  const now = Date.now();
  if (authorities.some((authority) => !authority.ca || !certificateIsCurrent(authority, now))) {
    fail(`${configMapName} ca.crt must contain currently valid CA certificates`);
  }
  return authorities;
}

function certificateFingerprint(certificate) {
  return createHash('sha256').update(certificate.raw).digest('hex');
}

const POWERSHELL_CERTIFICATE_CHAIN_VALIDATOR = [
  "$ErrorActionPreference = 'Stop'",
  '$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json',
  '$leaf = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($payload.leaf))',
  '$chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()',
  '$chain.ChainPolicy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust',
  '$chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck',
  '$chain.ChainPolicy.DisableCertificateDownloads = $true',
  "$null = $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))",
  'foreach ($encoded in @($payload.authorities)) {',
  '  $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($encoded))',
  '  $null = $chain.ChainPolicy.CustomTrustStore.Add($certificate)',
  '}',
  'foreach ($encoded in @($payload.intermediates)) {',
  '  $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($encoded))',
  '  $null = $chain.ChainPolicy.ExtraStore.Add($certificate)',
  '}',
  'if (-not $chain.Build($leaf)) { throw "X.509 server certificate chain validation failed" }',
  "[Console]::Out.Write('valid')",
].join('\n');

function verifyServerCertificateChain(bytes, hostname, authorities, resourceName, { root, run }) {
  const [leaf, ...chain] = parsePublicCertificateBundle(bytes, resourceName, 'tls.crt');
  const now = Date.now();
  if (chain.some((certificate) => !certificate.ca || !certificateIsCurrent(certificate, now)) ||
      leaf.ca || leaf.checkHost(hostname, { subject: 'never' }) !== hostname ||
      !certificateIsCurrent(leaf, now) || !leaf.keyUsage?.includes(SERVER_AUTH_EKU)) {
    fail(`${resourceName} must have a current serverAuth certificate for ${hostname} that chains to the configured CA bundle`);
  }
  let chainValidation;
  try {
    chainValidation = run('pwsh', [
      '-NoProfile', '-NonInteractive', '-Command', POWERSHELL_CERTIFICATE_CHAIN_VALIDATOR,
    ], {
      cwd: root,
      input: JSON.stringify({
        leaf: Buffer.from(leaf.raw).toString('base64'),
        intermediates: chain.map(certificate => Buffer.from(certificate.raw).toString('base64')),
        authorities: authorities.map(certificate => Buffer.from(certificate.raw).toString('base64')),
      }),
    });
  } catch (error) {
    if (error.message?.includes('Failed to start pwsh')) {
      fail('PowerShell 7+ (pwsh) is required to validate Broker run-binding certificate chains');
    }
    throw error;
  }
  if (chainValidation !== 'valid') {
    fail(`${resourceName} must have a current serverAuth certificate for ${hostname} that chains to the configured CA bundle`);
  }
  return leaf;
}

function readBrokerClientSecretKeys(target, { root, run }) {
  const template = '{{range $key, $value := .data}}{{$key}}{{"\\n"}}{{end}}';
  const output = run('kubectl', [
    'get', 'secret', 'identity-broker-client-secrets', '--namespace', target.namespace,
    '--ignore-not-found', `--output=go-template=${template}`,
  ], { cwd: root });
  return output.split(/\r?\n/).filter(Boolean);
}

/** Verifies the broker run-binding configuration and its paired Kubernetes ingress/egress controls. */
export function verifyBrokerRunBindingConfiguration(binding, target, { root, run, runRaw = run }) {
  const descriptor = validateBrokerRunBinding(binding);
  const brokerConfig = readRuntimeConfigMap('identity-broker-runtime-config-v2', target, { root, run });
  const projectsConfig = readRuntimeConfigMap('projects-config-runtime-config', target, { root, run });
  const brokerSettings = {
    'IdentityBroker__RunBinding__ProjectsOwnerAddress': descriptor.projectsOwnerAddress,
    'IdentityBroker__RunBinding__ProjectsAudience': descriptor.projectsAudience,
    'IdentityBroker__RunBinding__ProjectsServerCertificateSha256': descriptor.projectsServerCertificateSha256,
    'IdentityBroker__RunBinding__ProjectsTrustBundleSha256': descriptor.projectsTrustBundleSha256,
  };
  const projectsSettings = {
    'ProjectsConfig__Tls__CertificatePath': PROJECTS_CONFIG_TLS_CERTIFICATE_PATH,
    'ProjectsConfig__Tls__KeyPath': PROJECTS_CONFIG_TLS_KEY_PATH,
    'ProjectsConfig__Tls__ServerCertificateSha256': descriptor.projectsServerCertificateSha256,
    'ProjectsConfig__Authentication__BrokerRunBindingAudience': descriptor.projectsAudience,
  };
  assertNoUnexpectedRunBindingSettings(Object.fromEntries(
    Object.keys(brokerConfig).map((key) => [canonicalConfigKey(key), brokerConfig[key]])),
  'identitybroker:runbinding:', Object.keys(brokerSettings), 'identity-broker-runtime-config-v2');
  assertNoUnexpectedRunBindingSettings(Object.fromEntries(
    Object.keys(projectsConfig).map((key) => [canonicalConfigKey(key), projectsConfig[key]])),
  'projectsconfig:tls:', Object.keys(projectsSettings).slice(0, 3), 'projects-config-runtime-config');
  assertNoUnexpectedRunBindingSettings(Object.fromEntries(
    Object.keys(projectsConfig).map((key) => [canonicalConfigKey(key), projectsConfig[key]])),
  'projectsconfig:authentication:brokerrunbinding', [Object.keys(projectsSettings)[3]],
  'projects-config-runtime-config');
  const redemptionAudiences = Object.entries(brokerConfig).filter(([key]) =>
    canonicalConfigKey(key) === 'identitybroker:secretredemption:audience');
  const brokerClientResources = Object.entries(brokerConfig).filter(([key]) =>
    /^identitybroker:clients:\d+:resources:\d+$/.test(canonicalConfigKey(key)));
  if (redemptionAudiences.length !== 1 || typeof redemptionAudiences[0][1] !== 'string' ||
      !redemptionAudiences[0][1] || brokerClientResources.length === 0 ||
      brokerClientResources.some(([, value]) => typeof value !== 'string' || !value)) {
    fail('Identity Broker runtime config must contain its SecretRedemption audience and client resources');
  }
  const brokerPublicAudiences = [
    ...brokerClientResources.map(([, value]) => value),
  ];
  const privateAudienceAbsoluteUri = new URL(descriptor.projectsAudience).href;
  if (redemptionAudiences[0][1] === privateAudienceAbsoluteUri ||
      brokerPublicAudiences.some(value => audienceUrisMatch(value, descriptor.projectsAudience))) {
    fail('Projects Config run-binding audience must be distinct from all existing Identity Broker public audiences');
  }
  const ordinaryAudience = Object.entries(projectsConfig).filter(([key]) =>
    canonicalConfigKey(key) === 'projectsconfig:authentication:audience');
  if (ordinaryAudience.length !== 1 || typeof ordinaryAudience[0][1] !== 'string' ||
      !ordinaryAudience[0][1] ||
      audienceUrisMatch(ordinaryAudience[0][1], descriptor.projectsAudience)) {
    fail('Projects Config run-binding audience must be distinct from its existing authentication audience');
  }
  for (const [key, value] of Object.entries(brokerSettings)) {
    requireRuntimeConfigValue(brokerConfig, key, value, 'identity-broker-runtime-config-v2');
  }
  for (const [key, value] of Object.entries(projectsSettings)) {
    requireRuntimeConfigValue(projectsConfig, key, value, 'projects-config-runtime-config');
  }

  const bindingKeys = new Set(Object.keys(brokerSettings).map(canonicalConfigKey));
  const clientSecretKeys = readBrokerClientSecretKeys(target, { root, run });
  if (clientSecretKeys.some((key) => bindingKeys.has(canonicalConfigKey(key)))) {
    fail('identity-broker-client-secrets must not shadow the explicit run-binding settings');
  }

  const caRaw = runRaw('kubectl', [
    'get', 'configmap', BACKEND_TLS_CA_CONFIGMAP, '--namespace', target.namespace,
    '--output=jsonpath={.data.ca\\.crt}',
  ], { cwd: root });
  const caBytes = Buffer.isBuffer(caRaw) ? caRaw : Buffer.from(caRaw, 'utf8');
  const caSha256 = createHash('sha256').update(caBytes).digest('hex');
  if (caSha256 !== descriptor.projectsTrustBundleSha256) {
    fail(`${BACKEND_TLS_CA_CONFIGMAP} raw ca.crt bytes do not match the explicit run-binding fingerprint`);
  }
  const authorities = parseCaBundle(caBytes, BACKEND_TLS_CA_CONFIGMAP);

  const secretStatus = run('kubectl', [
    'get', 'secret', 'projects-config-tls', '--namespace', target.namespace,
    '--output=go-template={{.type}}|{{if index .data "tls.crt"}}certificate{{end}}|{{if index .data "tls.key"}}key{{end}}',
  ], { cwd: root });
  if (secretStatus !== 'kubernetes.io/tls|certificate|key') {
    fail('projects-config-tls must contain the public certificate and private key in an existing TLS Secret');
  }
  const certificateBase64 = run('kubectl', [
    'get', 'secret', 'projects-config-tls', '--namespace', target.namespace,
    '--output=jsonpath={.data.tls\\.crt}',
  ], { cwd: root });
  const certificateBytes = Buffer.from(certificateBase64, 'base64');
  if (!certificateBase64 || certificateBytes.toString('base64') !== certificateBase64) {
    fail('projects-config-tls must contain a valid public tls.crt');
  }
  const leaf = verifyServerCertificateChain(
    certificateBytes, PROJECTS_CONFIG_HOSTNAME, authorities, 'projects-config-tls', { root, run },
  );
  const leafSha256 = createHash('sha256').update(leaf.raw).digest('hex');
  if (leafSha256 !== descriptor.projectsServerCertificateSha256) {
    fail(`projects-config-tls leaf fingerprint does not match the explicit run-binding descriptor`);
  }

  return {
    configMaps: [
      {
        name: 'identity-broker-runtime-config-v2',
        sha256: hashRuntimeSettings(brokerConfig, Object.keys(brokerSettings)),
      },
      {
        name: 'projects-config-runtime-config',
        sha256: hashRuntimeSettings(projectsConfig, Object.keys(projectsSettings)),
      },
    ],
    brokerClientSecretKeysSha256: createHash('sha256')
      .update(JSON.stringify(clientSecretKeys.sort())).digest('hex'),
    trustBundleSha256: caSha256,
    projectsServerCertificateSha256: leafSha256,
  };
}

function readKustomizeJson(file, location) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    fail(`${location} is not valid JSON: ${error.message}`);
  }
}

function readAgentHostRuntimeSettings(directory) {
  let configMap;
  try {
    configMap = parseYaml(readFileSync(
      path.join(directory, 'profiles', 'agenthost', 'runtime-config.example.yaml'), 'utf8'));
  } catch (error) {
    fail(`AgentHost Kustomize ConfigMap is not valid YAML: ${error.message}`);
  }
  if (configMap?.kind !== 'ConfigMap' || configMap.metadata?.name !== 'agenthost-runtime-v1' ||
      !configMap.immutable || typeof configMap.data?.['appsettings.json'] !== 'string') {
    fail('AgentHost Kustomize source must define its immutable runtime ConfigMap');
  }
  try {
    return JSON.parse(configMap.data['appsettings.json']);
  } catch (error) {
    fail(`AgentHost Kustomize appsettings.json is not valid JSON: ${error.message}`);
  }
}

function setAgentHostRuntimeImage(directory, host) {
  const file = path.join(directory, 'profiles', 'agenthost', 'runtime-config.example.yaml');
  let configMap;
  try {
    configMap = parseYaml(readFileSync(file, 'utf8'));
  } catch (error) {
    fail(`AgentHost Kustomize ConfigMap is not valid YAML: ${error.message}`);
  }
  let settings;
  try {
    settings = JSON.parse(configMap?.data?.['appsettings.json'] ?? '');
  } catch (error) {
    fail(`AgentHost Kustomize appsettings.json is not valid JSON: ${error.message}`);
  }
  const image = settings?.AgentHost?.Image;
  if (!image || typeof image !== 'object' || Array.isArray(image)) {
    fail('AgentHost Kustomize source must include its receipt-pinned image identity');
  }
  // Keep the ConfigMap image identity bound to the same registry measurement as Environment.
  image.Digest = host.sandboxImageDigest;
  image.Platform = 'linux/amd64';
  image.CompressedPullBytes = host.compressedPullBytes;
  configMap.data['appsettings.json'] = JSON.stringify(settings, null, 2);
  writeFileSync(file, stringifyYaml(configMap), 'utf8');
}

function matchesKustomizeRuntimeInputs(directory, images, providerCatalog) {
  const expectedHost = images.find((image) => image.id === 'Agentweaver.AgentHost');
  const agentHostSettings = readAgentHostRuntimeSettings(directory)?.AgentHost;
  const agentHostImage = agentHostSettings?.Image;
  const environment = readKustomizeJson(
    path.join(directory, 'base', 'workloads', 'environment-deployment.json'),
    'Environment Kustomize source',
  );
  const environmentContainer = environment.spec?.template?.spec?.containers?.[0];
  if (!Array.isArray(environment.spec?.template?.spec?.containers) ||
      environment.spec.template.spec.containers.length !== 1 ||
      !Array.isArray(environmentContainer?.env)) {
    fail('Environment Kustomize source must define one container with explicit runtime environment fields');
  }
  const environmentValues = new Map();
  for (const entry of environmentContainer.env) {
    if (typeof entry?.name !== 'string') continue;
    environmentValues.set(entry.name, (environmentValues.get(entry.name) ?? []).concat(entry));
  }
  const exactEnvironmentValue = (name) => {
    const matches = environmentValues.get(name) ?? [];
    if (matches.length !== 1 || typeof matches[0].value !== 'string' || !matches[0].value) {
      fail(`Environment Kustomize source must define exactly one nonempty ${name}`);
    }
    return matches[0].value;
  };
  const agentHostImageSource =
    `ghcr.io/sabbour/${componentImageRepository('Agentweaver.AgentHost')}:receipt-pinned`;
  // The validated `images:` entry transforms this source token into the receipt digest during rendering.
  if (exactEnvironmentValue('Environment__Sandbox__AgentSandbox__ContainerImage') !== agentHostImageSource ||
      exactEnvironmentValue('Environment__Sandbox__AgentSandbox__ContainerImagePlatform') !== 'linux/amd64' ||
      exactEnvironmentValue('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes') !==
        String(expectedHost?.compressedPullBytes) ||
      agentHostImage?.Digest !== expectedHost?.sandboxImageDigest ||
      agentHostImage?.Platform !== 'linux/amd64' ||
      agentHostImage?.CompressedPullBytes !== expectedHost?.compressedPullBytes) {
    return false;
  }
  // Validate the same catalog input on every service that resolves provider options.
  for (const name of ['knowledge', 'orchestrator', 'projects-config']) {
    const workload = readKustomizeJson(
      path.join(directory, 'base', 'workloads', `${name}-deployment.json`),
      `${name} Kustomize source`,
    );
    const pod = workload.spec?.template;
    const envFrom = pod?.spec?.containers?.[0]?.envFrom;
    if (!Array.isArray(envFrom)) {
      fail(`${name} Kustomize source must include its explicit Projects provider catalog reference`);
    }
    const catalogs = envFrom
      .filter((entry) => entry?.prefix === 'ProjectsConfig__ProviderCatalog__');
    if (catalogs.length !== 1 ||
        catalogs[0].configMapRef?.name !== providerCatalog.configMapName ||
        catalogs[0].configMapRef?.optional === true) {
      fail(`${name} Kustomize source must reference the exact required Projects provider catalog`);
    }
    if (pod?.metadata?.annotations?.['agentweaver.io/provider-catalog-source'] !== providerCatalog.sourceCommit) {
      return false;
    }
  }
  return true;
}

/** Selects the checked-in release bundle or a temporary v1.0.0 compatibility render. */
function prepareKustomizeSource(root, releaseTag, images, providerCatalog) {
  if (providerCatalog.configMapName !== P0_PROVIDER_CATALOG_CONFIG_MAP) {
    fail(`the checked-in P0 installation uses ${P0_PROVIDER_CATALOG_CONFIG_MAP} as its provider catalog`);
  }
  const sourceDirectory = path.join(root, KUSTOMIZE_INSTALL_PATH);
  let sourceKustomization;
  try {
    sourceKustomization = parseYaml(readFileSync(path.join(sourceDirectory, 'kustomization.yaml'), 'utf8'));
  } catch (error) {
    fail(`cannot read the checked-in P0 Kustomization: ${error.message}`);
  }
  if (sourceKustomization?.apiVersion !== 'kustomize.config.k8s.io/v1beta1' ||
      sourceKustomization.kind !== 'Kustomization') {
    fail('the checked-in P0 Kustomization has an unsupported API version or kind');
  }
  let sourcePinsMatch = true;
  try {
    validateKustomizeImagePins(sourceKustomization, images);
  } catch (error) {
    if (!error.message.startsWith('release deployment: Kustomization')) throw error;
    sourcePinsMatch = false;
  }
  const sourceRuntimeMatches = matchesKustomizeRuntimeInputs(
    sourceDirectory, images, providerCatalog);
  if (sourcePinsMatch && sourceRuntimeMatches) {
    return { path: KUSTOMIZE_INSTALL_PATH, cleanup: () => undefined };
  }
  if (releaseTag !== BOOTSTRAP_RELEASE_TAG) {
    fail('checked-in P0 Kustomize images and runtime pins must match the exact publication receipt');
  }

  const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), 'agentweaver-v1-kustomize-'));
  const temporaryDirectory = path.join(temporaryRoot, KUSTOMIZE_INSTALL_PATH);
  try {
    cpSync(sourceDirectory, temporaryDirectory, { recursive: true });
    const kustomizationPath = path.join(temporaryDirectory, 'kustomization.yaml');
    const kustomization = parseYaml(readFileSync(kustomizationPath, 'utf8'));
    kustomization.images = createKustomizeImageEntries(images);
    validateKustomizeImagePins(kustomization, images);
    writeFileSync(kustomizationPath, stringifyYaml(kustomization), 'utf8');

    const host = images.find((image) => image.id === 'Agentweaver.AgentHost');
    const environmentPath = path.join(temporaryDirectory, 'base', 'workloads', 'environment-deployment.json');
    const environment = readKustomizeJson(environmentPath, 'Environment Kustomize source');
    const environmentContainer = environment.spec?.template?.spec?.containers?.[0];
    if (!Array.isArray(environment.spec?.template?.spec?.containers) ||
        environment.spec.template.spec.containers.length !== 1 ||
        !Array.isArray(environmentContainer?.env)) {
      fail('Environment Kustomize source must define one container with explicit runtime environment fields');
    }
    const setEnvironmentValue = (name, value) => {
      const matches = environmentContainer.env.filter((entry) => entry?.name === name);
      if (matches.length !== 1 || typeof matches[0].value !== 'string') {
        fail(`Environment Kustomize source must define exactly one ${name}`);
      }
      matches[0].value = value;
    };
    // Keep the image-transform target intact; only receipt-measured metadata is injected here.
    setEnvironmentValue('Environment__Sandbox__AgentSandbox__ContainerImagePlatform', 'linux/amd64');
    setEnvironmentValue('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes',
      String(host.compressedPullBytes));
    writeFileSync(environmentPath, `${JSON.stringify(environment, null, 2)}\n`, 'utf8');
    setAgentHostRuntimeImage(temporaryDirectory, host);

    // Bind all provider-catalog consumers to the exact source commit used for this deployment.
    for (const name of ['knowledge', 'orchestrator', 'projects-config']) {
      const file = path.join(temporaryDirectory, 'base', 'workloads', `${name}-deployment.json`);
      const workload = readKustomizeJson(file, `${name} Kustomize source`);
      const annotations = workload.spec?.template?.metadata?.annotations;
      if (!annotations || typeof annotations !== 'object' || Array.isArray(annotations)) {
        fail(`${name} Kustomize source must define pod annotations for its provider catalog source`);
      }
      annotations['agentweaver.io/provider-catalog-source'] = providerCatalog.sourceCommit;
      writeFileSync(file, `${JSON.stringify(workload, null, 2)}\n`, 'utf8');
    }
    if (!matchesKustomizeRuntimeInputs(temporaryDirectory, images, providerCatalog)) {
      fail('temporary v1.0.0 Kustomize source does not match its publication receipt');
    }
    return {
      path: temporaryDirectory,
      cleanup: () => rmSync(temporaryRoot, { recursive: true, force: true }),
    };
  } catch (error) {
    rmSync(temporaryRoot, { recursive: true, force: true });
    throw error;
  }
}

function verifyImageIds(runtimeManifest, images) {
  const expected = new Map(images.map((item) => [item.name, item.image]));
  for (const item of runtimeManifest.items.filter((resource) => resource.kind === 'Deployment')) {
    const pod = item.spec.template.spec;
    for (const container of [...(pod.initContainers ?? []), ...pod.containers]) {
      if (container.image !== expected.get(item.metadata.name)) {
        fail(`runtime image reference changed for ${item.metadata.name}`);
      }
    }
  }
}

function verifyMeasuredAgentHostDigest(images, acceptedDigests) {
  const agentHost = images.find((image) => image.id === 'Agentweaver.AgentHost');
  const accepted = acceptedDigests.get('agenthost');
  if (!agentHost?.sandboxImageDigest || !accepted?.has(agentHost.sandboxImageDigest)) {
    fail('GHCR does not attest the exact measured linux/amd64 AgentHost image used by Environment');
  }
}

/** Waits for service rollouts and verifies that running pods use accepted immutable image digests. */
export function verifyLiveDeployment(images, target, acceptedDigests, { root, run }) {
  const agentHost = images.find((image) => image.id === 'Agentweaver.AgentHost');
  for (const image of images.filter((entry) => entry.id !== 'Agentweaver.AgentHost')) {
    run('kubectl', ['rollout', 'status', `deployment/${image.name}`, '--namespace', target.namespace, '--timeout=5m'], { cwd: root });
    const deployment = parseJson(run('kubectl', ['get', 'deployment', image.name, '--namespace', target.namespace, '--output', 'json'], { cwd: root }), `${image.name} deployment`);
    const containers = deployment.spec?.template?.spec?.containers;
    if (!Array.isArray(containers) || containers.length !== 1 || containers[0].image !== image.image) {
      fail(`${image.name} is not running the exact published GHCR digest`);
    }
    const container = containers[0];
    const web = image.name === 'web';
    const port = web ? 'http' : 'https';
    const scheme = web ? 'HTTP' : 'HTTPS';
    for (const [probeName, probePath] of [
      ['livenessProbe', '/health/live'],
      ['readinessProbe', '/health/ready'],
    ]) {
      const probe = container[probeName];
      if (probe?.httpGet?.path !== probePath || probe.httpGet.scheme !== scheme ||
          probe.httpGet.port !== port) {
        fail(`${image.name} live Deployment does not preserve its exact ${scheme} ${probePath} probe`);
      }
    }
    if (image.name === 'environment') {
      const env = container.env ?? [];
      const expected = new Map([
        ['Environment__Sandbox__AgentSandbox__ContainerImage', agentHost?.sandboxImage],
        ['Environment__Sandbox__AgentSandbox__ContainerImagePlatform', 'linux/amd64'],
        ['Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes',
          String(agentHost?.compressedPullBytes)],
      ]);
      for (const [name, value] of expected) {
        const matches = env.filter((entry) => entry.name === name);
        if (matches.length !== 1 || matches[0].value !== value) {
          fail(`Environment live Deployment does not pin the exact published AgentHost ${name}`);
        }
      }
    }
    const pods = parseJson(run('kubectl', ['get', 'pods', '--namespace', target.namespace,
      '--selector', `app.kubernetes.io/name=${image.name}`, '--output', 'json'], { cwd: root }), `${image.name} pods`);
    const ready = (pods.items ?? []).filter((pod) => pod.status?.conditions?.some((condition) =>
      condition.type === 'Ready' && condition.status === 'True'));
    if (ready.length !== 1 || ready[0].spec?.containers?.[0]?.image !== image.image ||
        ready[0].status?.containerStatuses?.[0]?.ready !== true) {
      fail(`${image.name} must have exactly one ready pod with the immutable released image`);
    }
    const imageId = ready[0].status?.containerStatuses?.[0]?.imageID ?? '';
    const liveDigest = imageId.slice(imageId.lastIndexOf('@') + 1);
    const accepted = acceptedDigests.get(image.name) ?? new Set([image.image.slice(image.image.lastIndexOf('@') + 1)]);
    if (!accepted.has(liveDigest)) {
      fail(`${image.name} live container image ID does not attest its release digest`);
    }
  }
}

function helpText() {
  return `release:deploy -- verify and deploy an already-published v1 product release

Usage:
  npm run release:deploy -- v1.0.0 --manifest <release-manifest.json> --publication <publication.json> --subscription-id <id> --tenant-id <id> --resource-group <name> --cluster <name> --namespace <name> --node-min 2 --node-max 3 --provider-catalog-configmap projects-config-provider-catalog --provider-catalog-source-commit <sha> [--execute] [--resume]

The default is read-only dry-run. The exact P0 target is mandatory. A write requires
--execute. Deployment uses only digest-pinned GHCR images, never Docker or Podman.
The checked-in deploy/k8s Kustomize source supplies the complete P0 installation.
Its image pins must match the verified publication receipt exactly. Kustomize
renders the workloads, ServiceAccounts, runtime ConfigMaps, and network policies;
operator-owned credentials, certificates, and Gateway placement remain external.
The Projects provider catalog name and reviewed source commit are required.
Partial-publication receipts also require --completion, which verifies the
original partial receipt, immutable continuation records, the successful run,
and the exact merged helper PR. No environment approval history is required.
`;
}

/** Shows the source-bound bootstrap contract without implying release acceptance. */
function bootstrapHelpText() {
  return `release:deploy-bootstrap -- verify or deploy a published v1 release before live acceptance

Usage:
  npm run release:deploy-bootstrap -- v1.0.0 --manifest releases/foundation.json --publication <publication.json> --completion <continuation.json> --subscription-id <id> --tenant-id <id> --resource-group agentweaver --cluster agentweaver-aks --namespace agentweaver --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--execute] [--resume]
  npm run release:deploy-bootstrap -- v1.0.1 --manifest releases/foundation.json --publication <publication.json> --subscription-id <id> --tenant-id <id> --resource-group agentweaver --cluster agentweaver-aks --namespace agentweaver --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--execute] [--resume]
  npm run release:deploy-bootstrap -- v1.0.2 --manifest releases/foundation.json --publication <publication.json> --subscription-id <id> --tenant-id <id> --resource-group agentweaver --cluster agentweaver-aks --namespace agentweaver --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--execute] [--resume]
  npm run release:deploy-bootstrap -- v1.0.3 --manifest releases/foundation.json --publication <publication.json> --subscription-id <id> --tenant-id <id> --resource-group agentweaver --cluster agentweaver-aks --namespace agentweaver --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--execute] [--resume]
  npm run release:deploy-bootstrap -- v1.0.4 --manifest releases/foundation.json --publication <publication.json> --subscription-id <id> --tenant-id <id> --resource-group agentweaver --cluster agentweaver-aks --namespace agentweaver --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--execute] [--resume]

The default is a read-only pre-acceptance dry-run. v1.0.0 requires its immutable
continuation proof; v1.0.1, v1.0.2, v1.0.3, and v1.0.4 accept only their exact
source-bound normal publication receipts and reject --completion. All require
the tracked draft manifest and never create release acceptance evidence.
`;
}

/**
 * Validates release inputs and prerequisites, then optionally deploys and verifies the live workloads.
 * Without explicit execution approval it performs verification only; mismatches stop the release flow.
 */
export async function deployRelease(options = {}, dependencies = {}) {
  const parsed = options.argv ? parseArgs(options.argv) : options;
  if (parsed.help) {
    return {
      ok: true,
      help: true,
      text: parsed.preAcceptance ? bootstrapHelpText() : helpText(),
    };
  }
  if (parsed.runtimeManifestPath || Object.hasOwn(options, 'runtimeManifest')) {
    fail('release deployment uses the checked-in Kustomize installation; runtime manifests are not accepted');
  }
  const bootstrapRelease = parsed.preAcceptance ? BOOTSTRAP_RELEASES[parsed.releaseTag] : undefined;
  if (parsed.preAcceptance && !bootstrapRelease) {
    fail(`--pre-acceptance is limited to the exact ${Object.keys(BOOTSTRAP_RELEASES).join(' and ')} publications`);
  }
  if (bootstrapRelease && !bootstrapRelease.continuation &&
      (parsed.completionPath !== undefined || Object.hasOwn(options, 'completion'))) {
    fail(`--completion is not accepted for the ${parsed.releaseTag} normal publication`);
  }
  if (!parsed.target) fail('explicit P0 target is required');
  validateTarget(parsed.target);
  if (!RELEASE_TAG.test(parsed.releaseTag ?? '')) fail('an exact v1.x.y GitHub release tag is required');

  const root = path.resolve(dependencies.root ?? process.cwd());
  const run = dependencies.run ?? runCommand;
  const readFile = dependencies.readFile ?? readFileSync;
  const fetchImpl = dependencies.fetch ?? globalThis.fetch;
  const checkpointIo = {
    directory: dependencies.checkpointDirectory ?? checkpointDirectory,
    mkdir: dependencies.checkpointMkdir ?? mkdirSync,
    readFile: dependencies.checkpointReadFile ?? readFileSync,
    writeFile: dependencies.checkpointWriteFile ?? writeFileSync,
    rm: dependencies.checkpointRm ?? rmSync,
  };
  const manifest = options.manifest ?? parseJson(readFile(path.resolve(root, parsed.manifestPath), 'utf8'), 'release manifest');
  const publication = options.publication ?? parseJson(readFile(path.resolve(root, parsed.publicationPath), 'utf8'), 'publication receipt');
  const completion = parsed.completionPath
    ? options.completion ?? parseJson(readFile(path.resolve(root, parsed.completionPath), 'utf8'), 'completion receipt')
    : undefined;
  if (parsed.preAcceptance) validateBootstrapPublication(parsed.releaseTag, publication);

  const verifyCompletion = dependencies.verifyCompletion ??
    ((receipt, published, source) => verifyPartialPublicationCompletion(receipt, published, source,
      (file, args, input) => run(file, args, input === undefined ? {} : { input })));
  const verifySource = async () => {
    const source = parsed.preAcceptance
      ? await (dependencies.verifyBootstrapRelease ?? verifyBootstrapRelease)(
        parsed.releaseTag, publication, { root, run })
      : await (dependencies.verifyRelease ?? verifyRelease)(
        parsed.releaseTag, publication, { root, run });
    if (bootstrapRelease?.continuation) {
      if (!completion) fail('pre-acceptance bootstrap requires its exact continuation completion proof');
      await verifyCompletion(completion, publication, source);
    } else if (completion || publication.continuation !== undefined) {
      if (!completion) fail('a continuation publication receipt requires an explicit --completion proof');
      await verifyCompletion(completion, publication, source);
    }
    return source;
  };
  const sourceSha = await verifySource();
  const deploymentManifest = parsed.preAcceptance
    ? validateBootstrapManifest(manifest, publication, {
      root, sourceSha, releaseTag: parsed.releaseTag, manifestPath: parsed.manifestPath, run,
    })
    : validatePublishedManifest(manifest, { root, sourceSha });
  const images = validatePublicationReceipt(publication, deploymentManifest, sourceSha);
  const verifyImages = dependencies.verifyImages ?? (async (refs) => {
    const result = new Map();
    for (const image of refs) {
      if (image.id === 'Agentweaver.AgentHost') {
        const published = await verifyAgentHostRegistryImage(image, fetchImpl);
        image.sandboxImage = published.image;
        image.sandboxImageDigest = published.digest;
        image.compressedPullBytes = published.compressedPullBytes;
        result.set(image.name, published.acceptedDigests);
      } else {
        result.set(image.name, await verifyRegistryImage(image.image, fetchImpl));
      }
    }
    return result;
  });
  let acceptedDigests = await verifyImages(images);
  verifyMeasuredAgentHostDigest(images, acceptedDigests);
  const agentSandboxRelease = dependencies.agentSandboxRelease ?? AGENT_SANDBOX_RELEASE;
  const agentSandboxManifest = await fetchPinnedAgentSandboxManifest(
    dependencies.fetchAgentSandbox ?? fetchImpl, agentSandboxRelease);
  const providerCatalog = {
    configMapName: parsed.providerCatalogConfigMapName,
    sourceCommit: parsed.providerCatalogSourceCommit,
  };
  const kustomizeSource = prepareKustomizeSource(root, parsed.releaseTag, images, providerCatalog);
  try {
    const renderedText = run('kubectl', ['kustomize', kustomizeSource.path], { cwd: root });
    const installation = validateKustomizeInstall(
      parseKustomizeResources(renderedText), images, parsed.target.namespace, providerCatalog);
    const { installationManifest, runtimeManifest, incompleteRuntimeConfigMaps } = installation;
    verifyImageIds(runtimeManifest, images);
    // Keep pre-provisioning source review possible, but never apply workloads with unsourced identities or hosts.
    if (parsed.execute && incompleteRuntimeConfigMaps.length > 0) {
      fail(`workload deployment is blocked until the incomplete runtime ConfigMaps are sourced: ${incompleteRuntimeConfigMaps.join(', ')}`);
    }
    const targetInfo = await (dependencies.verifyTarget ?? verifyAzureTarget)(parsed.target, { root, run });
    const immutableRuntimeConfigMapReplacements = parsed.preAcceptance
      ? findImmutableRuntimeConfigMapReplacements(installationManifest, parsed.target.namespace, { root, run })
      : [];
    const immutableRuntimeConfigMapReplacementNames = immutableRuntimeConfigMapReplacements
      .map((resource) => resource.metadata.name);
    (dependencies.verifyPrerequisites ?? verifyRuntimePrerequisites)(
      runtimeManifest, parsed.target, { root, run, managedResources: installationManifest });
    if (parsed.execute) {
      verifyImmutableRuntimeConfigMapReplacementPermissions(
        immutableRuntimeConfigMapReplacements, parsed.target.namespace, { root, run });
    }
    run('kubectl', ['apply', '--dry-run=client', '--filename', '-'], {
      cwd: root,
      input: agentSandboxManifest,
    });
    run('kubectl', ['apply', '--dry-run=client', '-k', kustomizeSource.path], { cwd: root });
    if (parsed.execute) {
      try {
        const agentSandboxPreflight = prepareAgentSandboxServerPreflight(agentSandboxManifest);
        verifyAgentSandboxNamespacePermissions(agentSandboxPreflight.namespacedResources, { root, run });
        // Server-validate cluster-scoped objects; client validation and exact authorization checks cover
        // namespace-scoped objects until the controller namespace exists on a fresh installation.
        run('kubectl', [
          'apply', '--dry-run=server', '--request-timeout=30s', '--filename', '-',
        ], {
          cwd: root,
          timeout: 45_000,
          input: agentSandboxPreflight.clusterManifest,
        });
        const replacementNames = new Set(immutableRuntimeConfigMapReplacementNames);
        if (replacementNames.size > 0) {
          const serverPreflight = {
            ...installationManifest,
            items: installationManifest.items.filter((resource) =>
              resource.kind !== 'ConfigMap' || !replacementNames.has(resource.metadata?.name)),
          };
          run('kubectl', [
            'apply', '--dry-run=server', '--request-timeout=30s', '--filename', '-',
          ], {
            cwd: root,
            timeout: 45_000,
            input: serializeKustomizeResources(serverPreflight),
          });
        } else {
          run('kubectl', [
            'apply', '--dry-run=server', '--request-timeout=30s', '-k', kustomizeSource.path,
          ], { cwd: root, timeout: 45_000 });
        }
      } catch (error) {
        fail(
          'Installation manifest server-side preflight failed before any cluster write. Check the Kubernetes API ' +
            'diagnostic and provision required connectivity and permissions outside this tooling. The tooling ' +
            'never grants a person AKS Cluster Admin. ' +
            (error?.message ?? String(error)),
        );
      }
    }

    if (parsed.dryRun || !parsed.execute) {
      return {
        ok: true,
        dryRun: true,
        ...(parsed.preAcceptance ? { resultType: 'pre-acceptance' } : {}),
        sourceSha,
        target: parsed.target,
        ...(parsed.preAcceptance
          ? { immutableRuntimeConfigMapReplacements: immutableRuntimeConfigMapReplacementNames }
          : {}),
        services: images.filter((image) => image.id !== 'Agentweaver.AgentHost')
          .map(({ id, image }) => ({ id, image })),
        perSandboxAgentHost: images.find((image) => image.id === 'Agentweaver.AgentHost').sandboxImage,
        agentSandbox: {
          version: agentSandboxRelease.version,
          sourceCommit: agentSandboxRelease.sourceCommit,
          manifestSha256: agentSandboxRelease.manifestSha256,
          controllerImage: agentSandboxRelease.controllerImage,
        },
        renderedResources: installationManifest.items.length,
        targetInfo,
      };
    }

    const identity = {
      ...(parsed.preAcceptance ? { resultType: 'pre-acceptance' } : {}),
      releaseTag: parsed.releaseTag,
      sourceSha,
      target: parsed.target,
      manifestSha256: createHash('sha256').update(JSON.stringify(manifest)).digest('hex'),
      runtimeManifestSha256: createHash('sha256').update(JSON.stringify(installationManifest)).digest('hex'),
      publicationSha256: createHash('sha256').update(JSON.stringify(publication)).digest('hex'),
      agentSandboxRelease,
    };
    const key = checkpointKey(parsed.releaseTag, sourceSha, parsed.target, identity);
    let checkpoint;
    if (parsed.restart) {
      clearCheckpoint(key, checkpointIo);
    } else if (parsed.resume) {
      checkpoint = loadCheckpoint(key, identity, checkpointIo);
    }
    if (!checkpoint) {
      checkpoint = {
        version: CHECKPOINT_VERSION,
        key,
        ...identity,
        stages: {},
      };
    }
    const applied = checkpoint.stages.apply?.completed === true &&
      immutableRuntimeConfigMapReplacements.length === 0;
    if (!applied) {
      // Install CRDs before product workloads that depend on the Sandbox API.
      run('kubectl', ['apply', '--filename', '-'], {
        cwd: root,
        input: agentSandboxManifest,
      });
    }
    verifyAgentSandboxInstallation(agentSandboxRelease, { root, run });
    if (!applied) {
      for (const configMap of immutableRuntimeConfigMapReplacements) {
        // Force-replace only after all preflights, using the exact source-rendered map.
        run('kubectl', [
          'replace', '--force', '--filename', '-', '--namespace', parsed.target.namespace,
        ], { cwd: root, input: JSON.stringify(configMap) });
      }
      run('kubectl', ['apply', '-k', kustomizeSource.path], { cwd: root });
      checkpoint = {
        ...checkpoint,
        updatedAt: new Date().toISOString(),
        stages: { ...checkpoint.stages, apply: { completed: true, completedAt: new Date().toISOString() } },
      };
      saveCheckpoint(key, checkpoint, checkpointIo);
    }

    // Resume never skips current target, release, image, rollout, pod or health checks.
    validateTarget(parsed.target);
    const freshSourceSha = await verifySource();
    if (freshSourceSha !== sourceSha) fail('release source changed during deployment');
    acceptedDigests = await verifyImages(images);
    verifyMeasuredAgentHostDigest(images, acceptedDigests);
    await (dependencies.verifyTarget ?? verifyAzureTarget)(parsed.target, { root, run });
    const verifyLive = dependencies.verifyLive ?? ((refs, target, digests) =>
      verifyLiveDeployment(refs, target, digests, { root, run }));
    const deployedImages = images.filter((image) => image.id !== 'Agentweaver.AgentHost');
    await verifyLive(images, parsed.target, acceptedDigests);
    clearCheckpoint(key, checkpointIo);
    return {
      ok: true,
      dryRun: false,
      resumed: parsed.resume && applied,
      ...(parsed.preAcceptance ? { resultType: 'pre-acceptance' } : {}),
      sourceSha,
      ...(parsed.preAcceptance
        ? { immutableRuntimeConfigMapReplacements: immutableRuntimeConfigMapReplacementNames }
        : {}),
      services: deployedImages.map(({ id }) => id),
      perSandboxAgentHost: images.find((image) => image.id === 'Agentweaver.AgentHost').sandboxImage,
      agentSandbox: {
        version: agentSandboxRelease.version,
        sourceCommit: agentSandboxRelease.sourceCommit,
        manifestSha256: agentSandboxRelease.manifestSha256,
        controllerImage: agentSandboxRelease.controllerImage,
      },
    };
  } finally {
    kustomizeSource.cleanup();
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = await deployRelease({ argv: process.argv.slice(2) });
    if (result.help) console.log(result.text);
    else if (result.dryRun) {
      const prefix = result.resultType === 'pre-acceptance'
        ? 'Read-only pre-acceptance dry-run'
        : 'Read-only dry-run';
      console.log(`${prefix} validated ${result.services.length} product images and ${result.renderedResources} runtime resources at ${result.sourceSha}.`);
      if (result.resultType === 'pre-acceptance') {
        console.log('No release acceptance evidence was recorded.');
      }
      if (result.immutableRuntimeConfigMapReplacements?.length > 0) {
        console.log(
          `Would replace immutable runtime ConfigMaps: ${result.immutableRuntimeConfigMapReplacements.join(', ')}.`,
        );
      }
    } else if (result.resultType === 'pre-acceptance') {
      console.log(`Pre-acceptance bootstrap deployment and live verification completed for ${result.services.length} product services at ${result.sourceSha}. This is not a release acceptance result.`);
    } else {
      console.log(`Deployment and live verification completed for ${result.services.length} product services at ${result.sourceSha}.`);
    }
  } catch (error) {
    console.error(`Release deployment failed: ${error.message}`);
    process.exitCode = 1;
  }
}
