import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parse } from 'yaml';
import { run, runAz } from '../lib/exec.mjs';
import { buildP1PostgresPlan, p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';
import { parseKustomizeResources } from '../../release/kustomize-runtime.mjs';
import { renderEventsProfile } from '../../../deploy/k8s/profiles/events-and-sessions/render.mjs';

const containerUriKey = 'EventsAndSessions__SessionMaterial__ContainerUri';
const clientIdKey = 'EventsAndSessions__Database__WorkloadIdentity__ClientId';
const runtimeConfigName = 'events-and-sessions-runtime-config';
const credentialKey = /(?:AccountKey|SharedAccessKey|SharedAccessSignature|(?:Blob|SessionMaterial).*(?:Sas|Token|Secret|ConnectionString|Credential)|ConnectionStrings?.*(?:Blob|SessionMaterial))/i;
const credentialValue = /(?:AccountKey|SharedAccessKey|SharedAccessSignature)\s*=|(?:\?|&)(?:sig|sv|se|sp|sr)=|(?:DefaultEndpointsProtocol|BlobEndpoint)=/i;

// Resolve only named resources from local render output so the guard cannot silently follow a different workload.
function resource(items, kind, name) {
  const match = items.find(item => item.kind === kind && item.metadata?.name === name);
  assert.ok(match, `Rendered resources are missing ${kind}/${name}`);
  return match;
}

// Render checked-in Kustomize inputs locally; kubectl composes files and does not contact a cluster.
function renderKustomize(directory) {
  return parseKustomizeResources(run('kubectl', ['kustomize', directory]).stdout).items;
}

// Compile source Bicep into an ARM template only; this does not log in or deploy resources.
function compileBicep(file) {
  return JSON.parse(runAz(['bicep', 'build', '--file', file, '--stdout']).stdout);
}

// Load each source edge once, keeping raw configuration beside its Kustomize-rendered result for comparison.
function loadSources() {
  return {
    runtimeConfigSource: JSON.parse(readFileSync(
      'deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json', 'utf8')),
    runtimeProfileExample: JSON.parse(readFileSync(
      'deploy/k8s/profiles/events-and-sessions/runtime-inputs.example.json', 'utf8')),
    runtimeKustomization: parse(readFileSync('deploy/k8s/kustomization.yaml', 'utf8')),
    runtimeResources: renderKustomize('deploy/k8s'),
    migrationResources: renderKustomize('deploy/k8s/migrations/p1-postgres'),
    eventsProgram: readFileSync(
      'services/events-and-sessions/Agentweaver.EventsAndSessions/Program.cs', 'utf8'),
    postgresDataSource: readFileSync(
      'services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsPostgresDataSource.cs', 'utf8'),
    runtimeMaterialClient: readFileSync(
      'packages/Agentweaver.AgentRuntime/RuntimeSessionMaterialHttpClient.cs', 'utf8'),
    eventsMigration: readFileSync(
      'services/events-and-sessions/Agentweaver.EventsAndSessions/Migrations/010_session_object_inventory.sql', 'utf8'),
    eventsMigrator: readFileSync(
      'services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs', 'utf8'),
    committedPostgresPlan: JSON.parse(readFileSync('scripts/azure/lib/p1-postgres-plan.json', 'utf8')),
    generatedPostgresPlan: buildP1PostgresPlan(),
    compiledStorage: compileBicep('infra/bicep/modules/storage.bicep'),
    compiledRuntimeIdentities: compileBicep('infra/bicep/p1-runtime-identities.bicep'),
  };
}

function assertSourceComposition(input) {
  const runtimeConfig = resource(input.runtimeResources, 'ConfigMap', runtimeConfigName);
  assert.deepEqual(runtimeConfig.data, input.runtimeConfigSource.data,
    'Kustomize must render the checked-in Events runtime ConfigMap unchanged');
  assert.ok(input.runtimeKustomization.resources.includes(
    'runtime-configs/events-and-sessions-runtime-config.json'),
  'the root Kustomization must include the Events runtime ConfigMap source');

  // The existing profile renderer admits the production values while preserving its deliberate client-ID placeholder.
  const profileConfiguration = Object.fromEntries(
    Object.keys(input.runtimeProfileExample.configuration).map(key => [
      key,
      key === clientIdKey ? input.runtimeProfileExample.configuration[key] : runtimeConfig.data[key],
    ]),
  );
  const renderedProfile = renderEventsProfile({ configuration: profileConfiguration });
  for (const [key, value] of Object.entries(renderedProfile.data)) {
    if (key !== clientIdKey) {
      assert.equal(runtimeConfig.data[key], value,
        `the rendered Events profile must agree with the runtime ConfigMap for '${key}'`);
    }
  }
  const containerUri = new URL(runtimeConfig.data[containerUriKey]);
  assert.equal(containerUri.protocol, 'https:');
  assert.equal(containerUri.port, '', 'the Blob endpoint must use the policy-approved HTTPS port');
  assert.equal(containerUri.search, '');
  assert.equal(containerUri.pathname, '/session-material');

  // Events supplies a projected workload credential to its Blob client; the optional branch preserves journal-only host mode.
  assert.match(input.eventsProgram,
    /builder\.Services\.AddSingleton<TokenCredential>\(\s*_\s*=>\s*EventsAndSessionsPostgresDataSource\.CreateCredential\(runtimeConnection\)\s*\)/);
  assert.match(input.postgresDataSource,
    /new WorkloadIdentityCredential\(new WorkloadIdentityCredentialOptions/);
  assert.match(input.postgresDataSource, /TenantId = connection\.TenantId/);
  assert.match(input.postgresDataSource, /ClientId = connection\.ClientId/);
  assert.match(input.postgresDataSource, /TokenFilePath = connection\.TokenFilePath/);
  assert.match(input.eventsProgram,
    /var materialContainer = builder\.Configuration\["EventsAndSessions:SessionMaterial:ContainerUri"\]/);
  assert.match(input.eventsProgram,
    /new BlobContainerClient\(containerUri,\s*services\.GetRequiredService<TokenCredential>\(\)\)\)/);
  const registrationStart = input.eventsProgram.indexOf('if (materialContainer is not null)');
  const registrationEnd = input.eventsProgram.indexOf(
    'builder.Services.AddSingleton<IReadOnlyDictionary', registrationStart);
  assert.ok(registrationStart >= 0 && registrationEnd > registrationStart,
    'material registration must stay inside the optional configured-container branch');
  const materialRegistration = input.eventsProgram.slice(registrationStart, registrationEnd);
  assert.match(materialRegistration, /AddSingleton<IObjectStore>/);
  assert.match(materialRegistration, /AddSessionMaterialOwner\(\)/,
    'session-material endpoints must register their owner services in the configured-container branch');
  assert.doesNotMatch(materialRegistration, /connection\s*string|accountkey|sharedaccess|sas/i);
  assert.match(input.eventsProgram,
    /if \(materialContainer is not null\)\s*\{\s*app\.MapSessionMaterialEndpoints\(\);\s*app\.MapProducedRunCaptureEndpoints\(\);\s*\}/,
    'session-material routes must remain disabled when the optional container is absent');
  assert.equal((input.eventsProgram.match(/app\.MapSessionMaterialEndpoints\(\)/g) ?? []).length, 1);
  assert.equal((input.eventsProgram.match(/app\.MapProducedRunCaptureEndpoints\(\)/g) ?? []).length, 1);

  // The configured container must exist in the compiled private storage template.
  const configuredContainer = containerUri.pathname.slice(1);
  assert.ok(input.compiledStorage.variables.containerNames.includes(configuredContainer),
    'the configured URI must name a container emitted by the storage template');
  assert.match(input.compiledStorage.outputs.sessionMaterialContainerUri.value,
    /primaryEndpoints\.blob[\s\S]*session-material|session-material[\s\S]*primaryEndpoints\.blob/);
  const storageDeployment = input.compiledStorage.resources.find(item =>
    item.type === 'Microsoft.Resources/deployments');
  assert.ok(storageDeployment, 'compiled storage must include the container deployment');
  const container = storageDeployment.properties.template.resources.find(item =>
    item.type === 'Microsoft.Storage/storageAccounts/blobServices/containers');
  assert.ok(container, 'compiled storage must define Blob containers');
  assert.deepEqual(
    Object.keys(storageDeployment.properties.parameters).sort(),
    Object.keys(storageDeployment.properties.template.parameters).sort(),
    'the storage module deployment must pass every declared parameter to its template',
  );
  assert.equal(storageDeployment.properties.parameters.containerNames.value,
    "[variables('containerNames')]");
  assert.equal(container.copy.count, '[length(parameters(\'containerNames\'))]');
  assert.equal(container.name,
    "[format('{0}/{1}/{2}', parameters('storageAccountName'), 'default', parameters('containerNames')[copyIndex()])]");
  assert.equal(container.properties.publicAccess, 'None');
  const storageAccount = input.compiledStorage.resources.find(item =>
    item.type === 'Microsoft.Storage/storageAccounts');
  assert.ok(storageAccount);
  assert.equal(storageAccount.name, '[take(variables(\'storageAccountName\'), 24)]');
  assert.equal(storageAccount.properties.allowSharedKeyAccess, false);
  assert.equal(storageAccount.properties.publicNetworkAccess, 'Disabled');
  const configuredStorageAccountName = containerUri.hostname.replace('.blob.core.windows.net', '');
  const approvedPrefix = input.compiledRuntimeIdentities.parameters.namePrefix.allowedValues[0];
  assert.equal(configuredStorageAccountName, `${approvedPrefix}blob`);
  assert.equal(input.compiledStorage.variables.storageAccountName,
    "[toLower(replace(format('{0}blob', parameters('namePrefix')), '-', ''))]");
  assert.equal(storageDeployment.properties.parameters.storageAccountName.value,
    "[take(variables('storageAccountName'), 24)]");

  // The rendered Deployment, ServiceAccount, and replacement must converge on one Events workload identity.
  const deployment = resource(input.runtimeResources, 'Deployment', 'events-and-sessions');
  const pod = deployment.spec.template;
  const serviceAccountName = pod.spec.serviceAccountName;
  assert.equal(serviceAccountName, 'events-and-sessions');
  assert.equal(pod.metadata.labels['azure.workload.identity/use'], 'true');
  assert.ok(pod.spec.containers.some(containerSpec =>
    containerSpec.envFrom?.some(envFrom => envFrom.configMapRef?.name === runtimeConfigName)),
  'the Events Deployment must consume the rendered runtime ConfigMap');
  const directEnvironment = pod.spec.containers.flatMap(containerSpec => containerSpec.env ?? []);
  assert.equal(directEnvironment.some(variable =>
    variable.name === containerUriKey ||
    variable.name === clientIdKey ||
    variable.name === 'ConnectionStrings__EventsAndSessions' ||
    variable.name === 'ConnectionStrings__EventsAndSessionsMigration' ||
    variable.name.includes('__Migration__')), false,
  'runtime storage, database, and identity settings must arrive through the ConfigMap, not direct overrides');
  for (const variable of directEnvironment) {
    assert.doesNotMatch(variable.name, credentialKey,
      `direct runtime environment key '${variable.name}' must not provide Blob credentials`);
    assert.doesNotMatch(variable.value ?? '', credentialValue,
      `direct runtime environment value '${variable.name}' must not contain a Blob key or SAS`);
  }
  // AgentHost settings are embedded in a rendered ConfigMap; clients must receive owner addresses, not Blob access.
  const agentHostConfig = resource(input.runtimeResources, 'ConfigMap', 'agenthost-runtime-v1');
  const agentHostSettings = JSON.parse(agentHostConfig.data['appsettings.json']);
  assert.ok(agentHostSettings.AgentHost, 'the rendered AgentHost configuration must contain its runtime settings');
  const serializedAgentHostSettings = JSON.stringify(agentHostSettings);
  assert.doesNotMatch(serializedAgentHostSettings,
    /Blob|SessionMaterial|AccountKey|SharedAccessKey|SharedAccessSignature|ConnectionStrings?/i,
    'AgentHost configuration must not carry direct Blob settings or credentials');
  assert.doesNotMatch(serializedAgentHostSettings, credentialValue,
    'AgentHost configuration must not carry Blob keys or SAS values');
  const serviceAccount = resource(input.runtimeResources, 'ServiceAccount', serviceAccountName);
  assert.equal(serviceAccount.metadata.labels['azure.workload.identity/use'], 'true');
  assert.equal(serviceAccount.metadata.annotations['azure.workload.identity/client-id'],
    runtimeConfig.data[clientIdKey]);
  const clientIdReplacement = input.runtimeKustomization.replacements.find(replacement =>
    replacement.source?.kind === 'ConfigMap' &&
    replacement.source.name === runtimeConfigName &&
    replacement.source.fieldPath === `data.${clientIdKey}` &&
    replacement.targets?.some(target =>
      target.select?.kind === 'ServiceAccount' &&
      target.select.name === serviceAccountName &&
      target.fieldPaths?.includes('metadata.annotations.[azure.workload.identity/client-id]')));
  assert.ok(clientIdReplacement,
    'Kustomize must source the Events ServiceAccount client ID from the runtime ConfigMap');
  assert.ok(input.compiledRuntimeIdentities.variables.serviceNames.includes(serviceAccountName));
  assert.equal(input.compiledRuntimeIdentities.variables.eventsAndSessionsIndex,
    "[indexOf(variables('serviceNames'), 'events-and-sessions')]");

  // The compiled role must target the same configured container and the Events identity, not an account-wide scope.
  const roleDeployment = input.compiledRuntimeIdentities.resources.find(item =>
    item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
  assert.ok(roleDeployment, 'compiled runtime identities must include the Events material role');
  const roleParameters = roleDeployment.properties.parameters;
  assert.deepEqual(
    Object.keys(roleParameters).sort(),
    Object.keys(roleDeployment.properties.template.parameters).sort(),
    'the role deployment must pass every declared parameter to its assignment template',
  );
  assert.equal(roleParameters.containerName.value, configuredContainer);
  const eventsIdentityResourceId =
    "resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-id-{1}', parameters('namePrefix'), variables('services')[variables('eventsAndSessionsIndex')].name))";
  assert.equal(roleParameters.identityResourceId.value, `[${eventsIdentityResourceId}]`);
  assert.equal(roleParameters.principalId.value,
    `[reference(${eventsIdentityResourceId}, '2023-01-31').principalId]`);
  assert.equal(roleParameters.storageAccountName.value,
    input.compiledStorage.variables.storageAccountName);
  const role = roleDeployment.properties.template.resources.find(item =>
    item.type === 'Microsoft.Authorization/roleAssignments');
  assert.ok(role);
  assert.equal(role.properties.principalId, '[parameters(\'principalId\')]');
  assert.equal(role.scope,
    "[resourceId('Microsoft.Storage/storageAccounts/blobServices/containers', parameters('storageAccountName'), 'default', parameters('containerName'))]");
  assert.equal(role.properties.roleDefinitionId,
    "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('storageBlobDataContributorRoleId'))]");
  assert.equal(roleDeployment.properties.template.variables.storageBlobDataContributorRoleId,
    'ba92f5b4-2d11-453d-a403-e96b0029c9fe');

  // Every configured Blob, PostgreSQL, and private-owner destination must be allowed by the existing Events policy.
  const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
  assert.deepEqual(network.spec.endpointSelector.matchLabels, {
    'agentweaver.io/workload': pod.metadata.labels['agentweaver.io/workload'],
  }, 'the policy endpoint selector must select the rendered Events workload');
  const dnsRule = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
    endpoint.matchLabels?.['k8s:io.kubernetes.pod.namespace'] === 'kube-system' &&
    endpoint.matchLabels?.['k8s:k8s-app'] === 'kube-dns'));
  assert.ok(dnsRule, 'Events DNS destinations must use the kube-dns endpoint rule');
  const dnsPorts = dnsRule.toPorts.flatMap(port => port.ports ?? []);
  assert.ok(dnsPorts.some(port => port.port === '53' && port.protocol === 'UDP'));
  assert.ok(dnsPorts.some(port => port.port === '53' && port.protocol === 'TCP'));
  const dnsTargets = new Set(network.spec.egress.flatMap(rule =>
    (rule.toPorts ?? []).flatMap(port => (port.rules?.dns ?? []).map(item => item.matchName))));
  const fqdnTargets = network.spec.egress.flatMap(rule => rule.toFQDNs ?? [])
    .map(item => item.matchName);
  const privateBlobHost = containerUri.hostname.replace(
    '.blob.core.windows.net', '.privatelink.blob.core.windows.net');
  assert.ok(dnsTargets.has(containerUri.hostname));
  assert.ok(dnsTargets.has(privateBlobHost));
  assert.ok(fqdnTargets.includes(containerUri.hostname));
  const postgresHost = input.generatedPostgresPlan.target.postgresHost;
  assert.ok(runtimeConfig.data.ConnectionStrings__EventsAndSessions.includes(`Host=${postgresHost};`));
  assert.ok(dnsTargets.has(postgresHost));
  assert.ok(dnsTargets.has(postgresHost.replace(
    '.postgres.database.azure.com', '.privatelink.postgres.database.azure.com')));
  assert.ok(fqdnTargets.includes(postgresHost));
  const ownerServices = [
    ['EventsAndSessions__Knowledge__BaseAddress', 'knowledge'],
    ['ProjectsConfig__BaseAddress', 'projects-config'],
    ['EventsAndSessions__OrchestratorOwner__OwnerBaseAddress', 'orchestrator'],
  ];
  for (const [key, workload] of ownerServices) {
    const ownerUri = new URL(runtimeConfig.data[key]);
    const ownerHost = ownerUri.hostname;
    assert.ok(dnsTargets.has(ownerHost), `Events DNS policy must include ${ownerHost}`);
    assert.ok(dnsRule.toPorts.some(ports =>
      ports.rules?.dns?.some(item => item.matchName === ownerHost)),
    `Events kube-dns rule must resolve the ${workload} owner`);
    const ownerRule = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
      endpoint.matchLabels?.['agentweaver.io/workload'] === workload &&
      endpoint.matchLabels?.['k8s:io.kubernetes.pod.namespace'] === 'agentweaver'));
    assert.ok(ownerRule, `Events egress policy must include the ${workload} owner`);
    assert.ok(ownerRule.toPorts?.some(ports =>
      ports.ports?.some(port =>
        port.port === (ownerUri.port || '443') && port.protocol === 'TCP')),
    `Events egress policy must allow the configured ${workload} owner port`);
  }
  assert.ok(dnsRule.toPorts.some(ports =>
    ports.rules?.dns?.some(item => item.matchName === containerUri.hostname)));
  assert.ok(dnsRule.toPorts.some(ports =>
    ports.rules?.dns?.some(item => item.matchName === privateBlobHost)));
  assert.ok(dnsRule.toPorts.some(ports =>
    ports.rules?.dns?.some(item => item.matchName === postgresHost)));
  assert.ok(dnsRule.toPorts.some(ports =>
    ports.rules?.dns?.some(item => item.matchName === postgresHost.replace(
      '.postgres.database.azure.com', '.privatelink.postgres.database.azure.com'))));
  const blobEgress = network.spec.egress.find(rule => rule.toFQDNs?.some(item =>
    item.matchName === containerUri.hostname));
  assert.ok(blobEgress?.toPorts?.some(ports =>
    ports.ports?.some(port => port.port === '443' && port.protocol === 'TCP')));
  const postgresEgress = network.spec.egress.find(rule => rule.toFQDNs?.some(item =>
    item.matchName === postgresHost));
  assert.ok(postgresEgress?.toPorts?.some(ports =>
    ports.ports?.some(port => port.port === '5432' && port.protocol === 'TCP')));

  // Migration 010, its inventory grant, and the maintenance Job share one hashed plan; runtime keeps its separate identity.
  assert.match(input.eventsMigrator, /CurrentSchemaVersion\s*=\s*10/);
  assert.match(input.eventsMigrator,
    /SupportedVersions[\s\S]*Enumerable\.Range\(1,\s*CurrentSchemaVersion\)/,
    'the migrator version catalog must include migration 010');
  assert.match(input.eventsMigrator,
    /10 => "Agentweaver\.EventsAndSessions\.Migrations\.010_session_object_inventory\.sql"/);
  assert.match(input.eventsMigration, /CREATE TABLE \{schema\}\.session_object_inventory/);
  assert.deepEqual(input.generatedPostgresPlan, input.committedPostgresPlan,
    'the checked-in PostgreSQL plan must match its source builder');
  assert.ok(input.generatedPostgresPlan.eventsVersions.includes(10));
  const eventsPlan = input.generatedPostgresPlan.services.find(service =>
    service.service === 'events-and-sessions');
  assert.ok(eventsPlan);
  const inventoryGrant = eventsPlan.tables.find(table => table.table === 'session_object_inventory');
  assert.deepEqual(inventoryGrant?.privileges, ['SELECT', 'INSERT', 'UPDATE']);
  const migrationJob = resource(input.migrationResources, 'Job', 'events-and-sessions-migration');
  const migrationPod = migrationJob.spec.template.spec;
  assert.equal(migrationPod.serviceAccountName, 'p1-postgres-migration');
  assert.ok(migrationPod.containers[0].command.includes('--migrate'));
  const migrationEnvironment = Object.fromEntries(
    migrationPod.containers[0].env.map(({ name, value }) => [name, value]));
  assert.equal(migrationEnvironment.EventsAndSessions__Migration__RuntimeRole, eventsPlan.role);
  assert.equal(migrationEnvironment.EventsAndSessions__Migration__PlanSha256,
    p1PostgresPlanHash(input.committedPostgresPlan));
  assert.match(migrationEnvironment.ConnectionStrings__EventsAndSessionsMigration,
    /Username=agentweaver-id-p1-postgres-migration/);
  assert.match(runtimeConfig.data.ConnectionStrings__EventsAndSessions,
    /Username=agentweaver-id-events-and-sessions/);
  const migrationBranchStart = input.eventsProgram.indexOf('if (migrate)');
  const runtimeBranchStart = input.eventsProgram.indexOf(
    '// Runtime data access uses the runtime credential', migrationBranchStart);
  assert.ok(migrationBranchStart >= 0 && runtimeBranchStart > migrationBranchStart,
    'migration and runtime connection setup must remain separate branches');
  const migrationBranch = input.eventsProgram.slice(migrationBranchStart, runtimeBranchStart);
  assert.match(migrationBranch, /ReadMigrationConnection\(builder\.Configuration\)/);
  assert.match(migrationBranch, /EventsAndSessionsMigrator\.MigrateAsync/);
  assert.match(migrationBranch, /PlannedRuntimeGrants\.ApplyAndVerifyAsync/,
    'maintenance migration must apply and verify its planned runtime grants');
  const runtimeBranchEnd = input.eventsProgram.indexOf(
    'if (materialContainer is not null)', runtimeBranchStart);
  assert.ok(runtimeBranchEnd > runtimeBranchStart);
  const runtimeBranch = input.eventsProgram.slice(runtimeBranchStart, runtimeBranchEnd);
  assert.match(runtimeBranch, /ReadRuntimeConnection\(builder\.Configuration\)/);
  assert.doesNotMatch(runtimeBranch, /ReadMigrationConnection|PlannedRuntimeGrants/,
    'the Events runtime branch must not use the maintenance credential or grant planner');
  assert.equal(Object.keys(runtimeConfig.data).some(key => key.includes('__Migration__')), false,
    'the runtime ConfigMap must not receive migration credentials or plan settings');

  // Runtime clients use the Events owner over HTTP; neither the host path nor its profile carries Blob secrets.
  for (const [key, value] of Object.entries(runtimeConfig.data)) {
    assert.doesNotMatch(key, credentialKey, `runtime ConfigMap key '${key}' must not provide Blob credentials`);
    assert.doesNotMatch(value, credentialValue,
      `runtime ConfigMap value '${key}' must not contain a Blob key or SAS`);
  }
  assert.doesNotMatch(input.eventsProgram, credentialValue);
  assert.match(input.runtimeMaterialClient, /RuntimeOwnerHttpTransport\.SendAsync/);
  assert.doesNotMatch(input.runtimeMaterialClient,
    /Azure\.Storage\.Blobs|BlobContainerClient|AccountKey|SharedAccessKey|SharedAccessSignature|ConnectionStrings?/i);
}

