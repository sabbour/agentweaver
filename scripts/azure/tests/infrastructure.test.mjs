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
  assert.equal(cluster.properties.disableLocalAccounts, true);
  assert.deepEqual(cluster.properties.aadProfile, {
    managed: true, enableAzureRBAC: true, tenantID: "[parameters('tenantId')]",
  });
  assert.equal(aksDeployment.properties.parameters.tenantId.value, "[parameters('tenantId')]");
  const assignment = aks.resources.find(resource => resource.type === 'Microsoft.Authorization/roleAssignments');
  const principal = "[reference(resourceId('Microsoft.ContainerService/managedClusters', variables('clusterName')), '2024-02-01', 'full').identity.principalId]";
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
  assert.equal(assignment.name,
    "[guid(parameters('nodeSubnetId'), resourceId('Microsoft.ContainerService/managedClusters', variables('clusterName')), variables('networkContributorRoleId'))]");
  const identities = template.resources.find(resource =>
    resource.name === "[format('{0}-identity', parameters('namePrefix'))]").properties.template;
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
  for (const field of ['foundationProbeIdentity', 'foundationResources', 'sourceTree']) assert.ok(template.outputs[field]);
  const rendered = run('kubectl', ['kustomize', 'deploy/k8s/base']).stdout;
  assert.match(rendered, /kind: ServiceAccount/);
  assert.match(rendered, /foundation-probe/);
  assert.doesNotMatch(rendered, /kind: (Deployment|StatefulSet|Job|Service)\r?\n/);
  const dnsPolicy = rendered.split('---').find(document => document.includes('name: allow-dns-egress'));
  assert.match(dnsPolicy, /namespaceSelector:\s+matchLabels:\s+kubernetes\.io\/metadata\.name: kube-system/);
  assert.match(dnsPolicy, /podSelector:\s+matchLabels:\s+k8s-app: kube-dns/);
  assert.equal((dnsPolicy.match(/\n\s+to:/g) ?? []).length, 1);
  assert.equal((dnsPolicy.match(/- namespaceSelector:/g) ?? []).length, 1);
  assert.equal((dnsPolicy.match(/port: 53/g) ?? []).length, 2);
  assert.match(dnsPolicy, /port: 53\s+protocol: UDP/);
  assert.match(dnsPolicy, /port: 53\s+protocol: TCP/);
  assert.doesNotMatch(dnsPolicy, /namespaceSelector: \{\}/);
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
