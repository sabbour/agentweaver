/**
 * Shared Azure target, source-output, and image validation for deployment tooling.
 * Entry points call these checks before issuing Azure mutations.
 */
// Shared guardrails for every tool in scripts/azure: dedicated-target
// validation, image digest validation, and plan/deploy request shaping.
// No tool in this directory calls `az` with a mutating verb unless the
// caller has passed `--execute` on the CLI explicitly; every entry point
// defaults to a read-only plan.
import { validateAppRoutingDnsZoneResourceIds } from './app-routing-dns.mjs';

// Guard every deployment against shared or 0.x resource groups by allowing only the v1 target.
const DEDICATED_NAME_PATTERN = /^agentweaver$/;

const IMAGE_DIGEST_PATTERN = /^sha256:[0-9a-f]{64}$/;

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

/** Rejects a resource-group or target name that is not the explicitly approved dedicated P0 name. */
export function assertDedicatedTarget(name, kind = 'resource group') {
  if (typeof name !== 'string' || !DEDICATED_NAME_PATTERN.test(name)) {
    throw new Error(
      `Refusing ${kind} "${name}": must match the dedicated v1 P0 naming convention ` +
        `${DEDICATED_NAME_PATTERN} and must not be a shared/0.x target.`,
    );
  }
  return name;
}

/** Validates the required deployment-configured Key Vault name. */
export function assertApprovedKeyVaultName(resourceGroup, name) {
  if (typeof name !== 'string' || name.length < 3 || name.length > 24 ||
      !/^[a-z][a-z0-9-]*[a-z0-9]$/i.test(name) || name.includes('--')) {
    throw new Error(
      'Key Vault name must be configured and contain 3-24 letters, digits, or single hyphens; ' +
        'it must start with a letter and end with a letter or digit.',
    );
  }
  return name;
}

/** Requires an immutable SHA-256 image digest instead of a mutable tag. */
export function assertImageDigest(digest, imageRef = 'image') {
  if (typeof digest !== 'string' || !IMAGE_DIGEST_PATTERN.test(digest)) {
    throw new Error(
      `Refusing ${imageRef}: missing or invalid sha256 digest ("${digest}"). ` +
        'Exact-SHA deployment requires an immutable, digest-pinned image reference.',
    );
  }
  return digest;
}

/** Ensures the selected subscription is the exact authorized subscription. */
export function assertSubscription(subscriptionId, allowedSubscriptionId) {
  if (!allowedSubscriptionId) {
    throw new Error('No allowed subscription ID configured; refusing to target any subscription.');
  }
  if (subscriptionId !== allowedSubscriptionId) {
    throw new Error(
      `Refusing subscription "${subscriptionId}": does not match the authorized dedicated subscription.`,
    );
  }
  return subscriptionId;
}

/** Ensures the selected tenant is the exact authorized tenant. */
export function assertTenant(tenantId, allowedTenantId) {
  if (!allowedTenantId) {
    throw new Error('No allowed tenant ID configured; refusing to target any tenant.');
  }
  if (tenantId !== allowedTenantId) {
    throw new Error(`Refusing tenant "${tenantId}": does not match the authorized dedicated tenant.`);
  }
  return tenantId;
}

// Null tags are an untagged existing resource, never permission to create.
/** Checks resource-group ownership tags and rejects a group owned by another deployment. */
export function assertDedicatedResourceGroupOwnership(resourceGroup, existingTags) {
  if (!existingTags || existingTags['agentweaver:environment'] !== 'v1-p0' ||
      existingTags['agentweaver:managed-by'] !== 'bicep' ||
      !existingTags['agentweaver:owner'] || !existingTags['agentweaver:cost-center']) {
    throw new Error(
      `Refusing resource group "${resourceGroup}": it already exists but is not tagged ` +
        '"agentweaver:environment=v1-p0". Refusing to deploy into a shared or conflicting target.',
    );
  }
}

