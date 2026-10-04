// Shared guardrails for every tool in scripts/azure: dedicated-target
// validation, image digest validation, and plan/deploy request shaping.
// No tool in this directory calls `az` with a mutating verb unless the
// caller has passed `--execute` on the CLI explicitly; every entry point
// defaults to a read-only plan.
import { createHash } from 'node:crypto';
import { parseAppRoutingDnsZoneResourceId, validateAppRoutingDnsZoneResourceIds } from './app-routing-dns.mjs';

// Dedicated v1 P0 resources must use this naming convention. Anything else
// (including any 0.x resource group, however named) is rejected: this is
// an allowlist, not a denylist of known 0.x names, so it does not depend on
// knowing every existing 0.x resource name.
const DEDICATED_NAME_PATTERN = /^aw-v1-p0(-[a-z0-9]+)*$/;

const IMAGE_DIGEST_PATTERN = /^sha256:[0-9a-f]{64}$/;

export function assertDedicatedTarget(name, kind = 'resource group') {
  if (typeof name !== 'string' || !DEDICATED_NAME_PATTERN.test(name)) {
    throw new Error(
      `Refusing ${kind} "${name}": must match the dedicated v1 P0 naming convention ` +
        `${DEDICATED_NAME_PATTERN} and must not be a shared/0.x target.`,
    );
  }
  return name;
}

export function assertImageDigest(digest, imageRef = 'image') {
  if (typeof digest !== 'string' || !IMAGE_DIGEST_PATTERN.test(digest)) {
    throw new Error(
      `Refusing ${imageRef}: missing or invalid sha256 digest ("${digest}"). ` +
        'Exact-SHA deployment requires an immutable, digest-pinned image reference.',
    );
  }
  return digest;
}

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
export function readFoundationOutputs(outputs, { resourceGroup, subscriptionId, appRoutingDnsZoneResourceIds = [] }) {
  const zoneIds = validateAppRoutingDnsZoneResourceIds(appRoutingDnsZoneResourceIds, subscriptionId);
  const groupId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}`;
  const id = (type, name) => `${groupId}/providers/${type}/${name}`;
  const storageName = `${resourceGroup.replaceAll('-', '')}blob`.slice(0, 24);
  const resources = outputs?.foundationResources?.value;
  const identity = outputs?.foundationProbeIdentity?.value;
  const appRoutingIdentity = outputs?.appRoutingIdentity?.value;
  const appRoutingDomain = outputs?.appRoutingDomain?.value;
  const expected = {
    clusterId: id('Microsoft.ContainerService/managedClusters', `${resourceGroup}-aks`),
    keyVaultId: id('Microsoft.KeyVault/vaults', `${resourceGroup}-kv`),
    vaultUri: `https://${resourceGroup}-kv.vault.azure.net/`,
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
  const routingIdentityResourceIdPattern =
    /^\/subscriptions\/[0-9a-f-]{36}\/resourceGroups\/[^/]+\/providers\/Microsoft\.ManagedIdentity\/userAssignedIdentities\/[^/]+$/i;
  const dnsNamePattern = /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i;
  const managedDefaultRequested = zoneIds.length === 0;
  const validDomainName = typeof appRoutingDomain?.domainName === 'string' &&
    dnsNamePattern.test(appRoutingDomain.domainName) &&
    !appRoutingDomain.domainName.toLowerCase().startsWith('privatelink.');
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
      appRoutingIdentity.objectId.toLowerCase() === identity?.principalObjectId?.toLowerCase() ||
      appRoutingDomain?.managedDefaultRequested !== managedDefaultRequested ||
      (managedDefaultRequested ? !validDomainName : appRoutingDomain?.domainName !== null) ||
      identity?.resourceId?.toLowerCase() !== id('Microsoft.ManagedIdentity/userAssignedIdentities',
        `${resourceGroup}-id-foundation-probe`).toLowerCase() ||
      identity?.name !== 'foundation-probe' || identity?.namespace !== 'agentweaver-v1-p0' ||
      identity?.serviceAccount !== 'foundation-probe' ||
      !guid.test(identity?.clientId ?? '') || !guid.test(identity?.principalObjectId ?? '') ||
      identity.clientId.toLowerCase() === identity.principalObjectId.toLowerCase()) {
    throw new Error('Deployment outputs lack the exact App Routing identity, named probe principal, or workspace GUID.');
  }
  return { resources, foundationProbeIdentity: identity, appRoutingIdentity, appRoutingDomain };
}

