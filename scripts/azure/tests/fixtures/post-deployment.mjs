import { fixture, source, tags, deploymentOutputs } from './target.mjs';
import {
  nrmsNsgName, nrmsPolicyAssignments, nrmsRules, policyManagementGroupId, smartDetectorEvidence,
} from './owned-p0-evidence.mjs';

// GenericResource inventory shape: roots have tags, children and generated NICs do not.
const prefix = `${fixture.groupId}/providers/`;
const nsgId = `${prefix}Microsoft.Network/networkSecurityGroups/${nrmsNsgName}`;
const vnetId = `${prefix}Microsoft.Network/virtualNetworks/agentweaver-vnet`;
const item = (path, type, name, tagged = false) => ({
  id: prefix + path, type, name, tags: tagged ? { ...tags } : null,
});
export const postDeploymentInventory = [
  item('Microsoft.Network/virtualNetworks/agentweaver-vnet', 'Microsoft.Network/virtualNetworks', 'agentweaver-vnet', true),
  item('Microsoft.ContainerService/managedClusters/agentweaver-aks', 'Microsoft.ContainerService/managedClusters', 'agentweaver-aks', true),
  item('Microsoft.KeyVault/vaults/agentweaver-kv', 'Microsoft.KeyVault/vaults', 'agentweaver-kv', true),
  item('Microsoft.Storage/storageAccounts/agentweaverblob', 'Microsoft.Storage/storageAccounts', 'agentweaverblob', true),
  item('Microsoft.DBforPostgreSQL/flexibleServers/agentweaver-pg', 'Microsoft.DBforPostgreSQL/flexibleServers', 'agentweaver-pg', true),
  item('Microsoft.OperationalInsights/workspaces/agentweaver-law', 'Microsoft.OperationalInsights/workspaces', 'agentweaver-law', true),
  item('Microsoft.Insights/components/agentweaver-appi', 'Microsoft.Insights/components', 'agentweaver-appi', true),
  item('Microsoft.Insights/privateLinkScopes/agentweaver-ampls', 'Microsoft.Insights/privateLinkScopes', 'agentweaver-ampls', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-foundation-probe',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'agentweaver-id-foundation-probe', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-identity-broker',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'agentweaver-id-identity-broker', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-identity-broker-migration',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'agentweaver-id-identity-broker-migration', true),
  ...['aks', 'postgres', 'private-endpoints'].map(name => item(
    `Microsoft.Network/virtualNetworks/agentweaver-vnet/subnets/${name}`,
    'Microsoft.Network/virtualNetworks/subnets', `agentweaver-vnet/${name}`)),
  ...[
    ['privatelink.vaultcore.azure.net', 'kv'], ['privatelink.blob.core.windows.net', 'blob'],
    ['privatelink.monitor.azure.com', 'monitor'], ['privatelink.oms.opinsights.azure.com', 'oms'],
    ['privatelink.ods.opinsights.azure.com', 'ods'], ['privatelink.agentsvc.azure-automation.net', 'agentsvc'],
    ['privatelink.postgres.database.azure.com', 'pg'],
  ].flatMap(([zone, suffix]) => [
    item(`Microsoft.Network/privateDnsZones/${zone}`, 'Microsoft.Network/privateDnsZones', zone),
    item(`Microsoft.Network/privateDnsZones/${zone}/virtualNetworkLinks/agentweaver-${suffix}-dns-link`,
      'Microsoft.Network/privateDnsZones/virtualNetworkLinks', `${zone}/agentweaver-${suffix}-dns-link`),
  ]),
  item(`Microsoft.Network/networkSecurityGroups/${nrmsNsgName}`,
    'Microsoft.Network/networkSecurityGroups', nrmsNsgName),
  item(`Microsoft.AlertsManagement/smartDetectorAlertRules/${smartDetectorEvidence.name}`,
    smartDetectorEvidence.type, smartDetectorEvidence.name),
  ...['kv', 'blob', 'ampls'].flatMap(suffix => [
    item(`Microsoft.Network/privateEndpoints/agentweaver-${suffix}-pe`,
      'Microsoft.Network/privateEndpoints', `agentweaver-${suffix}-pe`, true),
    item(`Microsoft.Network/privateEndpoints/agentweaver-${suffix}-pe/privateDnsZoneGroups/default`,
      'Microsoft.Network/privateEndpoints/privateDnsZoneGroups', `agentweaver-${suffix}-pe/default`),
    item(`Microsoft.Network/networkInterfaces/agentweaver-${suffix}-pe.nic-generated`,
      'Microsoft.Network/networkInterfaces', `agentweaver-${suffix}-pe.nic-generated`),
  ]),
  ...['law', 'appi'].map(suffix => item(
    `Microsoft.Insights/privateLinkScopes/agentweaver-ampls/scopedResources/agentweaver-${suffix}-scope`,
    'Microsoft.Insights/privateLinkScopes/scopedResources', `agentweaver-ampls/agentweaver-${suffix}-scope`)),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-foundation-probe/federatedIdentityCredentials/foundation-probe-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'agentweaver-id-foundation-probe/foundation-probe-workload-identity'),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-identity-broker/federatedIdentityCredentials/identity-broker-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'agentweaver-id-identity-broker/identity-broker-workload-identity'),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/agentweaver-id-identity-broker-migration/federatedIdentityCredentials/identity-broker-migration-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'agentweaver-id-identity-broker-migration/identity-broker-migration-workload-identity'),
  item(`Microsoft.DBforPostgreSQL/flexibleServers/agentweaver-pg/administrators/${source.postgresEntraAdminObjectId}`,
    'Microsoft.DBforPostgreSQL/flexibleServers/administrators', `agentweaver-pg/${source.postgresEntraAdminObjectId}`),
  item('Microsoft.Storage/storageAccounts/agentweaverblob/blobServices/default',
    'Microsoft.Storage/storageAccounts/blobServices', 'agentweaverblob/default'),
  item('Microsoft.Storage/storageAccounts/agentweaverblob/blobServices/default/containers/platform-artifacts',
    'Microsoft.Storage/storageAccounts/blobServices/containers', 'agentweaverblob/default/platform-artifacts'),
  ...['network', 'aks', 'keyvault', 'storage', 'monitor', 'postgres', 'identity', source.sha.slice(0, 12)].map(name =>
    item(`Microsoft.Resources/deployments/agentweaver-${name}`, 'Microsoft.Resources/deployments', `agentweaver-${name}`)),
  ...[
    ['Microsoft.KeyVault/vaults/agentweaver-kv', 'cf9a2ab8-b3f4-5c0a-832d-5a05eeef3a33'],
    ['Microsoft.KeyVault/vaults/agentweaver-kv', 'ac4f4dde-7b16-5392-b3ce-f8a1e7a03fd8'],
    ['Microsoft.Storage/storageAccounts/agentweaverblob', '0133d08c-b0aa-5320-8b14-1ce6fc6bd00f'],
    ['Microsoft.Network/virtualNetworks/agentweaver-vnet/subnets/aks', 'a6a3ecb9-58a4-59db-8c8a-1248d94475b7'],
    ['Microsoft.OperationalInsights/workspaces/agentweaver-law', 'ee7d863d-35f1-55fa-8da3-ffa3d7660e2e'],
    ['Microsoft.Insights/components/agentweaver-appi', '0fd5a58a-b5b1-5374-8317-2a4484f8b81d'],
  ].map(([scope, name]) => item(`${scope}/providers/Microsoft.Authorization/roleAssignments/${name}`,
    'Microsoft.Authorization/roleAssignments', name)),
];

