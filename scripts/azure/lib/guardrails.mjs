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

const PRIVATE_DNS_ZONES = [
  { name: 'privatelink.vaultcore.azure.net', suffix: 'kv' },
  { name: 'privatelink.blob.core.windows.net', suffix: 'blob' },
  { name: 'privatelink.monitor.azure.com', suffix: 'monitor' },
  { name: 'privatelink.oms.opinsights.azure.com', suffix: 'oms' },
  { name: 'privatelink.ods.opinsights.azure.com', suffix: 'ods' },
  { name: 'privatelink.agentsvc.azure-automation.net', suffix: 'agentsvc' },
  { name: 'privatelink.postgres.database.azure.com', suffix: 'pg' },
];
const NRMS_NSG_NAME = 'NRMS-gxlttooqhupscaw-v1-p0-vnet';
const FAILURE_ANOMALIES_NAME = 'Failure Anomalies - aw-v1-p0-appi';
const POLICY_MANAGEMENT_GROUP =
  '/providers/Microsoft.Management/managementGroups/48fed3a1-0814-4847-88ce-b766155f2792';
const NRMS_POLICY_ASSIGNMENTS = new Map([
  [101, ['9d78e6174e6e69be', 'nrms-nsg-dine-sr101-v013']],
  [103, ['3c07197392ad62f', 'nrms-nsg-dine-sr103-v014']],
  [104, ['bac0fb65020410a4', 'nrms-nsg-dine-sr104-v013']],
  [105, ['91f42c0ca66ff7dd', 'nrms-nsg-dine-sr105-v013']],
  [106, ['9b8d76c443040b08', 'nrms-nsg-dine-sr106-v013']],
  [107, ['fb6de85c9e746cf1', 'nrms-nsg-dine-sr107-v013']],
  [108, ['532396f35af78946', 'nrms-nsg-dine-sr108-v013']],
  [109, ['e0bc08af3bd773ff', 'nrms-nsg-dine-sr109-v013']],
]);
const NRMS_RULE_SHAPES = [
  { name: 'NRMS-Rule-101', priority: 101, direction: 'Inbound', access: 'Allow', protocol: 'Tcp',
    sourceAddressPrefix: 'VirtualNetwork', destinationPortRange: '443', destinationPortRanges: [] },
  { name: 'NRMS-Rule-103', priority: 103, direction: 'Inbound', access: 'Allow', protocol: '*',
    sourceAddressPrefix: 'CorpNetPublic', destinationPortRange: '*', destinationPortRanges: [] },
  { name: 'NRMS-Rule-104', priority: 104, direction: 'Inbound', access: 'Allow', protocol: '*',
    sourceAddressPrefix: 'CorpNetSaw', destinationPortRange: '*', destinationPortRanges: [] },
  { name: 'NRMS-Rule-105', priority: 105, direction: 'Inbound', access: 'Deny', protocol: '*',
    sourceAddressPrefix: 'Internet', destinationPortRange: null,
    destinationPortRanges: ['1433', '1434', '3306', '4333', '5432', '6379', '7000', '7001', '7199',
      '9042', '9160', '9300', '16379', '26379', '27017'] },
  { name: 'NRMS-Rule-106', priority: 106, direction: 'Inbound', access: 'Deny', protocol: 'Tcp',
    sourceAddressPrefix: 'Internet', destinationPortRange: null, destinationPortRanges: ['22', '3389'] },
  { name: 'NRMS-Rule-107', priority: 107, direction: 'Inbound', access: 'Deny', protocol: 'Tcp',
    sourceAddressPrefix: 'Internet', destinationPortRange: null, destinationPortRanges: ['23', '135', '445', '5985', '5986'] },
  { name: 'NRMS-Rule-108', priority: 108, direction: 'Inbound', access: 'Deny', protocol: '*',
    sourceAddressPrefix: 'Internet', destinationPortRange: null,
    destinationPortRanges: ['13', '17', '19', '53', '69', '111', '123', '512', '514', '593', '873',
      '1900', '5353', '11211'] },
  { name: 'NRMS-Rule-109', priority: 109, direction: 'Inbound', access: 'Deny', protocol: '*',
    sourceAddressPrefix: 'Internet', destinationPortRange: null,
    destinationPortRanges: ['119', '137', '138', '139', '161', '162', '389', '636', '2049', '2301',
      '2381', '3268', '5800', '5900'] },
].map(rule => ({
  ...rule,
  sourcePortRange: '*',
  sourcePortRanges: [],
  sourceAddressPrefixes: [],
  destinationAddressPrefix: '*',
  destinationAddressPrefixes: [],
  sourceApplicationSecurityGroups: [],
  destinationApplicationSecurityGroups: [],
}));

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

