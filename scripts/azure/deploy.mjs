#!/usr/bin/env node
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { bootstrapP0Namespace } from './lib/namespace-bootstrap.mjs';
import {
  AKS_RBAC_CLUSTER_ADMIN_ROLE_ID,
  armGuid,
  assertDedicatedTarget,
  assertSubscription,
  assertTenant,
  findDanglingAksSubnetRoleAssignment,
  guardAzureTarget,
  readFoundationOutputs,
  resolveClusterAdminRoleAssignmentName,
  verifyClusterAdminRoleAssignment,
} from './lib/guardrails.mjs';

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

export function buildDeployArgs({
  resourceGroup, template, parametersFile, deploymentName, subscription, sourceSha, sourceTree, sourceHash,
  operatorObjectId, operatorRoleAssignmentName,
}) {
  assertDedicatedTarget(resourceGroup);
  if (!subscription || !/^[0-9a-f]{40}$/.test(sourceSha ?? '') || !/^[0-9a-f]{40}$/.test(sourceTree ?? '') ||
      !/^[0-9a-f]{64}$/.test(sourceHash ?? '') || !GUID_PATTERN.test(operatorObjectId ?? '') ||
      !GUID_PATTERN.test(operatorRoleAssignmentName ?? '')) {
    throw new Error('Exact source receipt, operator, role-assignment name and explicit subscription are required.');
  }
  return ['deployment', 'group', 'create', '--resource-group', resourceGroup, '--template-file', template,
    '--parameters', `@${parametersFile}`, `sourceSha=${sourceSha}`, `sourceTree=${sourceTree}`, `sourceHash=${sourceHash}`,
    `operatorObjectId=${operatorObjectId}`, `operatorRoleAssignmentName=${operatorRoleAssignmentName}`,
    '--mode', 'Incremental', '--name', deploymentName, '--subscription', subscription, '-o', 'json'];
}