// Change both copies of the runtime input so a negative case exercises the relevant guard, not fixture drift.
function setContainerUri(input, value) {
  input.runtimeConfigSource.data[containerUriKey] = value;
  resource(input.runtimeResources, 'ConfigMap', runtimeConfigName).data[containerUriKey] = value;
}

test('Events session material source composition rejects broken links', async context => {
  const sources = loadSources();
  assertSourceComposition(sources);

  const mutations = [
    ['omitted configured container', input => {
      delete input.runtimeConfigSource.data[containerUriKey];
      delete resource(input.runtimeResources, 'ConfigMap', runtimeConfigName).data[containerUriKey];
    }],
    ['wrong configured container', input =>
      setContainerUri(input, 'https://agentweaverblob.blob.core.windows.net/other-container')],
    ['SAS query on configured container', input =>
      setContainerUri(input, 'https://agentweaverblob.blob.core.windows.net/session-material?sig=fixture')],
    ['nonstandard Blob endpoint port', input =>
      setContainerUri(input, 'https://agentweaverblob.blob.core.windows.net:444/session-material')],
    ['storage template omits the configured private container', input => {
      input.compiledStorage.variables.containerNames =
        input.compiledStorage.variables.containerNames.filter(name => name !== 'session-material');
    }],
    ['storage deployment disconnects the container-name list', input => {
      const deployment = input.compiledStorage.resources.find(item =>
        item.type === 'Microsoft.Resources/deployments');
      deployment.properties.parameters.containerNames.value = "['platform-artifacts']";
    }],
    ['storage module emits a different container name', input => {
      const deployment = input.compiledStorage.resources.find(item =>
        item.type === 'Microsoft.Resources/deployments');
      const container = deployment.properties.template.resources.find(item =>
        item.type === 'Microsoft.Storage/storageAccounts/blobServices/containers');
      container.name = "[format('{0}/{1}/{2}', parameters('storageAccountName'), 'default', 'other-container')]";
    }],
    ['Events Deployment does not consume the runtime ConfigMap', input => {
      resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
        .spec.template.spec.containers[0].envFrom = [];
    }],
    ['Events Deployment overrides its runtime database connection', input => {
      resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
        .spec.template.spec.containers[0].env.push({
          name: 'ConnectionStrings__EventsAndSessions',
          value: 'Host=fixture;Username=agentweaver-id-p1-postgres-migration',
        });
    }],
    ['Events Deployment overrides its workload identity client ID', input => {
        resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
          .spec.template.spec.containers[0].env.push({
            name: clientIdKey,
            value: 'orchestrator-client-id',
          });
    }],
    ['Events Deployment injects a direct Blob credential', input => {
      resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
        .spec.template.spec.containers[0].env.push({
          name: 'EventsAndSessions__SessionMaterial__AccountKey',
          value: 'fixture',
        });
    }],
    ['Kustomize replaces the wrong ServiceAccount client ID', input => {
      const replacement = input.runtimeKustomization.replacements.find(item =>
        item.source?.name === runtimeConfigName && item.source.fieldPath === `data.${clientIdKey}`);
      replacement.targets[0].select.name = 'orchestrator';
    }],
    ['Events Deployment selects another ServiceAccount', input => {
      resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
        .spec.template.spec.serviceAccountName = 'orchestrator';
    }],
    ['Events workload identity label is missing', input => {
      delete resource(input.runtimeResources, 'Deployment', 'events-and-sessions')
        .spec.template.metadata.labels['azure.workload.identity/use'];
    }],
    ['compiled Blob role targets another identity', input => {
      const role = input.compiledRuntimeIdentities.resources.find(item =>
        item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
      role.properties.parameters.identityResourceId.value = 'orchestrator-identity';
    }],
    ['compiled Blob role principal resolves to another identity', input => {
      const role = input.compiledRuntimeIdentities.resources.find(item =>
        item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
      role.properties.parameters.principalId.value =
        "[reference(resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', 'orchestrator-id'), '2023-01-31').principalId]";
    }],
    ['compiled Blob role targets another storage account', input => {
      const role = input.compiledRuntimeIdentities.resources.find(item =>
        item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
      role.properties.parameters.storageAccountName.value = "'foreignstorage'";
    }],
    ['compiled role assignment uses a disconnected principal parameter', input => {
      const role = input.compiledRuntimeIdentities.resources.find(item =>
        item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
      role.properties.template.resources.find(item =>
        item.type === 'Microsoft.Authorization/roleAssignments')
        .properties.principalId = "[parameters('otherPrincipalId')]";
    }],
    ['compiled Blob role is not container-scoped', input => {
      const role = input.compiledRuntimeIdentities.resources.find(item =>
        item.name === "[format('{0}-events-session-material-blob-role', parameters('namePrefix'))]");
      role.properties.template.resources.find(item =>
        item.type === 'Microsoft.Authorization/roleAssignments').scope = '[resourceId(\'Microsoft.Storage/storageAccounts\', parameters(\'storageAccountName\'))]';
    }],
    ['Events network policy targets another Blob host', input => {
      const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
      const rule = network.spec.egress.find(item => item.toFQDNs?.some(target =>
        target.matchName === 'agentweaverblob.blob.core.windows.net'));
      rule.toFQDNs.find(target =>
        target.matchName === 'agentweaverblob.blob.core.windows.net').matchName =
        'foreign.blob.core.windows.net';
    }],
    ['Events network policy selects another workload', input => {
      resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network')
        .spec.endpointSelector.matchLabels['agentweaver.io/workload'] = 'orchestrator';
    }],
    ['Events DNS rule omits DNS over TCP', input => {
      const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
      const dns = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
        endpoint.matchLabels?.['k8s:k8s-app'] === 'kube-dns'));
      dns.toPorts[0].ports = dns.toPorts[0].ports.filter(port => port.protocol !== 'TCP');
    }],
    ['Events owner egress omits its configured port', input => {
      const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
      const ownerRule = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
        endpoint.matchLabels?.['agentweaver.io/workload'] === 'knowledge'));
      ownerRule.toPorts[0].ports[0].port = '8444';
    }],
    ['Events DNS policy omits the private Blob endpoint', input => {
      const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
      const dns = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
        endpoint.matchLabels?.['k8s:k8s-app'] === 'kube-dns'));
      const privateBlob = dns.toPorts[0].rules.dns.find(item =>
        item.matchName === 'agentweaverblob.privatelink.blob.core.windows.net');
      privateBlob.matchName = 'foreign.privatelink.blob.core.windows.net';
    }],
    ['Events DNS rule omits a configured owner', input => {
      const network = resource(input.runtimeResources, 'CiliumNetworkPolicy', 'events-and-sessions-network');
      const dns = network.spec.egress.find(rule => rule.toEndpoints?.some(endpoint =>
        endpoint.matchLabels?.['k8s:k8s-app'] === 'kube-dns'));
      dns.toPorts[0].rules.dns = dns.toPorts[0].rules.dns.filter(item =>
        item.matchName !== 'knowledge.agentweaver.svc.cluster.local');
    }],
    ['Events migration does not register migration 010', input => {
      input.eventsMigrator = input.eventsMigrator.replace(
        '10 => "Agentweaver.EventsAndSessions.Migrations.010_session_object_inventory.sql"',
        '10 => "Agentweaver.EventsAndSessions.Migrations.010_removed.sql"');
    }],
    ['session-material owner services are not registered', input => {
        input.eventsProgram = input.eventsProgram.replace(
          'builder.Services.AddSessionMaterialOwner();', '');
    }],
    ['Events migrator catalog excludes migration 010', input => {
      input.eventsMigrator = input.eventsMigrator.replace(
        'CurrentSchemaVersion = 10', 'CurrentSchemaVersion = 9');
    }],
    ['Events migration 010 does not create its inventory', input => {
      input.eventsMigration = input.eventsMigration.replace(
        'CREATE TABLE {schema}.session_object_inventory', 'CREATE TABLE {schema}.removed_inventory');
    }],
    ['runtime grant plan omits migration 010', input => {
      for (const plan of [input.committedPostgresPlan, input.generatedPostgresPlan]) {
        plan.eventsVersions = plan.eventsVersions.filter(version => version !== 10);
      }
    }],
    ['runtime grant plan omits inventory permissions', input => {
      for (const plan of [input.committedPostgresPlan, input.generatedPostgresPlan]) {
        const service = plan.services.find(item => item.service === 'events-and-sessions');
        service.tables = service.tables.filter(table => table.table !== 'session_object_inventory');
      }
    }],
    ['Events runtime uses the migration connection', input => {
      input.eventsProgram = input.eventsProgram.replace(
        'ReadRuntimeConnection(builder.Configuration)',
        'ReadMigrationConnection(builder.Configuration)');
    }],
    ['maintenance migration skips planned grant verification', input => {
      input.eventsProgram = input.eventsProgram.replace(
        'await PlannedRuntimeGrants.ApplyAndVerifyAsync(grantConnection, plannedGrants);', '');
    }],
    ['migration Job has a mismatched plan hash', input => {
      const job = resource(input.migrationResources, 'Job', 'events-and-sessions-migration');
      job.spec.template.spec.containers[0].env.find(item =>
        item.name === 'EventsAndSessions__Migration__PlanSha256').value = 'wrong-plan';
    }],
    ['runtime ConfigMap receives a Blob account key', input => {
      const secretKey = 'EventsAndSessions__SessionMaterial__AccountKey';
      input.runtimeConfigSource.data[secretKey] = 'fixture';
      resource(input.runtimeResources, 'ConfigMap', runtimeConfigName).data[secretKey] = 'fixture';
    }],
    ['runtime ConfigMap receives a Blob connection string', input => {
      const connectionKey = 'ConnectionStrings__Blob';
      const connection = 'DefaultEndpointsProtocol=https;AccountName=fixture;EndpointSuffix=core.windows.net';
      input.runtimeConfigSource.data[connectionKey] = connection;
      resource(input.runtimeResources, 'ConfigMap', runtimeConfigName).data[connectionKey] = connection;
    }],
    ['runtime ConfigMap receives migration settings', input => {
      const migrationKey = 'EventsAndSessions__Migration__PlanSha256';
      input.runtimeConfigSource.data[migrationKey] = 'fixture';
      resource(input.runtimeResources, 'ConfigMap', runtimeConfigName).data[migrationKey] = 'fixture';
    }],
    ['AgentHost config receives direct Blob credentials', input => {
      const config = resource(input.runtimeResources, 'ConfigMap', 'agenthost-runtime-v1');
      const settings = JSON.parse(config.data['appsettings.json']);
      settings.AgentHost.SessionMaterial = {
        ContainerUri: 'https://agentweaverblob.blob.core.windows.net/session-material?sig=fixture',
      };
      config.data['appsettings.json'] = JSON.stringify(settings);
    }],
    ['session-material routes are mapped outside the optional container branch', input => {
      input.eventsProgram = input.eventsProgram.replace(
        /if \(materialContainer is not null\)\s*\{\s*app\.MapSessionMaterialEndpoints\(\);\s*app\.MapProducedRunCaptureEndpoints\(\);\s*\}/,
        'app.MapSessionMaterialEndpoints();\napp.MapProducedRunCaptureEndpoints();');
    }],
    ['session-material routes are also mapped unconditionally', input => {
      input.eventsProgram += '\napp.MapSessionMaterialEndpoints();';
    }],
    ['runtime client constructs a direct Blob client', input => {
      input.runtimeMaterialClient += '\nBlobContainerClient AccountKey=fixture';
    }],
  ];

  // Clone before each mutation so one broken edge cannot mask another or alter the passing source baseline.
  for (const [name, mutate] of mutations) {
    await context.test(name, () => {
      const broken = structuredClone(sources);
      mutate(broken);
      assert.throws(() => assertSourceComposition(broken), Error, `${name} must be rejected`);
    });
  }
});
