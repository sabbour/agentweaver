import { fixture, source, tags, deploymentOutputs } from './target.mjs';
import {
  nrmsNsgName, nrmsPolicyAssignments, nrmsRules, policyManagementGroupId, smartDetectorEvidence,
} from './owned-p0-evidence.mjs';

// GenericResource inventory shape: roots have tags, children and generated NICs do not.
const prefix = `${fixture.groupId}/providers/`;
const nsgId = `${prefix}Microsoft.Network/networkSecurityGroups/${nrmsNsgName}`;
const vnetId = `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet`;
const item = (path, type, name, tagged = false) => ({
  id: prefix + path, type, name, tags: tagged ? { ...tags } : null,
});
export const postDeploymentInventory = [
  item('Microsoft.Network/virtualNetworks/aw-v1-p0-vnet', 'Microsoft.Network/virtualNetworks', 'aw-v1-p0-vnet', true),
  item('Microsoft.ContainerService/managedClusters/aw-v1-p0-aks', 'Microsoft.ContainerService/managedClusters', 'aw-v1-p0-aks', true),
  item('Microsoft.KeyVault/vaults/aw-v1-p0-kv', 'Microsoft.KeyVault/vaults', 'aw-v1-p0-kv', true),
  item('Microsoft.Storage/storageAccounts/awv1p0blob', 'Microsoft.Storage/storageAccounts', 'awv1p0blob', true),
  item('Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg', 'Microsoft.DBforPostgreSQL/flexibleServers', 'aw-v1-p0-pg', true),
  item('Microsoft.OperationalInsights/workspaces/aw-v1-p0-law', 'Microsoft.OperationalInsights/workspaces', 'aw-v1-p0-law', true),
  item('Microsoft.Insights/components/aw-v1-p0-appi', 'Microsoft.Insights/components', 'aw-v1-p0-appi', true),
  item('Microsoft.Insights/privateLinkScopes/aw-v1-p0-ampls', 'Microsoft.Insights/privateLinkScopes', 'aw-v1-p0-ampls', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'aw-v1-p0-id-foundation-probe', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'aw-v1-p0-id-identity-broker', true),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker-migration',
    'Microsoft.ManagedIdentity/userAssignedIdentities', 'aw-v1-p0-id-identity-broker-migration', true),
  ...['aks', 'postgres', 'private-endpoints'].map(name => item(
    `Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/${name}`,
    'Microsoft.Network/virtualNetworks/subnets', `aw-v1-p0-vnet/${name}`)),
  ...[
    ['privatelink.vaultcore.azure.net', 'kv'], ['privatelink.blob.core.windows.net', 'blob'],
    ['privatelink.monitor.azure.com', 'monitor'], ['privatelink.oms.opinsights.azure.com', 'oms'],
    ['privatelink.ods.opinsights.azure.com', 'ods'], ['privatelink.agentsvc.azure-automation.net', 'agentsvc'],
    ['privatelink.postgres.database.azure.com', 'pg'],
  ].flatMap(([zone, suffix]) => [
    item(`Microsoft.Network/privateDnsZones/${zone}`, 'Microsoft.Network/privateDnsZones', zone),
    item(`Microsoft.Network/privateDnsZones/${zone}/virtualNetworkLinks/aw-v1-p0-${suffix}-dns-link`,
      'Microsoft.Network/privateDnsZones/virtualNetworkLinks', `${zone}/aw-v1-p0-${suffix}-dns-link`),
  ]),
  item(`Microsoft.Network/networkSecurityGroups/${nrmsNsgName}`,
    'Microsoft.Network/networkSecurityGroups', nrmsNsgName),
  item(`Microsoft.AlertsManagement/smartDetectorAlertRules/${smartDetectorEvidence.name}`,
    smartDetectorEvidence.type, smartDetectorEvidence.name),
  ...['kv', 'blob', 'ampls'].flatMap(suffix => [
    item(`Microsoft.Network/privateEndpoints/aw-v1-p0-${suffix}-pe`,
      'Microsoft.Network/privateEndpoints', `aw-v1-p0-${suffix}-pe`, true),
    item(`Microsoft.Network/privateEndpoints/aw-v1-p0-${suffix}-pe/privateDnsZoneGroups/default`,
      'Microsoft.Network/privateEndpoints/privateDnsZoneGroups', `aw-v1-p0-${suffix}-pe/default`),
    item(`Microsoft.Network/networkInterfaces/aw-v1-p0-${suffix}-pe.nic-generated`,
      'Microsoft.Network/networkInterfaces', `aw-v1-p0-${suffix}-pe.nic-generated`),
  ]),
  ...['law', 'appi'].map(suffix => item(
    `Microsoft.Insights/privateLinkScopes/aw-v1-p0-ampls/scopedResources/aw-v1-p0-${suffix}-scope`,
    'Microsoft.Insights/privateLinkScopes/scopedResources', `aw-v1-p0-ampls/aw-v1-p0-${suffix}-scope`)),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe/federatedIdentityCredentials/foundation-probe-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'aw-v1-p0-id-foundation-probe/foundation-probe-workload-identity'),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker/federatedIdentityCredentials/identity-broker-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'aw-v1-p0-id-identity-broker/identity-broker-workload-identity'),
  item('Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker-migration/federatedIdentityCredentials/identity-broker-migration-workload-identity',
    'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials',
    'aw-v1-p0-id-identity-broker-migration/identity-broker-migration-workload-identity'),
  item(`Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg/administrators/${source.postgresEntraAdminObjectId}`,
    'Microsoft.DBforPostgreSQL/flexibleServers/administrators', `aw-v1-p0-pg/${source.postgresEntraAdminObjectId}`),
  item('Microsoft.Storage/storageAccounts/awv1p0blob/blobServices/default',
    'Microsoft.Storage/storageAccounts/blobServices', 'awv1p0blob/default'),
  item('Microsoft.Storage/storageAccounts/awv1p0blob/blobServices/default/containers/platform-artifacts',
    'Microsoft.Storage/storageAccounts/blobServices/containers', 'awv1p0blob/default/platform-artifacts'),
  ...['network', 'aks', 'keyvault', 'storage', 'monitor', 'postgres', 'identity', source.sha.slice(0, 12)].map(name =>
    item(`Microsoft.Resources/deployments/aw-v1-p0-${name}`, 'Microsoft.Resources/deployments', `aw-v1-p0-${name}`)),
  ...[
    ['Microsoft.KeyVault/vaults/aw-v1-p0-kv', '8db7f890-33c1-5cde-af9b-c73bef44f133'],
    ['Microsoft.Storage/storageAccounts/awv1p0blob', '74bad0fd-31fc-564b-a825-826ac0cf822c'],
    ['Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/aks', '31529a81-ec1c-5107-ae70-ffea10ee05af'],
    ['Microsoft.OperationalInsights/workspaces/aw-v1-p0-law', '034a4a0c-024d-5d7e-b880-d9cfeff88669'],
    ['Microsoft.Insights/components/aw-v1-p0-appi', '8f0580d4-e64d-5e45-97c5-831beb65c29d'],
  ].map(([scope, name]) => item(`${scope}/providers/Microsoft.Authorization/roleAssignments/${name}`,
    'Microsoft.Authorization/roleAssignments', name)),
];

