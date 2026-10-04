#!/usr/bin/env node
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { run, runAz } from './lib/exec.mjs';
import { guardAzureTarget, readFoundationOutputs } from './lib/guardrails.mjs';
import { resolveSource, isFullSha } from './lib/git.mjs';
import { collectFoundationProbeEvidence } from './lib/foundation-probe-runtime.mjs';
import { cliConfig, cliOptions } from './deploy.mjs';

const blocked = (name, reason, evidence) => ({ name, scope: 'configuration', status: 'blocked', reason, evidence });
const configured = (name, evidence) => ({ name, scope: 'configuration', status: 'passed', evidence });
const integrationBlocked = (name, reason, evidence) => ({ name, scope: 'integration', status: 'blocked', reason, evidence });
const integrationPassed = (name, evidence) => ({ name, scope: 'integration', status: 'passed', evidence });

export function checkAksNetworkSecurity({ clusterId, resourceGroup, subscriptionId }, execAz) {
  const name = 'aks-observed-network-security';
  const expectedClusterId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}` +
    `/providers/Microsoft.ContainerService/managedClusters/${resourceGroup}-aks`;
  if (!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(subscriptionId ?? '') ||
      !/^[a-zA-Z0-9._()-]{1,90}$/.test(resourceGroup ?? '') ||
      typeof clusterId !== 'string' || clusterId !== expectedClusterId) {
    return blocked(name, 'The exact AKS resource ID from the successful source-bound deployment receipt is required.');
  }

  const apiVersion = '2024-09-01';
  let result;
  try {
    result = execAz(['rest', '--method', 'get', '--url',
      `https://management.azure.com${clusterId}?api-version=${apiVersion}`, '-o', 'json'], { check: false });
  } catch (error) {
    return blocked(name, `Observed AKS resource query failed: ${error.message}`);
  }

  let cluster;
  try {
    if (result.status !== 0) return blocked(name, 'Observed AKS resource query did not succeed.');
    cluster = JSON.parse(result.stdout);
  } catch {
    return blocked(name, 'Observed AKS resource response is malformed.');
  }
  if (!cluster || typeof cluster !== 'object' || Array.isArray(cluster) ||
      typeof cluster.id !== 'string' || cluster.id !== clusterId) {
    return blocked(name, 'Observed AKS resource ID differs from the approved deployment receipt.');
  }

  const versionMatch = /^(\d+)\.(\d+)(?:\.\d+)?$/.exec(
    cluster.properties?.kubernetesVersion ?? '');
  if (!versionMatch || Number(versionMatch[1]) < 1 ||
      (Number(versionMatch[1]) === 1 && Number(versionMatch[2]) < 29)) {
    return blocked(name, 'Observed AKS Kubernetes version must be 1.29 or later.');
  }

  const networkProfile = cluster.properties?.networkProfile;
  if (typeof networkProfile?.networkDataplane !== 'string' ||
      networkProfile.networkDataplane.toLowerCase() !== 'cilium' ||
      networkProfile.advancedNetworking?.enabled !== true ||
      networkProfile.advancedNetworking?.security?.enabled !== true) {
    return blocked(name, 'Observed AKS must have the Cilium dataplane and advanced network security enabled.');
  }

  return configured(name, {
    clusterId: cluster.id,
    kubernetesVersion: cluster.properties.kubernetesVersion,
    networkDataplane: networkProfile.networkDataplane,
    advancedNetworkingEnabled: true,
    advancedNetworkingSecurityEnabled: true,
    apiServerHosts: [cluster.properties?.privateFqdn, cluster.properties?.fqdn]
      .filter(value => typeof value === 'string')
      .map(value => value.toLowerCase().replace(/\.$/, '')),
    apiVersion,
  });
}