function jsonResult(result, label) {
  if (result.status !== 0) throw new Error(`${label} failed: ${result.stderr}`);
  try {
    const value = JSON.parse(result.stdout);
    if (!value || typeof value !== 'object') throw new Error('missing object');
    return value;
  } catch {
    throw new Error(`${label} returned malformed JSON.`);
  }
}

// Deployment outputs select configuration, never prove a workload ran.
/** Validates infrastructure outputs against the configured target and returns normalized deployment values. */
export function readFoundationOutputs(outputs, {
  resourceGroup, subscriptionId, keyVaultName: configuredKeyVaultName, appRoutingDnsZoneResourceIds = [],
}) {
  const zoneIds = validateAppRoutingDnsZoneResourceIds(appRoutingDnsZoneResourceIds, subscriptionId);
  const keyVaultName = assertApprovedKeyVaultName(resourceGroup, configuredKeyVaultName);
  const groupId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}`;
  const id = (type, name) => `${groupId}/providers/${type}/${name}`;
  const storageName = `${resourceGroup.replaceAll('-', '')}blob`.slice(0, 24);
  const resources = outputs?.foundationResources?.value;
  const identity = outputs?.foundationProbeIdentity?.value;
  const identityBrokerRuntimeIdentity = outputs?.identityBrokerRuntimeIdentity?.value;
  const identityBrokerMigrationIdentity = outputs?.identityBrokerMigrationIdentity?.value;
  const appRoutingIdentity = outputs?.appRoutingIdentity?.value;
  const appRoutingDomain = outputs?.appRoutingDomain?.value;
  const expected = {
    clusterId: id('Microsoft.ContainerService/managedClusters', `${resourceGroup}-aks`),
    keyVaultId: id('Microsoft.KeyVault/vaults', keyVaultName),
    vaultUri: `https://${keyVaultName.toLowerCase()}.vault.azure.net/`,
    storageAccountId: id('Microsoft.Storage/storageAccounts', storageName),
    blobContainerId: id('Microsoft.Storage/storageAccounts', `${storageName}/blobServices/default/containers/platform-artifacts`),
    blobContainerUri: `https://${storageName}.blob.core.windows.net/platform-artifacts`,
    postgresServerId: id('Microsoft.DBforPostgreSQL/flexibleServers', `${resourceGroup}-pg`),
    postgresHost: `${resourceGroup}-pg.postgres.database.azure.com`,
    monitorWorkspaceResourceId: id('Microsoft.OperationalInsights/workspaces', `${resourceGroup}-law`),
    appInsightsResourceId: id('Microsoft.Insights/components', `${resourceGroup}-appi`),
  };
  for (const [key, value] of Object.entries(expected)) {
    const actual = resources?.[key];
    const matches = key.endsWith('Id') ? typeof actual === 'string' && actual.toLowerCase() === value.toLowerCase() :
      actual === value;
    if (!matches) throw new Error(`Deployment output ${key} is not the exact dedicated target.`);
  }
  const guid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
  const exactServiceIdentity = (candidate, name, serviceAccount) =>
    candidate?.name === name && candidate?.namespace === 'agentweaver' &&
    candidate?.serviceAccount === serviceAccount &&
    typeof candidate?.resourceId === 'string' &&
    candidate?.resourceId?.toLowerCase() === id('Microsoft.ManagedIdentity/userAssignedIdentities',
      `${resourceGroup}-id-${name}`).toLowerCase() &&
    typeof candidate?.clientId === 'string' && typeof candidate?.principalObjectId === 'string' &&
    guid.test(candidate.clientId) && guid.test(candidate.principalObjectId) &&
    candidate.clientId.toLowerCase() !== candidate.principalObjectId.toLowerCase();
  const routingIdentityResourceIdPattern =
    /^\/subscriptions\/[0-9a-f-]{36}\/resourceGroups\/[^/]+\/providers\/Microsoft\.ManagedIdentity\/userAssignedIdentities\/[^/]+$/i;
  const dnsNamePattern = /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i;
  const managedDefaultRequested = zoneIds.length === 0;
  const validDomainName = typeof appRoutingDomain?.domainName === 'string' &&
    dnsNamePattern.test(appRoutingDomain.domainName) &&
    !appRoutingDomain.domainName.toLowerCase().startsWith('privatelink.');
  const serviceIdentityIds = [
    identity?.clientId, identity?.principalObjectId,
    identityBrokerRuntimeIdentity?.clientId, identityBrokerRuntimeIdentity?.principalObjectId,
    identityBrokerMigrationIdentity?.clientId, identityBrokerMigrationIdentity?.principalObjectId,
  ].map(value => typeof value === 'string' ? value.toLowerCase() : undefined);
  const distinctServiceIdentityIds = serviceIdentityIds.every(value => guid.test(value ?? '')) &&
    new Set(serviceIdentityIds).size === serviceIdentityIds.length;
  if (!guid.test(resources?.monitorWorkspaceId ?? '') ||
      outputs.monitorWorkspaceId?.value !== resources.monitorWorkspaceId ||
      outputs.aksClusterName?.value !== `${resourceGroup}-aks` ||
      outputs.storageAccountName?.value !== storageName ||
      typeof appRoutingIdentity?.resourceId !== 'string' ||
      !routingIdentityResourceIdPattern.test(appRoutingIdentity.resourceId) ||
      !appRoutingIdentity.resourceId.toLowerCase().startsWith(`/subscriptions/${subscriptionId.toLowerCase()}/`) ||
      !guid.test(appRoutingIdentity?.clientId ?? '') || !guid.test(appRoutingIdentity?.objectId ?? '') ||
      appRoutingIdentity.clientId.toLowerCase() === appRoutingIdentity.objectId.toLowerCase() ||
      appRoutingIdentity.objectId.toLowerCase() === outputs.aksControlPlanePrincipalId?.value?.toLowerCase() ||
      serviceIdentityIds.includes(appRoutingIdentity.objectId.toLowerCase()) ||
      serviceIdentityIds.includes(appRoutingIdentity.clientId.toLowerCase()) ||
      appRoutingDomain?.managedDefaultRequested !== managedDefaultRequested ||
      (managedDefaultRequested ? !validDomainName : appRoutingDomain?.domainName !== null) ||
      identity?.resourceId?.toLowerCase() !== id('Microsoft.ManagedIdentity/userAssignedIdentities',
        `${resourceGroup}-id-foundation-probe`).toLowerCase() ||
      identity?.name !== 'foundation-probe' || identity?.namespace !== 'agentweaver' ||
      identity?.serviceAccount !== 'foundation-probe' ||
      !guid.test(identity?.clientId ?? '') || !guid.test(identity?.principalObjectId ?? '') ||
      identity.clientId.toLowerCase() === identity.principalObjectId.toLowerCase() ||
      !exactServiceIdentity(identityBrokerRuntimeIdentity, 'identity-broker', 'identity-broker') ||
      !exactServiceIdentity(identityBrokerMigrationIdentity, 'identity-broker-migration', 'identity-broker-migration') ||
      !distinctServiceIdentityIds) {
    throw new Error('Deployment outputs lack the exact App Routing identity, named service principals, or workspace GUID.');
  }
  return {
    resources,
    foundationProbeIdentity: identity,
    identityBrokerRuntimeIdentity,
    identityBrokerMigrationIdentity,
    appRoutingIdentity,
    appRoutingDomain,
  };
}

