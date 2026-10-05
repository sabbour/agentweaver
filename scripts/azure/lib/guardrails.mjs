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
export const AKS_RBAC_ADMIN_ROLE_ID = '3498e952-d568-435e-9b2c-8d77e338d7f7';
export const AKS_RBAC_CLUSTER_ADMIN_ROLE_ID = 'b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b';
export const AKS_NETWORK_CONTRIBUTOR_ROLE_ID = '4d97b98b-1d4f-4787-a291-c67834d212e7';
const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const ROLE_ASSIGNMENT_API_VERSION = '2022-04-01';
const AKS_WORKLOAD_IDENTITIES = [
  ['foundation-probe', 'foundation-probe-workload-identity'],
  ['identity-broker', 'identity-broker-workload-identity'],
  ['identity-broker-migration', 'identity-broker-migration-workload-identity'],
];

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
  const keys = ['agentweaver:environment', 'agentweaver:managed-by', 'agentweaver:owner', 'agentweaver:cost-center'];
  const expected = [
    expectedTags.environment, expectedTags.managedBy, expectedTags.owner, expectedTags.costCenter,
  ];
  return resources.map(resource => {
    const tags = resource?.tags;
    const validTags = tags === undefined || tags === null ||
      (typeof tags === 'object' && !Array.isArray(tags));
    const values = validTags && tags && typeof tags === 'object' ? tags : {};
    const ownershipMatches = validTags && keys.every((key, index) =>
      values[key] === undefined || values[key] === expected[index]);
    const ownershipComplete = validTags && keys.every((key, index) => values[key] === expected[index]);
    return {
      id: resource?.id,
      type: resource?.type,
      name: resource?.name,
      tagEvidence: {
        valid: validTags,
        ownershipMatches,
        ownershipComplete,
      },
    };
  });
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
export function armGuid(...values) {
  const namespace = Buffer.from('11fb06fb712d4ddd98c7e71bbd588830', 'hex');
  const bytes = createHash('sha1').update(namespace).update(values.join('-')).digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function resolveClusterAdminRoleAssignmentName({ resourceGroup, subscriptionId, operatorObjectId }, execAz) {
  if (!GUID_PATTERN.test(operatorObjectId ?? '')) throw new Error('Kubernetes operator object ID is invalid.');
  const clusterId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${resourceGroup}-aks`;
  const roleDefinitionId =
    `/subscriptions/${subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/${AKS_RBAC_CLUSTER_ADMIN_ROLE_ID}`;
  const result = execAz(['role', 'assignment', 'list', '--scope', clusterId, '-o', 'json'], { check: false });
  if (result.status !== 0) throw new Error(`AKS operator role lookup failed: ${result.stderr}`);
  let assignments;
  try {
    assignments = JSON.parse(result.stdout);
  } catch {
    throw new Error('AKS operator role lookup returned malformed JSON.');
  }
  if (!Array.isArray(assignments)) throw new Error('AKS operator role lookup did not return a list.');
  const matches = assignments.filter(assignment =>
    assignment?.principalId?.toLowerCase() === operatorObjectId.toLowerCase() &&
    assignment?.roleDefinitionId?.toLowerCase() === roleDefinitionId.toLowerCase());
  if (matches.length > 1) throw new Error('Duplicate AKS Cluster Admin assignments exist for the selected operator.');
  if (matches.length === 0) return armGuid(clusterId, operatorObjectId, AKS_RBAC_CLUSTER_ADMIN_ROLE_ID);
  const [assignment] = matches;
  if (assignment.principalType !== 'User' ||
      assignment.scope?.toLowerCase() !== clusterId.toLowerCase() ||
      !GUID_PATTERN.test(assignment.name ?? '') ||
      assignment.id?.toLowerCase() !==
        `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${assignment.name}`.toLowerCase()) {
    throw new Error('The existing AKS Cluster Admin assignment is not the exact operator-scoped assignment.');
  }
  return assignment.name;
}

function readRoleAssignment(assignmentId, execAz) {
  return execAz(['resource', 'show', '--ids', assignmentId, '--api-version', ROLE_ASSIGNMENT_API_VERSION, '-o', 'json'],
    { check: false });
}

export function verifyClusterAdminRoleAssignment({ resourceGroup, subscriptionId, operatorObjectId }, assignmentName, execAz) {
  if (!GUID_PATTERN.test(assignmentName ?? '') || !GUID_PATTERN.test(operatorObjectId ?? '')) {
    throw new Error('AKS Cluster Admin assignment name or operator object ID is invalid.');
  }
  const clusterId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${resourceGroup}-aks`;
  const assignmentId = `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${assignmentName}`;
  const roleDefinitionId =
    `/subscriptions/${subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/${AKS_RBAC_CLUSTER_ADMIN_ROLE_ID}`;
  const result = readRoleAssignment(assignmentId, execAz);
  if (result.status !== 0) throw new Error(`AKS Cluster Admin assignment verification failed: ${result.stderr}`);
  let assignment;
  try {
    assignment = JSON.parse(result.stdout);
  } catch {
    throw new Error('AKS Cluster Admin assignment verification returned malformed JSON.');
  }
  if (assignment?.id?.toLowerCase() !== assignmentId.toLowerCase() ||
      assignment?.name?.toLowerCase() !== assignmentName.toLowerCase() ||
      assignment?.properties?.scope?.toLowerCase() !== clusterId.toLowerCase() ||
      assignment?.properties?.principalId?.toLowerCase() !== operatorObjectId.toLowerCase() ||
      assignment?.properties?.principalType !== 'User' ||
      assignment?.properties?.roleDefinitionId?.toLowerCase() !== roleDefinitionId.toLowerCase()) {
    throw new Error('AKS Cluster Admin assignment does not match the exact approved user, built-in role and cluster scope.');
  }
  return assignmentId;
}

export function findDanglingAksSubnetRoleAssignment({ resourceGroup, subscriptionId }, execAz) {
  const clusterId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${resourceGroup}-aks`;
  const subnetId = `/subscriptions/${subscriptionId}/resourceGroups/${resourceGroup}/providers/Microsoft.Network/virtualNetworks/${resourceGroup}-vnet/subnets/aks`;
  const assignmentName = armGuid(subnetId, clusterId, AKS_NETWORK_CONTRIBUTOR_ROLE_ID);
  const assignmentId = `${subnetId}/providers/Microsoft.Authorization/roleAssignments/${assignmentName}`;
  const assignmentResult = readRoleAssignment(assignmentId, execAz);
  if (assignmentResult.status !== 0) {
    if (/\((?:RoleAssignmentNotFound|ResourceNotFound)\)|\b404\b/i.test(assignmentResult.stderr)) return undefined;
    throw new Error(`Exact AKS subnet role lookup failed: ${assignmentResult.stderr}`);
  }
  let assignment;
  try {
    assignment = JSON.parse(assignmentResult.stdout);
  } catch {
    throw new Error('Exact AKS subnet role lookup returned malformed JSON.');
  }
  const expectedRole =
    `/subscriptions/${subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/${AKS_NETWORK_CONTRIBUTOR_ROLE_ID}`;
  const properties = assignment?.properties;
  if (assignment?.id?.toLowerCase() !== assignmentId.toLowerCase() ||
      assignment?.name?.toLowerCase() !== assignmentName.toLowerCase() ||
      properties?.scope?.toLowerCase() !== subnetId.toLowerCase() ||
      properties?.roleDefinitionId?.toLowerCase() !== expectedRole.toLowerCase() ||
      properties?.principalType !== 'ServicePrincipal' ||
      !GUID_PATTERN.test(properties?.principalId ?? '')) {
    throw new Error('The exact AKS subnet assignment does not match the reviewed Network Contributor binding.');
  }
  const clusterResult = execAz(['aks', 'show', '--resource-group', resourceGroup, '--name', `${resourceGroup}-aks`, '-o', 'json'],
    { check: false });
  let clusterPrincipalId;
  if (clusterResult.status === 0) {
    let cluster;
    try {
      cluster = JSON.parse(clusterResult.stdout);
    } catch {
      throw new Error('AKS lookup returned malformed JSON while checking the subnet role.');
    }
    if (cluster?.id?.toLowerCase() !== clusterId.toLowerCase() ||
        !GUID_PATTERN.test(cluster?.identity?.principalId ?? '')) {
      throw new Error('AKS identity does not match the exact target while checking the subnet role.');
    }
    clusterPrincipalId = cluster.identity.principalId;
    if (clusterPrincipalId.toLowerCase() === properties.principalId.toLowerCase()) return undefined;
  } else if (!/\(ResourceNotFound\)/i.test(clusterResult.stderr)) {
    throw new Error(`AKS lookup failed while checking the subnet role: ${clusterResult.stderr}`);
  }
  const principalResult = execAz(['ad', 'sp', 'show', '--id', properties.principalId, '-o', 'json'], { check: false });
  if (principalResult.status === 0) {
    let principal;
    try {
      principal = JSON.parse(principalResult.stdout);
    } catch {
      throw new Error('The AKS subnet role principal lookup returned malformed JSON.');
    }
    if (principal?.id?.toLowerCase() !== properties.principalId.toLowerCase()) {
      throw new Error('The AKS subnet role principal lookup returned a different service principal.');
    }
    throw new Error(clusterPrincipalId
      ? 'The prior AKS subnet role principal still exists; refusing to replace its assignment automatically.'
      : 'The cluster is absent but its subnet role principal still exists; refusing to remove the assignment automatically.');
  }
  const notFound = new RegExp(`Resource ['"]?${properties.principalId}['"]? does not exist`, 'i');
  if (!notFound.test(principalResult.stderr)) {
    throw new Error(`The AKS subnet role principal could not be verified as deleted: ${principalResult.stderr}`);
  }
  return assignmentId;
}

function assertExistingAksFederatedCredentials({ resourceGroup }, execAz) {
  for (const [service, credentialName] of AKS_WORKLOAD_IDENTITIES) {
    const result = execAz(['identity', 'federated-credential', 'show', '--name', credentialName,
      '--identity-name', `${resourceGroup}-id-${service}`, '--resource-group', resourceGroup, '-o', 'json'],
    { check: false });
    if (result.status !== 0) {
      throw new Error(`Existing AKS workload identity credential lookup failed for ${credentialName}: ${result.stderr}`);
    }
    let credential;
    try {
      credential = JSON.parse(result.stdout);
    } catch {
      throw new Error(`Existing AKS workload identity credential ${credentialName} returned malformed JSON.`);
    }
    const subject = `system:serviceaccount:agentweaver-v1-p0:${service}`;
    if (typeof credential?.issuer !== 'string' || !credential.issuer.startsWith('https://') ||
        credential.subject !== subject || !Array.isArray(credential.audiences) ||
        credential.audiences.length !== 1 || credential.audiences[0] !== 'api://AzureADTokenExchange') {
      throw new Error(`Existing AKS workload identity credential ${credentialName} has unexpected bindings.`);
    }
  }
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
    type: 'microsoft.network/networksecuritygroups', name: NRMS_NSG_NAME, tagged: false,
  });
  expectedIds.set(failureAnomaliesId.toLowerCase(), {
    type: 'microsoft.alertsmanagement/smartdetectoralertrules',
    name: FAILURE_ANOMALIES_NAME, tagged: false,
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
  for (const name of ['network', 'aks', 'keyvault', 'storage', 'monitor', 'postgres', 'identity',
    'workload-identity-federation']) {
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
  const seen = new Set();
  const operatorClusterRoleAssignments = new Set();
  const operatorRoleAssignmentPrefix = `${aksId}/providers/Microsoft.Authorization/roleAssignments/`.toLowerCase();
  let observedPostgresAdministrator;
  for (const resource of resources) {
    if (typeof resource.id !== 'string' || !resource.id.toLowerCase().startsWith(`${groupId.toLowerCase()}/providers/`)) {
      throw new Error('Resource inventory contains a resource outside the exact dedicated group.');
    }
    const id = resource.id.toLowerCase();
    const type = resource.type?.toLowerCase();
    if (seen.has(id)) throw new Error('Resource inventory contains a duplicate ID.');
    seen.add(id);
    const expected = expectedIds.get(id);
    if (type === 'microsoft.authorization/roleassignments' &&
        resource.id.toLowerCase().startsWith(operatorRoleAssignmentPrefix)) {
      const role = readResource(resource.id);
      const roleId = role.properties?.roleDefinitionId?.split('/').at(-1)?.toLowerCase();
      const allowedRoles = new Set([AKS_RBAC_ADMIN_ROLE_ID, AKS_RBAC_CLUSTER_ADMIN_ROLE_ID]);
      const semanticKey = `${role.properties?.principalId?.toLowerCase()}:${roleId}`;
      const expectedId = `${aksId}/providers/Microsoft.Authorization/roleAssignments/${resource.name}`;
      if (!GUID_PATTERN.test(config.operatorObjectId ?? '') ||
          !sameId(role.id, resource.id) || role.type?.toLowerCase() !== type ||
          role.name?.toLowerCase() !== resource.name?.toLowerCase() ||
          !GUID_PATTERN.test(resource.name ?? '') || !sameId(resource.id, expectedId) ||
          !sameId(role.properties?.scope, aksId) ||
          !sameId(role.properties?.principalId, config.operatorObjectId) ||
          role.properties?.principalType !== 'User' || !allowedRoles.has(roleId)) {
        throw new Error('AKS cluster role assignment is outside the exact approved operator and built-in role scopes.');
      }
      if (operatorClusterRoleAssignments.has(semanticKey)) {
        throw new Error('Duplicate permanent AKS operator role assignments exist for the same role.');
      }
      operatorClusterRoleAssignments.add(semanticKey);
      continue;
    }
    if (!expected && config.scope === 'aks-only' &&
        type === 'microsoft.dbforpostgresql/flexibleservers/administrators') {
      const parentId = `${resourceId('Microsoft.DBforPostgreSQL/flexibleServers', `${resourceGroup}-pg`)}/administrators/`;
      const principalObjectId = resource.id.slice(parentId.length);
      if (observedPostgresAdministrator || !GUID_PATTERN.test(principalObjectId) ||
          !sameId(resource.id, `${parentId}${principalObjectId}`) ||
          resource.name?.toLowerCase() !== `${resourceGroup}-pg/${principalObjectId}`.toLowerCase()) {
        throw new Error('Existing PostgreSQL administrator is not the single exact P0 server binding.');
      }
      observedPostgresAdministrator = principalObjectId;
      continue;
    }
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
    // Top-level deployment records are control-plane history, not deployed resources.
    if (!expected && type === 'microsoft.resources/deployments' &&
        typeof resource.name === 'string' && resource.name.startsWith(`${resourceGroup}-`) &&
        /^(?:[0-9a-f]{12}|aks-[0-9a-f]{12})$/.test(resource.name.slice(resourceGroup.length + 1)) &&
        sameId(resource.id, resourceId('Microsoft.Resources/deployments', resource.name))) {
      continue;
    }
    if (!expected || type !== expected.type || resource.name?.toLowerCase() !== expected.name.toLowerCase()) {
      throw new Error('Resource inventory contains an unexpected type/name outside the reviewed P0 layout.');
    }
    if (expected.tagged) {
      const tags = resource.tagEvidence;
      if (expected.kind === 'private-dns-root') {
        if (!tags?.valid || !tags.ownershipMatches) {
          throw new Error('Private DNS root ownership conflicts with the dedicated resource group.');
        }
      } else if (!tags?.valid || !tags.ownershipComplete) {
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
  const validatePrivateDns = hasPrivateDnsInventory;
  const inventoryEvidence = {};

  if (validatePrivateDns) {
    if (privateDnsLinkIds.some(id => !seen.has(id.toLowerCase()))) {
      throw new Error('Existing P0 Private DNS roots are missing expected VNet links.');
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
    inventoryEvidence.privateDnsZones = {
      rootCount: privateDnsRootIds.filter(id => seen.has(id.toLowerCase())).length,
      vnetLinkCount: privateDnsLinkIds.filter(id => seen.has(id.toLowerCase())).length,
      vnetId,
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
  if (config.scope === 'aks-only') assertExistingAksFederatedCredentials({ resourceGroup, subscriptionId }, boundAz);
  return { execAz: boundAz, group, inventoryEvidence, resources };
}
