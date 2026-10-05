import { buildImageReceipt, verifyPublishedImageReceipt } from '../../build-foundation-probe-image.mjs';
import { deploymentOutputs, fixture as deploymentFixture, source } from './target.mjs';

export const namespace = 'agentweaver-v1-p0';
export const imageReference = `registry.example/agentweaver/foundation-probe:source-${source.sha}`;
export const manifestDigest = `sha256:${'d'.repeat(64)}`;
export const expectedImage = `registry.example/agentweaver/foundation-probe@${manifestDigest}`;
export const startedAt = '2026-10-03T12:00:00.000Z';
export const finishedAt = '2026-10-03T12:01:00.000Z';
export const completedAt = '2026-10-03T12:01:01.000Z';
export const probeNonce = '11111111222233334444555555555555';

const identity = deploymentOutputs.foundationProbeIdentity.value;
const resources = deploymentOutputs.foundationResources.value;
const deploymentName = deploymentFixture.deploymentName;
const deploymentId = `${deploymentFixture.groupId}/providers/Microsoft.Resources/deployments/${deploymentName}`;
const infrastructure = {
  scope: source.scope,
  sourceSha: source.sha,
  sourceTree: source.sourceTree,
  sourceHash: source.sourceHash,
  foundation: {
    scope: 'infrastructure-only',
    sourceSha: source.sha,
    sourceTree: source.sourceTree,
    sourceHash: source.sourceHash,
    deploymentName,
    deploymentId,
  },
};
export const target = {
  sourceSha: source.sha,
  sourceTree: source.sourceTree,
  sourceHash: source.sourceHash,
  subscriptionId: deploymentFixture.subscriptionId,
  tenantId: deploymentFixture.tenantId,
  resourceGroup: deploymentFixture.resourceGroup,
  resourceGroupId: deploymentFixture.groupId,
  deploymentName,
  deploymentId,
  infrastructure: structuredClone(infrastructure),
  aksOidcIssuerUrl: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
  foundationProbeIdentity: structuredClone(identity),
  foundationResources: structuredClone(resources),
  runtime: {
    databaseName: 'agentweaver',
    databaseRole: 'foundation_probe_runtime',
    schemaName: 'foundation_probe',
    keyVaultSecretName: 'foundation-probe',
    keyVaultSecretVersion: 'e'.repeat(32),
  },
};

export const deployment = {
  subscriptionId: target.subscriptionId,
  tenantId: target.tenantId,
  resourceGroup: target.resourceGroup,
  resourceGroupId: target.resourceGroupId,
  deploymentName,
  deploymentId,
  scope: source.scope,
  sourceSha: source.sha,
  sourceTree: source.sourceTree,
  sourceHash: source.sourceHash,
  foundation: structuredClone(infrastructure.foundation),
  aksOidcIssuerUrl: target.aksOidcIssuerUrl,
  foundationProbeIdentity: structuredClone(identity),
  resources: structuredClone(resources),
};

const deploymentBinding = {
  subscriptionId: target.subscriptionId,
  tenantId: target.tenantId,
  resourceGroup: target.resourceGroup,
  resourceGroupId: target.resourceGroupId,
  deploymentName,
  deploymentId,
  aksOidcIssuerUrl: target.aksOidcIssuerUrl,
  identity: structuredClone(identity),
  resources: structuredClone(resources),
  infrastructure: structuredClone(infrastructure),
};

const providerPins = [
  {
    seam: 'Secrets', providerId: 'azure-key-vault', adapterVersion: '0.1.0', optionsSchemaVersion: 1,
    optionsRevision: 'foundation-probe-v1', resourceId: resources.keyVaultId, generation: 1,
    negotiatedCapabilities: [],
  },
  {
    seam: 'ObjectStore', providerId: 'azure-blob', adapterVersion: '0.1.0', optionsSchemaVersion: 1,
    optionsRevision: 'foundation-probe-v1', resourceId: resources.blobContainerId, generation: 1,
    negotiatedCapabilities: [],
  },
  {
    seam: 'Telemetry', providerId: 'azure-monitor', adapterVersion: '0.1.0', optionsSchemaVersion: 1,
    optionsRevision: 'foundation-probe-v1', resourceId: resources.appInsightsResourceId, generation: 1,
    negotiatedCapabilities: [],
  },
];

