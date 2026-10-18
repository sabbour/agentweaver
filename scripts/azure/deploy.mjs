#!/usr/bin/env node
/**
 * Azure deployment entry point for the dedicated v1 P0 environment.
 * It validates the selected source and target before provisioning the foundation and identity resources.
 */
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { bootstrapIdentityPostgres } from './lib/identity-postgres-bootstrap.mjs';
import { alignExistingP0BrokerHostname, assertIdentityRoutingPlacement, bootstrapIdentityRouting } from './lib/identity-broker-routing.mjs';
import { bootstrapIdentityBrokerState } from './lib/identity-broker-state.mjs';
import { assertBrokerRuntimeInputs, bootstrapIdentityBrokerRuntime } from './lib/identity-broker-runtime.mjs';
import { bootstrapFoundationProbeInputs } from './lib/foundation-probe-inputs.mjs';
import { readSystemNodePool } from './lib/aks-autoscaling.mjs';
import {
  assertDedicatedTarget,
  assertSubscription,
  assertTenant,
  findDanglingAksSubnetRoleAssignment,
  guardAzureTarget,
  readFoundationOutputs,
  readExistingIdentityPostgresMetadata,
} from './lib/guardrails.mjs';

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

/** Builds the literal Azure CLI argument list for a source-bound infrastructure deployment. */
export function buildDeployArgs({
  resourceGroup, template, parametersFile, deploymentName, subscription, sourceSha, sourceTree, sourceHash,
  nodePoolCount = 2,
}) {
  assertDedicatedTarget(resourceGroup);
  if (!subscription || !/^[0-9a-f]{40}$/.test(sourceSha ?? '') || !/^[0-9a-f]{40}$/.test(sourceTree ?? '') ||
      !/^[0-9a-f]{64}$/.test(sourceHash ?? '') || ![2, 3].includes(nodePoolCount)) {
    throw new Error('Exact source receipt and explicit subscription are required.');
  }
  return ['deployment', 'group', 'create', '--resource-group', resourceGroup, '--template-file', template,
    '--parameters', `@${parametersFile}`, `sourceSha=${sourceSha}`, `sourceTree=${sourceTree}`, `sourceHash=${sourceHash}`,
    `nodePoolCount=${nodePoolCount}`,
    '--mode', 'Incremental', '--name', deploymentName, '--subscription', subscription, '-o', 'json'];
}

/**
 * Validates the Azure target and source, then runs the requested deployment and bootstrap steps.
 * Azure or bootstrap errors are propagated; mutating work is gated by the explicit execute setting.
 */