// ARM guid() uses UUID v5 with this namespace and hyphen-joined arguments.
function armGuid(...values) {
  const namespace = Buffer.from('11fb06fb712d4ddd98c7e71bbd588830', 'hex');
  const bytes = createHash('sha1').update(namespace).update(values.join('-')).digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// Shared by plan, deploy and acceptance. A caller's ID is not account evidence.
export function guardAzureTarget(config, execAz) {
  const { resourceGroup, subscriptionId, tenantId, allowedSubscriptionId, allowedTenantId } = config;
  const appRoutingDnsZoneResourceIds = validateAppRoutingDnsZoneResourceIds(
    config.appRoutingDnsZoneResourceIds, subscriptionId,
  );
  assertDedicatedTarget(resourceGroup);
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
  const resources = jsonResult(boundAz(['resource', 'list', '--resource-group', resourceGroup, '-o', 'json'],
    { check: false }), 'Resource inventory');
  if (!Array.isArray(resources)) throw new Error('Resource inventory is not an array.');
  const expectedResources = new Map([
    ['microsoft.network/virtualnetworks', [`${resourceGroup}-vnet`]],
    ['microsoft.containerservice/managedclusters', [`${resourceGroup}-aks`]],
    ['microsoft.keyvault/vaults', [`${resourceGroup}-kv`]],
    ['microsoft.storage/storageaccounts', [`${resourceGroup.replaceAll('-', '')}blob`.slice(0, 24)]],
    ['microsoft.dbforpostgresql/flexibleservers', [`${resourceGroup}-pg`]],
    ['microsoft.operationalinsights/workspaces', [`${resourceGroup}-law`]],
    ['microsoft.insights/components', [`${resourceGroup}-appi`]],
    ['microsoft.insights/privatelinkscopes', [`${resourceGroup}-ampls`]],
    ['microsoft.managedidentity/userassignedidentities', [`${resourceGroup}-id-foundation-probe`]],
    ['microsoft.network/privateendpoints', [`${resourceGroup}-kv-pe`, `${resourceGroup}-blob-pe`, `${resourceGroup}-ampls-pe`]],
    ['microsoft.network/privatednszones', [
      'privatelink.vaultcore.azure.net', 'privatelink.blob.core.windows.net',
      'privatelink.monitor.azure.com', 'privatelink.oms.opinsights.azure.com',
      'privatelink.ods.opinsights.azure.com', 'privatelink.agentsvc.azure-automation.net',
      'privatelink.postgres.database.azure.com',
    ]],
  ]);
  const resourceId = (type, name) => {
    const [provider, ...segments] = type.split('/');
    const names = name.split('/');
    return `${groupId}/providers/${provider}/${segments.map((segment, i) => `${segment}/${names[i]}`).join('/')}`;
  };
  const sameId = (left, right) => typeof left === 'string' && left.toLowerCase() === right.toLowerCase();
  const expectedIds = new Map();
  for (const [type, names] of expectedResources) {
    for (const name of names) expectedIds.set(resourceId(type, name).toLowerCase(), { type, name, tagged: true });
  }
  const child = (type, name) => expectedIds.set(resourceId(type, name).toLowerCase(), { type, name, tagged: false });
  const storageName = resourceGroup.replaceAll('-', '').concat('blob').slice(0, 24);
  for (const name of ['aks', 'postgres', 'private-endpoints']) {
    child('microsoft.network/virtualnetworks/subnets', `${resourceGroup}-vnet/${name}`);
  }
  for (const [zone, suffix] of [
    ['privatelink.vaultcore.azure.net', 'kv'], ['privatelink.blob.core.windows.net', 'blob'],
    ['privatelink.monitor.azure.com', 'monitor'], ['privatelink.oms.opinsights.azure.com', 'oms'],
    ['privatelink.ods.opinsights.azure.com', 'ods'], ['privatelink.agentsvc.azure-automation.net', 'agentsvc'],
    ['privatelink.postgres.database.azure.com', 'pg'],
  ]) child('microsoft.network/privatednszones/virtualnetworklinks', `${zone}/${resourceGroup}-${suffix}-dns-link`);
  const endpointIds = ['kv', 'blob', 'ampls'].map(suffix =>
    resourceId('Microsoft.Network/privateEndpoints', `${resourceGroup}-${suffix}-pe`));
  for (const suffix of ['kv', 'blob', 'ampls']) {
    child('microsoft.network/privateendpoints/privatednszonegroups', `${resourceGroup}-${suffix}-pe/default`);
  }
  for (const suffix of ['law', 'appi']) {
    child('microsoft.insights/privatelinkscopes/scopedresources', `${resourceGroup}-ampls/${resourceGroup}-${suffix}-scope`);
  }
  child('microsoft.managedidentity/userassignedidentities/federatedidentitycredentials',
    `${resourceGroup}-id-foundation-probe/foundation-probe-workload-identity`);
  child('microsoft.storage/storageaccounts/blobservices', `${storageName}/default`);
  child('microsoft.storage/storageaccounts/blobservices/containers', `${storageName}/default/platform-artifacts`);
  if (/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(config.postgresEntraAdminObjectId ?? '')) {
    child('microsoft.dbforpostgresql/flexibleservers/administrators', `${resourceGroup}-pg/${config.postgresEntraAdminObjectId}`);
  }
  for (const name of ['network', 'aks', 'keyvault', 'storage', 'monitor', 'postgres', 'identity']) {
    child('microsoft.resources/deployments', `${resourceGroup}-${name}`);
  }
  const identityId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', `${resourceGroup}-id-foundation-probe`);
  const aksId = resourceId('Microsoft.ContainerService/managedClusters', `${resourceGroup}-aks`);
  const subnetId = resourceId('Microsoft.Network/virtualNetworks/subnets', `${resourceGroup}-vnet/aks`);
  // Extension resources retain their exact parent scope and ARM-generated name.
  for (const [scope, principalResource, role] of [
    [resourceId('Microsoft.KeyVault/vaults', `${resourceGroup}-kv`), identityId, '4633458b-17de-408a-b874-0445c86b69e6'],
    [resourceId('Microsoft.KeyVault/vaults', `${resourceGroup}-kv`), aksId, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'],
    [resourceId('Microsoft.Storage/storageAccounts', storageName), identityId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'],
    [resourceId('Microsoft.OperationalInsights/workspaces', `${resourceGroup}-law`), identityId, '73c42c96-874c-492b-b04d-ab87d138a893'],
    [resourceId('Microsoft.Insights/components', `${resourceGroup}-appi`), identityId, '3913510d-42f4-4e42-8a64-420c390055eb'],
    [subnetId, aksId, '4d97b98b-1d4f-4787-a291-c67834d212e7'],
  ]) {
    const name = armGuid(scope, principalResource, role);
    expectedIds.set(`${scope}/providers/Microsoft.Authorization/roleAssignments/${name}`.toLowerCase(),
      { type: 'microsoft.authorization/roleassignments', name, tagged: false });
  }
  for (const [index, zoneId] of appRoutingDnsZoneResourceIds.entries()) {
    const zone = parseAppRoutingDnsZoneResourceId(zoneId);
    const zoneType = zone.zoneIsPrivate
      ? 'microsoft.network/privatednszones'
      : 'microsoft.network/dnszones';
    expectedIds.set(zone.resourceId.toLowerCase(), {
      type: zoneType, name: zone.zoneName, tagged: false,
    });
    const name = armGuid(zone.resourceId, aksId, zone.roleDefinitionId);
    expectedIds.set(`${zone.resourceId}/providers/Microsoft.Authorization/roleAssignments/${name}`.toLowerCase(),
      { type: 'microsoft.authorization/roleassignments', name, tagged: false });
    const deploymentName = `${resourceGroup}-app-routing-dns-${index}`;
    const zoneGroupId = `/subscriptions/${subscriptionId}/resourceGroups/${zone.resourceGroup}`;
    expectedIds.set(`${zoneGroupId}/providers/Microsoft.Resources/deployments/${deploymentName}`.toLowerCase(),
      { type: 'microsoft.resources/deployments', name: deploymentName, tagged: false });
  }
  const readResource = id => jsonResult(boundAz(['resource', 'show', '--ids', id, '-o', 'json'],
    { check: false }), 'Resource relationship lookup');
  const seen = new Set();
  for (const resource of resources) {
    if (typeof resource.id !== 'string' || !resource.id.toLowerCase().startsWith(`${groupId.toLowerCase()}/providers/`)) {
      throw new Error('Resource inventory contains a resource outside the exact dedicated group.');
    }
    const id = resource.id.toLowerCase();
    const type = resource.type?.toLowerCase();
    if (seen.has(id)) throw new Error('Resource inventory contains a duplicate ID.');
    seen.add(id);
    const expected = expectedIds.get(id);
    if (type === 'microsoft.network/networkinterfaces' &&
        sameId(resource.id, resourceId('Microsoft.Network/networkInterfaces', resource.name))) {
      const nic = readResource(resource.id);
      const endpointId = nic.properties?.privateEndpoint?.id;
      if (!sameId(nic.id, resource.id) || nic.type?.toLowerCase() !== type ||
          !endpointIds.some(approved => sameId(endpointId, approved)) ||
          !resources.some(item => sameId(item.id, endpointId) &&
            item.type?.toLowerCase() === 'microsoft.network/privateendpoints')) {
        throw new Error('NIC is not owned by an exact approved Private Endpoint.');
      }
      const endpoint = readResource(endpointId);
      const privateSubnet = resourceId('Microsoft.Network/virtualNetworks/subnets', `${resourceGroup}-vnet/private-endpoints`);
      if (!sameId(endpoint.id, endpointId) || endpoint.type?.toLowerCase() !== 'microsoft.network/privateendpoints' ||
          !sameId(endpoint.properties?.subnet?.id, privateSubnet) ||
          endpoint.properties?.networkInterfaces?.length !== 1 ||
          !sameId(endpoint.properties.networkInterfaces[0].id, nic.id) ||
          !Array.isArray(nic.properties?.ipConfigurations) || nic.properties.ipConfigurations.length === 0 ||
          !nic.properties.ipConfigurations.every(ip => sameId(ip.properties?.subnet?.id, privateSubnet))) {
        throw new Error('Private Endpoint NIC/subnet relationship is not the exact approved layout.');
      }
      continue;
    }
    // Historical top-level deployments have SHA-derived names, not ownership tags.
    if (!expected && type === 'microsoft.resources/deployments' &&
        typeof resource.name === 'string' && resource.name.startsWith(`${resourceGroup}-`) &&
        /^[0-9a-f]{12}$/.test(resource.name.slice(resourceGroup.length + 1)) &&
        sameId(resource.id, resourceId('Microsoft.Resources/deployments', resource.name))) {
      const deployment = jsonResult(boundAz(['deployment', 'group', 'show', '--resource-group', resourceGroup,
        '--name', resource.name, '-o', 'json'], { check: false }), 'Deployment inventory receipt');
      const outputs = deployment.properties?.outputs;
      if (!sameId(deployment.id, resource.id) || !/^[0-9a-f]{40}$/.test(outputs?.sourceSha?.value ?? '') ||
          !/^[0-9a-f]{40}$/.test(outputs?.sourceTree?.value ?? '') ||
          resource.name !== `${resourceGroup}-${outputs.sourceSha.value.slice(0, 12)}` ||
          !/^[0-9a-f]{64}$/.test(outputs?.sourceHash?.value ?? '')) {
        throw new Error('Deployment inventory entry has no matching source-bound receipt.');
      }
      continue;
    }
    if (!expected || type !== expected.type || resource.name?.toLowerCase() !== expected.name.toLowerCase()) {
      throw new Error('Resource inventory contains an unexpected type/name outside the reviewed P0 layout.');
    }
    if (expected.tagged) {
      assertDedicatedResourceGroupOwnership(resourceGroup, resource.tags);
      if (resource.tags['agentweaver:owner'] !== group.tags['agentweaver:owner'] ||
          resource.tags['agentweaver:cost-center'] !== group.tags['agentweaver:cost-center']) {
        throw new Error('Resource ownership differs from the dedicated resource group.');
      }
    }
  }
  for (const zoneId of appRoutingDnsZoneResourceIds) {
    const zone = parseAppRoutingDnsZoneResourceId(zoneId);
    if (zone.resourceGroup.toLowerCase() === resourceGroup.toLowerCase()) {
      if (!seen.has(zone.resourceId.toLowerCase())) {
        throw new Error('Custom App Routing DNS zone is missing from the dedicated resource inventory.');
      }
      continue;
    }
    const zoneResource = readResource(zone.resourceId);
    const expectedType = zone.zoneIsPrivate
      ? 'microsoft.network/privatednszones'
      : 'microsoft.network/dnszones';
    if (!sameId(zoneResource.id, zone.resourceId) || zoneResource.type?.toLowerCase() !== expectedType ||
        zoneResource.name?.toLowerCase() !== zone.zoneName.toLowerCase()) {
      throw new Error('Custom App Routing DNS zone is not the exact existing zone resource.');
    }
  }
  return { execAz: boundAz, group };
}