export function checkDeployedSha({
  resourceGroup, subscriptionId, deploymentName, expectedSha, sourceTree, sourceHash,
  appRoutingDnsZoneResourceIds = [],
}, execAz) {
  const name = 'deployed-sha';
  if (!isFullSha(expectedSha) || !isFullSha(sourceTree) || !/^[0-9a-f]{64}$/.test(sourceHash ?? '') ||
      deploymentName !== `${resourceGroup}-${expectedSha.slice(0, 12)}`) {
    return blocked(name, 'Exact source SHA, Git tree, input hash and SHA-derived deployment name are required.');
  }
  const result = execAz(['deployment', 'group', 'show', '--resource-group', resourceGroup,
    '--name', deploymentName, '-o', 'json'], { check: false });
  try {
    const deployment = JSON.parse(result.stdout);
    const outputs = deployment.properties?.outputs;
    const deploymentId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}/providers/Microsoft.Resources/deployments/${deploymentName}`;
    if (result.status !== 0 || deployment.properties?.provisioningState !== 'Succeeded' ||
        deployment.id?.toLowerCase() !== deploymentId.toLowerCase() ||
        outputs?.sourceSha?.value !== expectedSha || outputs?.sourceTree?.value !== sourceTree ||
        outputs?.sourceHash?.value !== sourceHash) {
      return blocked(name, 'No successful deployment with matching Bicep source outputs.');
    }
    return configured(name, { deployedSha: expectedSha, sourceTree, sourceHash, deploymentId: deployment.id,
      scope: 'infrastructure-only', servicesDeployed: false,
      ...readFoundationOutputs(outputs, { resourceGroup, subscriptionId, appRoutingDnsZoneResourceIds }) });
  } catch (error) {
    return blocked(name, `Deployment evidence is missing, malformed or mismatched: ${error.message}`);
  }
}

export function checkServiceDigests({ services = {} }) {
  return blocked('service-image-digests', 'Caller digest strings do not prove a running pod/image. #1784 workload evidence is required.',
    { services, verified: false });
}

export function checkWorkloadIdentity({ clusterName, resourceGroup, identityChecks = [] }, execAz) {
  const name = 'workload-identity-oidc';
  const issuer = execAz(['aks', 'show', '--resource-group', resourceGroup, '--name', clusterName,
    '--query', 'oidcIssuerProfile.issuerUrl', '-o', 'tsv'], { check: false });
  const issuerUrl = issuer.stdout.trim();
  if (issuer.status !== 0 || !issuerUrl.startsWith('https://')) return blocked(name, 'AKS issuer is missing.');
  const identities = [];
  for (const { identityName, federatedCredentialName, expectedSubject } of identityChecks) {
    if (expectedSubject !== 'system:serviceaccount:agentweaver-v1-p0:foundation-probe') {
      return blocked(name, 'This layout reserves only the foundation-probe principal.');
    }
    const result = execAz(['identity', 'federated-credential', 'show', '--name', federatedCredentialName,
      '--identity-name', identityName, '--resource-group', resourceGroup, '-o', 'json'], { check: false });
    let credential;
    try { credential = JSON.parse(result.stdout); } catch { return blocked(name, 'Malformed federation evidence.'); }
    if (result.status !== 0 || credential.subject !== expectedSubject || credential.issuer !== issuerUrl ||
        !Array.isArray(credential.audiences) || credential.audiences.length !== 1 ||
        credential.audiences[0] !== 'api://AzureADTokenExchange') {
      return blocked(name, 'Exact subject/issuer/audience configuration differs.');
    }
    identities.push({ identityName, issuer: credential.issuer, subject: credential.subject,
      audience: credential.audiences[0] });
  }
  if (identities.length === 0) return blocked(name, 'Exact foundation-probe federation configuration is missing.',
    { issuerUrl, identities, exact: false, tokenExchangeVerified: false });
  return configured(name, { issuerUrl, identities, exact: true, tokenExchangeVerified: false });
}

export function checkKeyVaultSecretVersion({ secretVersion }) {
  return blocked('key-vault-secret-version', secretVersion ?
    'An operator secret lookup is not workload-identity proof. #1784 must redeem this exact version.' :
    'Exact Key Vault version is missing. Latest-version resolution cannot satisfy acceptance.');
}

// Operator diagnostics only. #1784 owns the SDK workload probe.
// A unique generation plus If-None-Match prevents overwriting another writer.
export function checkBlobRoundtrip({ accountName, container, executeProbes = false, repoRoot = process.cwd() },
  execAz, { uuid = randomUUID } = {}) {
  const name = 'blob-roundtrip';
  if (!executeProbes) return blocked(name, 'Owned write effects require separate authorization; no probe was run.');
  const generation = uuid();
  if (!/^[a-zA-Z0-9-]{1,64}$/.test(generation)) return blocked(name, 'Invalid generated nonce.');
  const blobName = `acceptance-${generation}.txt`;
  const directory = resolve(repoRoot, 'artifacts', 'azure', generation);
  const input = resolve(directory, 'input.txt');
  const output = resolve(directory, 'output.txt');
  const content = `agentweaver-v1-p0:${generation}`;
  const common = ['--account-name', accountName, '--container-name', container, '--name', blobName, '--auth-mode', 'login'];
  let primaryFailure;
  let cleanupFailure;
  let fixtureOwned = false;
  let uploadAttempted = false;
  try {
    mkdirSync(resolve(repoRoot, 'artifacts', 'azure'), { recursive: true });
    mkdirSync(directory);
    fixtureOwned = true;
    writeFileSync(input, content, { flag: 'wx' });
    uploadAttempted = true;
    const upload = execAz(['storage', 'blob', 'upload', ...common, '--file', input, '--overwrite', 'false',
      '--if-none-match', '*', '--metadata', `generation=${generation}`, '-o', 'json'], { check: false, timeout: 60_000 });
    if (upload.status !== 0) throw new Error(`Upload failed: ${upload.stderr}`);
    const download = execAz(['storage', 'blob', 'download', ...common, '--file', output], { check: false, timeout: 60_000 });
    if (download.status !== 0 || readFileSync(output, 'utf8') !== content) throw new Error('Readback did not match the owned nonce.');
  } catch (error) {
    primaryFailure = error.message;
  } finally {
    // Upload can time out after the server created the blob. Inspect ownership
    // even on throw/failure, then remove only that observed generation/ETag.
    try {
      if (uploadAttempted) {
        const shown = execAz(['storage', 'blob', 'show', ...common, '-o', 'json'], { check: false, timeout: 60_000 });
        if (shown.status !== 0) {
          if (!/\(BlobNotFound\)/.test(shown.stderr ?? '')) throw new Error(`Cleanup lookup failed: ${shown.stderr}`);
        } else {
          const blob = JSON.parse(shown.stdout);
          if (blob.metadata?.generation !== generation || typeof blob.properties?.etag !== 'string' || !blob.properties.etag) {
            throw new Error('Cleanup blocked: exact generation/ETag ownership is missing.');
          }
          const removed = execAz(['storage', 'blob', 'delete', ...common, '--if-match', blob.properties.etag],
            { check: false, timeout: 60_000 });
          if (removed.status !== 0) throw new Error(`Conditional cleanup failed: ${removed.stderr}`);
        }
      }
    } catch (error) {
      cleanupFailure = error.message;
    }
    try {
      if (fixtureOwned) rmSync(directory, { recursive: true, force: true });
    } catch (error) {
      cleanupFailure = [cleanupFailure, `Local fixture cleanup failed: ${error.message}`].filter(Boolean).join('; ');
    }
  }
  if (primaryFailure || cleanupFailure) return blocked(name, 'Owned Blob diagnostic failed.',
    { blobName, generation, primaryFailure, cleanupFailure, cleanupFailed: Boolean(cleanupFailure) });
  return blocked(name, 'Operator Blob diagnostic completed, but does not prove the pod identity.',
    { blobName, generation, cleanupFailed: false, operatorOnly: true });
}

export function checkMonitorTrace({
  workspaceId, query, runId, expectedSha, sourceTree, traceId, spanId, startedAt, completedAt,
},
  execAz, { now = Date.now } = {}) {
  const name = 'monitor-trace';
  const start = Date.parse(startedAt);
  const complete = Date.parse(completedAt);
  const current = now();
  if (query || !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(workspaceId ?? '') ||
      !/^[0-9a-f]{32}$/.test(runId ?? '') || !isFullSha(expectedSha) || !isFullSha(sourceTree) ||
      !/^[0-9a-f]{32}$/.test(traceId ?? '') || /^0+$/.test(traceId) ||
      !/^[0-9a-f]{16}$/.test(spanId ?? '') || /^0+$/.test(spanId) ||
      !Number.isFinite(start) || !Number.isFinite(complete) || start > complete || complete >= current ||
      current - start > 15 * 60 * 1000) {
    return integrationBlocked(name,
      'Exact workspace GUID, completed Job, fresh start time, SHA/tree, 32-hex nonce, trace and span are required. Custom queries are not evidence.');
  }
  const effectiveQuery = `union AppDependencies, AppRequests | where TimeGenerated >= datetime(${new Date(start).toISOString()}) ` +
    `and TimeGenerated <= datetime(${new Date(current).toISOString()}) | where Name == "foundation-probe" ` +
    `and OperationId == "${traceId}" and Id == "${spanId}" ` +
    `| where tostring(Properties["probe.source_sha"]) == "${expectedSha}" ` +
    `and tostring(Properties["probe.source_tree"]) == "${sourceTree}" and tostring(Properties["probe.nonce"]) == "${runId}" ` +
    '| project TimeGenerated, Name, OperationId, Id, Properties';
  const result = execAz(['monitor', 'log-analytics', 'query', '--workspace', workspaceId,
    '--analytics-query', effectiveQuery, '-o', 'json'], { check: false });
  try {
    const rows = JSON.parse(result.stdout);
    const matched = result.status === 0 && Array.isArray(rows) && rows.some(row => {
      const properties = typeof row.Properties === 'string' ? JSON.parse(row.Properties) : row.Properties;
      const timestamp = Date.parse(row.TimeGenerated);
      return row.Name === 'foundation-probe' && row.OperationId === traceId && row.Id === spanId &&
        properties?.['probe.source_sha'] === expectedSha && properties?.['probe.source_tree'] === sourceTree &&
        properties?.['probe.nonce'] === runId && timestamp >= start && timestamp <= current;
    });
    if (!matched) return integrationBlocked(name, 'No fresh exact-SHA/nonce telemetry from the Logs data-plane API.');
    return integrationPassed(name, {
      sourceSha: expectedSha,
      sourceTree,
      nonce: runId,
      traceId,
      spanId,
      startedAt,
      completedAt,
      queriedAt: new Date(current).toISOString(),
      query: effectiveQuery,
    });
  } catch {
    return integrationBlocked(name, 'Monitor result is malformed or has no correlated evidence.');
  }
}

export function runAcceptance(config, {
  execAz = runAz,
  execKubectl = (args, options) => run('kubectl', args, options),
  sourceResolver = resolveSource,
  verifyImage,
  now = Date.now,
  uuid = randomUUID,
} = {}) {
  const checks = [];
  let candidate;
  let source;
  let boundAz;
  let deploymentEvidence;
  let aksEvidence;
  let identityEvidence;
  try {
    source = sourceResolver(config);
    candidate = { sourceSha: source.sha, sourceTree: source.sourceTree, sourceHash: source.sourceHash, scope: source.scope };
    if (config.expectedSha !== source.sha) throw new Error('Expected deployment SHA differs from reviewed HEAD.');
    const target = guardAzureTarget({ ...config, ...source }, execAz);
    boundAz = target.execAz;
    const { group } = target;
    if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
      throw new Error('Source ownership differs from the actual target.');
    }
    const check = (name, operation) => {
      try {
        const result = operation();
        checks.push(result);
        return result;
      } catch (error) {
        const result = blocked(name, error.message);
        checks.push(result);
        return result;
      }
    };
    const deploymentCheck = check('deployed-sha', () => checkDeployedSha({ ...config, sourceTree: source.sourceTree,
      sourceHash: source.sourceHash, appRoutingDnsZoneResourceIds: source.appRoutingDnsZoneResourceIds }, boundAz));
    deploymentEvidence = deploymentCheck.status === 'passed' ? deploymentCheck.evidence : undefined;
    const clusterName = `${config.resourceGroup}-aks`;
    const accountName = `${config.resourceGroup.replaceAll('-', '')}blob`.slice(0, 24);
    const networkCheck = check('aks-observed-network-security', () => deploymentEvidence?.resources.clusterId ?
      checkAksNetworkSecurity({ ...config, clusterId: deploymentEvidence.resources.clusterId }, boundAz) :
      blocked('aks-observed-network-security',
        'The successful source-bound deployment receipt must name the exact AKS resource before preflight.'));
    aksEvidence = networkCheck.status === 'passed' ? networkCheck.evidence : undefined;
    const federationCheck = check('workload-identity-oidc', () => checkWorkloadIdentity({ ...config, clusterName, identityChecks: [{
      identityName: `${config.resourceGroup}-id-foundation-probe`,
      federatedCredentialName: 'foundation-probe-workload-identity',
      expectedSubject: 'system:serviceaccount:agentweaver-v1-p0:foundation-probe',
    }] }, boundAz));
    identityEvidence = federationCheck.status === 'passed' ? federationCheck.evidence : undefined;
    if (config.collectRuntimeEvidence !== true) {
      check('key-vault-secret-version', () => checkKeyVaultSecretVersion(config));
      checks.push(checkServiceDigests(config));
    }
    if (config.executeProbes === true) {
      check('blob-roundtrip', () => deploymentEvidence?.resources.storageAccountId?.toLowerCase() ===
        `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.Storage/storageAccounts/${accountName}`.toLowerCase() ?
        checkBlobRoundtrip({ ...config, accountName }, boundAz, { uuid }) :
        blocked('blob-roundtrip', 'The successful source-bound receipt must name the exact dedicated storage account before a diagnostic.'));
    } else {
      checks.push({ name: 'blob-roundtrip', scope: 'diagnostic', status: 'not-run',
        reason: 'Operator Blob diagnostics require separate authorization and do not contribute to probe acceptance.' });
    }
  } catch (error) {
    checks.push(blocked('target-and-source', error.message));
  }

  if (config.collectRuntimeEvidence === true && source && deploymentEvidence && aksEvidence && identityEvidence && boundAz) {
    const deployment = {
      subscriptionId: config.subscriptionId,
      tenantId: config.tenantId,
      resourceGroup: config.resourceGroup,
      resourceGroupId: `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}`,
      deploymentName: config.deploymentName,
      deploymentId: deploymentEvidence.deploymentId,
      aksOidcIssuerUrl: identityEvidence.issuerUrl,
      foundationProbeIdentity: deploymentEvidence.foundationProbeIdentity,
      resources: deploymentEvidence.resources,
    };
    const runtime = collectFoundationProbeEvidence({
      source,
      deployment,
      observedCluster: aksEvidence,
      kubeContext: config.kubeContext,
      imageReference: config.imageReference,
      imageReceiptPath: config.imageReceiptPath ?? resolve(process.cwd(), 'artifacts', 'images', 'foundation-probe.json'),
      repoRoot: config.repoRoot ?? process.cwd(),
    }, { execKubectl, verifyImage, now });
    checks.push(...runtime.checks);
    if (runtime.probeReceipt) {
      const workspaceId = deploymentEvidence.resources.monitorWorkspaceId;
      if (!/^[0-9a-f-]{36}$/i.test(workspaceId ?? '') ||
          (config.workspaceId && config.workspaceId !== workspaceId)) {
        checks.push(integrationBlocked('monitor-trace',
          'The exact workspace GUID must come from the successful source-bound deployment receipt.'));
      } else {
        checks.push(checkMonitorTrace({
          workspaceId,
          query: config.query,
          runId: runtime.probeReceipt.nonce,
          expectedSha: source.sha,
          sourceTree: source.sourceTree,
          traceId: runtime.probeReceipt.telemetry.traceId,
          spanId: runtime.probeReceipt.telemetry.spanId,
          startedAt: runtime.probeReceipt.telemetry.startedAt,
          completedAt: runtime.completedAt,
        }, boundAz, { now }));
      }
    } else {
      checks.push(integrationBlocked('monitor-trace',
        'No validated completed probe receipt is available for a post-completion Monitor query.'));
    }
  } else if (config.collectRuntimeEvidence === true) {
    for (const name of ['kubernetes-target', 'foundation-probe-target', 'foundation-probe-job-pod',
      'foundation-probe-workload-identity', 'foundation-probe-registry-image', 'foundation-probe-receipt',
      'foundation-probe-workload-identity-exchange', 'monitor-trace']) {
      const reason = 'Runtime collection is blocked until exact source, deployment, observed AKS, and federation configuration pass.';
      checks.push(['kubernetes-target', 'foundation-probe-target', 'foundation-probe-workload-identity'].includes(name)
        ? blocked(name, reason)
        : integrationBlocked(name, reason));
    }
  } else {
    checks.push(integrationBlocked('runtime-workload-evidence',
      'Full P0 acceptance requires explicit --collect-runtime-evidence to independently observe the completed Job, pod, projected identity, registry manifest, probe receipt, and post-completion Monitor row.'));
    checks.push(integrationBlocked('monitor-trace',
      'A Monitor query needs trace IDs from a validated completed probe receipt; caller-supplied trace fields are not evidence.'));
  }

  if (config.collectRuntimeEvidence === true) {
    const requiredRuntimeChecks = [
      'foundation-probe-job-pod', 'foundation-probe-registry-image',
      'foundation-probe-receipt', 'foundation-probe-workload-identity-exchange', 'monitor-trace',
    ];
    const runtimeChecks = requiredRuntimeChecks.map(name => checks.find(check => check.name === name));
    const runtimePassed = runtimeChecks.every(check => check?.status === 'passed');
    checks.push(runtimePassed
      ? integrationPassed('runtime-workload-evidence', { independentRuntimeChecks: requiredRuntimeChecks })
      : integrationBlocked('runtime-workload-evidence', 'One or more required independent runtime observations are blocked.',
        { blockedChecks: runtimeChecks.filter(check => check?.status !== 'passed').map(check => check?.name) }));
  }

  const requiredChecks = [
    'deployed-sha', 'aks-observed-network-security', 'workload-identity-oidc',
    'kubernetes-target', 'foundation-probe-target', 'foundation-probe-workload-identity',
  ];
  const allRequired = config.collectRuntimeEvidence === true &&
    requiredChecks.every(name => checks.find(check => check.name === name)?.status === 'passed') &&
    checks.find(check => check.name === 'runtime-workload-evidence')?.status === 'passed';
  return {
    scope: 'p0-integration',
    overall: allRequired ? 'passed' : 'blocked',
    candidate,
    checks,
    deployedAcceptance: allRequired,
  };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const options = { ...cliOptions };
    delete options.execute;
    const { values } = parseArgs({ options: { ...options, 'expected-sha': { type: 'string' },
      'deployment-name': { type: 'string' },
      'workspace-id': { type: 'string' }, 'collect-runtime-evidence': { type: 'boolean', default: false },
      'kube-context': { type: 'string' }, 'image-reference': { type: 'string' },
      'image-receipt': { type: 'string' } } });
    const report = runAcceptance({ ...cliConfig(values), expectedSha: values['expected-sha'],
      deploymentName: values['deployment-name'],
      workspaceId: values['workspace-id'],
      collectRuntimeEvidence: values['collect-runtime-evidence'], kubeContext: values['kube-context'],
      imageReference: values['image-reference'], imageReceiptPath: values['image-receipt'] });
    console.log(JSON.stringify(report, null, 2));
    process.exitCode = report.deployedAcceptance ? 0 : 1;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