export const postDeploymentDetails = {};
const subnetId = `${prefix}Microsoft.Network/virtualNetworks/agentweaver-vnet/subnets/private-endpoints`;
for (const suffix of ['kv', 'blob', 'ampls']) {
  const endpointId = `${prefix}Microsoft.Network/privateEndpoints/agentweaver-${suffix}-pe`;
  const nicId = `${prefix}Microsoft.Network/networkInterfaces/agentweaver-${suffix}-pe.nic-generated`;
  postDeploymentDetails[endpointId] = { id: endpointId, type: 'Microsoft.Network/privateEndpoints',
    properties: { subnet: { id: subnetId }, networkInterfaces: [{ id: nicId }] } };
  postDeploymentDetails[nicId] = { id: nicId, type: 'Microsoft.Network/networkInterfaces',
    properties: { privateEndpoint: { id: endpointId },
      ipConfigurations: [{ properties: { subnet: { id: subnetId } } }] } };
}

for (const name of ['aks', 'postgres', 'private-endpoints']) {
  const id = `${prefix}Microsoft.Network/virtualNetworks/agentweaver-vnet/subnets/${name}`;
  postDeploymentDetails[id] = {
    id,
    type: 'Microsoft.Network/virtualNetworks/subnets',
    properties: { networkSecurityGroup: { id: nsgId } },
  };
}