function projectInventory(resources, expectedTags) {
  if (!Array.isArray(resources)) return { valid: false };
  return resources.map(resource => {
    const tags = resource?.tags;
    const validTags = tags === undefined || tags === null ||
      (typeof tags === 'object' && !Array.isArray(tags));
    const values = validTags && tags && typeof tags === 'object' ? tags : {};
    const keys = Object.keys(values);
    return {
      id: resource?.id,
      type: resource?.type,
      name: resource?.name,
      tagEvidence: {
        valid: validTags,
        count: keys.length,
        empty: keys.length === 0,
        environmentMatches: values['agentweaver:environment'] === expectedTags.environment,
        managedByMatches: values['agentweaver:managed-by'] === expectedTags.managedBy,
        ownerMatches: values['agentweaver:owner'] === expectedTags.owner,
        costCenterMatches: values['agentweaver:cost-center'] === expectedTags.costCenter,
        sourceShaMatches: values['agentweaver:sourceSha'] === expectedTags.sourceSha,
        exactExpected: keys.length === 5 &&
          values['agentweaver:environment'] === expectedTags.environment &&
          values['agentweaver:managed-by'] === expectedTags.managedBy &&
          values['agentweaver:owner'] === expectedTags.owner &&
          values['agentweaver:cost-center'] === expectedTags.costCenter &&
          values['agentweaver:sourceSha'] === expectedTags.sourceSha,
      },
    };
  });
}

function projectDeploymentReceipt(deployment) {
  const outputs = deployment?.properties?.outputs;
  return {
    id: deployment?.id,
    provisioningState: deployment?.properties?.provisioningState,
    sourceSha: outputs?.sourceSha?.value,
    sourceTree: outputs?.sourceTree?.value,
    sourceHash: outputs?.sourceHash?.value,
  };
}

function normalizeNsgRule(rule) {
  const properties = rule?.properties ?? {};
  const groupIds = value => value === undefined || value === null ? [] :
    Array.isArray(value) ? value.map(item => item?.id ?? null) : null;
  return {
    name: rule?.name,
    priority: properties.priority,
    direction: properties.direction,
    access: properties.access,
    protocol: properties.protocol,
    sourcePortRange: properties.sourcePortRange ?? null,
    sourcePortRanges: properties.sourcePortRanges ?? [],
    destinationPortRange: properties.destinationPortRange ?? null,
    destinationPortRanges: properties.destinationPortRanges ?? [],
    sourceAddressPrefix: properties.sourceAddressPrefix ?? null,
    sourceAddressPrefixes: properties.sourceAddressPrefixes ?? [],
    destinationAddressPrefix: properties.destinationAddressPrefix ?? null,
    destinationAddressPrefixes: properties.destinationAddressPrefixes ?? [],
    sourceApplicationSecurityGroups: groupIds(properties.sourceApplicationSecurityGroups),
    destinationApplicationSecurityGroups: groupIds(properties.destinationApplicationSecurityGroups),
  };
}

