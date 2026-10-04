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
  assert.equal(template.parameters.monitorLocation.type, 'string');
  assert.deepEqual(template.parameters.postgresEntraAdminPrincipalType.allowedValues, [
    'User', 'Group', 'ServicePrincipal',
  ]);
  assert.ok(template.outputs.sourceSha && template.outputs.sourceHash);
  const networkDeployment = template.resources.find(resource =>
    resource.name === "[format('{0}-network', parameters('namePrefix'))]");
  assert.equal(networkDeployment.properties.parameters.location.value, "[parameters('location')]");
  const postgresDeployment = template.resources.find(resource =>
    resource.name === "[format('{0}-postgres', parameters('namePrefix'))]");
  assert.equal(postgresDeployment.properties.parameters.location.value, "[parameters('location')]");
  assert.match(postgresDeployment.properties.parameters.delegatedSubnetId.value, /postgresSubnetId/);
  assert.equal(postgresDeployment.properties.parameters.entraAdminPrincipalType.value,
    "[parameters('postgresEntraAdminPrincipalType')]");
  const postgres = postgresDeployment.properties.template;
  const postgresServer = postgres.resources.find(resource =>
    resource.type === 'Microsoft.DBforPostgreSQL/flexibleServers');
  const postgresAdmin = postgres.resources.find(resource =>
    resource.type === 'Microsoft.DBforPostgreSQL/flexibleServers/administrators');
  assert.equal(postgresServer.location, "[parameters('location')]");
  assert.equal(postgresAdmin.properties.principalType, "[parameters('entraAdminPrincipalType')]");
  const monitorDeployment = template.resources.find(resource =>
    resource.name === "[format('{0}-monitor', parameters('namePrefix'))]");
  assert.equal(monitorDeployment.properties.parameters.location.value, "[parameters('location')]");
  assert.equal(monitorDeployment.properties.parameters.monitorLocation.value, "[parameters('monitorLocation')]");
  const monitor = monitorDeployment.properties.template;
  const workspace = monitor.resources.find(resource => resource.type === 'Microsoft.OperationalInsights/workspaces');
  const appInsights = monitor.resources.find(resource => resource.type === 'Microsoft.Insights/components');
  const monitorPrivateEndpoint = monitor.resources.find(resource => resource.type === 'Microsoft.Network/privateEndpoints');
  const ampls = monitor.resources.find(resource =>
    resource.type.toLowerCase() === 'microsoft.insights/privatelinkscopes');
  assert.equal(workspace.location, "[parameters('monitorLocation')]");
  assert.equal(appInsights.location, "[parameters('monitorLocation')]");
  assert.equal(monitorPrivateEndpoint.location, "[parameters('location')]");
  assert.equal(ampls.location, 'global');
  const exampleParameters = JSON.parse(readFileSync('infra/bicep/parameters/p0-integration.example.json', 'utf8')).parameters;
  assert.equal(exampleParameters.location.value, 'eastus2euap');
  assert.equal(exampleParameters.monitorLocation.value, 'eastus2');
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
  for (const identityName of ['identityBrokerRuntimeIdentity', 'identityBrokerMigrationIdentity']) {
    assert.deepEqual(Object.keys(identities.outputs[identityName].value).sort(), [
      'clientId', 'name', 'namespace', 'principalObjectId', 'resourceId', 'serviceAccount',
    ]);
  }
  const configuredServices = identities.parameters.services.defaultValue;
  assert.deepEqual(configuredServices.map(service => service.name), [
    'foundation-probe', 'identity-broker', 'identity-broker-migration',
  ]);
  const runtimeService = configuredServices.find(service => service.name === 'identity-broker');
  const migrationService = configuredServices.find(service => service.name === 'identity-broker-migration');
  assert.equal(runtimeService.needsKeyVault, true);
  assert.equal(runtimeService.needsBlob, false);
  assert.equal(runtimeService.needsMonitor, false);
  assert.equal(migrationService.needsKeyVault, false);
  assert.equal(migrationService.needsBlob, false);
  assert.equal(migrationService.needsMonitor, false);
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
  for (const field of [
    'appRoutingIdentity', 'appRoutingDomain', 'foundationProbeIdentity',
    'identityBrokerRuntimeIdentity', 'identityBrokerMigrationIdentity', 'foundationResources', 'sourceTree',
  ]) {
    assert.ok(template.outputs[field]);
  }
  const rendered = run('kubectl', ['kustomize', 'deploy/k8s/base']).stdout;
  assert.match(rendered, /kind: ServiceAccount/);
  assert.match(rendered, /foundation-probe/);
  assert.match(rendered, /kind: Deployment/);
  assert.match(rendered, /kind: Service/);
  assert.doesNotMatch(rendered, /^kind: (Ingress|IngressClass|Gateway|StatefulSet|DaemonSet|Job)$/m);
  const identityDeploymentDocument = rendered.split('---').find(document =>
    document.includes('kind: Deployment') && document.includes('name: identity-broker'));
  assert.ok(identityDeploymentDocument);
  assert.match(identityDeploymentDocument, /serviceAccountName: identity-broker/);
  assert.match(identityDeploymentDocument, /azure\.workload\.identity\/use: "true"/);
  assert.match(identityDeploymentDocument, /image: registry\.invalid\/agentweaver-identity-broker@sha256:0{64}/);
  assert.match(identityDeploymentDocument, /ASPNETCORE_URLS\s*\n\s+value: https:\/\/\+:8443/);
  assert.match(identityDeploymentDocument, /ASPNETCORE_Kestrel__Certificates__Default__Path\s*\n\s+value: \/var\/run\/identity-broker-tls\/tls\.crt/);
  assert.match(identityDeploymentDocument, /ASPNETCORE_Kestrel__Certificates__Default__KeyPath\s*\n\s+value: \/var\/run\/identity-broker-tls\/tls\.key/);
  assert.match(identityDeploymentDocument, /claimName: identity-broker-key-ring/);
  assert.match(identityDeploymentDocument, /configMapRef:\s+name: identity-broker-runtime-config\s+optional: false/);
  assert.match(identityDeploymentDocument, /secretRef:\s+name: identity-broker-client-secrets\s+optional: true/);
  assert.match(identityDeploymentDocument, /optional: false\s+secretName: identity-broker-signing/);
  assert.match(identityDeploymentDocument, /optional: false\s+secretName: identity-broker-tls/);
  assert.match(identityDeploymentDocument, /runAsNonRoot: true/);
  assert.match(identityDeploymentDocument, /runAsUser: 10001/);
  const identityService = rendered.split('---').find(document =>
    document.includes('kind: Service\n') && document.includes('name: identity-broker'));
  assert.ok(identityService);
  assert.match(identityService, /type: ClusterIP/);
  assert.match(identityService, /port: 443/);
  assert.match(identityService, /targetPort: https/);
  const migrationRender = run('kubectl', ['kustomize', 'deploy/k8s/migrations/identity-broker']).stdout;
  const migrationJob = migrationRender.split('---').find(document =>
    document.includes('kind: Job') && document.includes('name: identity-broker-migration'));
  assert.ok(migrationJob);
  assert.match(migrationJob, /serviceAccountName: identity-broker-migration/);
  assert.match(migrationJob, /--migrate/);
  assert.match(migrationJob, /identity-broker-migration-config/);
  const identityEgress = rendered.split('---').find(document =>
    document.includes('name: identity-broker-egress'));
  const migrationEgress = migrationRender.split('---').find(document =>
    document.includes('name: identity-broker-migration-egress'));
  assert.ok(identityEgress && migrationEgress);
  for (const host of [
    'login.microsoftonline.com',
    'CHANGEME-KEYVAULT-HOST',
    'CHANGEME-POSTGRES-HOST',
    'CHANGEME-UPSTREAM-OIDC-AUTHORITY',
    'CHANGEME-UPSTREAM-OIDC-METADATA-HOST',
  ]) assert.ok(identityEgress.includes(host));
  assert.ok(migrationEgress.includes('login.microsoftonline.com'));
  assert.ok(migrationEgress.includes('CHANGEME-POSTGRES-HOST'));
  assert.doesNotMatch(`${identityEgress}\n${migrationEgress}`, /toEntities:\s*\n\s+- world|0\.0\.0\.0\/0|matchPattern:\s*\*/);
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
  const identityBootstrap = readFileSync('infra/bicep/postgres-identity-bootstrap.sql', 'utf8');
  assert.match(identityBootstrap, /:'runtime_principal_oid'/);
  assert.match(identityBootstrap, /:'migration_principal_oid'/);
  assert.match(identityBootstrap, /CREATE SCHEMA identity_broker AUTHORIZATION/);
  assert.match(identityBootstrap, /GRANT USAGE ON SCHEMA identity_broker TO %I/);
  assert.doesNotMatch(identityBootstrap, /azure_pg_admin|GRANT CREATE .*runtime_role/);
  const identityGrants = readFileSync('infra/bicep/postgres-identity-runtime-grants.sql', 'utf8');
  assert.match(identityGrants, /GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE/);
  assert.match(identityGrants, /GRANT SELECT ON TABLE %I.%I TO %I/);
  assert.match(identityGrants, /OpenIddictApplications/);
  assert.match(identityGrants, /REVOKE CREATE ON SCHEMA identity_broker/);
  assert.doesNotMatch(identityGrants, /ALL TABLES|ALTER DEFAULT PRIVILEGES/);
});
