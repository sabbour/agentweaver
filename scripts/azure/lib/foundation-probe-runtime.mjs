import { run } from './exec.mjs';
import { verifyPublishedFoundationProbeImage } from '../build-foundation-probe-image.mjs';

const namespace = 'agentweaver-v1-p0';
const jobName = 'foundation-probe';
const configMapName = 'foundation-probe-target';
const imageName = 'foundation-probe';
const workloadAudience = 'api://AzureADTokenExchange';
const workloadTokenPath = 'azure-identity-token';
const workloadTokenDirectory = '/var/run/secrets/azure/tokens';
const fullSha = /^[0-9a-f]{40}$/;
const fullHash = /^[0-9a-f]{64}$/;
const guid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const noncePattern = /^[0-9a-f]{32}$/;
const manifestDigest = /^sha256:[0-9a-f]{64}$/;

const passed = (name, scope, evidence) => ({ name, scope, status: 'passed', evidence });
const blocked = (name, reason, evidence) => ({ name, scope: 'integration', status: 'blocked', reason, evidence });
const configurationBlocked = (name, reason, evidence) => ({
  name, scope: 'configuration', status: 'blocked', reason, evidence,
});

function redactDiagnostic(value) {
  return String(value ?? '')
    .replace(/Bearer\s+[A-Za-z0-9._-]+/gi, 'Bearer [redacted]')
    .replace(/[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g, '[redacted]')
    .replace(/\b(InstrumentationKey|AccountKey|SharedAccessKey|Password|Secret|Token|client_secret|sig)\s*=\s*[^;\s,]+/gi,
      '$1=[redacted]')
    .slice(0, 1000);
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasExactKeys(value, keys) {
  return isObject(value) && Object.keys(value).length === keys.length &&
    keys.every(key => Object.hasOwn(value, key));
}

function parseJsonObject(text, label) {
  if (typeof text !== 'string' || text.length > 1024 * 1024) throw new Error(`${label} output is missing or too large.`);
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    throw new Error(`${label} returned malformed JSON.`);
  }
  if (!isObject(value)) throw new Error(`${label} did not return an object.`);
  return value;
}

function sameResourceId(actual, expected) {
  return typeof actual === 'string' && typeof expected === 'string' &&
    actual.toLowerCase() === expected.toLowerCase();
}

function sameIdentity(actual, expected) {
  return isObject(actual) && isObject(expected) &&
    identityKeys.every(key => ['resourceId', 'clientId', 'principalObjectId'].includes(key)
      ? sameResourceId(actual[key], expected[key])
      : actual[key] === expected[key]);
}

function validIssuer(value) {
  if (typeof value !== 'string') return false;
  try {
    const issuer = new URL(value);
    return issuer.protocol === 'https:' && issuer.port === '' && !issuer.username && !issuer.password &&
      !issuer.search && !issuer.hash && issuer.pathname.endsWith('/');
  } catch {
    return false;
  }
}

function imageRepository(reference) {
  if (typeof reference !== 'string' || reference.length === 0 || reference.includes('@') ||
      /\s|[\r\n]/.test(reference)) return null;
  const slash = reference.lastIndexOf('/');
  const colon = reference.lastIndexOf(':');
  if (colon <= slash || !/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(reference.slice(colon + 1))) return null;
  const repository = reference.slice(0, colon);
  return repository.length > 0 ? repository : null;
}

function normalizeImageId(imageId) {
  if (typeof imageId !== 'string') return '';
  return imageId.replace(/^(?:docker-pullable|containerd|docker):\/\//, '');
}

function checkKubernetesTarget(kubeConfig, kubeContext, cluster) {
  const name = 'kubernetes-target';
  if (!isObject(kubeConfig) || !Array.isArray(kubeConfig.clusters) || kubeConfig.clusters.length !== 1 ||
      typeof kubeConfig.clusters[0]?.cluster?.server !== 'string') {
    return configurationBlocked(name, 'The selected Kubernetes context has no single API server endpoint.');
  }
  if (typeof kubeContext !== 'string' || !/^[^\s\0-\x1f]{1,256}$/.test(kubeContext)) {
    return configurationBlocked(name, 'An explicit Kubernetes context is required.');
  }

  const server = new URL(kubeConfig.clusters[0].cluster.server);
  const observedHosts = cluster?.apiServerHosts ??
    [cluster?.properties?.fqdn, cluster?.properties?.privateFqdn]
    .filter(value => typeof value === 'string')
    .map(value => value.toLowerCase().replace(/\.$/, ''));
  const clusterId = cluster?.clusterId ?? cluster?.id;
  if (server.protocol !== 'https:' || server.username || server.password || server.search || server.hash ||
      server.pathname !== '/' || (server.port && server.port !== '443') ||
      !Array.isArray(observedHosts) || observedHosts.length === 0 ||
      !observedHosts.includes(server.hostname.toLowerCase().replace(/\.$/, ''))) {
    return configurationBlocked(name, 'The Kubernetes API endpoint does not match the observed dedicated AKS cluster.');
  }
  return passed(name, 'configuration', {
    context: kubeContext,
    apiServerHost: server.hostname,
    clusterId,
  });
}

const targetKeys = [
  'sourceSha', 'sourceTree', 'sourceHash', 'subscriptionId', 'tenantId', 'resourceGroup', 'resourceGroupId',
  'deploymentName', 'deploymentId', 'aksOidcIssuerUrl', 'foundationProbeIdentity', 'foundationResources', 'runtime',
];
const identityKeys = ['name', 'resourceId', 'clientId', 'principalObjectId', 'namespace', 'serviceAccount'];
const resourceKeys = [
  'clusterId', 'keyVaultId', 'vaultUri', 'storageAccountId', 'blobContainerId', 'blobContainerUri',
  'postgresServerId', 'postgresHost', 'monitorWorkspaceResourceId', 'monitorWorkspaceId', 'appInsightsResourceId',
];
const runtimeKeys = ['databaseName', 'databaseRole', 'schemaName', 'keyVaultSecretName', 'keyVaultSecretVersion'];

function targetMatchesSourceAndDeployment(target, source, deployment) {
  const identity = deployment?.foundationProbeIdentity;
  const resources = deployment?.resources;
  return target.sourceSha === source.sha && target.sourceTree === source.sourceTree &&
    target.sourceHash === source.sourceHash &&
    sameResourceId(target.subscriptionId, deployment.subscriptionId) &&
    sameResourceId(target.tenantId, deployment.tenantId) &&
    target.resourceGroup === deployment.resourceGroup &&
    sameResourceId(target.resourceGroupId, deployment.resourceGroupId) &&
    target.deploymentName === deployment.deploymentName && sameResourceId(target.deploymentId, deployment.deploymentId) &&
    target.aksOidcIssuerUrl === deployment.aksOidcIssuerUrl &&
    sameIdentity(target.foundationProbeIdentity, identity) &&
    resourceKeys.every(key => {
      const expected = key === 'monitorWorkspaceId' ? resources?.monitorWorkspaceId : resources?.[key];
      return key.endsWith('Id') || key === 'monitorWorkspaceResourceId'
        ? sameResourceId(target.foundationResources[key], expected)
        : target.foundationResources[key] === expected;
    });
}

function validateTarget(target, source, deployment) {
  if (!hasExactKeys(target, targetKeys) ||
      !hasExactKeys(target.foundationProbeIdentity, identityKeys) ||
      !hasExactKeys(target.foundationResources, resourceKeys) ||
      !hasExactKeys(target.runtime, runtimeKeys)) {
    throw new Error('Observed probe target does not match the strict #1784 target shape.');
  }
  if (!fullSha.test(target.sourceSha ?? '') || !fullSha.test(target.sourceTree ?? '') ||
      !fullHash.test(target.sourceHash ?? '') || !guid.test(target.subscriptionId ?? '') ||
      !guid.test(target.tenantId ?? '') || target.resourceGroup !== 'aw-v1-p0' ||
      target.deploymentName !== `${target.resourceGroup}-${target.sourceSha.slice(0, 12)}` ||
      !validIssuer(target.aksOidcIssuerUrl) ||
      !/^[a-z][a-z0-9_]{0,62}$/.test(target.runtime.databaseName ?? '') ||
      target.runtime.databaseRole !== 'foundation_probe_runtime' ||
      target.runtime.schemaName !== 'foundation_probe' ||
      target.runtime.keyVaultSecretName !== 'foundation-probe' ||
      !noncePattern.test(target.runtime.keyVaultSecretVersion ?? '')) {
    throw new Error('Observed probe target has invalid or unsupported source/runtime values.');
  }
  if (!targetMatchesSourceAndDeployment(target, source, deployment)) {
    throw new Error('Observed probe target does not match the admitted source and deployment receipt.');
  }
  return target;
}

function checkTargetConfigMap(configMap, source, deployment) {
  try {
    if (configMap?.apiVersion !== 'v1' || configMap.kind !== 'ConfigMap' ||
        configMap.metadata?.name !== configMapName || configMap.metadata?.namespace !== namespace ||
        !guid.test(configMap.metadata?.uid ?? '') || typeof configMap.metadata?.resourceVersion !== 'string' ||
        configMap.metadata.resourceVersion.length === 0 || configMap.immutable !== true ||
        !hasExactKeys(configMap.data, ['target.json']) || configMap.binaryData !== undefined) {
      throw new Error('Observed target ConfigMap is not the exact immutable foundation-probe configuration.');
    }
    const target = parseJsonObject(configMap.data['target.json'], 'Probe target ConfigMap');
    validateTarget(target, source, deployment);
    return { check: passed('foundation-probe-target', 'configuration', {
      name: configMapName,
      namespace,
      uid: configMap.metadata.uid,
      resourceVersion: configMap.metadata.resourceVersion,
      sourceSha: target.sourceSha,
      sourceTree: target.sourceTree,
      deploymentId: target.deploymentId,
      keyVaultSecretVersion: target.runtime.keyVaultSecretVersion,
    }), target };
  } catch (error) {
    return { check: configurationBlocked('foundation-probe-target', error.message, {
      name: configMap?.metadata?.name,
      namespace: configMap?.metadata?.namespace,
      uid: configMap?.metadata?.uid,
    }) };
  }
}

function checkJobAndPod(job, pods, target, now) {
  const name = 'foundation-probe-job-pod';
  try {
    const jobPodSpec = job.spec?.template?.spec;
    const jobContainer = jobPodSpec?.containers?.[0];
    if (job?.apiVersion !== 'batch/v1' || job.kind !== 'Job' || job.metadata?.name !== jobName ||
        job.metadata?.namespace !== namespace || !guid.test(job.metadata?.uid ?? '') ||
        job.metadata?.labels?.['agentweaver.io/service'] !== 'foundation-probe' ||
        job.spec?.completions !== 1 || job.spec?.parallelism !== 1 || job.spec?.backoffLimit !== 0 ||
        !Number.isInteger(job.spec?.activeDeadlineSeconds) ||
        job.spec.activeDeadlineSeconds < 360 || job.spec.activeDeadlineSeconds > 420 ||
        job.spec?.template?.metadata?.labels?.['agentweaver.io/probe'] !== 'foundation-probe' ||
        job.spec?.template?.metadata?.labels?.['azure.workload.identity/use'] !== 'true' ||
        jobPodSpec?.serviceAccountName !== target.foundationProbeIdentity.serviceAccount ||
        jobPodSpec?.automountServiceAccountToken !== true || jobPodSpec?.restartPolicy !== 'Never' ||
        jobContainer?.name !== imageName || !Array.isArray(jobContainer.args) ||
        jobContainer.args.length !== 3 || jobContainer.args[0] !== '--execute' ||
        jobContainer.args[1] !== '--target' || jobContainer.args[2] !== '/run/foundation-probe/target.json' ||
        !Array.isArray(jobPodSpec.volumes) ||
        !jobPodSpec.volumes.some(volume => volume.name === 'target' &&
          volume.configMap?.name === configMapName &&
          volume.configMap?.items?.length === 1 &&
          volume.configMap.items[0].key === 'target.json' &&
          volume.configMap.items[0].path === 'target.json') ||
        !Array.isArray(jobContainer.volumeMounts) ||
        !jobContainer.volumeMounts.some(mount =>
          mount.name === 'target' && mount.mountPath === '/run/foundation-probe' && mount.readOnly === true) ||
        !Array.isArray(jobContainer.env) ||
        !jobContainer.env.some(entry => entry.name === 'APPLICATIONINSIGHTS_CONNECTION_STRING' &&
          entry.valueFrom?.secretKeyRef?.name === 'foundation-probe-monitor' &&
          entry.valueFrom.secretKeyRef.key === 'APPLICATIONINSIGHTS_CONNECTION_STRING') ||
        jobContainer.securityContext?.allowPrivilegeEscalation !== false ||
        jobContainer.securityContext?.readOnlyRootFilesystem !== true ||
        jobContainer.securityContext?.runAsNonRoot !== true ||
        jobContainer.securityContext?.runAsUser !== 10001 || jobContainer.securityContext?.runAsGroup !== 10001 ||
        jobContainer.securityContext?.seccompProfile?.type !== 'RuntimeDefault' ||
        !Array.isArray(jobContainer.securityContext?.capabilities?.drop) ||
        !jobContainer.securityContext.capabilities.drop.includes('ALL') ||
        jobContainer.resources?.requests?.cpu !== '100m' || jobContainer.resources?.requests?.memory !== '128Mi' ||
        jobContainer.resources?.limits?.cpu !== '1' || jobContainer.resources?.limits?.memory !== '512Mi' ||
        job.status?.succeeded !== 1 || (job.status?.failed ?? 0) !== 0 || (job.status?.active ?? 0) !== 0 ||
        !job.status?.conditions?.some(condition =>
          condition.type === 'Complete' && condition.status === 'True') ||
        job.status.conditions.some(condition => condition.type === 'Failed' && condition.status === 'True') ||
        !Array.isArray(pods) || pods.length !== 1) {
      throw new Error('Observed Job is missing, incomplete, retried, failed, or outside the bounded one-shot contract.');
    }
    const pod = pods[0];
    const podStart = Date.parse(pod.status?.containerStatuses?.[0]?.state?.terminated?.startedAt ?? '');
    const podFinish = Date.parse(pod.status?.containerStatuses?.[0]?.state?.terminated?.finishedAt ?? '');
    const jobStart = Date.parse(job.status?.startTime ?? '');
    const jobFinish = Date.parse(job.status?.completionTime ?? '');
    if (pod?.apiVersion !== 'v1' || pod.kind !== 'Pod' ||
        typeof pod.metadata?.name !== 'string' || pod.metadata.name.length === 0 ||
        pod.metadata?.namespace !== namespace ||
        !guid.test(pod.metadata?.uid ?? '') ||
        pod.metadata?.labels?.['job-name'] !== jobName ||
        pod.status?.phase !== 'Succeeded' ||
        !Array.isArray(pod.metadata?.ownerReferences) || pod.metadata.ownerReferences.length !== 1 ||
        pod.metadata.ownerReferences[0].kind !== 'Job' || pod.metadata.ownerReferences[0].name !== jobName ||
        pod.metadata.ownerReferences[0].uid !== job.metadata.uid ||
        pod.metadata.ownerReferences[0].controller !== true ||
        !Number.isFinite(jobStart) || !Number.isFinite(jobFinish) || !Number.isFinite(podStart) ||
        !Number.isFinite(podFinish) || jobStart > podStart || podStart > podFinish ||
        podFinish > jobFinish || jobFinish < jobStart ||
        jobFinish - jobStart > job.spec.activeDeadlineSeconds * 1000 || jobFinish > now) {
      throw new Error('Observed pod identity, owner, phase, or bounded completion does not match its Job.');
    }
    const containerStatus = pod.status.containerStatuses?.[0];
    if (pod.spec?.serviceAccountName !== target.foundationProbeIdentity.serviceAccount ||
        pod.spec?.automountServiceAccountToken !== true || pod.spec?.restartPolicy !== 'Never' ||
        !Array.isArray(pod.spec?.containers) || pod.spec.containers.length !== 1 ||
        pod.spec.containers[0].name !== imageName || pod.spec.initContainers?.length > 0 ||
        pod.spec.containers[0].image !== job.spec.template?.spec?.containers?.[0]?.image ||
        pod.spec.containers[0].image !== containerStatus?.image ||
        !Array.isArray(pod.status.containerStatuses) || pod.status.containerStatuses.length !== 1 ||
        containerStatus?.name !== imageName || containerStatus.state?.terminated?.exitCode !== 0 ||
        containerStatus.state.terminated.reason !== 'Completed' ||
        pod.metadata?.labels?.['agentweaver.io/probe'] !== 'foundation-probe' ||
        pod.metadata?.labels?.['azure.workload.identity/use'] !== 'true' ||
        pod.spec?.containers?.[0]?.securityContext?.allowPrivilegeEscalation !== false ||
        pod.spec.containers[0].securityContext?.readOnlyRootFilesystem !== true ||
        pod.spec.containers[0].securityContext?.runAsNonRoot !== true ||
        pod.spec.containers[0].securityContext?.runAsUser !== 10001 ||
        pod.spec.containers[0].securityContext?.runAsGroup !== 10001 ||
        pod.spec.containers[0].securityContext?.seccompProfile?.type !== 'RuntimeDefault' ||
        !pod.spec.containers[0].securityContext?.capabilities?.drop?.includes('ALL') ||
        pod.spec.containers[0].resources?.requests?.cpu !== '100m' ||
        pod.spec.containers[0].resources?.requests?.memory !== '128Mi' ||
        pod.spec.containers[0].resources?.limits?.cpu !== '1' ||
        pod.spec.containers[0].resources?.limits?.memory !== '512Mi' ||
        !pod.spec.volumes?.some(volume => volume.name === 'target' &&
          volume.configMap?.name === configMapName && volume.configMap?.items?.length === 1 &&
          volume.configMap.items[0].key === 'target.json' &&
          volume.configMap.items[0].path === 'target.json') ||
        !pod.spec.containers[0].volumeMounts?.some(mount =>
          mount.name === 'target' && mount.mountPath === '/run/foundation-probe' && mount.readOnly === true) ||
        pod.spec.containers[0].env?.filter(entry => entry.name === 'APPLICATIONINSIGHTS_CONNECTION_STRING').length !== 1 ||
        !pod.spec.containers[0].env?.some(entry => entry.name === 'APPLICATIONINSIGHTS_CONNECTION_STRING' &&
          entry.valueFrom?.secretKeyRef?.name === 'foundation-probe-monitor' &&
          entry.valueFrom.secretKeyRef.key === 'APPLICATIONINSIGHTS_CONNECTION_STRING')) {
      throw new Error('Observed pod container or successful process termination does not match the probe Job.');
    }
    return {
      check: passed(name, 'integration', {
        jobUid: job.metadata.uid,
        podUid: pod.metadata.uid,
        ownerJobUid: pod.metadata.ownerReferences[0].uid,
        namespace,
        serviceAccountName: pod.spec.serviceAccountName,
        startedAt: job.status.startTime,
        completedAt: job.status.completionTime,
        exitCode: containerStatus.state.terminated.exitCode,
        activeDeadlineSeconds: job.spec.activeDeadlineSeconds,
      }),
      pod,
      completedAt: job.status.completionTime,
    };
  } catch (error) {
    return { check: blocked(name, error.message, {
      jobName: job?.metadata?.name,
      jobUid: job?.metadata?.uid,
      jobNamespace: job?.metadata?.namespace,
      succeeded: job?.status?.succeeded,
      failed: job?.status?.failed,
      podCount: Array.isArray(pods) ? pods.length : undefined,
      podUid: pods?.[0]?.metadata?.uid,
      podNamespace: pods?.[0]?.metadata?.namespace,
      podPhase: pods?.[0]?.status?.phase,
    }), pod: Array.isArray(pods) && pods.length === 1 && podOwnedByJob(job, pods[0]) ? pods[0] : undefined,
    completedAt: job?.status?.completionTime };
  }
}

function podOwnedByJob(job, pod) {
  const owner = pod?.metadata?.ownerReferences;
  return job?.metadata?.name === jobName && job.metadata.namespace === namespace &&
    guid.test(job.metadata.uid ?? '') && pod?.apiVersion === 'v1' && pod.kind === 'Pod' &&
    pod.metadata?.namespace === namespace && guid.test(pod.metadata?.uid ?? '') &&
    pod.metadata?.labels?.['job-name'] === jobName && Array.isArray(owner) && owner.length === 1 &&
    owner[0].kind === 'Job' && owner[0].name === jobName && owner[0].uid === job.metadata.uid &&
    owner[0].controller === true;
}

function podHasTerminalProbeContainer(pod) {
  return ['Succeeded', 'Failed'].includes(pod?.status?.phase) &&
    Array.isArray(pod.status?.containerStatuses) && pod.status.containerStatuses.length === 1 &&
    pod.status.containerStatuses[0].name === imageName &&
    isObject(pod.status.containerStatuses[0].state?.terminated);
}

function checkWorkloadIdentityProjection(pod, serviceAccount, target) {
  const name = 'foundation-probe-workload-identity';
  const identity = target.foundationProbeIdentity;
  const subject = `system:serviceaccount:${namespace}:${identity.serviceAccount}`;
  try {
    const container = pod.spec.containers[0];
    const envList = container.env ?? [];
    const env = new Map(envList.map(entry => [entry.name, entry]));
    const serviceAccountTokens = (pod.spec.volumes ?? []).flatMap(volume =>
      (volume.projected?.sources ?? [])
        .filter(source => source.serviceAccountToken)
        .map(source => ({ volume, token: source.serviceAccountToken })));
    const workloadTokens = serviceAccountTokens.filter(({ token }) => token.audience === workloadAudience);
    const projectedTokens = workloadTokens.filter(({ volume, token }) => volume.name === 'azure-identity-token' &&
      token.expirationSeconds === 3600 && token.path === workloadTokenPath);
    const tokenMounts = projectedTokens.length === 1
      ? (container.volumeMounts ?? []).filter(mount =>
        mount.name === projectedTokens[0].volume.name && mount.mountPath === workloadTokenDirectory &&
        mount.readOnly === true)
      : [];
    if (serviceAccount?.apiVersion !== 'v1' || serviceAccount.kind !== 'ServiceAccount' ||
        serviceAccount.metadata?.name !== identity.serviceAccount ||
        serviceAccount.metadata?.namespace !== namespace || !guid.test(serviceAccount.metadata?.uid ?? '') ||
        serviceAccount.automountServiceAccountToken !== true ||
        serviceAccount.metadata?.labels?.['azure.workload.identity/use'] !== 'true' ||
        serviceAccount.metadata?.annotations?.['azure.workload.identity/client-id']?.toLowerCase() !==
          identity.clientId.toLowerCase() ||
        serviceAccount.metadata?.annotations?.['azure.workload.identity/tenant-id']?.toLowerCase() !==
          target.tenantId.toLowerCase() ||
        pod.metadata?.labels?.['azure.workload.identity/use'] !== 'true' ||
        pod.spec?.automountServiceAccountToken !== true ||
        workloadTokens.length !== 1 || projectedTokens.length !== 1 || tokenMounts.length !== 1 ||
        ['AZURE_FEDERATED_TOKEN_FILE', 'AZURE_CLIENT_ID', 'AZURE_TENANT_ID']
          .some(name => envList.filter(entry => entry.name === name).length !== 1) ||
        env.get('AZURE_FEDERATED_TOKEN_FILE')?.value !== `${workloadTokenDirectory}/${workloadTokenPath}` ||
        env.get('AZURE_CLIENT_ID')?.value?.toLowerCase() !== identity.clientId.toLowerCase() ||
        env.get('AZURE_TENANT_ID')?.value?.toLowerCase() !== target.tenantId.toLowerCase()) {
      throw new Error('Observed ServiceAccount or projected token does not match the exact workload identity.');
    }
    return passed(name, 'configuration', {
      issuer: target.aksOidcIssuerUrl,
      subject,
      audience: workloadAudience,
      clientId: identity.clientId,
      tenantId: target.tenantId,
      serviceAccountUid: serviceAccount.metadata.uid,
      projectedTokenPath: `${workloadTokenDirectory}/${workloadTokenPath}`,
    });
  } catch (error) {
    return configurationBlocked(name, error.message, {
      serviceAccountName: serviceAccount?.metadata?.name,
      serviceAccountNamespace: serviceAccount?.metadata?.namespace,
      serviceAccountUid: serviceAccount?.metadata?.uid,
      podServiceAccountName: pod?.spec?.serviceAccountName,
    });
  }
}

function checkProbeJobImage(job, pod, verifiedReceipt, imageReference, source) {
  const name = 'foundation-probe-registry-image';
  try {
    const repository = imageRepository(imageReference);
    if (!repository || verifiedReceipt?.kind !== 'foundation-probe-image' ||
        verifiedReceipt.schemaVersion !== 1 || verifiedReceipt.sourceSha !== source.sha ||
        verifiedReceipt.sourceTree !== source.sourceTree || verifiedReceipt.sourceHash !== source.sourceHash ||
        verifiedReceipt.image?.publishedReference !== imageReference ||
        verifiedReceipt.image?.configUser !== '10001:10001' ||
        !manifestDigest.test(verifiedReceipt.image?.localConfigDigest ?? '') ||
        !Array.isArray(verifiedReceipt.image?.repositoryDigests)) {
      throw new Error('Registry verification is missing or does not match the admitted source and image receipt.');
    }
    const matches = [...new Set(verifiedReceipt.image.repositoryDigests
      .filter(value => typeof value === 'string' && value.startsWith(`${repository}@`)))];
    if (matches.length !== 1 || !matches[0].slice(repository.length + 1).match(manifestDigest)) {
      throw new Error('Registry inspection did not return one exact manifest digest for the selected repository.');
    }
    const expectedImage = matches[0];
    const jobImage = job.spec.template.spec.containers[0].image;
    const podContainer = pod.spec.containers[0];
    const pulledImage = normalizeImageId(pod.status.containerStatuses[0].imageID);
    if (jobImage !== expectedImage || podContainer.image !== expectedImage || pulledImage !== expectedImage) {
      throw new Error('Job image, observed pod image, or pulled manifest digest differs from the registry receipt.');
    }
    return passed(name, 'integration', {
      image: expectedImage,
      sourceSha: source.sha,
      sourceTree: source.sourceTree,
      sourceHash: source.sourceHash,
      configUser: verifiedReceipt.image.configUser,
      localConfigDigest: verifiedReceipt.image.localConfigDigest,
      registryManifestDigest: matches[0].slice(repository.length + 1),
      podPulledImageId: pulledImage,
    });
  } catch (error) {
    return blocked(name, error.message, {
      imageReference,
      sourceSha: verifiedReceipt?.sourceSha,
      sourceTree: verifiedReceipt?.sourceTree,
      repositoryDigests: verifiedReceipt?.image?.repositoryDigests,
      jobImage: job?.spec?.template?.spec?.containers?.[0]?.image,
      podImage: pod?.spec?.containers?.[0]?.image,
      podImageId: pod?.status?.containerStatuses?.[0]?.imageID,
    });
  }
}

const receiptKeys = [
  'sourceSha', 'sourceTree', 'sourceHash', 'nonce', 'deployment', 'workloadIdentity', 'monitorConfiguration',
  'providerBindings', 'keyVault', 'blob', 'postgres', 'telemetry',
];
const deploymentKeys = [
  'subscriptionId', 'tenantId', 'resourceGroup', 'resourceGroupId', 'deploymentName', 'deploymentId',
  'aksOidcIssuerUrl', 'identity', 'resources',
];
const monitorConfigKeys = ['appInsightsResourceId', 'ingestionEndpoint', 'instrumentationKeyConfigured'];
const providerPinKeys = [
  'seam', 'providerId', 'adapterVersion', 'optionsSchemaVersion', 'optionsRevision', 'resourceId',
  'generation', 'negotiatedCapabilities',
];
const keyVaultKeys = ['resourceId', 'vaultUri', 'secretName', 'secretVersion', 'redeemed'];
const blobKeys = [
  'containerResourceId', 'containerUri', 'objectKey', 'ownershipNonce', 'eTag', 'contentSha256', 'cleanupConfirmed',
];
const postgresKeys = [
  'serverResourceId', 'host', 'databaseName', 'schemaName', 'runtimeRole', 'effectId', 'inboxMessageId',
  'inboxDisposition', 'outboxEventId', 'outboxSequence', 'transactionCommitted',
];
const telemetryKeys = ['name', 'traceId', 'spanId', 'startedAt'];

function checkProbeReceipt(receipt, target, source, completedAt) {
  const name = 'foundation-probe-receipt';
  try {
    if (!hasExactKeys(receipt, receiptKeys) ||
        !hasExactKeys(receipt.deployment, deploymentKeys) ||
        !hasExactKeys(receipt.workloadIdentity, ['issuer', 'subject', 'audience']) ||
        !hasExactKeys(receipt.monitorConfiguration, monitorConfigKeys) ||
        !hasExactKeys(receipt.keyVault, keyVaultKeys) || !hasExactKeys(receipt.blob, blobKeys) ||
        !hasExactKeys(receipt.postgres, postgresKeys) || !hasExactKeys(receipt.telemetry, telemetryKeys) ||
        !Array.isArray(receipt.providerBindings) || receipt.providerBindings.length !== 3 ||
        receipt.providerBindings.some(pin => !hasExactKeys(pin, providerPinKeys))) {
      throw new Error('Probe output does not match the native #1784 receipt shape.');
    }
    const nonce = receipt.nonce;
    const identity = target.foundationProbeIdentity;
    const resources = target.foundationResources;
    const deployment = receipt.deployment;
    const monitorUrl = new URL(receipt.monitorConfiguration.ingestionEndpoint);
    const startedAt = Date.parse(receipt.telemetry.startedAt);
    const completed = Date.parse(completedAt);
    const expectedPins = [
      ['Secrets', 'azure-key-vault', resources.keyVaultId],
      ['ObjectStore', 'azure-blob', resources.blobContainerId],
      ['Telemetry', 'azure-monitor', resources.appInsightsResourceId],
    ];
    const pinsMatch = receipt.providerBindings.every((pin, index) => {
      const [seam, providerId, resourceId] = expectedPins[index];
      return pin.seam === seam && pin.providerId === providerId && pin.adapterVersion === '0.1.0' &&
        pin.optionsSchemaVersion === 1 && pin.optionsRevision === 'foundation-probe-v1' &&
        sameResourceId(pin.resourceId, resourceId) && pin.generation === 1 &&
        Array.isArray(pin.negotiatedCapabilities) && pin.negotiatedCapabilities.length === 0;
    });
    const deploymentMatches = hasExactKeys(deployment.identity, identityKeys) &&
      sameIdentity(deployment.identity, identity) &&
      hasExactKeys(deployment.resources, resourceKeys) &&
      resourceKeys.every(key => key.endsWith('Id') || key === 'monitorWorkspaceResourceId'
        ? sameResourceId(deployment.resources[key], resources[key])
        : deployment.resources[key] === resources[key]);
    const postgres = receipt.postgres;
    const blob = receipt.blob;
    const keyVault = receipt.keyVault;
    if (receipt.sourceSha !== source.sha || receipt.sourceTree !== source.sourceTree ||
        receipt.sourceHash !== source.sourceHash || !noncePattern.test(nonce ?? '') ||
        deployment.subscriptionId !== target.subscriptionId || deployment.tenantId !== target.tenantId ||
        deployment.resourceGroup !== target.resourceGroup || deployment.resourceGroupId !== target.resourceGroupId ||
        deployment.deploymentName !== target.deploymentName || deployment.deploymentId !== target.deploymentId ||
        deployment.aksOidcIssuerUrl !== target.aksOidcIssuerUrl || !deploymentMatches ||
        receipt.workloadIdentity.issuer !== target.aksOidcIssuerUrl ||
        receipt.workloadIdentity.subject !== `system:serviceaccount:${namespace}:${identity.serviceAccount}` ||
        receipt.workloadIdentity.audience !== workloadAudience ||
        receipt.monitorConfiguration.instrumentationKeyConfigured !== true ||
        !sameResourceId(receipt.monitorConfiguration.appInsightsResourceId, resources.appInsightsResourceId) ||
        monitorUrl.protocol !== 'https:' || monitorUrl.username || monitorUrl.password || monitorUrl.search ||
        monitorUrl.hash || monitorUrl.port !== '' || !['', '/'].includes(monitorUrl.pathname) ||
        !monitorUrl.hostname.toLowerCase().endsWith('.in.applicationinsights.azure.com') ||
        !pinsMatch ||
        !sameResourceId(keyVault.resourceId, resources.keyVaultId) || keyVault.vaultUri !== resources.vaultUri ||
        keyVault.secretName !== target.runtime.keyVaultSecretName ||
        keyVault.secretVersion !== target.runtime.keyVaultSecretVersion || keyVault.redeemed !== true ||
        !sameResourceId(blob.containerResourceId, resources.blobContainerId) ||
        blob.containerUri !== resources.blobContainerUri ||
        blob.objectKey !== `foundation-probe/${nonce}/roundtrip.json` || blob.ownershipNonce !== nonce ||
        typeof blob.eTag !== 'string' || blob.eTag.trim().length === 0 || !fullHash.test(blob.contentSha256 ?? '') ||
        blob.cleanupConfirmed !== true ||
        !sameResourceId(postgres.serverResourceId, resources.postgresServerId) ||
        postgres.host !== resources.postgresHost || postgres.databaseName !== target.runtime.databaseName ||
        postgres.schemaName !== target.runtime.schemaName || postgres.runtimeRole !== target.runtime.databaseRole ||
        !guid.test(postgres.effectId ?? '') || postgres.inboxMessageId !== nonce ||
        postgres.inboxDisposition !== 'Admitted' || !guid.test(postgres.outboxEventId ?? '') ||
        !Number.isSafeInteger(postgres.outboxSequence) || postgres.outboxSequence < 1 ||
        postgres.transactionCommitted !== true ||
        receipt.telemetry.name !== 'foundation-probe' || !/^[0-9a-f]{32}$/.test(receipt.telemetry.traceId ?? '') ||
        /^0+$/.test(receipt.telemetry.traceId) || !/^[0-9a-f]{16}$/.test(receipt.telemetry.spanId ?? '') ||
        /^0+$/.test(receipt.telemetry.spanId) || !Number.isFinite(startedAt) ||
        !Number.isFinite(completed) || startedAt > completed) {
      throw new Error('Probe output is incomplete or differs from the observed source, target, identity, or owned effects.');
    }
    return passed(name, 'integration', {
      sourceSha: receipt.sourceSha,
      sourceTree: receipt.sourceTree,
      nonce,
      workloadIdentity: { issuer: receipt.workloadIdentity.issuer, subject: receipt.workloadIdentity.subject,
        audience: receipt.workloadIdentity.audience },
      keyVault: { secretName: keyVault.secretName, secretVersion: keyVault.secretVersion, redeemed: true },
      blob: { objectKey: blob.objectKey, eTag: blob.eTag, cleanupConfirmed: true },
      postgres: { effectId: postgres.effectId, inboxDisposition: postgres.inboxDisposition,
        outboxEventId: postgres.outboxEventId, outboxSequence: postgres.outboxSequence, transactionCommitted: true },
      telemetry: receipt.telemetry,
    });
  } catch (error) {
    return blocked(name, error.message, {
      sourceSha: receipt?.sourceSha,
      sourceTree: receipt?.sourceTree,
      nonce: receipt?.nonce,
      keyVaultSecretName: receipt?.keyVault?.secretName,
      keyVaultSecretVersion: receipt?.keyVault?.secretVersion,
      blobCleanupConfirmed: receipt?.blob?.cleanupConfirmed,
      postgresInboxDisposition: receipt?.postgres?.inboxDisposition,
      postgresTransactionCommitted: receipt?.postgres?.transactionCommitted,
      telemetryTraceId: receipt?.telemetry?.traceId,
      telemetrySpanId: receipt?.telemetry?.spanId,
    });
  }
}

function runKubectl(args, execKubectl) {
  const result = execKubectl(args, { check: false, timeout: 30_000 });
  if (result?.status !== 0) throw new Error(`kubectl observation failed: ${redactDiagnostic(result?.stderr || result?.stdout)}`);
  return result.stdout ?? '';
}

function observeObject(args, label, execKubectl) {
  return parseJsonObject(runKubectl(args, execKubectl), label);
}

function observePods(args, execKubectl) {
  const output = parseJsonObject(runKubectl(args, execKubectl), 'Probe pod query');
  if (output.apiVersion !== 'v1' || output.kind !== 'List' || !Array.isArray(output.items)) {
    throw new Error('Probe pod query returned an unexpected Kubernetes list.');
  }
  return output.items;
}

function parseProbeLog(text) {
  if (typeof text !== 'string' || text.length === 0 || text.length > 64 * 1024) {
    throw new Error('Completed probe pod did not return a bounded receipt.');
  }
  const lines = text.trim().split(/\r?\n/);
  if (lines.length !== 1) throw new Error('Completed probe pod returned output other than one native receipt.');
  return parseJsonObject(lines[0], 'Probe log');
}

function summarizeFailedProbeLog(text) {
  if (typeof text !== 'string' || text.length === 0) return { logBytes: 0, logLines: 0 };
  if (text.length > 64 * 1024) return { logBytes: text.length, logTooLarge: true };
  const lines = text.trim().split(/\r?\n/);
  const failures = [];
  let unstructuredLines = 0;
  for (const line of lines) {
    try {
      const entry = JSON.parse(line);
      if (entry?.status === 'failed' && /^[a-z][a-z0-9_]{0,79}$/.test(entry.code ?? '')) {
        failures.push({
          code: entry.code,
          ...(typeof entry.failureType === 'string' &&
            /^[A-Za-z][A-Za-z0-9.]{0,79}$/.test(entry.failureType)
            ? { failureType: entry.failureType }
            : {}),
        });
      } else {
        unstructuredLines++;
      }
    } catch {
      unstructuredLines++;
    }
  }
  return { logBytes: text.length, logLines: lines.length, unstructuredLines, failures };
}

export function collectFoundationProbeEvidence({
  source,
  deployment,
  observedCluster,
  kubeContext,
  imageReference,
  imageReceiptPath = 'artifacts/images/foundation-probe.json',
  repoRoot = process.cwd(),
}, {
  execKubectl = (args, options) => run('kubectl', args, options),
  verifyImage = (reference, options) => verifyPublishedFoundationProbeImage(reference, options),
  now = Date.now,
} = {}) {
  const checks = [];
  const collected = { checks, probeReceipt: undefined, completedAt: undefined };
  if (!source || !deployment || !observedCluster || !kubeContext) {
    checks.push(configurationBlocked('kubernetes-target',
      'Exact source, deployment, observed AKS resource, and explicit context are required.'));
    checks.push(configurationBlocked('foundation-probe-target',
      'Target configuration was not read because the exact target is incomplete.'));
    for (const name of ['foundation-probe-job-pod', 'foundation-probe-registry-image',
      'foundation-probe-receipt', 'foundation-probe-workload-identity-exchange']) {
      checks.push(blocked(name, 'Runtime observation was not attempted because the exact target is incomplete.'));
    }
    checks.push(configurationBlocked('foundation-probe-workload-identity',
      'Workload identity configuration was not observed because the exact target is incomplete.'));
    return collected;
  }

  const contextArgs = ['config', 'view', '--minify', '--context', kubeContext, '-o', 'json'];
  let kubeConfig;
  try {
    kubeConfig = observeObject(contextArgs, 'Kubernetes context', execKubectl);
    const contextCheck = checkKubernetesTarget(kubeConfig, kubeContext, observedCluster);
    checks.push(contextCheck);
    if (contextCheck.status !== 'passed') throw new Error(contextCheck.reason);
  } catch (error) {
    if (!checks.some(check => check.name === 'kubernetes-target')) {
      checks.push(configurationBlocked('kubernetes-target', redactDiagnostic(error.message)));
    }
    for (const name of ['foundation-probe-target', 'foundation-probe-job-pod',
      'foundation-probe-workload-identity', 'foundation-probe-registry-image', 'foundation-probe-receipt',
      'foundation-probe-workload-identity-exchange']) {
      const reason = name === 'foundation-probe-target'
        ? 'Target configuration was not read because the Kubernetes endpoint is not the observed AKS cluster.'
        : 'Runtime observation was not attempted because the Kubernetes endpoint is not the observed AKS cluster.';
      checks.push(['foundation-probe-target', 'foundation-probe-workload-identity'].includes(name)
        ? configurationBlocked(name, reason)
        : blocked(name, reason));
    }
    return collected;
  }

  const context = ['--context', kubeContext];
  const observations = {};
  for (const [key, args, label] of [
    ['job', ['get', 'job', jobName, '-n', namespace, '-o', 'json'], 'Probe Job'],
    ['pods', ['get', 'pods', '-n', namespace, '-l', `job-name=${jobName}`, '-o', 'json'], 'Probe pod query'],
    ['serviceAccount', ['get', 'serviceaccount', 'foundation-probe', '-n', namespace, '-o', 'json'], 'Probe ServiceAccount'],
    ['configMap', ['get', 'configmap', configMapName, '-n', namespace, '-o', 'json'], 'Probe target ConfigMap'],
  ]) {
    try {
      const output = key === 'pods'
        ? observePods([...args, ...context], execKubectl)
        : observeObject([...args, ...context], label, execKubectl);
      observations[key] = output;
    } catch (error) {
      observations[key] = { error: redactDiagnostic(error.message) };
    }
  }

  let target;
  if (observations.configMap && !observations.configMap.error) {
    const result = checkTargetConfigMap(observations.configMap, source, deployment);
    checks.push(result.check);
    target = result.target;
  } else {
    checks.push(configurationBlocked('foundation-probe-target',
      observations.configMap?.error ?? 'Observed target ConfigMap is missing.'));
  }

  let jobPod;
  if (target && observations.job && !observations.job.error &&
      Array.isArray(observations.pods) && observations.serviceAccount && !observations.serviceAccount.error) {
    jobPod = checkJobAndPod(observations.job, observations.pods, target, now());
    checks.push(jobPod.check);
    const identityCheck = jobPod.pod
      ? checkWorkloadIdentityProjection(jobPod.pod, observations.serviceAccount, target)
      : configurationBlocked('foundation-probe-workload-identity',
        'No independently observed completed pod is available.');
    checks.push(identityCheck);
  } else {
    const reason = observations.job?.error || observations.pods?.error ||
      observations.serviceAccount?.error || 'The source-bound target configuration is unavailable.';
    checks.push(blocked('foundation-probe-job-pod', reason));
    checks.push(configurationBlocked('foundation-probe-workload-identity', reason));
  }

  let verifiedImage;
  let imageStatus;
  if (jobPod?.pod &&
      checks.find(check => check.name === 'foundation-probe-workload-identity')?.status === 'passed') {
    try {
      if (!imageReference || !imageReceiptPath) throw new Error('A registry image reference and admitted CI image receipt path are required.');
      verifiedImage = verifyImage(imageReference, {
        repoRoot,
        readReceiptPath: imageReceiptPath,
      }).receipt;
      const imageCheck = checkProbeJobImage(observations.job, jobPod.pod, verifiedImage, imageReference, source);
      checks.push(imageCheck);
      imageStatus = imageCheck.status;
    } catch (error) {
      checks.push(blocked('foundation-probe-registry-image', redactDiagnostic(error.message), { imageReference }));
      imageStatus = 'blocked';
    }
  } else {
    checks.push(blocked('foundation-probe-registry-image',
      'Registry inspection was not attempted because the independently observed Job, pod, or identity projection is invalid.'));
    imageStatus = 'blocked';
  }

  const identityProjectionPassed =
    checks.find(check => check.name === 'foundation-probe-workload-identity')?.status === 'passed';
  if (jobPod?.pod && podHasTerminalProbeContainer(jobPod.pod) &&
      identityProjectionPassed && imageStatus === 'passed') {
    try {
      const podName = jobPod.pod.metadata.name;
      const logs = runKubectl([
        'logs', podName, '-n', namespace, '-c', imageName, '--limit-bytes=65536', ...context,
      ], execKubectl);
      if (jobPod.check.status !== 'passed') {
        checks.push({
          name: 'foundation-probe-failure-diagnostic',
          scope: 'diagnostic',
          status: 'captured',
          evidence: {
            jobUid: observations.job.metadata.uid,
            podUid: jobPod.pod.metadata.uid,
            completedAt: jobPod.completedAt,
            exitCode: jobPod.pod.status.containerStatuses[0].state.terminated.exitCode,
            ...summarizeFailedProbeLog(logs),
          },
        });
        checks.push(blocked('foundation-probe-receipt',
          'The Job or pod did not complete successfully; its log output is diagnostic only.'));
        checks.push(blocked('foundation-probe-workload-identity-exchange',
          'Identity exchange was not accepted because the Job or pod did not complete successfully.'));
        return collected;
      }
      const receipt = parseProbeLog(logs);
      const result = checkProbeReceipt(receipt, target, source, jobPod.completedAt);
      checks.push(result);
      if (result.status === 'passed') {
        checks.push(passed('foundation-probe-workload-identity-exchange', 'integration', {
          issuer: receipt.workloadIdentity.issuer,
          subject: receipt.workloadIdentity.subject,
          audience: receipt.workloadIdentity.audience,
          keyVaultResourceId: receipt.keyVault.resourceId,
          secretName: receipt.keyVault.secretName,
          secretVersion: receipt.keyVault.secretVersion,
          redeemed: receipt.keyVault.redeemed,
        }));
        collected.probeReceipt = receipt;
        collected.completedAt = jobPod.completedAt;
      }
    } catch (error) {
      if (jobPod.check.status !== 'passed') {
        checks.push({
          name: 'foundation-probe-failure-diagnostic',
          scope: 'diagnostic',
          status: 'unavailable',
          reason: redactDiagnostic(error.message),
          evidence: { podUid: jobPod.pod.metadata.uid, completedAt: jobPod.completedAt },
        });
      }
      checks.push(blocked('foundation-probe-receipt', redactDiagnostic(error.message), {
        podUid: jobPod.pod.metadata.uid,
        completedAt: jobPod.completedAt,
      }));
      checks.push(blocked('foundation-probe-workload-identity-exchange',
        'The native probe receipt did not prove the exact projected identity and Key Vault redemption.'));
    }
  } else {
    if (jobPod?.pod && podHasTerminalProbeContainer(jobPod.pod) && identityProjectionPassed &&
        imageStatus !== 'passed') {
      checks.push({
        name: 'foundation-probe-failure-diagnostic',
        scope: 'diagnostic',
        status: 'not-captured',
        reason: 'Pod logs were not read because the source-bound registry image did not verify.',
      });
    }
    checks.push(blocked('foundation-probe-receipt',
      'The probe receipt was not consumed because independent Job, pod, identity, and image evidence is incomplete.'));
    checks.push(blocked('foundation-probe-workload-identity-exchange',
      'Identity exchange was not verified by a valid receipt and exact-version Key Vault redemption.'));
  }
  return collected;
}