export function deploy(config, {
  execAz = runAz, sourceResolver = resolveSource, bootstrapNamespace = bootstrapP0Namespace,
} = {}) {
  assertDedicatedTarget(config.resourceGroup);
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  const source = sourceResolver(config);
  const deploymentName = `${config.resourceGroup}-${source.scope === 'aks-only' ? 'aks-' : ''}${source.sha.slice(0, 12)}`;
  const clusterId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${config.resourceGroup}-aks`;
  let operatorRoleAssignmentName = armGuid(clusterId, source.operatorObjectId, AKS_RBAC_CLUSTER_ADMIN_ROLE_ID);
  let args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source, operatorRoleAssignmentName,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName });
  const summary = { ...source, resourceGroup: config.resourceGroup, deploymentName, args, executed: false };
  if (!config.execute) return summary;
  const { execAz: boundAz, group, resources } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  if (source.scope === 'infrastructure-only' &&
      resources.some(resource => resource.type?.toLowerCase() !== 'microsoft.resources/deployments')) {
    throw new Error('Full foundation deployment is limited to an empty dedicated P0 resource group; use the AKS-only template to preserve existing resources.');
  }
  operatorRoleAssignmentName = resolveClusterAdminRoleAssignmentName({ ...config, ...source }, boundAz);
  args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source, operatorRoleAssignmentName,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName });
  summary.args = args;
  // A guarded what-if precedes create. Failure never admits a mutation.
  const whatIfArgs = ['deployment', 'group', 'what-if', '--resource-group', config.resourceGroup,
    '--template-file', source.template, '--parameters', `@${source.parametersFile}`, `sourceSha=${source.sha}`,
    `sourceTree=${source.sourceTree}`, `sourceHash=${source.sourceHash}`,
    `operatorObjectId=${source.operatorObjectId}`, `operatorRoleAssignmentName=${operatorRoleAssignmentName}`,
    '--no-pretty-print'];
  function runWhatIf() {
    const result = boundAz(whatIfArgs, { check: false });
    if (result.status !== 0) throw new Error(`What-if failed: ${result.stderr}`);
  }
  function assertSourceUnchanged() {
    const confirmed = sourceResolver(config);
    if (confirmed.sha !== source.sha || confirmed.sourceTree !== source.sourceTree ||
        confirmed.sourceHash !== source.sourceHash || confirmed.template !== source.template ||
        confirmed.parametersFile !== source.parametersFile ||
        confirmed.operatorObjectId !== source.operatorObjectId) {
      throw new Error('Source inputs changed after what-if; refusing deployment.');
    }
  }
  runWhatIf();
  assertSourceUnchanged();
  const staleAssignmentId = findDanglingAksSubnetRoleAssignment(config, boundAz);
  if (staleAssignmentId) {
    if (!config.allowStaleAksSubnetRoleCleanup) {
      throw new Error('An exact stale AKS subnet role assignment was proven; pass --allow-stale-aks-subnet-role-cleanup to authorize its removal.');
    }
    const cleanup = boundAz(['role', 'assignment', 'delete', '--ids', staleAssignmentId, '--yes', '-o', 'none'],
      { check: false });
    if (cleanup.status !== 0) throw new Error(`Exact stale AKS subnet role cleanup failed: ${cleanup.stderr}`);
    runWhatIf();
    assertSourceUnchanged();
  }
  const result = execAz(args, { check: false });
  if (result.status !== 0) throw new Error(`Infrastructure deployment failed: ${result.stderr}`);
  const deployment = JSON.parse(result.stdout);
  const outputs = deployment.properties?.outputs;
  const deploymentId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.Resources/deployments/${deploymentName}`;
  if (deployment.properties?.provisioningState !== 'Succeeded' ||
      deployment.id?.toLowerCase() !== deploymentId.toLowerCase() ||
      outputs?.sourceSha?.value !== source.sha || outputs?.sourceTree?.value !== source.sourceTree ||
      outputs?.sourceHash?.value !== source.sourceHash ||
      outputs?.operatorRoleAssignmentId?.value?.toLowerCase() !==
        `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${operatorRoleAssignmentName}`.toLowerCase()) {
    throw new Error('Deployment did not return a successful source-bound infrastructure receipt.');
  }
  const verifiedOperatorRoleAssignmentId =
    verifyClusterAdminRoleAssignment({ ...config, ...source }, operatorRoleAssignmentName, boundAz);
  let deploymentReceipt;
  if (source.scope === 'aks-only') {
    const issuer = outputs?.oidcIssuerUrl?.value;
    if (outputs?.clusterName?.value !== `${config.resourceGroup}-aks` ||
        outputs?.clusterId?.value?.toLowerCase() !== clusterId.toLowerCase() ||
        !GUID_PATTERN.test(outputs?.controlPlanePrincipalId?.value ?? '') ||
        typeof issuer !== 'string' || !issuer.startsWith('https://') ||
        !issuer.toLowerCase().includes(`/${config.tenantId.toLowerCase()}/`)) {
      throw new Error('AKS-only deployment outputs do not identify the exact public-cluster source and tenant.');
    }
    deploymentReceipt = {
      clusterId,
      clusterName: outputs.clusterName.value,
      controlPlanePrincipalId: outputs.controlPlanePrincipalId.value,
      oidcIssuerUrl: issuer,
      operatorRoleAssignmentId: verifiedOperatorRoleAssignmentId,
      appRoutingDomain: outputs.appRoutingDomain?.value,
    };
  } else {
    deploymentReceipt = {
      ...readFoundationOutputs(outputs, { ...config, ...source }),
      operatorRoleAssignmentId: verifiedOperatorRoleAssignmentId,
    };
  }
  try {
    assertSourceUnchanged();
    bootstrapNamespace({
      resourceGroup: config.resourceGroup,
      subscriptionId: config.subscriptionId,
      repoRoot: config.repoRoot,
      clusterName: `${config.resourceGroup}-aks`,
    });
  } catch (error) {
    throw new Error(`Infrastructure deployment succeeded, but namespace-only bootstrap failed: ${error.message}`);
  }
  return { ...summary, executed: true, receipt: { scope: source.scope, sourceSha: source.sha,
    sourceTree: source.sourceTree, sourceHash: source.sourceHash, subscriptionId: config.subscriptionId, tenantId: config.tenantId,
    resourceGroup: config.resourceGroup, deploymentName, deploymentId: deployment.id,
    ...deploymentReceipt } };
}

export function cliConfig(values) {
  return { resourceGroup: values['resource-group'], template: values.template, parametersFile: values.parameters,
    repoRoot: process.cwd(), execute: values.execute, subscriptionId: values.subscription,
    allowedSubscriptionId: values['allowed-subscription'], tenantId: values.tenant, allowedTenantId: values['allowed-tenant'],
    operatorObjectId: values['operator-object-id'],
    allowStaleAksSubnetRoleCleanup: values['allow-stale-aks-subnet-role-cleanup'] };
}

export const cliOptions = {
  'resource-group': { type: 'string' }, template: { type: 'string', default: 'infra/bicep/main.bicep' },
  parameters: { type: 'string' }, subscription: { type: 'string' }, 'allowed-subscription': { type: 'string' },
  tenant: { type: 'string' }, 'allowed-tenant': { type: 'string' },
  'operator-object-id': { type: 'string' },
  'allow-stale-aks-subnet-role-cleanup': { type: 'boolean', default: false },
  execute: { type: 'boolean', default: false },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const { values } = parseArgs({ options: cliOptions });
    console.log(JSON.stringify(deploy(cliConfig(values)), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