export const probeReceipt = {
  sourceSha: source.sha,
  sourceTree: source.sourceTree,
  sourceHash: source.sourceHash,
  nonce: probeNonce,
  deployment: deploymentBinding,
  workloadIdentity: {
    issuer: target.aksOidcIssuerUrl,
    subject: `system:serviceaccount:${namespace}:foundation-probe`,
    audience: 'api://AzureADTokenExchange',
  },
  monitorConfiguration: {
    appInsightsResourceId: resources.appInsightsResourceId,
    ingestionEndpoint: 'https://eastus-1.in.applicationinsights.azure.com/',
    instrumentationKeyConfigured: true,
  },
  providerBindings: providerPins,
  keyVault: {
    resourceId: resources.keyVaultId,
    vaultUri: resources.vaultUri,
    secretName: target.runtime.keyVaultSecretName,
    secretVersion: target.runtime.keyVaultSecretVersion,
    redeemed: true,
  },
  blob: {
    containerResourceId: resources.blobContainerId,
    containerUri: resources.blobContainerUri,
    objectKey: `foundation-probe/${probeNonce}/roundtrip.json`,
    ownershipNonce: probeNonce,
    eTag: '"etag-1"',
    contentSha256: 'f'.repeat(64),
    cleanupConfirmed: true,
  },
  postgres: {
    serverResourceId: resources.postgresServerId,
    host: resources.postgresHost,
    databaseName: target.runtime.databaseName,
    schemaName: target.runtime.schemaName,
    runtimeRole: target.runtime.databaseRole,
    effectId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    inboxMessageId: probeNonce,
    inboxDisposition: 'Admitted',
    outboxEventId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    outboxSequence: 1,
    transactionCommitted: true,
  },
  telemetry: {
    name: 'foundation-probe',
    traceId: 'c'.repeat(32),
    spanId: 'd'.repeat(16),
    startedAt,
  },
};

const localConfigDigest = `sha256:${'c'.repeat(64)}`;
const inspectedImage = {
  Id: localConfigDigest,
  RepoDigests: [expectedImage],
  Config: {
    User: '10001:10001',
    Labels: {
      'org.opencontainers.image.revision': source.sha,
      'io.agentweaver.source-tree': source.sourceTree,
      'io.agentweaver.infrastructure-source-hash': source.sourceHash,
    },
  },
};
export const localImageReceipt = buildImageReceipt({
  sourceSha: source.sha,
  sourceTree: source.sourceTree,
  sourceHash: source.sourceHash,
  imageReference: `agentweaver-foundation-probe:source-${source.sha}`,
  inspect: { ...inspectedImage, RepoDigests: [] },
});
export const verifiedImageReceipt = verifyPublishedImageReceipt(
  localImageReceipt,
  imageReference,
  inspectedImage,
);

const jobUid = '11111111-1111-1111-1111-111111111111';
const tokenVolume = {
  name: 'azure-identity-token',
  projected: {
    sources: [{
      serviceAccountToken: {
        audience: 'api://AzureADTokenExchange',
        expirationSeconds: 3600,
        path: 'azure-identity-token',
      },
    }],
  },
};
const probeContainer = {
  name: 'foundation-probe',
  image: expectedImage,
  args: ['--execute', '--target', '/run/foundation-probe/target.json'],
  env: [{
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING',
    valueFrom: {
      secretKeyRef: {
        name: 'foundation-probe-monitor',
        key: 'APPLICATIONINSIGHTS_CONNECTION_STRING',
      },
    },
  }],
  volumeMounts: [
    { name: 'target', mountPath: '/run/foundation-probe', readOnly: true },
  ],
  resources: {
    requests: { cpu: '100m', memory: '128Mi' },
    limits: { cpu: '1', memory: '512Mi' },
  },
  securityContext: {
    allowPrivilegeEscalation: false,
    readOnlyRootFilesystem: true,
    runAsNonRoot: true,
    runAsUser: 10001,
    runAsGroup: 10001,
    seccompProfile: { type: 'RuntimeDefault' },
    capabilities: { drop: ['ALL'] },
  },
};

export const job = {
  apiVersion: 'batch/v1',
  kind: 'Job',
  metadata: { name: 'foundation-probe', namespace, uid: jobUid, labels: { 'agentweaver.io/service': 'foundation-probe' } },
  spec: {
    activeDeadlineSeconds: 420,
    backoffLimit: 0,
    completions: 1,
    parallelism: 1,
    template: {
      metadata: { labels: {
        'agentweaver.io/probe': 'foundation-probe',
        'azure.workload.identity/use': 'true',
      } },
      spec: {
        serviceAccountName: 'foundation-probe',
        automountServiceAccountToken: true,
        restartPolicy: 'Never',
        terminationGracePeriodSeconds: 30,
        containers: [structuredClone(probeContainer)],
        volumes: [{ name: 'target', configMap: { name: 'foundation-probe-target', items: [{ key: 'target.json', path: 'target.json' }] } }],
      },
    },
  },
  status: {
    active: 0,
    failed: 0,
    succeeded: 1,
    startTime: startedAt,
    completionTime: completedAt,
    conditions: [{ type: 'Complete', status: 'True', reason: 'CompletionsReached' }],
  },
};