export async function deploy(config, {
  execAz = runAz, sourceResolver = resolveSource,
  initializeIdentityPostgres = bootstrapIdentityPostgres,
  initializeIdentityRouting = bootstrapIdentityRouting,
  initializeIdentityBrokerState = bootstrapIdentityBrokerState,
  alignIdentityBrokerHostname = alignExistingP0BrokerHostname,
  initializeIdentityBrokerRuntime = bootstrapIdentityBrokerRuntime,
  initializeFoundationProbeInputs = bootstrapFoundationProbeInputs,
} = {}) {
  assertDedicatedTarget(config.resourceGroup);
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  if (config.bootstrapIdentityBrokerRuntime) {
    assertBrokerRuntimeInputs({ ...config, clusterName: `${config.resourceGroup}-aks` });
  } else if (config.acceptanceRunId || config.acceptanceRedirectUri || config.brokerImage) {
    throw new Error('Broker runtime inputs require --bootstrap-identity-broker-runtime.');
  }
  if (config.bootstrapIdentityRouting) {
    assertIdentityRoutingPlacement(config);
    if (!GUID_PATTERN.test(config.upstreamClientId ?? '')) {
      throw new Error('Identity routing requires the exact upstream public application client ID.');
    }
    if (config.registerBrokerCallback && !config.confirmBrokerCallback) {
      throw new Error('Callback registration requires --confirm-identity-broker-callback with the exact route-derived URI.');
    }
    if (config.confirmBrokerCallback && !config.registerBrokerCallback) {
      throw new Error('Callback confirmation requires --register-identity-broker-callback.');
    }
  } else if (config.gatewayNamespace || config.gatewaySecurityPolicy ||
      config.upstreamClientId && !config.bootstrapIdentityBrokerRuntime ||
      config.verifyBrokerReadiness || config.registerBrokerCallback || config.confirmBrokerCallback) {
    throw new Error('Identity routing inputs require the separate --bootstrap-identity-routing option.');
  }
  const source = sourceResolver(config);
  if (source.nodePoolMinCount !== 2 || source.nodePoolMaxCount !== 3) {
    throw new Error('Reviewed deployment source must bind the approved system autoscaler bounds 2-3.');
  }
  const deploymentName = `${config.resourceGroup}-${source.scope === 'aks-only' ? 'aks-' : ''}${source.sha.slice(0, 12)}`;
  const clusterId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${config.resourceGroup}-aks`;
  let args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName });
  const summary = { ...source, resourceGroup: config.resourceGroup, deploymentName, args, executed: false,
    bootstrapIdentityPostgres: Boolean(config.bootstrapIdentityPostgres),
    bootstrapIdentityRouting: Boolean(config.bootstrapIdentityRouting),
    bootstrapIdentityBrokerState: Boolean(config.bootstrapIdentityBrokerState),
    alignExistingIdentityBrokerHostname: Boolean(config.alignExistingIdentityBrokerHostname),
    bootstrapIdentityBrokerRuntime: Boolean(config.bootstrapIdentityBrokerRuntime),
    bootstrapFoundationProbeInputs: Boolean(config.bootstrapFoundationProbeInputs),
    bootstrapFoundationProbePostgres: Boolean(config.bootstrapFoundationProbePostgres),
    ...(config.bootstrapIdentityRouting ? { identityRoutingPlan: {
      gatewayNamespace: config.gatewayNamespace, gatewaySecurityPolicy: config.gatewaySecurityPolicy,
      upstreamClientId: config.upstreamClientId, verifyBrokerReadiness: Boolean(config.verifyBrokerReadiness),
      appRegistrationMutationRequested: Boolean(config.registerBrokerCallback),
      callbackConfirmation: config.confirmBrokerCallback,
      appRegistrationMutation: false,
    } } : {}) };
  if (!config.execute) return summary;
  const { execAz: boundAz, group, resources } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  if (source.scope === 'infrastructure-only' &&
      resources.some(resource => resource.type?.toLowerCase() !== 'microsoft.resources/deployments')) {
    throw new Error('Full foundation deployment is limited to an empty dedicated P0 resource group; use the AKS-only template to preserve existing resources.');
  }
  const existingIdentityPostgres = (config.bootstrapIdentityPostgres || config.bootstrapFoundationProbePostgres ||
    config.bootstrapIdentityBrokerRuntime) && source.scope === 'aks-only'
    ? readExistingIdentityPostgresMetadata({ ...config, ...source }, boundAz)
    : undefined;
  const autoscalerTarget = { ...config, ...source };
  const poolBefore = source.scope === 'aks-only' ? readSystemNodePool(autoscalerTarget, boundAz) : undefined;
  const nodePoolCount = poolBefore?.count ?? source.nodePoolMinCount;
  args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName, nodePoolCount });
  summary.args = args;
  // A guarded what-if precedes create. Failure never admits a mutation.
  const whatIfArgs = ['deployment', 'group', 'what-if', '--resource-group', config.resourceGroup,
    '--template-file', source.template, '--parameters', `@${source.parametersFile}`, `sourceSha=${source.sha}`,
    `sourceTree=${source.sourceTree}`, `sourceHash=${source.sourceHash}`,
    `nodePoolCount=${nodePoolCount}`,
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
        confirmed.nodePoolMinCount !== source.nodePoolMinCount ||
        confirmed.nodePoolMaxCount !== source.nodePoolMaxCount) {
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
  if (poolBefore) {
    const poolAtCreate = readSystemNodePool(autoscalerTarget, boundAz);
    if (poolAtCreate.count !== nodePoolCount ||
        poolAtCreate.enableAutoScaling !== poolBefore.enableAutoScaling ||
        poolAtCreate.minCount !== poolBefore.minCount || poolAtCreate.maxCount !== poolBefore.maxCount) {
      throw new Error('System node pool changed after what-if; refusing to reset autoscaler state or count.');
    }
  }
  const result = execAz(args, { check: false });
  if (result.status !== 0) throw new Error(`Infrastructure deployment failed: ${result.stderr}`);
  const deployment = JSON.parse(result.stdout);
  const outputs = deployment.properties?.outputs;
  const deploymentId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.Resources/deployments/${deploymentName}`;
  if (deployment.properties?.provisioningState !== 'Succeeded' ||
      deployment.id?.toLowerCase() !== deploymentId.toLowerCase() ||
      outputs?.sourceSha?.value !== source.sha || outputs?.sourceTree?.value !== source.sourceTree ||
      outputs?.sourceHash?.value !== source.sourceHash) {
    throw new Error('Deployment did not return a successful source-bound infrastructure receipt.');
  }
  const poolAfter = readSystemNodePool(autoscalerTarget, boundAz, { requireAutoscaling: true });
  const autoscaling = { poolName: poolAfter.name, vmSize: poolAfter.vmSize,
    enabled: poolAfter.enableAutoScaling, minCount: poolAfter.minCount, maxCount: poolAfter.maxCount,
    countBefore: poolBefore?.count ?? null, countSubmitted: nodePoolCount, countAfter: poolAfter.count,
    provisioningState: poolAfter.provisioningState, scaleUpProven: false };
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
      appRoutingDomain: outputs.appRoutingDomain?.value,
    };
  } else {
    deploymentReceipt = {
      ...readFoundationOutputs(outputs, { ...config, ...source }),
    };
  }
  let identityPostgresBootstrap;
  if (config.bootstrapIdentityPostgres) {
    try {
      assertSourceUnchanged();
      const target = existingIdentityPostgres ?? {
        resources: deploymentReceipt.resources,
        postgresEntraAdminPrincipalName: source.postgresEntraAdminPrincipalName,
        identityBrokerRuntimeIdentity: deploymentReceipt.identityBrokerRuntimeIdentity,
        identityBrokerMigrationIdentity: deploymentReceipt.identityBrokerMigrationIdentity,
      };
      identityPostgresBootstrap = await initializeIdentityPostgres({
        repoRoot: config.repoRoot,
        resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId,
        tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`,
        postgresHost: target.resources.postgresHost,
        adminUsername: target.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: target.identityBrokerRuntimeIdentity.principalObjectId,
        migrationPrincipalObjectId: target.identityBrokerMigrationIdentity.principalObjectId,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Identity PostgreSQL bootstrap failed: ${error.message}`);
    }
  }
  let foundationProbePostgresBootstrap;
  if (config.bootstrapFoundationProbePostgres) {
    try {
      assertSourceUnchanged();
      const target = existingIdentityPostgres ?? {
        resources: deploymentReceipt.resources,
        postgresEntraAdminPrincipalName: source.postgresEntraAdminPrincipalName,
        identityBrokerRuntimeIdentity: deploymentReceipt.identityBrokerRuntimeIdentity,
        identityBrokerMigrationIdentity: deploymentReceipt.identityBrokerMigrationIdentity,
      };
      const probe = boundAz(['identity', 'show', '--resource-group', config.resourceGroup,
        '--name', `${config.resourceGroup}-id-foundation-probe`, '-o', 'json'],
      { check: false, projectJson: value => ({ id: value.id, principalId: value.principalId }), preserveProjectedJson: true });
      if (probe.status !== 0) throw new Error(`Probe native identity lookup failed: ${probe.stderr}`);
      const identity = JSON.parse(probe.stdout);
      const expected = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/${config.resourceGroup}-id-foundation-probe`;
      if (identity.id?.toLowerCase() !== expected.toLowerCase() || !GUID_PATTERN.test(identity.principalId ?? '')) {
        throw new Error('Probe PostgreSQL bootstrap requires the exact existing approved Probe principal.');
      }
      foundationProbePostgresBootstrap = await initializeIdentityPostgres({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`, postgresHost: target.resources.postgresHost,
        adminUsername: target.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: target.identityBrokerRuntimeIdentity.principalObjectId,
        migrationPrincipalObjectId: target.identityBrokerMigrationIdentity.principalObjectId,
        foundationProbePrincipalObjectId: identity.principalId,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Foundation Probe PostgreSQL bootstrap failed: ${error.message}`);
    }
  }
  let identityHostnameAlignment;
  if (config.alignExistingIdentityBrokerHostname) {
    try {
      assertSourceUnchanged();
      identityHostnameAlignment = await alignIdentityBrokerHostname({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, clusterName: `${config.resourceGroup}-aks`,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but existing P0 Identity hostname alignment failed: ${error.message}`);
    }
  }
  let identityBrokerState;
  if (config.bootstrapIdentityBrokerState) {
    try {
      assertSourceUnchanged();
      identityBrokerState = await initializeIdentityBrokerState({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, clusterName: `${config.resourceGroup}-aks`,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but initial Identity Broker state bootstrap failed: ${error.message}`);
    }
  }
  let identityBrokerRuntime;
  let identityPostgresRuntimeBootstrap;
  if (config.bootstrapIdentityBrokerRuntime) {
    try {
      assertSourceUnchanged();
      const target = existingIdentityPostgres ?? {
        resources: deploymentReceipt.resources,
        postgresEntraAdminPrincipalName: source.postgresEntraAdminPrincipalName,
        identityBrokerRuntimeIdentity: deploymentReceipt.identityBrokerRuntimeIdentity,
        identityBrokerMigrationIdentity: deploymentReceipt.identityBrokerMigrationIdentity,
      };
      identityPostgresRuntimeBootstrap = await initializeIdentityPostgres({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`, postgresHost: target.resources.postgresHost,
        adminUsername: target.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: target.identityBrokerRuntimeIdentity.principalObjectId,
        migrationPrincipalObjectId: target.identityBrokerMigrationIdentity.principalObjectId,
      });
      assertSourceUnchanged();
      identityBrokerRuntime = await initializeIdentityBrokerRuntime({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`, upstreamClientId: config.upstreamClientId,
        keyVaultName: source.keyVaultName,
        acceptanceRunId: config.acceptanceRunId, acceptanceRedirectUri: config.acceptanceRedirectUri,
        brokerImage: config.brokerImage,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Identity Broker runtime setup failed: ${error.message}`);
    }
  }
  let foundationProbeInputs;
  if (config.bootstrapFoundationProbeInputs) {
    try {
      assertSourceUnchanged();
      foundationProbeInputs = await initializeFoundationProbeInputs({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`, keyVaultName: source.keyVaultName,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Foundation Probe input setup failed: ${error.message}`);
    }
  }
  let identityRouting;
  if (config.bootstrapIdentityRouting) {
    try {
      assertSourceUnchanged();
      identityRouting = await initializeIdentityRouting({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`,
        gatewayNamespace: config.gatewayNamespace, gatewaySecurityPolicy: config.gatewaySecurityPolicy,
        upstreamClientId: config.upstreamClientId, verifyBrokerReadiness: Boolean(config.verifyBrokerReadiness),
        registerBrokerCallback: Boolean(config.registerBrokerCallback), confirmBrokerCallback: config.confirmBrokerCallback,
      });
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Identity routing bootstrap failed: ${error.message}`);
    }
  }
  return { ...summary, executed: true, receipt: { scope: source.scope, sourceSha: source.sha,
    sourceTree: source.sourceTree, sourceHash: source.sourceHash, subscriptionId: config.subscriptionId, tenantId: config.tenantId,
    resourceGroup: config.resourceGroup, deploymentName, deploymentId: deployment.id,
    ...(identityPostgresBootstrap ? { identityPostgresBootstrap } : {}),
    ...(identityRouting ? { identityRouting } : {}),
    ...(identityBrokerState ? { identityBrokerState } : {}),
    ...(identityHostnameAlignment ? { identityHostnameAlignment } : {}),
    ...(identityBrokerRuntime ? { identityBrokerRuntime } : {}),
    ...(identityPostgresRuntimeBootstrap ? { identityPostgresRuntimeBootstrap } : {}),
    ...(foundationProbeInputs ? { foundationProbeInputs } : {}),
    ...(foundationProbePostgresBootstrap ? { foundationProbePostgresBootstrap } : {}),
    ...deploymentReceipt, autoscaling } };
}

/** Converts parsed command-line values into the deployment configuration consumed by deploy(). */
export function cliConfig(values) {
  return { resourceGroup: values['resource-group'], template: values.template, parametersFile: values.parameters,
    repoRoot: process.cwd(), execute: values.execute, subscriptionId: values.subscription,
    allowedSubscriptionId: values['allowed-subscription'], tenantId: values.tenant, allowedTenantId: values['allowed-tenant'],
    allowStaleAksSubnetRoleCleanup: values['allow-stale-aks-subnet-role-cleanup'],
    bootstrapIdentityPostgres: values['bootstrap-identity-postgres'] ?? false,
    bootstrapIdentityRouting: values['bootstrap-identity-routing'] ?? false,
    bootstrapIdentityBrokerState: values['bootstrap-identity-broker-state'] ?? false,
    alignExistingIdentityBrokerHostname: values['align-existing-identity-broker-hostname'] ?? false,
    bootstrapIdentityBrokerRuntime: values['bootstrap-identity-broker-runtime'] ?? false,
    acceptanceRunId: values['identity-acceptance-run-id'],
    acceptanceRedirectUri: values['identity-acceptance-redirect-uri'],
    brokerImage: values['identity-broker-image'],
    bootstrapFoundationProbeInputs: values['bootstrap-foundation-probe-inputs'] ?? false,
    bootstrapFoundationProbePostgres: values['bootstrap-foundation-probe-postgres'] ?? false,
    gatewayNamespace: values['identity-gateway-namespace'],
    gatewaySecurityPolicy: values['identity-gateway-security-policy'],
    upstreamClientId: values['identity-upstream-client-id'],
    verifyBrokerReadiness: values['verify-identity-broker-readiness'] ?? false,
    registerBrokerCallback: values['register-identity-broker-callback'] ?? false,
    confirmBrokerCallback: values['confirm-identity-broker-callback'] };
}

/** CLI option definitions shared by the deployment and planning entry points. */
export const cliOptions = {
  'resource-group': { type: 'string' }, template: { type: 'string', default: 'infra/bicep/main.bicep' },
  parameters: { type: 'string' }, subscription: { type: 'string' }, 'allowed-subscription': { type: 'string' },
  tenant: { type: 'string' }, 'allowed-tenant': { type: 'string' },
  'allow-stale-aks-subnet-role-cleanup': { type: 'boolean', default: false },
  'bootstrap-identity-postgres': { type: 'boolean', default: false },
  'bootstrap-identity-routing': { type: 'boolean', default: false },
  'bootstrap-identity-broker-state': { type: 'boolean', default: false },
  'align-existing-identity-broker-hostname': { type: 'boolean', default: false },
  'bootstrap-identity-broker-runtime': { type: 'boolean', default: false },
  'identity-acceptance-run-id': { type: 'string' },
  'identity-acceptance-redirect-uri': { type: 'string' },
  'identity-broker-image': { type: 'string' },
  'bootstrap-foundation-probe-inputs': { type: 'boolean', default: false },
  'bootstrap-foundation-probe-postgres': { type: 'boolean', default: false },
  'identity-gateway-namespace': { type: 'string' },
  'identity-gateway-security-policy': { type: 'string' },
  'identity-upstream-client-id': { type: 'string' },
  'verify-identity-broker-readiness': { type: 'boolean', default: false },
  'register-identity-broker-callback': { type: 'boolean', default: false },
  'confirm-identity-broker-callback': { type: 'string' },
  execute: { type: 'boolean', default: false },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  Promise.resolve().then(() => {
    const { values } = parseArgs({ options: cliOptions });
    return deploy(cliConfig(values));
  }).then(result => {
    console.log(JSON.stringify(result, null, 2));
  }).catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
