import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runAz, run } from '../lib/exec.mjs';

test('Bicep compilation and Kustomize rendering require no credentials or live target', () => {
  const bicep = runAz(['bicep', 'build', '--file', 'infra/bicep/main.bicep', '--stdout']);
  const template = JSON.parse(bicep.stdout);
  assert.equal(template.parameters.sourceSha.type, 'string');
  assert.equal(template.parameters.sourceTree.type, 'string');
  assert.equal(template.parameters.sourceHash.type, 'string');
  assert.ok(template.outputs.sourceSha && template.outputs.sourceHash);
  const aksDeployment = template.resources.find(resource =>
    resource.name === "[format('{0}-aks', parameters('namePrefix'))]");
  const aks = aksDeployment.properties.template;
  const cluster = aks.resources.find(resource => resource.type === 'Microsoft.ContainerService/managedClusters');
  assert.equal(cluster.apiVersion, '2026-07-02-preview');
  assert.equal(cluster.properties.disableLocalAccounts, true);
  assert.equal(cluster.properties.ingressProfile.webAppRouting.enabled, true);
  assert.equal(cluster.properties.ingressProfile.webAppRouting.nginx.defaultIngressControllerType, 'None');
  assert.match(cluster.properties.ingressProfile.webAppRouting.dnsZoneResourceIds, /appRoutingDnsZoneResourceIds/);
  assert.equal(cluster.properties.ingressProfile.webAppRouting.defaultDomain.enabled,
    "[variables('managedDefaultDomainRequested')]");
  assert.equal(aks.variables.managedDefaultDomainRequested,
    "[empty(parameters('appRoutingDnsZoneResourceIds'))]");
  assert.deepEqual(cluster.properties.addonProfiles.azureKeyvaultSecretsProvider, {
    enabled: true,
    config: { enableSecretRotation: 'true', rotationPollInterval: '2m' },
  });
  assert.deepEqual(cluster.properties.networkProfile.advancedNetworking, {
    enabled: true,
    security: { enabled: true },
    observability: { enabled: false },
  });
  assert.deepEqual(cluster.properties.aadProfile, {
    managed: true, enableAzureRBAC: true, tenantID: "[parameters('tenantId')]",
  });
  assert.equal(aksDeployment.properties.parameters.tenantId.value, "[parameters('tenantId')]");
  const assignment = aks.resources.find(resource => resource.type === 'Microsoft.Authorization/roleAssignments');
  const principal = "[reference(resourceId('Microsoft.ContainerService/managedClusters', variables('clusterName')), '2026-07-02-preview', 'full').identity.principalId]";
  assert.equal(aks.outputs.controlPlanePrincipalId.value, principal);
  assert.equal(assignment.properties.principalId, principal);
  assert.equal(assignment.properties.principalType, 'ServicePrincipal');
  assert.equal(aks.variables.networkContributorRoleId, '4d97b98b-1d4f-4787-a291-c67834d212e7');
  assert.equal(assignment.properties.roleDefinitionId,
    "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('networkContributorRoleId'))]");
  assert.equal(assignment.scope,
    "[resourceId('Microsoft.Network/virtualNetworks/subnets', format('{0}-vnet', parameters('namePrefix')), 'aks')]");
  assert.deepEqual(assignment.dependsOn, ["[resourceId('Microsoft.ContainerService/managedClusters', variables('clusterName'))]"]);
  assert.ok(aksDeployment.dependsOn.some(dependency => dependency.includes("'-network'") ||
    dependency.includes("{0}-network")));
  assert.ok(!aksDeployment.dependsOn.some(dependency => dependency.includes("{0}-identity")));
  assert.equal(assignment.name,
    "[guid(parameters('nodeSubnetId'), resourceId('Microsoft.ContainerService/managedClusters', variables('clusterName')), variables('networkContributorRoleId'))]");
  const identityDeployment = template.resources.find(resource =>
    resource.name === "[format('{0}-identity', parameters('namePrefix'))]");
  const identities = identityDeployment.properties.template;
  assert.ok(identityDeployment.dependsOn.some(dependency => dependency.includes("{0}-aks")));
  assert.ok(identityDeployment.dependsOn.some(dependency => dependency.includes("{0}-keyvault")));
  assert.deepEqual(Object.keys(identities.outputs.foundationProbeIdentity.value).sort(), [
    'clientId', 'name', 'namespace', 'principalObjectId', 'resourceId', 'serviceAccount',
  ]);
  for (const [name, roleId, scope] of [
    ['monitorQueryRoleAssignments', '73c42c96-874c-492b-b04d-ab87d138a893',
      "[resourceId('Microsoft.OperationalInsights/workspaces', last(split(parameters('monitorWorkspaceResourceId'), '/')))]"],
    ['monitorIngestionRoleAssignments', '3913510d-42f4-4e42-8a64-420c390055eb',
      "[resourceId('Microsoft.Insights/components', last(split(parameters('appInsightsResourceId'), '/')))]"],
  ]) {
    const role = identities.resources.find(resource => resource.copy?.name === name);
    assert.ok(role);
    assert.equal(role.scope, scope);
    assert.equal(role.properties.principalType, 'ServicePrincipal');
    assert.ok(Object.values(identities.variables).includes(roleId));
    assert.match(role.properties.principalId, /userAssignedIdentities/);
    assert.doesNotMatch(role.properties.principalId, /clientId/);
  }
  const appRoutingRole = identities.resources.find(resource =>
    resource.type === 'Microsoft.Authorization/roleAssignments' &&
    resource.name === "[guid(parameters('keyVaultId'), parameters('aksClusterId'), variables('keyVaultCertificateUserRoleId'))]");
  assert.ok(appRoutingRole);
  assert.equal(appRoutingRole.properties.principalType, 'ServicePrincipal');
  assert.equal(appRoutingRole.properties.principalId, "[parameters('appRoutingIdentityObjectId')]");
  assert.equal(appRoutingRole.scope,
    "[resourceId('Microsoft.KeyVault/vaults', last(split(parameters('keyVaultId'), '/')))]");
  assert.equal(appRoutingRole.name,
    "[guid(parameters('keyVaultId'), parameters('aksClusterId'), variables('keyVaultCertificateUserRoleId'))]");
  assert.equal(identities.variables.keyVaultCertificateUserRoleId, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba');
  assert.equal(aks.outputs.appRoutingIdentity.type, 'object');
  assert.match(aks.outputs.appRoutingIdentity.value.resourceId, /webAppRouting.*identity.*resourceId/);
  assert.match(aks.outputs.appRoutingIdentity.value.clientId, /webAppRouting.*identity.*clientId/);
  assert.match(aks.outputs.appRoutingIdentity.value.objectId, /webAppRouting.*identity.*objectId/);
  assert.match(identityDeployment.properties.parameters.appRoutingIdentityObjectId.value,
    /outputs\.appRoutingIdentity\.value\.objectId/);
  assert.doesNotMatch(identityDeployment.properties.parameters.appRoutingIdentityObjectId.value, /clientId/);
  assert.match(aks.outputs.appRoutingDomain.value.domainName, /domainName/);
  assert.doesNotMatch(aks.outputs.appRoutingDomain.value.domainName, /dnsPrefix|fqdn/);
  const aksSource = readFileSync('infra/bicep/modules/aks.bicep', 'utf8');
  assert.match(aksSource,
    /domainName: managedDefaultDomainRequested[\s\S]*?aks\.properties\.ingressProfile\.webAppRouting\.defaultDomain\.domainName/);
  assert.match(template.outputs.appRoutingDomain.value, /outputs\.appRoutingDomain\.value/);
  const appRoutingDnsDeployment = template.resources.find(resource => resource.copy?.name === 'appRoutingDnsRoles');
  assert.ok(appRoutingDnsDeployment);
  assert.ok(appRoutingDnsDeployment.dependsOn.some(dependency => dependency.includes("{0}-aks")));
  assert.equal(appRoutingDnsDeployment.resourceGroup,
    "[split(parameters('appRoutingDnsZoneResourceIds')[copyIndex()], '/')[4]]");
  assert.match(appRoutingDnsDeployment.properties.parameters.appRoutingIdentityObjectId.value,
    /outputs\.appRoutingIdentity\.value\.objectId/);
  const appRoutingDns = appRoutingDnsDeployment.properties.template;
  assert.equal(appRoutingDns.variables.dnsZoneContributorRoleId, 'befefa01-2a29-4197-83a8-272ff33ce314');
  assert.equal(appRoutingDns.variables.privateDnsZoneContributorRoleId, 'b12aa53e-6015-4669-85d0-8515ebb3ae7f');
  const publicDnsRole = appRoutingDns.resources.find(resource => resource.name.includes('dnsZoneContributorRoleId'));
  const privateDnsRole = appRoutingDns.resources.find(resource => resource.name.includes('privateDnsZoneContributorRoleId'));
  assert.ok(publicDnsRole && privateDnsRole);
  assert.equal(publicDnsRole.scope, "[resourceId('Microsoft.Network/dnsZones', variables('zoneName'))]");
  assert.equal(privateDnsRole.scope,
    "[resourceId('Microsoft.Network/privateDnsZones', variables('zoneName'))]");
  for (const role of [publicDnsRole, privateDnsRole]) {
    assert.equal(role.properties.principalId, "[parameters('appRoutingIdentityObjectId')]");
    assert.equal(role.properties.principalType, 'ServicePrincipal');
    assert.match(role.name, /parameters\('zoneResourceId'\).*parameters\('aksClusterId'\).*variables/);
  }
  for (const field of ['appRoutingIdentity', 'appRoutingDomain', 'foundationProbeIdentity', 'foundationResources', 'sourceTree']) {
    assert.ok(template.outputs[field]);
  }
  const rendered = run('kubectl', ['kustomize', 'deploy/k8s/base']).stdout;
  assert.match(rendered, /kind: ServiceAccount/);
  assert.match(rendered, /foundation-probe/);
  assert.doesNotMatch(rendered, /kind: (Deployment|StatefulSet|Job|Service)\r?\n/);
  assert.doesNotMatch(rendered, /^kind: (Ingress|IngressClass|Deployment|StatefulSet|DaemonSet|Job|Service)$/m);
  const dnsPolicy = rendered.split('---').find(document => document.includes('name: allow-dns-egress'));
  assert.match(dnsPolicy, /namespaceSelector:\s+matchLabels:\s+kubernetes\.io\/metadata\.name: kube-system/);
  assert.match(dnsPolicy, /podSelector:\s+matchLabels:\s+k8s-app: kube-dns/);
  assert.equal((dnsPolicy.match(/\n\s+to:/g) ?? []).length, 1);
  assert.equal((dnsPolicy.match(/- namespaceSelector:/g) ?? []).length, 1);
  assert.equal((dnsPolicy.match(/port: 53/g) ?? []).length, 2);
  assert.match(dnsPolicy, /port: 53\s+protocol: UDP/);
  assert.match(dnsPolicy, /port: 53\s+protocol: TCP/);
  assert.doesNotMatch(dnsPolicy, /namespaceSelector: \{\}/);

  const probeOverlay = run('kubectl', ['kustomize', 'deploy/k8s/acceptance/foundation-probe']).stdout;
  const probeEgress = probeOverlay.split('---').find(document =>
    document.includes('name: foundation-probe-egress'));
  assert.ok(probeEgress);
  assert.match(probeEgress, /rules:\s+dns:/);
  for (const host of [
    'login.microsoftonline.com',
    'CHANGEME-vault-name.vault.azure.net',
    'CHANGEME-storage-account.blob.core.windows.net',
    'CHANGEME-monitor-ingestion-host.in.applicationinsights.azure.com',
    'CHANGEME-postgres-server.postgres.database.azure.com',
  ]) assert.ok(probeEgress.includes(host));
  const probeJob = probeOverlay.split('---').find(document =>
    document.includes('kind: Job') && document.includes('name: foundation-probe'));
  assert.ok(probeJob);
  assert.match(probeJob, /runAsNonRoot: true/);
  assert.match(probeJob, /runAsUser: 10001/);
  assert.match(probeJob, /runAsGroup: 10001/);
});

test('Monitor defines five DNS zones and PE depends on both associations; KV/Blob PEs remain', () => {
  const main = readFileSync('infra/bicep/main.bicep', 'utf8');
  const monitor = readFileSync('infra/bicep/modules/monitor.bicep', 'utf8');
  for (const zone of ['monitor.azure.com', 'oms.opinsights.azure.com', 'ods.opinsights.azure.com',
    'agentsvc.azure-automation.net', 'blob.core.windows.net']) assert.ok(main.includes(`privatelink.${zone}`));
  assert.match(monitor, /dependsOn:\s*\[\s*amplsWorkspaceScope\s*amplsAppInsightsScope\s*\]/);
  assert.equal((monitor.match(/publicNetworkAccessForQuery: 'Disabled'/g) ?? []).length, 2);
  assert.equal((monitor.match(/publicNetworkAccessForIngestion: 'Enabled'/g) ?? []).length, 2);
  for (const module of ['keyvault', 'storage']) {
    const text = readFileSync(`infra/bicep/modules/${module}.bicep`, 'utf8');
    assert.match(text, /Microsoft.Network\/privateEndpoints@/);
    assert.match(text, /privateDnsZoneId/);
  }
});

test('native PG bootstrap is a definition, maps object ID, separates runtime and migration owner', () => {
  const sql = readFileSync('infra/bicep/postgres-bootstrap.sql', 'utf8');
  assert.match(sql, /pg_catalog\.pgaadauth_create_principal_with_oid\(\s*:'runtime_role', :'principal_oid', 'service', false, false\)/);
  assert.match(sql, /NOCREATEROLE NOCREATEDB NOSUPERUSER NOREPLICATION NOINHERIT/);
  assert.match(sql, /CREATE SCHEMA %I AUTHORIZATION %I.*:'service_schema', :'migration_role'/);
  assert.match(sql, /GRANT USAGE ON SCHEMA/);
  assert.doesNotMatch(sql, /GRANT azure_pg_admin|GRANT CREATE .*runtime_role/);
  const grants = readFileSync('infra/bicep/postgres-runtime-grants.sql', 'utf8');
  assert.match(grants, /GRANT SELECT, INSERT, UPDATE ON TABLE/);
  assert.match(grants, /GRANT SELECT, INSERT ON TABLE/);
  assert.match(grants, /REVOKE ALL ON TABLE %I.outbox_schema_migrations/);
  assert.doesNotMatch(grants, /GRANT.*ALL TABLES|ALTER DEFAULT PRIVILEGES/);
});