/** Reads existing workload identity IDs needed by the PostgreSQL bootstrap. */
export function readIdentityPostgresBootstrapValues(source, execAz) {
  const {
    resourceGroup, postgresEntraAdminObjectId,
    postgresEntraAdminPrincipalName, postgresEntraAdminPrincipalType,
  } = source;
  if (!GUID_PATTERN.test(postgresEntraAdminObjectId ?? '') ||
      typeof postgresEntraAdminPrincipalName !== 'string' || !postgresEntraAdminPrincipalName.trim() ||
      !['User', 'Group', 'ServicePrincipal'].includes(postgresEntraAdminPrincipalType)) {
    throw new Error('AKS-only PostgreSQL bootstrap needs the approved foundation Entra administrator inputs.');
  }
  const serverName = `${resourceGroup}-pg`;
  const postgresHost = `${serverName}.postgres.database.azure.com`;

  function readIdentity(name) {
    const identityName = `${resourceGroup}-id-${name}`;
    const identity = jsonResult(execAz([
      'identity', 'show', '--resource-group', resourceGroup, '--name', identityName, '-o', 'json',
    ], { check: false, projectJson: value => ({ principalId: value.principalId }), preserveProjectedJson: true }),
    `Existing ${name} workload identity lookup`);
    if (!GUID_PATTERN.test(identity.principalId ?? '')) {
      throw new Error(`Existing ${name} workload identity returned an invalid PostgreSQL bootstrap principal ID.`);
    }
    return { principalObjectId: identity.principalId };
  }

  const identityBrokerRuntimeIdentity = readIdentity('identity-broker');
  const identityBrokerMigrationIdentity = readIdentity('identity-broker-migration');
  const principalIds = [identityBrokerRuntimeIdentity.principalObjectId, identityBrokerMigrationIdentity.principalObjectId]
    .map(value => value.toLowerCase());
  if (new Set(principalIds).size !== principalIds.length) {
    throw new Error('Existing Identity Broker workload identities must have distinct PostgreSQL bootstrap principals.');
  }

  return {
    resources: { postgresHost },
    postgresEntraAdminPrincipalName,
    identityBrokerRuntimeIdentity,
    identityBrokerMigrationIdentity,
  };
}

