#!/usr/bin/env node
/**
 * Azure deployment entry point for the dedicated v1 P0 environment.
 * It validates the selected source and target before provisioning the foundation and identity resources.
 */
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { redact, runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { assertNewFoundation, checkNewFoundation, checkNewFoundationPlan } from './lib/new-foundation.mjs';
import { bootstrapIdentityPostgres } from './lib/identity-postgres-bootstrap.mjs';
import {
  alignExistingP0BrokerHostname, assertIdentityRoutingPlacement, bootstrapIdentityRouting,
  preflightPublicBrokerRegistration,
} from './lib/identity-broker-routing.mjs';
import { bootstrapIdentityBrokerState } from './lib/identity-broker-state.mjs';
import {
  assertBrokerConfigOnlyInputs, assertBrokerRuntimeInputs, bootstrapIdentityBrokerConfigOnly,
  bootstrapIdentityBrokerRuntime,
} from './lib/identity-broker-runtime.mjs';
import {
  activateIdentityBrokerProviderSetup,
  buildIdentityBrokerProviderPlan,
  IdentityBrokerProviderSetupError,
  providerSetupErrorLine,
  readIdentityBrokerProviderInput,
  writeIdentityBrokerProviderPlan,
} from './lib/identity-broker-provider-setup.mjs';
import { bootstrapFoundationProbeInputs } from './lib/foundation-probe-inputs.mjs';
import {
  assertDedicatedTarget,
  assertSubscription,
  assertTenant,
  guardAzureTarget,
  readFoundationOutputs,
  readIdentityPostgresBootstrapValues,
} from './lib/guardrails.mjs';

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

/** Builds source-bound CLI args; only full-foundation creation submits a node-pool count. */
export function buildDeployArgs({
  resourceGroup, template, parametersFile, deploymentName, subscription, sourceSha, sourceTree, sourceHash,
  scope = 'infrastructure-only', nodePoolCount, newFoundation = false, nodeResourceGroup,
}) {
  if (!newFoundation) assertDedicatedTarget(resourceGroup);
  else if (!/^[a-z][a-z0-9]{5,11}$/.test(resourceGroup ?? '') || resourceGroup === 'agentweaver' ||
      scope !== 'infrastructure-only' || nodeResourceGroup !== `${resourceGroup}-aks-nodes`) {
    throw new Error('Invalid new foundation target or managed resource group.');
  }
  if (!subscription || !/^[0-9a-f]{40}$/.test(sourceSha ?? '') || !/^[0-9a-f]{40}$/.test(sourceTree ?? '') ||
      !/^[0-9a-f]{64}$/.test(sourceHash ?? '') ||
      !['infrastructure-only', 'aks-only'].includes(scope) ||
      scope === 'aks-only' && nodePoolCount !== undefined ||
      scope === 'infrastructure-only' && nodePoolCount !== undefined && ![2, 3].includes(nodePoolCount)) {
    throw new Error('Exact source receipt and explicit subscription are required.');
  }
  const args = ['deployment', 'group', 'create', '--resource-group', resourceGroup, '--template-file', template,
    '--parameters', `@${parametersFile}`, `sourceSha=${sourceSha}`, `sourceTree=${sourceTree}`, `sourceHash=${sourceHash}`];
  if (scope === 'infrastructure-only') args.push(`nodePoolCount=${nodePoolCount ?? 2}`);
  if (newFoundation) args.push(`nodeResourceGroup=${nodeResourceGroup}`);
  args.push('--mode', 'Incremental', '--name', deploymentName, '--subscription', subscription, '-o', 'json');
  return args;
}

const RUNTIME_IDENTITY_SERVICES = ['projects-config', 'environment', 'knowledge', 'events-and-sessions', 'orchestrator'];

/**
 * Deploys infra/bicep/p1-runtime-identities.bicep for the selected target and checks the returned identities.
 * execAz must be the target-bound runner, which adds the subscription.
 */
export function deployRuntimeIdentities({ resourceGroup, sourceSha, tags, groupLocation, oidcIssuerUrl }, execAz) {
  const cluster = execAz(['aks', 'show', '--resource-group', resourceGroup, '--name', `${resourceGroup}-aks`,
    '--query', 'location', '-o', 'tsv'], { check: false });
  const clusterLocation = cluster.status === 0 ? cluster.stdout.trim().toLowerCase() : '';
  if (!clusterLocation || clusterLocation !== String(groupLocation).toLowerCase()) {
    throw new Error(`The cluster location (${clusterLocation || 'unknown'}) differs from the resource group location (${groupLocation}).`);
  }
  const result = execAz(['deployment', 'group', 'create', '--resource-group', resourceGroup,
    '--name', `${resourceGroup}-p1-runtime-identities`, '--template-file', 'infra/bicep/p1-runtime-identities.bicep',
    '--parameters', `namePrefix=${resourceGroup}`, `tags=${JSON.stringify(tags)}`, `sourceSha=${sourceSha}`,
    '--mode', 'Incremental', '-o', 'json'], { check: false });
  if (result.status !== 0) throw new Error(`az deployment group create failed: ${result.stderr}`);
  const properties = JSON.parse(result.stdout).properties;
  const identities = properties?.outputs?.runtimeIdentities?.value;
  if (properties?.provisioningState !== 'Succeeded' || properties.outputs.oidcIssuerUrl?.value !== oidcIssuerUrl ||
      !Array.isArray(identities) || identities.length !== RUNTIME_IDENTITY_SERVICES.length ||
      identities.some((identity, i) => identity.name !== RUNTIME_IDENTITY_SERVICES[i] ||
        identity.namespace !== 'agentweaver' || identity.serviceAccount !== identity.name ||
        !GUID_PATTERN.test(identity.clientId ?? '') || !GUID_PATTERN.test(identity.principalObjectId ?? ''))) {
    throw new Error('The deployment did not return the five service identities for this cluster.');
  }
  return identities.map(({ name, clientId, principalObjectId }) => ({ name, clientId, principalObjectId }));
}

/** Canary (EUAP) regions; their clusters can only associate data collection rules in a canary region. */
const isCanaryRegion = location => /euap$/i.test(location);

/**
 * Enables the AKS Azure Monitor add-on with the install workspace, or adopts it when it is already enabled.
 * A different workspace or shared-key auth stops the install. A canary-region cluster with a workspace
 * outside a canary region cannot use Container Insights, so the step is skipped with a warning.
 * execAz must be the target-bound runner, which adds the subscription.
 */
export function ensureAzureMonitorAddon({ resourceGroup, subscriptionId }, execAz, warn = console.warn) {
  const groupId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}`;
  const workspaceResourceId = `${groupId}/providers/Microsoft.OperationalInsights/workspaces/${resourceGroup}-law`;
  const cluster = ['--resource-group', resourceGroup, '--name', `${resourceGroup}-aks`];
  const read = () => {
    const result = execAz(['aks', 'show', ...cluster, '--query', 'addonProfiles.omsagent', '-o', 'json'], { check: false });
    if (result.status !== 0) throw new Error(`Could not read the AKS add-on profile: ${result.stderr}`);
    return JSON.parse(result.stdout.trim() || 'null');
  };
  const state = profile => {
    if (profile?.enabled !== true) return 'disabled';
    if (profile.config?.logAnalyticsWorkspaceResourceID?.toLowerCase() !== workspaceResourceId.toLowerCase()) {
      throw new Error(`The add-on sends to another workspace (${profile.config?.logAnalyticsWorkspaceResourceID}); ` +
        `expected ${workspaceResourceId}. Disable the add-on, then run the deployment again.`);
    }
    if (String(profile.config?.useAADAuth).toLowerCase() !== 'true') {
      throw new Error('The add-on uses shared-key authentication. Disable the add-on, then run the deployment again.');
    }
    return 'enabled';
  };
  if (state(read()) === 'enabled') return { workspaceResourceId, action: 'adopted' };
  const location = (args, name) => {
    const result = execAz(args, { check: false });
    const value = result.status === 0 ? result.stdout.trim() : '';
    if (!/^[a-z0-9]+$/i.test(value)) throw new Error(`Could not read the ${name} location: ${result.stderr}`);
    return value.toLowerCase();
  };
  const clusterLocation = location(['aks', 'show', ...cluster, '--query', 'location', '-o', 'tsv'], 'AKS cluster');
  const workspaceLocation = location(['resource', 'show', '--ids', workspaceResourceId, '--query', 'location',
    '-o', 'tsv'], 'Log Analytics workspace');
  if (isCanaryRegion(clusterLocation) && !isCanaryRegion(workspaceLocation)) {
    warn(`Azure Monitor add-on skipped: cluster region ${clusterLocation} is a canary region and workspace region ` +
      `${workspaceLocation} is not. Container Insights, ContainerNetworkLogs, and egress audit data are not collected.`);
    return { workspaceResourceId, action: 'unsupported-region', clusterLocation, workspaceLocation };
  }
  const enable = execAz(['aks', 'enable-addons', ...cluster, '--addons', 'monitoring',
    '--workspace-resource-id', workspaceResourceId, '--enable-msi-auth-for-monitoring',
    '-o', 'none'], { check: false });
  if (enable.status !== 0) throw new Error(`az aks enable-addons failed: ${enable.stderr}`);
  if (state(read()) !== 'enabled') throw new Error('The AKS add-on profile does not show the add-on as enabled.');
  return { workspaceResourceId, action: 'enabled' };
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
  initializeIdentityBrokerConfigOnly = bootstrapIdentityBrokerConfigOnly,
  preflightIdentityBrokerRegistration = preflightPublicBrokerRegistration,
  initializeFoundationProbeInputs = bootstrapFoundationProbeInputs,
} = {}) {
  if (!config.newFoundation) assertDedicatedTarget(config.resourceGroup);
  if (config.resourceGroup !== 'agentweaver' && (config.bootstrapFoundationProbeInputs ||
      config.bootstrapFoundationProbePostgres || config.alignExistingIdentityBrokerHostname)) {
    throw new Error('Foundation Probe and existing Broker hostname alignment options accept only the agentweaver target.');
  }
  if (config.bootstrapIdentityBrokerConfigOnly && (
    config.newFoundation || config.bootstrapIdentityPostgres || config.bootstrapRuntimeIdentities ||
    config.bootstrapIdentityRouting || config.identityRoutingOnly || config.identityRoutingCertificatesOnly ||
    config.bootstrapIdentityBrokerState || config.alignExistingIdentityBrokerHostname ||
    config.bootstrapIdentityBrokerRuntime || config.bootstrapFoundationProbeInputs ||
    config.bootstrapFoundationProbePostgres || config.gatewayNamespace || config.gatewaySecurityPolicy ||
    config.verifyBrokerReadiness || config.registerBrokerCallback || config.confirmBrokerCallback ||
    config.acceptanceRunId || config.acceptanceRedirectUri || config.brokerImage
  )) {
    throw new Error('Broker ConfigMap-only mode cannot be combined with deployment, routing, runtime, or callback options.');
  }
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  const routingRequested = Boolean(config.bootstrapIdentityRouting || config.identityRoutingOnly);
  if (config.identityRoutingOnly && (
    config.registerBrokerCallback || config.confirmBrokerCallback ||
    config.bootstrapIdentityPostgres || config.bootstrapIdentityBrokerState ||
    config.alignExistingIdentityBrokerHostname || config.bootstrapIdentityBrokerRuntime ||
    config.bootstrapFoundationProbeInputs || config.bootstrapFoundationProbePostgres ||
    config.bootstrapRuntimeIdentities
  )) {
    throw new Error('Identity-routing-only cannot be combined with callback registration or unrelated bootstrap options.');
  }
  if (!config.bootstrapIdentityBrokerRuntime && !config.bootstrapIdentityBrokerConfigOnly &&
      (config.acceptanceRunId || config.acceptanceRedirectUri || config.brokerImage)) {
    throw new Error('Broker runtime inputs require --bootstrap-identity-broker-runtime.');
  }
  if (routingRequested) {
    assertIdentityRoutingPlacement(config);
    if (!GUID_PATTERN.test(config.upstreamClientId ?? '')) {
      throw new Error('Identity routing requires the exact upstream public application client ID.');
    }
    if (config.identityRoutingCertificatesOnly && config.verifyBrokerReadiness) {
      throw new Error('Certificate-only identity routing cannot verify Broker readiness.');
    }
    if (config.registerBrokerCallback && !config.confirmBrokerCallback) {
      throw new Error('Callback registration requires --confirm-identity-broker-callback with the exact route-derived URI.');
    }
    if (config.confirmBrokerCallback && !config.registerBrokerCallback) {
      throw new Error('Callback confirmation requires --register-identity-broker-callback.');
    }
  } else if (config.identityRoutingCertificatesOnly || config.gatewayNamespace || config.gatewaySecurityPolicy ||
      config.upstreamClientId && !config.bootstrapIdentityBrokerRuntime &&
        !config.bootstrapIdentityBrokerConfigOnly ||
      config.verifyBrokerReadiness || config.registerBrokerCallback || config.confirmBrokerCallback) {
    throw new Error('Identity routing inputs require the separate --bootstrap-identity-routing option.');
  }
  const source = sourceResolver(config);
  if (config.newFoundation) assertNewFoundation(config, source);
  if (config.bootstrapIdentityBrokerRuntime) {
    // The approved parameters own the vault name; the CLI does not accept one.
    assertBrokerRuntimeInputs({ ...config, clusterName: `${config.resourceGroup}-aks`,
      keyVaultName: source.keyVaultName });
  }
  if (config.bootstrapIdentityBrokerConfigOnly) {
    assertBrokerConfigOnlyInputs({ ...config, clusterName: `${config.resourceGroup}-aks`,
      keyVaultName: source.keyVaultName });
  }
  if (source.nodePoolMinCount !== 2 || source.nodePoolMaxCount !== 3) {
    throw new Error('Reviewed deployment source must bind the approved system autoscaler bounds 2-3.');
  }
  if (config.identityRoutingOnly && source.scope !== 'aks-only') {
    throw new Error('Identity-routing-only requires the reviewed AKS-only template and parameters.');
  }
  if (config.bootstrapIdentityBrokerConfigOnly && source.scope !== 'aks-only') {
    throw new Error('Broker ConfigMap-only mode requires the reviewed AKS-only template and parameters.');
  }
  if (config.bootstrapRuntimeIdentities && source.scope !== 'aks-only') {
    throw new Error('Runtime identity setup requires the reviewed AKS-only template and parameters.');
  }
  const deploymentName = `${config.resourceGroup}-${source.scope === 'aks-only' ? 'aks-' : ''}${source.sha.slice(0, 12)}`;
  const clusterId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${config.resourceGroup}-aks`;
  const noDeployment = config.identityRoutingOnly || config.bootstrapIdentityBrokerConfigOnly;
  let args = noDeployment ? undefined : buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName,
    newFoundation: config.newFoundation, nodeResourceGroup: `${config.resourceGroup}-aks-nodes` });
  const summary = { ...source, resourceGroup: config.resourceGroup,
    ...(config.identityRoutingOnly ? { identityRoutingOnly: true }
      : config.bootstrapIdentityBrokerConfigOnly ? {} : { deploymentName, args }), executed: false,
    bootstrapIdentityPostgres: Boolean(config.bootstrapIdentityPostgres),
    bootstrapRuntimeIdentities: Boolean(config.bootstrapRuntimeIdentities),
    bootstrapIdentityRouting: routingRequested,
    bootstrapIdentityBrokerState: Boolean(config.bootstrapIdentityBrokerState),
    alignExistingIdentityBrokerHostname: Boolean(config.alignExistingIdentityBrokerHostname),
    bootstrapIdentityBrokerRuntime: Boolean(config.bootstrapIdentityBrokerRuntime),
    bootstrapIdentityBrokerConfigOnly: Boolean(config.bootstrapIdentityBrokerConfigOnly),
    bootstrapFoundationProbeInputs: Boolean(config.bootstrapFoundationProbeInputs),
    bootstrapFoundationProbePostgres: Boolean(config.bootstrapFoundationProbePostgres),
    ...(routingRequested ? { identityRoutingPlan: {
      gatewayNamespace: config.gatewayNamespace, gatewaySecurityPolicy: config.gatewaySecurityPolicy,
      upstreamClientId: config.upstreamClientId, verifyBrokerReadiness: Boolean(config.verifyBrokerReadiness),
      certificatesOnly: Boolean(config.identityRoutingCertificatesOnly),
      appRegistrationMutationRequested: Boolean(config.registerBrokerCallback),
      callbackConfirmation: config.confirmBrokerCallback,
      appRegistrationMutation: false,
    } } : {}) };
  if (!config.execute) return summary;
  const preflight = config.newFoundation
    ? checkNewFoundation(config, source, execAz)
    : guardAzureTarget({ ...config, ...source }, execAz);
  const { execAz: boundAz, group } = preflight;
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  if (routingRequested || config.bootstrapIdentityBrokerRuntime) {
    // Validate the selected registration before what-if/create; any app write still requires explicit confirmation.
    const preflight = await preflightIdentityBrokerRegistration({
      ...config, clusterName: `${config.resourceGroup}-aks`,
    }, { execAz });
    summary.identityBrokerRegistrationPreflight = {
      callbackUri: preflight.callbackUri,
      registrationReady: preflight.registrationReady,
      mutationExecuted: preflight.mutationExecuted,
    };
  }
  function assertSourceUnchanged(stage = 'after what-if') {
    const confirmed = sourceResolver(config);
    if (confirmed.sha !== source.sha || confirmed.sourceTree !== source.sourceTree ||
        confirmed.sourceHash !== source.sourceHash || confirmed.template !== source.template ||
        confirmed.parametersFile !== source.parametersFile ||
        confirmed.nodePoolMinCount !== source.nodePoolMinCount ||
        confirmed.nodePoolMaxCount !== source.nodePoolMaxCount) {
      throw new Error(`Source inputs changed ${stage}; refusing deployment.`);
    }
  }
  function initializeRouting() {
    return initializeIdentityRouting({
      repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
      subscriptionId: config.subscriptionId, tenantId: config.tenantId,
      clusterName: `${config.resourceGroup}-aks`,
      gatewayNamespace: config.gatewayNamespace, gatewaySecurityPolicy: config.gatewaySecurityPolicy,
      upstreamClientId: config.upstreamClientId, verifyBrokerReadiness: Boolean(config.verifyBrokerReadiness),
      identityRoutingCertificatesOnly: Boolean(config.identityRoutingCertificatesOnly),
      registerBrokerCallback: Boolean(config.registerBrokerCallback), confirmBrokerCallback: config.confirmBrokerCallback,
    });
  }
  if (config.identityRoutingOnly) {
    try {
      assertSourceUnchanged('after routing preflight');
      const identityRouting = await initializeRouting();
      return { ...summary, executed: true, receipt: {
        scope: source.scope, sourceSha: source.sha, sourceTree: source.sourceTree, sourceHash: source.sourceHash,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId, resourceGroup: config.resourceGroup,
        identityRouting,
      } };
    } catch (error) {
      throw new Error(`Identity routing bootstrap failed: ${error.message}`);
    }
  }
  if (config.bootstrapIdentityBrokerConfigOnly) {
    try {
      assertSourceUnchanged('after Broker ConfigMap preflight');
      const identityBrokerConfig = await initializeIdentityBrokerConfigOnly({
        repoRoot: config.repoRoot, resourceGroup: config.resourceGroup,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId,
        clusterName: `${config.resourceGroup}-aks`, keyVaultName: source.keyVaultName,
        upstreamClientId: config.upstreamClientId,
      });
      return { ...summary, executed: true, receipt: {
        scope: source.scope, sourceSha: source.sha, sourceTree: source.sourceTree, sourceHash: source.sourceHash,
        subscriptionId: config.subscriptionId, tenantId: config.tenantId, resourceGroup: config.resourceGroup,
        identityBrokerConfig,
      } };
    } catch (error) {
      throw new Error(`Identity Broker ConfigMap bootstrap failed: ${error.message}`);
    }
  }
  const existingIdentityPostgres = (config.bootstrapIdentityPostgres || config.bootstrapFoundationProbePostgres ||
    config.bootstrapIdentityBrokerRuntime) && source.scope === 'aks-only'
    ? readIdentityPostgresBootstrapValues({ ...config, ...source }, boundAz)
    : undefined;
  // Only full-foundation creation owns the pool count; AKS-only reads the cluster without updating it.
  const nodePoolCount = source.scope === 'infrastructure-only' ? source.nodePoolMinCount : undefined;
  args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName, nodePoolCount,
    newFoundation: config.newFoundation, nodeResourceGroup: `${config.resourceGroup}-aks-nodes` });
  summary.args = args;
  // A guarded what-if precedes create. Failure never admits a mutation.
  const whatIfArgs = ['deployment', 'group', 'what-if', '--resource-group', config.resourceGroup,
    '--template-file', source.template, '--parameters', `@${source.parametersFile}`, `sourceSha=${source.sha}`,
    `sourceTree=${source.sourceTree}`, `sourceHash=${source.sourceHash}`];
  if (nodePoolCount !== undefined) whatIfArgs.push(`nodePoolCount=${nodePoolCount}`);
  if (config.newFoundation) whatIfArgs.push(`nodeResourceGroup=${preflight.nodeResourceGroup}`);
  whatIfArgs.push('--no-pretty-print');
  if (config.newFoundation) whatIfArgs.push('--name', deploymentName, '--mode', 'Incremental', '-o', 'json');
  function runWhatIf() {
    const result = boundAz(whatIfArgs, { check: false });
    if (result.status !== 0) throw new Error(`What-if failed: ${result.stderr}`);
    return config.newFoundation ? checkNewFoundationPlan(result, config) : undefined;
  }
  const firstPlan = runWhatIf();
  assertSourceUnchanged();
  if (config.newFoundation) {
    checkNewFoundation(config, source, execAz, preflight.identity);
    if (runWhatIf() !== firstPlan) throw new Error('What-if changed after preflight; refusing deployment.');
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
      outputs?.sourceHash?.value !== source.sourceHash) {
    throw new Error('Deployment did not return a successful source-bound infrastructure receipt.');
  }
  const autoscaling = {
    minCount: source.nodePoolMinCount,
    maxCount: source.nodePoolMaxCount,
    countBefore: null,
    countSubmitted: nodePoolCount ?? null,
    scaleUpProven: false,
  };
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
  if (config.newFoundation) {
    const observed = boundAz(['aks', 'show', '--resource-group', config.resourceGroup, '--name',
      `${config.resourceGroup}-aks`, '--query', 'nodeResourceGroup', '-o', 'json'], { check: false });
    if (observed.status !== 0) throw new Error(`Infrastructure deployment succeeded, but AKS managed group lookup failed: ${observed.stderr}`);
    let nodeGroup;
    try { nodeGroup = JSON.parse(observed.stdout); } catch { throw new Error('Infrastructure deployment succeeded, but AKS managed group lookup returned malformed JSON.'); }
    if (nodeGroup !== preflight.nodeResourceGroup) {
      throw new Error('Infrastructure deployment succeeded, but AKS managed group does not match the selected target.');
    }
    const identityPrefix = `/subscriptions/${config.subscriptionId}/resourceGroups/${nodeGroup}/`.toLowerCase();
    if (!deploymentReceipt.appRoutingIdentity.resourceId.toLowerCase().startsWith(identityPrefix)) {
      throw new Error('Infrastructure deployment succeeded, but the routing identity is outside the AKS managed group.');
    }
  }
  let azureMonitorAddon;
  try {
    azureMonitorAddon = ensureAzureMonitorAddon(config, boundAz);
  } catch (error) {
    throw new Error(`Infrastructure deployment succeeded, but Azure Monitor add-on setup failed: ${redact(error.message)}`);
  }
  let runtimeIdentities;
  if (config.bootstrapRuntimeIdentities) {
    try {
      assertSourceUnchanged();
      runtimeIdentities = deployRuntimeIdentities({ resourceGroup: config.resourceGroup, sourceSha: source.sha,
        tags: group.tags, groupLocation: group.location, oidcIssuerUrl: deploymentReceipt.oidcIssuerUrl }, boundAz);
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but runtime identity setup failed: ${redact(error.message)}`);
    }
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
      { check: false, projectJson: value => ({ principalId: value.principalId }), preserveProjectedJson: true });
      if (probe.status !== 0) throw new Error(`Probe native identity lookup failed: ${probe.stderr}`);
      const identity = JSON.parse(probe.stdout);
      if (!GUID_PATTERN.test(identity.principalId ?? '')) {
        throw new Error('Probe PostgreSQL bootstrap requires a valid existing Probe principal ID.');
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
      identityRouting = await initializeRouting();
    } catch (error) {
      throw new Error(`Infrastructure deployment succeeded, but Identity routing bootstrap failed: ${error.message}`);
    }
  }
  return { ...summary, executed: true, receipt: { scope: source.scope, sourceSha: source.sha,
    sourceTree: source.sourceTree, sourceHash: source.sourceHash, subscriptionId: config.subscriptionId, tenantId: config.tenantId,
    resourceGroup: config.resourceGroup, deploymentName, deploymentId: deployment.id, azureMonitorAddon,
    ...(runtimeIdentities ? { runtimeIdentities } : {}),
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
    newFoundation: values['new-foundation'] ?? false,
    repoRoot: process.cwd(), execute: values.execute, subscriptionId: values.subscription,
    allowedSubscriptionId: values['allowed-subscription'], tenantId: values.tenant, allowedTenantId: values['allowed-tenant'],
    bootstrapIdentityPostgres: values['bootstrap-identity-postgres'] ?? false,
    bootstrapRuntimeIdentities: values['bootstrap-runtime-identities'] ?? false,
    bootstrapIdentityRouting: Boolean(values['bootstrap-identity-routing'] || values['identity-routing-only']),
    identityRoutingOnly: values['identity-routing-only'] ?? false,
    identityRoutingCertificatesOnly: values['identity-routing-certificates-only'] ?? false,
    bootstrapIdentityBrokerState: values['bootstrap-identity-broker-state'] ?? false,
    alignExistingIdentityBrokerHostname: values['align-existing-identity-broker-hostname'] ?? false,
    bootstrapIdentityBrokerRuntime: values['bootstrap-identity-broker-runtime'] ?? false,
    bootstrapIdentityBrokerConfigOnly: values['bootstrap-identity-broker-config-only'] ?? false,
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
    confirmBrokerCallback: values['confirm-identity-broker-callback'],
    identityBrokerProviderConfig: values['identity-broker-provider-config'],
    identityBrokerProviderMode: values['identity-broker-provider-mode'],
    identityBrokerProviderOutput: values['identity-broker-provider-output'],
    expectedConfigResourceVersion: values['expected-identity-broker-config-resource-version'],
    expectedDeploymentResourceVersion: values['expected-identity-broker-deployment-resource-version'] };
}

/** Runs the offline plan or revision-fenced provider activation through the existing installer entry point. */
export async function setupIdentityBrokerProviders(config, dependencies = {}) {
  const invalid = field => {
    throw new IdentityBrokerProviderSetupError('identity_broker_provider_input_invalid', { field });
  };
  if (!['render', 'activate'].includes(config.identityBrokerProviderMode)) {
    invalid('--identity-broker-provider-mode');
  }
  if (!config.identityBrokerProviderConfig) invalid('--identity-broker-provider-config');
  if (config.execute) invalid('--execute');
  if (config.bootstrapIdentityPostgres || config.bootstrapRuntimeIdentities ||
      config.bootstrapIdentityRouting || config.identityRoutingOnly ||
      config.identityRoutingCertificatesOnly || config.bootstrapIdentityBrokerState ||
      config.alignExistingIdentityBrokerHostname || config.bootstrapIdentityBrokerRuntime ||
      config.bootstrapIdentityBrokerConfigOnly ||
      config.bootstrapFoundationProbeInputs || config.bootstrapFoundationProbePostgres ||
      config.gatewayNamespace || config.gatewaySecurityPolicy || config.upstreamClientId ||
      config.verifyBrokerReadiness || config.registerBrokerCallback || config.confirmBrokerCallback) {
    invalid('--identity-broker-provider-mode');
  }
  const input = readIdentityBrokerProviderInput(config.identityBrokerProviderConfig);
  if (config.identityBrokerProviderMode === 'render') {
    if (!config.identityBrokerProviderOutput) invalid('--identity-broker-provider-output');
    if (config.subscriptionId || config.allowedSubscriptionId ||
        config.tenantId || config.allowedTenantId) invalid('--identity-broker-provider-mode');
    if (config.expectedConfigResourceVersion !== undefined) {
      invalid('--expected-identity-broker-config-resource-version');
    }
    if (config.expectedDeploymentResourceVersion !== undefined) {
      invalid('--expected-identity-broker-deployment-resource-version');
    }
    const plan = buildIdentityBrokerProviderPlan(input, config.repoRoot, config.resourceGroup ?? 'agentweaver');
    writeIdentityBrokerProviderPlan(plan, config.identityBrokerProviderOutput);
    return {
      mode: 'render',
      output: config.identityBrokerProviderOutput,
      configMap: plan.configMap.metadata.name,
      registrationChecklist: plan.registrationChecklist,
    };
  }
  if (config.identityBrokerProviderOutput !== undefined) {
    invalid('--identity-broker-provider-output');
  }
  return activateIdentityBrokerProviderSetup(input, config, dependencies);
}

/** CLI option definitions shared by the deployment and planning entry points. */
export const cliOptions = {
  'resource-group': { type: 'string' }, template: { type: 'string', default: 'infra/bicep/main.bicep' },
  'new-foundation': { type: 'boolean', default: false },
  parameters: { type: 'string' }, subscription: { type: 'string' }, 'allowed-subscription': { type: 'string' },
  tenant: { type: 'string' }, 'allowed-tenant': { type: 'string' },
  'bootstrap-identity-postgres': { type: 'boolean', default: false },
  'bootstrap-runtime-identities': { type: 'boolean', default: false },
  'bootstrap-identity-routing': { type: 'boolean', default: false },
  'identity-routing-only': { type: 'boolean', default: false },
  // This stage creates managed TLS certificates only; route writes remain service-guarded.
  'identity-routing-certificates-only': { type: 'boolean', default: false },
  'bootstrap-identity-broker-state': { type: 'boolean', default: false },
  'align-existing-identity-broker-hostname': { type: 'boolean', default: false },
  'bootstrap-identity-broker-runtime': { type: 'boolean', default: false },
  'bootstrap-identity-broker-config-only': { type: 'boolean', default: false },
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
  'identity-broker-provider-config': { type: 'string' },
  'identity-broker-provider-mode': { type: 'string' },
  'identity-broker-provider-output': { type: 'string' },
  'expected-identity-broker-config-resource-version': { type: 'string' },
  'expected-identity-broker-deployment-resource-version': { type: 'string' },
  execute: { type: 'boolean', default: false },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  let providerSetup = false;
  Promise.resolve().then(() => {
    const { values } = parseArgs({ options: cliOptions });
    const config = cliConfig(values);
    providerSetup = Boolean(config.identityBrokerProviderConfig || config.identityBrokerProviderMode ||
      config.identityBrokerProviderOutput || config.expectedConfigResourceVersion !== undefined ||
      config.expectedDeploymentResourceVersion !== undefined);
    return providerSetup ? setupIdentityBrokerProviders(config) : deploy(config);
  }).then(result => {
    console.log(JSON.stringify(result, null, 2));
  }).catch(error => {
    console.error(providerSetup ? providerSetupErrorLine(error) : error.message);
    process.exitCode = 1;
  });
}