// Deployment outputs select configuration, never prove a workload ran.
export function readFoundationOutputs(outputs, { resourceGroup, subscriptionId, appRoutingDnsZoneResourceIds = [] }) {
  const zoneIds = validateAppRoutingDnsZoneResourceIds(appRoutingDnsZoneResourceIds, subscriptionId);
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
  const exactServiceIdentity = (candidate, name, serviceAccount) =>
    candidate?.name === name && candidate?.namespace === 'agentweaver-v1-p0' &&
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
      identity?.name !== 'foundation-probe' || identity?.namespace !== 'agentweaver-v1-p0' ||
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
  const expectedTags = {
    environment: 'v1-p0',
    managedBy: 'bicep',
    owner: group.tags['agentweaver:owner'],
    costCenter: group.tags['agentweaver:cost-center'],
    sourceSha: config.sha,
  };
  const resources = jsonResult(boundAz(['resource', 'list', '--resource-group', resourceGroup, '-o', 'json'],
    { check: false, projectJson: value => projectInventory(value, expectedTags) }), 'Resource inventory');
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
    ['microsoft.managedidentity/userassignedidentities', [
      `${resourceGroup}-id-foundation-probe`,
      `${resourceGroup}-id-identity-broker`,
      `${resourceGroup}-id-identity-broker-migration`,
    ]],
    ['microsoft.network/privateendpoints', [`${resourceGroup}-kv-pe`, `${resourceGroup}-blob-pe`, `${resourceGroup}-ampls-pe`]],
    ['microsoft.network/privatednszones', PRIVATE_DNS_ZONES.map(zone => zone.name)],
  ]);
  const resourceId = (type, name) => {
    const [provider, ...segments] = type.split('/');
    const names = name.split('/');
    return `${groupId}/providers/${provider}/${segments.map((segment, i) => `${segment}/${names[i]}`).join('/')}`;
  };
  const sameId = (left, right) => typeof left === 'string' && left.toLowerCase() === right.toLowerCase();
  const expectedIds = new Map();
  for (const [type, names] of expectedResources) {
    for (const name of names) expectedIds.set(resourceId(type, name).toLowerCase(), {
      type, name, tagged: true,
      ...(type === 'microsoft.network/privatednszones' ? { kind: 'private-dns-root' } : {}),
    });
  }
  const nrmsNsgId = resourceId('Microsoft.Network/networkSecurityGroups', NRMS_NSG_NAME);
  const failureAnomaliesId = resourceId('Microsoft.AlertsManagement/smartDetectorAlertRules', FAILURE_ANOMALIES_NAME);
  expectedIds.set(nrmsNsgId.toLowerCase(), {
    type: 'microsoft.network/networksecuritygroups', name: NRMS_NSG_NAME, tagged: false, kind: 'nrms-nsg',
  });
  expectedIds.set(failureAnomaliesId.toLowerCase(), {
    type: 'microsoft.alertsmanagement/smartdetectoralertrules',
    name: FAILURE_ANOMALIES_NAME, tagged: false, kind: 'failure-anomalies',
  });
  const child = (type, name) => expectedIds.set(resourceId(type, name).toLowerCase(), { type, name, tagged: false });
  const storageName = resourceGroup.replaceAll('-', '').concat('blob').slice(0, 24);
  for (const name of ['aks', 'postgres', 'private-endpoints']) {
    child('microsoft.network/virtualnetworks/subnets', `${resourceGroup}-vnet/${name}`);
  }
  for (const { name: zone, suffix } of PRIVATE_DNS_ZONES) {
    child('microsoft.network/privatednszones/virtualnetworklinks', `${zone}/${resourceGroup}-${suffix}-dns-link`);
  }
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
  child('microsoft.managedidentity/userassignedidentities/federatedidentitycredentials',
    `${resourceGroup}-id-identity-broker/identity-broker-workload-identity`);
  child('microsoft.managedidentity/userassignedidentities/federatedidentitycredentials',
    `${resourceGroup}-id-identity-broker-migration/identity-broker-migration-workload-identity`);
  child('microsoft.storage/storageaccounts/blobservices', `${storageName}/default`);
  child('microsoft.storage/storageaccounts/blobservices/containers', `${storageName}/default/platform-artifacts`);
  if (/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(config.postgresEntraAdminObjectId ?? '')) {
    child('microsoft.dbforpostgresql/flexibleservers/administrators', `${resourceGroup}-pg/${config.postgresEntraAdminObjectId}`);
  }
  for (const name of ['network', 'aks', 'keyvault', 'storage', 'monitor', 'postgres', 'identity']) {
    child('microsoft.resources/deployments', `${resourceGroup}-${name}`);
  }
  const identityId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', `${resourceGroup}-id-foundation-probe`);
  const identityBrokerRuntimeId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', `${resourceGroup}-id-identity-broker`);
  const aksId = resourceId('Microsoft.ContainerService/managedClusters', `${resourceGroup}-aks`);
  const subnetId = resourceId('Microsoft.Network/virtualNetworks/subnets', `${resourceGroup}-vnet/aks`);
  // Extension resources retain their exact parent scope and ARM-generated name.
  for (const [scope, principalResource, role] of [
    [resourceId('Microsoft.KeyVault/vaults', `${resourceGroup}-kv`), identityId, '4633458b-17de-408a-b874-0445c86b69e6'],
    [resourceId('Microsoft.KeyVault/vaults', `${resourceGroup}-kv`), identityBrokerRuntimeId, '4633458b-17de-408a-b874-0445c86b69e6'],
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
  const readResource = (id, projectJson) => jsonResult(boundAz(['resource', 'show', '--ids', id, '-o', 'json'],
    { check: false, ...(projectJson ? { projectJson } : {}) }), 'Resource relationship lookup');
  const deploymentCache = new Map();
  const readDeployment = name => {
    if (!deploymentCache.has(name)) {
      const deployment = jsonResult(boundAz(['deployment', 'group', 'show', '--resource-group', resourceGroup,
        '--name', name, '-o', 'json'], { check: false, projectJson: projectDeploymentReceipt }),
      'Deployment inventory receipt');
      deploymentCache.set(name, deployment);
    }
    return deploymentCache.get(name);
  };
  const seen = new Set();
  const inventoryById = new Map();
  for (const resource of resources) {
    if (typeof resource.id !== 'string' || !resource.id.toLowerCase().startsWith(`${groupId.toLowerCase()}/providers/`)) {
      throw new Error('Resource inventory contains a resource outside the exact dedicated group.');
    }
    const id = resource.id.toLowerCase();
    const type = resource.type?.toLowerCase();
    if (seen.has(id)) throw new Error('Resource inventory contains a duplicate ID.');
    seen.add(id);
    inventoryById.set(id, resource);
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
      const deployment = readDeployment(resource.name);
      if (!sameId(deployment.id, resource.id) || !/^[0-9a-f]{40}$/.test(deployment.sourceSha ?? '') ||
          !/^[0-9a-f]{40}$/.test(deployment.sourceTree ?? '') ||
          resource.name !== `${resourceGroup}-${deployment.sourceSha.slice(0, 12)}` ||
          !/^[0-9a-f]{64}$/.test(deployment.sourceHash ?? '')) {
        throw new Error('Deployment inventory entry has no matching source-bound receipt.');
      }
      continue;
    }
    if (!expected || type !== expected.type || resource.name?.toLowerCase() !== expected.name.toLowerCase()) {
      throw new Error('Resource inventory contains an unexpected type/name outside the reviewed P0 layout.');
    }
    if (expected.tagged) {
      const tags = resource.tagEvidence;
      if (!tags?.valid) {
        throw new Error('Resource ownership differs from the dedicated resource group.');
      }
      if (expected.kind === 'private-dns-root') {
        if (tags.count !== 0 && !tags.exactExpected) {
          throw new Error('Private DNS root has conflicting or incomplete ownership tags.');
        }
      } else if (!tags.environmentMatches || !tags.managedByMatches ||
          !tags.ownerMatches || !tags.costCenterMatches) {
        throw new Error('Resource ownership differs from the dedicated resource group.');
      }
    }
  }
  const privateDnsRootIds = PRIVATE_DNS_ZONES.map(({ name }) =>
    resourceId('Microsoft.Network/privateDnsZones', name));
  const privateDnsLinkIds = PRIVATE_DNS_ZONES.map(({ name, suffix }) =>
    resourceId('Microsoft.Network/privateDnsZones/virtualNetworkLinks', `${name}/${resourceGroup}-${suffix}-dns-link`));
  const vnetId = resourceId('Microsoft.Network/virtualNetworks', `${resourceGroup}-vnet`);
  const hasPrivateDnsInventory = privateDnsRootIds.some(id => seen.has(id.toLowerCase()));
  const validatePrivateDns = hasPrivateDnsInventory || config.requireCompleteP0Evidence === true;
  const inventoryEvidence = {};

  if (config.requireCompleteP0Evidence === true) {
    if (privateDnsRootIds.some(id => !seen.has(id.toLowerCase())) ||
        privateDnsLinkIds.some(id => !seen.has(id.toLowerCase())) ||
        !seen.has(nrmsNsgId.toLowerCase()) || !seen.has(failureAnomaliesId.toLowerCase())) {
      throw new Error('The exact P0 DNS, NRMS NSG, and Failure Anomalies resources are required for acceptance.');
    }
  }

  if (validatePrivateDns) {
    if (privateDnsRootIds.some(id => !seen.has(id.toLowerCase())) ||
        privateDnsLinkIds.some(id => !seen.has(id.toLowerCase()))) {
      throw new Error('The seven exact P0 private DNS roots and VNet links must all be present.');
    }
    const tagStates = privateDnsRootIds.map(id => inventoryById.get(id.toLowerCase())?.tagEvidence);
    const allTagless = tagStates.every(tags => tags?.valid && tags.count === 0 && tags.empty);
    const allTagged = tagStates.every(tags => tags?.valid && tags.exactExpected);
    if (!allTagless && !allTagged) {
      throw new Error('Private DNS root tags are partially missing or do not exactly match the source-bound target.');
    }

    for (const [index, linkId] of privateDnsLinkIds.entries()) {
      const link = readResource(linkId, value => ({
        id: value?.id,
        type: value?.type,
        name: value?.name,
        properties: {
          provisioningState: value?.properties?.provisioningState,
          registrationEnabled: value?.properties?.registrationEnabled,
          virtualNetworkId: value?.properties?.virtualNetwork?.id,
        },
      }));
      if (!sameId(link.id, linkId) || link.type?.toLowerCase() !==
          'microsoft.network/privatednszones/virtualnetworklinks' ||
          link.name?.toLowerCase() !==
            `${resourceGroup}-${PRIVATE_DNS_ZONES[index].suffix}-dns-link`.toLowerCase() ||
          link.properties?.provisioningState !== 'Succeeded' ||
          link.properties?.registrationEnabled !== false ||
          !sameId(link.properties?.virtualNetworkId, vnetId)) {
        throw new Error('Private DNS VNet link is not the exact successful, registration-disabled P0 link.');
      }
    }

    let sourceReceiptEvidence;
    if (allTagless) {
      const expectedSha = config.sha;
      const expectedTree = config.sourceTree;
      const expectedHash = config.sourceHash;
      const deploymentName = config.deploymentName ?? `${resourceGroup}-${expectedSha?.slice(0, 12)}`;
      if (!/^[0-9a-f]{40}$/.test(expectedSha ?? '') || !/^[0-9a-f]{40}$/.test(expectedTree ?? '') ||
          !/^[0-9a-f]{64}$/.test(expectedHash ?? '') ||
          deploymentName !== `${resourceGroup}-${expectedSha.slice(0, 12)}`) {
        throw new Error('Tagless private DNS roots require the exact SHA-derived source receipt.');
      }
      const deploymentId = resourceId('Microsoft.Resources/deployments', deploymentName);
      const deployment = readDeployment(deploymentName);
      if (!sameId(deployment.id, deploymentId) || deployment.provisioningState !== 'Succeeded' ||
          deployment.sourceSha !== expectedSha || deployment.sourceTree !== expectedTree ||
          deployment.sourceHash !== expectedHash) {
        throw new Error('Tagless private DNS roots lack a matching successful source-bound deployment receipt.');
      }
      const expectedZoneIds = new Set(privateDnsRootIds.map(id => id.toLowerCase()));
      const operations = jsonResult(boundAz(['deployment', 'operation', 'group', 'list',
        '--resource-group', resourceGroup, '--name', deploymentName, '-o', 'json'], {
        check: false,
        projectJson: value => Array.isArray(value) ? value.flatMap(operation => {
          const targetResourceId = operation?.properties?.targetResource?.id;
          if (typeof targetResourceId !== 'string' || !expectedZoneIds.has(targetResourceId.toLowerCase())) return [];
          return [{
            targetResourceId,
            provisioningOperation: operation?.properties?.provisioningOperation,
            provisioningState: operation?.properties?.provisioningState,
          }];
        }) : { valid: false },
      }), 'Private DNS deployment operations');
      if (!Array.isArray(operations)) throw new Error('Private DNS deployment operations are malformed.');
      for (const zoneId of privateDnsRootIds) {
        const matches = operations.filter(operation => sameId(operation.targetResourceId, zoneId));
        if (matches.length !== 1 || matches[0].provisioningOperation !== 'Create' ||
            matches[0].provisioningState !== 'Succeeded') {
          throw new Error('Each tagless private DNS root requires exactly one successful Create operation.');
        }
      }
      sourceReceiptEvidence = {
        deploymentId,
        sourceSha: expectedSha,
        sourceTree: expectedTree,
        sourceHash: expectedHash,
        successfulRootCreates: privateDnsRootIds.length,
      };
    }
    inventoryEvidence.privateDnsZones = {
      rootCount: privateDnsRootIds.length,
      tagsPersisted: allTagged,
      taglessRootCount: allTagless ? privateDnsRootIds.length : 0,
      vnetLinkCount: privateDnsLinkIds.length,
      registrationEnabled: false,
      vnetId,
      ...(sourceReceiptEvidence ? { sourceReceipt: sourceReceiptEvidence } : {}),
    };
  }

  const hasNsg = seen.has(nrmsNsgId.toLowerCase());
  const hasAlert = seen.has(failureAnomaliesId.toLowerCase());
  if (config.requireCompleteP0Evidence === true && (!hasNsg || !hasAlert)) {
    throw new Error('The exact P0 NRMS NSG and isolated Failure Anomalies rule are required for acceptance.');
  }
  if (hasNsg) {
    const nsg = readResource(nrmsNsgId, value => ({
      id: value?.id,
      type: value?.type,
      name: value?.name,
      properties: {
        securityRules: Array.isArray(value?.properties?.securityRules)
          ? value.properties.securityRules.map(normalizeNsgRule) : null,
        subnetIds: Array.isArray(value?.properties?.subnets)
          ? value.properties.subnets.map(subnet => subnet?.id ?? null) : null,
        networkInterfaceIds: Array.isArray(value?.properties?.networkInterfaces)
          ? value.properties.networkInterfaces.map(nic => nic?.id ?? null) : null,
      },
    }));
    if (!sameId(nsg.id, nrmsNsgId) || nsg.type?.toLowerCase() !== 'microsoft.network/networksecuritygroups' ||
        nsg.name !== NRMS_NSG_NAME || !Array.isArray(nsg.properties?.securityRules) ||
        nsg.properties.securityRules.length !== NRMS_RULE_SHAPES.length) {
      throw new Error('The exact inherited NRMS NSG inventory or rule count differs from the observed baseline.');
    }
    const actualRules = new Map(nsg.properties.securityRules.map(rule => [rule.name, rule]));
    const canonicalRule = rule => JSON.stringify(Object.fromEntries(
      Object.keys(rule ?? {}).sort().map(key => [key, rule[key]]),
    ));
    for (const expectedRule of NRMS_RULE_SHAPES) {
      if (canonicalRule(actualRules.get(expectedRule.name)) !== canonicalRule(expectedRule)) {
        throw new Error('An inherited NRMS NSG rule differs from its observed exact shape.');
      }
    }
    const expectedSubnetIds = ['aks', 'postgres', 'private-endpoints'].map(name =>
      resourceId('Microsoft.Network/virtualNetworks/subnets', `${resourceGroup}-vnet/${name}`));
    const actualSubnetIds = nsg.properties.subnetIds;
    if (!Array.isArray(actualSubnetIds) || actualSubnetIds.length !== expectedSubnetIds.length ||
        !expectedSubnetIds.every(id => actualSubnetIds.some(actual => sameId(actual, id)))) {
      throw new Error('The inherited NRMS NSG must attach only to the three exact P0 subnets.');
    }
    if (!Array.isArray(nsg.properties.networkInterfaceIds) || nsg.properties.networkInterfaceIds.length !== 0) {
      throw new Error('The inherited NRMS NSG must not have a network-interface association.');
    }
    for (const subnetId of expectedSubnetIds) {
      const subnet = readResource(subnetId, value => ({
        id: value?.id,
        type: value?.type,
        networkSecurityGroupId: value?.properties?.networkSecurityGroup?.id,
      }));
      if (!sameId(subnet.id, subnetId) || subnet.type?.toLowerCase() !== 'microsoft.network/virtualnetworks/subnets' ||
          !sameId(subnet.networkSecurityGroupId, nrmsNsgId)) {
        throw new Error('The NRMS NSG attachment is not reciprocal on each exact P0 subnet.');
      }
    }

    const policyStates = jsonResult(boundAz(['policy', 'state', 'list', '--resource', nrmsNsgId, '-o', 'json'], {
      check: false,
      projectJson: value => Array.isArray(value) ? value.map(state => ({
        resourceId: state?.resourceId,
        complianceState: state?.complianceState,
        policyDefinitionId: state?.policyDefinitionId,
        policyAssignmentId: state?.policyAssignmentId,
      })) : { valid: false },
    }), 'NRMS policy state');
    if (!Array.isArray(policyStates) || policyStates.length !== NRMS_POLICY_ASSIGNMENTS.size) {
      throw new Error('The inherited NRMS policy-state set is not the exact eight-rule baseline.');
    }
    const observedPolicyRules = new Set();
    for (const state of policyStates) {
      const ruleNumber = [...NRMS_POLICY_ASSIGNMENTS.entries()].find(([, [, assignmentName]]) =>
        sameId(state.policyAssignmentId,
          `${POLICY_MANAGEMENT_GROUP}/providers/Microsoft.Authorization/policyAssignments/${assignmentName}`))?.[0];
      const policy = ruleNumber === undefined ? undefined : NRMS_POLICY_ASSIGNMENTS.get(ruleNumber);
      const expectedDefinitionId = policy && `${POLICY_MANAGEMENT_GROUP}/providers/Microsoft.Authorization/policyDefinitions/${policy[0]}`;
      if (!sameId(state.resourceId, nrmsNsgId) || state.complianceState !== 'Compliant' ||
          !policy || !sameId(state.policyDefinitionId, expectedDefinitionId) || observedPolicyRules.has(ruleNumber)) {
        throw new Error('An NRMS policy state is not the exact compliant management-group assignment for its rule.');
      }
      observedPolicyRules.add(ruleNumber);
    }
    if (observedPolicyRules.size !== NRMS_POLICY_ASSIGNMENTS.size) {
      throw new Error('One or more exact NRMS rules lack a compliant policy state.');
    }

    for (const [ruleNumber] of NRMS_POLICY_ASSIGNMENTS) {
      const ruleId = `${nrmsNsgId}/securityRules/NRMS-Rule-${ruleNumber}`;
      const events = jsonResult(boundAz(['monitor', 'activity-log', 'list', '--resource-id', ruleId,
        '--offset', '7d', '-o', 'json'], {
        check: false,
        projectJson: value => Array.isArray(value) ? value.map(event => ({
          resourceId: event?.resourceId,
          operation: event?.operationName?.value,
          status: event?.status?.value,
        })) : { valid: false },
      }), 'NRMS security-rule Activity Log');
      const operation = 'Microsoft.Network/networkSecurityGroups/securityRules/write';
      if (!Array.isArray(events) || events.some(event => !sameId(event.resourceId, ruleId) ||
          event.operation !== operation) ||
          events.filter(event => event.status === 'Succeeded').length !== 1) {
        throw new Error('Each exact NRMS rule requires one successful child securityRules/write event.');
      }
    }
    inventoryEvidence.inheritedNetworkSecurityGroup = {
      resourceId: nrmsNsgId,
      ruleCount: NRMS_RULE_SHAPES.length,
      compliantPolicyStateCount: observedPolicyRules.size,
      successfulRuleWriteEventCount: NRMS_POLICY_ASSIGNMENTS.size,
      subnetAssociations: expectedSubnetIds.length,
      networkInterfaceAssociations: 0,
    };
  }

  if (hasAlert) {
    const alert = readResource(failureAnomaliesId, value => {
      const properties = value?.properties;
      return {
        id: value?.id,
        type: value?.type,
        name: value?.name,
        properties: {
          state: properties?.state,
          severity: properties?.severity,
          frequency: properties?.frequency,
          detectorId: properties?.detector?.id,
          detectorName: properties?.detector?.name,
          scopes: properties?.scope,
          throttling: properties?.throttling,
          actionGroupIds: properties?.actionGroups?.groupIds,
          customEmailSubject: properties?.actionGroups?.customEmailSubject,
          customWebhookPayload: properties?.actionGroups?.customWebhookPayload,
        },
      };
    });
    const alertProperties = alert.properties;
    const appInsightsId = resourceId('Microsoft.Insights/components', `${resourceGroup}-appi`);
    if (!sameId(alert.id, failureAnomaliesId) ||
        alert.type?.toLowerCase() !== 'microsoft.alertsmanagement/smartdetectoralertrules' ||
        alert.name !== FAILURE_ANOMALIES_NAME || alertProperties?.state !== 'Enabled' ||
        alertProperties?.severity !== 'Sev3' || alertProperties?.frequency !== 'PT1M' ||
        alertProperties?.detectorId !== 'FailureAnomaliesDetector' ||
        alertProperties?.detectorName !== 'Failure Anomalies' || alertProperties?.throttling !== null ||
        !Array.isArray(alertProperties?.scopes) || alertProperties.scopes.length !== 1 ||
        !sameId(alertProperties.scopes[0], appInsightsId) ||
        !Array.isArray(alertProperties?.actionGroupIds) || alertProperties.actionGroupIds.length !== 0 ||
        alertProperties.customEmailSubject !== null || alertProperties.customWebhookPayload !== null) {
      throw new Error('The owned Failure Anomalies rule is not the exact isolated P0 Smart Detector.');
    }
    inventoryEvidence.smartDetectorAlert = {
      resourceId: failureAnomaliesId,
      state: alertProperties.state,
      severity: alertProperties.severity,
      detectorId: alertProperties.detectorId,
      detectorName: alertProperties.detectorName,
      frequency: alertProperties.frequency,
      scopeId: appInsightsId,
      actionGroupCount: 0,
      customEmailSubject: null,
      customWebhookPayload: null,
    };
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
  return { execAz: boundAz, group, inventoryEvidence };
}