for (const [zone, suffix] of [
  ['privatelink.vaultcore.azure.net', 'kv'], ['privatelink.blob.core.windows.net', 'blob'],
  ['privatelink.monitor.azure.com', 'monitor'], ['privatelink.oms.opinsights.azure.com', 'oms'],
  ['privatelink.ods.opinsights.azure.com', 'ods'], ['privatelink.agentsvc.azure-automation.net', 'agentsvc'],
  ['privatelink.postgres.database.azure.com', 'pg'],
]) {
  const id = `${prefix}Microsoft.Network/privateDnsZones/${zone}/virtualNetworkLinks/agentweaver-${suffix}-dns-link`;
  postDeploymentDetails[id] = {
    id,
    type: 'Microsoft.Network/privateDnsZones/virtualNetworkLinks',
    name: `agentweaver-${suffix}-dns-link`,
    properties: { provisioningState: 'Succeeded', registrationEnabled: false, virtualNetwork: { id: vnetId } },
  };
}

postDeploymentDetails[nsgId] = {
  id: nsgId,
  type: 'Microsoft.Network/networkSecurityGroups',
  name: nrmsNsgName,
  properties: {
    securityRules: nrmsRules,
    subnets: ['aks', 'postgres', 'private-endpoints'].map(name => ({
      id: `${prefix}Microsoft.Network/virtualNetworks/agentweaver-vnet/subnets/${name}`,
    })),
    networkInterfaces: [],
  },
};

const appInsightsId = `${prefix}Microsoft.Insights/components/agentweaver-appi`;
const alertId = `${prefix}${smartDetectorEvidence.type}/${smartDetectorEvidence.name}`;
postDeploymentDetails[alertId] = {
  id: alertId,
  type: smartDetectorEvidence.type,
  name: smartDetectorEvidence.name,
  properties: {
    state: smartDetectorEvidence.state,
    severity: smartDetectorEvidence.severity,
    frequency: smartDetectorEvidence.frequency,
    detector: { id: smartDetectorEvidence.detectorId, name: smartDetectorEvidence.detectorName },
    scope: [appInsightsId],
    throttling: smartDetectorEvidence.throttling,
    actionGroups: {
      groupIds: [],
      customEmailSubject: smartDetectorEvidence.customEmailSubject,
      customWebhookPayload: smartDetectorEvidence.customWebhookPayload,
    },
  },
};

export const postDeploymentPolicyStates = nrmsPolicyAssignments.map(([, definitionTail, assignmentName]) => ({
  resourceId: nsgId,
  complianceState: 'Compliant',
  policyDefinitionId: `${policyManagementGroupId}/providers/Microsoft.Authorization/policyDefinitions/${definitionTail}`,
  policyAssignmentId: `${policyManagementGroupId}/providers/Microsoft.Authorization/policyAssignments/${assignmentName}`,
}));

export const postDeploymentActivityEvents = Object.fromEntries(nrmsPolicyAssignments.map(([ruleNumber]) => {
  const resourceId = `${nsgId}/securityRules/NRMS-Rule-${ruleNumber}`;
  return [resourceId, ['Started', 'Accepted', 'Succeeded'].map(status => ({
    resourceId,
    operationName: { value: 'Microsoft.Network/networkSecurityGroups/securityRules/write' },
    status: { value: status },
  }))];
}));

export const postDeploymentOperations = [
  'privatelink.vaultcore.azure.net', 'privatelink.blob.core.windows.net',
  'privatelink.monitor.azure.com', 'privatelink.oms.opinsights.azure.com',
  'privatelink.ods.opinsights.azure.com', 'privatelink.agentsvc.azure-automation.net',
  'privatelink.postgres.database.azure.com',
].map(name => ({
  properties: {
    targetResource: { id: `${prefix}Microsoft.Network/privateDnsZones/${name}` },
    provisioningOperation: 'Create',
    provisioningState: 'Succeeded',
  },
}));

export const postDeploymentAzure = {
  resources: postDeploymentInventory,
  details: postDeploymentDetails,
  policyStates: postDeploymentPolicyStates,
  activityEvents: postDeploymentActivityEvents,
  deploymentOperations: postDeploymentOperations,
  create: { status: 0, stderr: '', stdout: JSON.stringify({
    id: `${prefix}Microsoft.Resources/deployments/agentweaver-${source.sha.slice(0, 12)}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs },
  }) },
};
