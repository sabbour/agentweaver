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
import {
  alignExistingP0BrokerHostname, assertIdentityRoutingPlacement, bootstrapIdentityRouting,
  preflightPublicBrokerRegistration,
} from './lib/identity-broker-routing.mjs';
import { bootstrapIdentityBrokerState } from './lib/identity-broker-state.mjs';
import { assertBrokerRuntimeInputs, bootstrapIdentityBrokerRuntime } from './lib/identity-broker-runtime.mjs';
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
  scope = 'infrastructure-only', nodePoolCount,
}) {
  assertDedicatedTarget(resourceGroup);
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
  args.push('--mode', 'Incremental', '--name', deploymentName, '--subscription', subscription, '-o', 'json');
  return args;
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
  preflightIdentityBrokerRegistration = preflightPublicBrokerRegistration,
  initializeFoundationProbeInputs = bootstrapFoundationProbeInputs,
} = {}) {
  assertDedicatedTarget(config.resourceGroup);
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  const routingRequested = Boolean(config.bootstrapIdentityRouting || config.identityRoutingOnly);
  if (config.identityRoutingOnly && (
    config.registerBrokerCallback || config.confirmBrokerCallback ||
    config.bootstrapIdentityPostgres || config.bootstrapIdentityBrokerState ||
    config.alignExistingIdentityBrokerHostname || config.bootstrapIdentityBrokerRuntime ||
    config.bootstrapFoundationProbeInputs || config.bootstrapFoundationProbePostgres
  )) {
    throw new Error('Identity-routing-only cannot be combined with callback registration or unrelated bootstrap options.');
  }
  if (config.bootstrapIdentityBrokerRuntime) {
    assertBrokerRuntimeInputs({ ...config, clusterName: `${config.resourceGroup}-aks` });
  } else if (config.acceptanceRunId || config.acceptanceRedirectUri || config.brokerImage) {
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
      config.upstreamClientId && !config.bootstrapIdentityBrokerRuntime ||
      config.verifyBrokerReadiness || config.registerBrokerCallback || config.confirmBrokerCallback) {
    throw new Error('Identity routing inputs require the separate --bootstrap-identity-routing option.');
  }
  const source = sourceResolver(config);
  if (source.nodePoolMinCount !== 2 || source.nodePoolMaxCount !== 3) {
    throw new Error('Reviewed deployment source must bind the approved system autoscaler bounds 2-3.');
  }
  if (config.identityRoutingOnly && source.scope !== 'aks-only') {
    throw new Error('Identity-routing-only requires the reviewed AKS-only template and parameters.');
  }
  const deploymentName = `${config.resourceGroup}-${source.scope === 'aks-only' ? 'aks-' : ''}${source.sha.slice(0, 12)}`;
  const clusterId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${config.resourceGroup}-aks`;
  let args = config.identityRoutingOnly ? undefined : buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName });
  const summary = { ...source, resourceGroup: config.resourceGroup,
    ...(config.identityRoutingOnly ? { identityRoutingOnly: true } : { deploymentName, args }), executed: false,
    bootstrapIdentityPostgres: Boolean(config.bootstrapIdentityPostgres),
    bootstrapIdentityRouting: routingRequested,
    bootstrapIdentityBrokerState: Boolean(config.bootstrapIdentityBrokerState),
    alignExistingIdentityBrokerHostname: Boolean(config.alignExistingIdentityBrokerHostname),
    bootstrapIdentityBrokerRuntime: Boolean(config.bootstrapIdentityBrokerRuntime),
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
  const { execAz: boundAz, group } = guardAzureTarget({ ...config, ...source }, execAz);
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
  const existingIdentityPostgres = (config.bootstrapIdentityPostgres || config.bootstrapFoundationProbePostgres ||
    config.bootstrapIdentityBrokerRuntime) && source.scope === 'aks-only'
    ? readIdentityPostgresBootstrapValues({ ...config, ...source }, boundAz)
    : undefined;
  // Only full-foundation creation owns the pool count; AKS-only reads the cluster without updating it.
  const nodePoolCount = source.scope === 'infrastructure-only' ? source.nodePoolMinCount : undefined;
  args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName, nodePoolCount });
  summary.args = args;
  // A guarded what-if precedes create. Failure never admits a mutation.
  const whatIfArgs = ['deployment', 'group', 'what-if', '--resource-group', config.resourceGroup,
    '--template-file', source.template, '--parameters', `@${source.parametersFile}`, `sourceSha=${source.sha}`,
    `sourceTree=${source.sourceTree}`, `sourceHash=${source.sourceHash}`];
  if (nodePoolCount !== undefined) whatIfArgs.push(`nodePoolCount=${nodePoolCount}`);
  whatIfArgs.push('--no-pretty-print');
  function runWhatIf() {
    const result = boundAz(whatIfArgs, { check: false });
    if (result.status !== 0) throw new Error(`What-if failed: ${result.stderr}`);
  }
  runWhatIf();
  assertSourceUnchanged();
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
    bootstrapIdentityPostgres: values['bootstrap-identity-postgres'] ?? false,
    bootstrapIdentityRouting: Boolean(values['bootstrap-identity-routing'] || values['identity-routing-only']),
    identityRoutingOnly: values['identity-routing-only'] ?? false,
    identityRoutingCertificatesOnly: values['identity-routing-certificates-only'] ?? false,
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
  'bootstrap-identity-postgres': { type: 'boolean', default: false },
  'bootstrap-identity-routing': { type: 'boolean', default: false },
  'identity-routing-only': { type: 'boolean', default: false },
  // This stage creates managed TLS certificates only; route writes remain service-guarded.
  'identity-routing-certificates-only': { type: 'boolean', default: false },
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