// Shared by plan, deploy and acceptance. A caller's ID is not account evidence.
/**
 * Validates the selected account and dedicated resource-group target.
 * It does not enumerate resources or compare post-deployment resource properties.
 */
export function guardAzureTarget(config, execAz) {
  const { resourceGroup, subscriptionId, tenantId, allowedSubscriptionId, allowedTenantId } = config;
  validateAppRoutingDnsZoneResourceIds(config.appRoutingDnsZoneResourceIds, subscriptionId);
  assertDedicatedTarget(resourceGroup);
  assertApprovedKeyVaultName(resourceGroup, config.keyVaultName);
  assertSubscription(subscriptionId, allowedSubscriptionId);
  assertTenant(tenantId, allowedTenantId);

  const boundAz = (args, options) => execAz([...args, '--subscription', subscriptionId], options);
  const account = jsonResult(boundAz(['account', 'show', '-o', 'json'], { check: false }), 'Account lookup');
  if (account.id !== subscriptionId || account.tenantId !== tenantId || account.state !== 'Enabled') {
    throw new Error('Actual Azure account/tenant is not the selected enabled target.');
  }

  const groupResult = boundAz(['group', 'show', '--name', resourceGroup, '-o', 'json'], { check: false });
  if (groupResult.status !== 0) {
    // This route never creates a resource group. Even explicit not-found blocks.
    const notFound = /\(ResourceGroupNotFound\)/.test(groupResult.stderr ?? '');
    throw new Error(notFound ? 'Dedicated resource group does not exist; separately approved bootstrap required.' :
      `Resource group lookup failed closed: ${groupResult.stderr}`);
  }
  const group = jsonResult(groupResult, 'Resource group lookup');
  const groupId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}`;
  if (group.id?.toLowerCase() !== groupId.toLowerCase() || group.name !== resourceGroup) {
    throw new Error('Resource group response is not the exact selected target.');
  }
  assertDedicatedResourceGroupOwnership(resourceGroup, group.tags);

  return {
    execAz: boundAz,
    group,
    targetEvidence: { subscriptionId, tenantId, resourceGroupId: groupId },
  };
}