export const pod = {
  apiVersion: 'v1',
  kind: 'Pod',
  metadata: {
    name: 'foundation-probe-abcde',
    namespace,
    uid: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
    labels: {
      'job-name': 'foundation-probe',
      'agentweaver.io/probe': 'foundation-probe',
      'azure.workload.identity/use': 'true',
    },
    ownerReferences: [{ apiVersion: 'batch/v1', kind: 'Job', name: 'foundation-probe', uid: jobUid, controller: true }],
  },
  spec: {
    serviceAccountName: 'foundation-probe',
    automountServiceAccountToken: true,
    restartPolicy: 'Never',
    containers: [{
      ...structuredClone(probeContainer),
      env: [
        ...structuredClone(probeContainer.env),
        { name: 'AZURE_FEDERATED_TOKEN_FILE', value: '/var/run/secrets/azure/tokens/azure-identity-token' },
        { name: 'AZURE_CLIENT_ID', value: identity.clientId },
        { name: 'AZURE_TENANT_ID', value: target.tenantId },
      ],
      volumeMounts: [
        ...structuredClone(probeContainer.volumeMounts),
        { name: 'azure-identity-token', mountPath: '/var/run/secrets/azure/tokens', readOnly: true },
      ],
    }],
    volumes: [
      { name: 'target', configMap: { name: 'foundation-probe-target', items: [{ key: 'target.json', path: 'target.json' }] } },
      structuredClone(tokenVolume),
    ],
  },
  status: {
    phase: 'Succeeded',
    containerStatuses: [{
      name: 'foundation-probe',
      image: expectedImage,
      imageID: `docker-pullable://${expectedImage}`,
      state: { terminated: { reason: 'Completed', exitCode: 0, startedAt, finishedAt } },
    }],
  },
};

export const serviceAccount = {
  apiVersion: 'v1',
  kind: 'ServiceAccount',
  metadata: {
    name: 'foundation-probe',
    namespace,
    uid: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
    labels: { 'azure.workload.identity/use': 'true' },
    annotations: {
      'azure.workload.identity/client-id': identity.clientId,
      'azure.workload.identity/tenant-id': target.tenantId,
    },
  },
  automountServiceAccountToken: true,
};

export const configMap = {
  apiVersion: 'v1',
  kind: 'ConfigMap',
  metadata: {
    name: 'foundation-probe-target',
    namespace,
    uid: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
    resourceVersion: '17',
  },
  immutable: true,
  data: { 'target.json': JSON.stringify(target) },
};

export const observedCluster = {
  clusterId: resources.clusterId,
  apiServerFqdn: 'api.example.azmk8s.io',
};

export const kubeConfig = {
  clusters: [{ name: 'aw-v1-p0-aks', cluster: { server: 'https://api.example.azmk8s.io:443/' } }],
};

export function makeRuntimeFixture() {
  const state = {
    source: structuredClone(source),
    deployment: structuredClone(deployment),
    observedCluster: structuredClone(observedCluster),
    kubeContext: 'aw-v1-p0',
    imageReference,
    imageReceiptPath: 'artifacts/images/foundation-probe.json',
    repoRoot: 'repo',
    kubeConfig: structuredClone(kubeConfig),
    job: structuredClone(job),
    pod: structuredClone(pod),
    serviceAccount: structuredClone(serviceAccount),
    configMap: structuredClone(configMap),
    probeReceipt: structuredClone(probeReceipt),
    localImageReceipt: structuredClone(localImageReceipt),
    inspectedImage: structuredClone(inspectedImage),
  };
  const calls = [];
  const execKubectl = (args, options) => {
    calls.push({ args, options });
    const output = args[0] === 'config' ? state.kubeConfig
      : args[0] === 'get' && args[1] === 'job' ? state.job
        : args[0] === 'get' && args[1] === 'pods' ? { apiVersion: 'v1', kind: 'List', items: [state.pod] }
          : args[0] === 'get' && args[1] === 'serviceaccount' ? state.serviceAccount
            : args[0] === 'get' && args[1] === 'configmap' ? state.configMap
              : args[0] === 'logs' ? JSON.stringify(state.probeReceipt)
                : undefined;
    if (output === undefined) throw new Error(`Unexpected kubectl operation: ${args.join(' ')}`);
    return { status: 0, stdout: typeof output === 'string' ? output : JSON.stringify(output), stderr: '' };
  };
  const verifyImage = (reference, options) => {
    assertImageVerifierInput(reference, options, state.imageReference);
    return {
      receiptPath: options.readReceiptPath,
      receipt: verifyPublishedImageReceipt(state.localImageReceipt, reference, state.inspectedImage),
    };
  };
  return {
    state,
    calls,
    execKubectl,
    verifyImage,
    options: {
      imageSource: state.source,
      deployment: state.deployment,
      observedCluster: state.observedCluster,
      kubeContext: state.kubeContext,
      imageReference: state.imageReference,
      imageReceiptPath: state.imageReceiptPath,
      repoRoot: state.repoRoot,
    },
  };
}

function assertImageVerifierInput(reference, options, expectedReference = imageReference) {
  if (reference !== expectedReference || options.readReceiptPath !== 'artifacts/images/foundation-probe.json') {
    throw new Error('Unexpected registry verifier input.');
  }
}