export const postDeploymentDetails = {};
const subnetId = `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/private-endpoints`;
for (const suffix of ['kv', 'blob', 'ampls']) {
  const endpointId = `${prefix}Microsoft.Network/privateEndpoints/aw-v1-p0-${suffix}-pe`;
  const nicId = `${prefix}Microsoft.Network/networkInterfaces/aw-v1-p0-${suffix}-pe.nic-generated`;
  postDeploymentDetails[endpointId] = { id: endpointId, type: 'Microsoft.Network/privateEndpoints',
    properties: { subnet: { id: subnetId }, networkInterfaces: [{ id: nicId }] } };
  postDeploymentDetails[nicId] = { id: nicId, type: 'Microsoft.Network/networkInterfaces',
    properties: { privateEndpoint: { id: endpointId },
      ipConfigurations: [{ properties: { subnet: { id: subnetId } } }] } };
}

for (const name of ['aks', 'postgres', 'private-endpoints']) {
  const id = `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/${name}`;
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
  const id = `${prefix}Microsoft.Network/privateDnsZones/${zone}/virtualNetworkLinks/aw-v1-p0-${suffix}-dns-link`;
  postDeploymentDetails[id] = {
    id,
    type: 'Microsoft.Network/privateDnsZones/virtualNetworkLinks',
    name: `aw-v1-p0-${suffix}-dns-link`,
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
      id: `${prefix}Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/${name}`,
    })),
    networkInterfaces: [],
  },
};

const appInsightsId = `${prefix}Microsoft.Insights/components/aw-v1-p0-appi`;
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
    id: `${prefix}Microsoft.Resources/deployments/aw-v1-p0-${source.sha.slice(0, 12)}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs },
  }) },
};
