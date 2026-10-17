// Checks that committed P0 runtime values are sourced and that unknown values stay visibly incomplete.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const namespace = 'agentweaver-v1-p0';
const tenant = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const broker = 'https://agentweaver.6ac31fa7dd86cf00014876f0.eastus2euap.aksapp.io/';
const tokenFile = '/var/run/secrets/azure/tokens/azure-identity-token';
const host = 'aw-v1-p0-pg.postgres.database.azure.com';
const database = 'agentweaver';
const serviceAddress = (name, port = 8443) =>
  `https://${name}.${namespace}.svc.cluster.local:${port}/`;
const manifest = (name) => JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', `${name}.json`), 'utf8'));
const environment = manifest('environment-runtime-config');
const orchestrator = manifest('orchestrator-runtime-config');
const events = manifest('events-and-sessions-runtime-config');
const cilium = manifest('environment-cilium-config');

test('P0 runtime ConfigMaps remain immutable, incomplete, and free of placeholders and credentials', () => {
  for (const [name, resource] of [
    ['environment-runtime-config', environment],
    ['orchestrator-runtime-config', orchestrator],
    ['events-and-sessions-runtime-config', events],
    ['environment-cilium-config', cilium],
  ]) {
    assert.equal(resource.apiVersion, 'v1');
    assert.equal(resource.kind, 'ConfigMap');
    assert.equal(resource.metadata.name, name);
    assert.equal(resource.metadata.namespace, namespace);
    assert.equal(
      resource.metadata.annotations?.['agentweaver.io/runtime-config-status'],
      name === 'environment-cilium-config' ? undefined : 'incomplete');
    assert.equal(resource.immutable, true);
    assert.ok(Object.keys(resource.data).length > 0);
    for (const [key, value] of Object.entries(resource.data)) {
      assert.equal(typeof value, 'string', `${name}: ${key}`);
      assert.ok(value.trim(), `${name}: ${key} must not be blank`);
      assert.doesNotMatch(value, /CHANGEME|TODO|PLACEHOLDER|DUMMY|<[^>]+>/i, `${name}: ${key}`);
      assert.doesNotMatch(key, /(?:^|__)(?:Password|ClientSecret|CredentialValue|SecretKey)(?:__|$)/i,
        `${name}: ${key}`);
      assert.doesNotMatch(value, /(?:^|;)Password\s*=|sig=|SharedAccessSignature=/i, `${name}: ${key}`);
    }
  }
  for (const resource of [environment, orchestrator, events])
    assert.ok(Object.keys(resource.data).every(key => /^[A-Za-z][A-Za-z0-9_]*$/.test(key)));
});

test('P0 PostgreSQL roles, projected identities, and issuer use the exact approved inputs', () => {
  for (const [resource, owner, role, clientId] of [
    [environment, 'Environment', 'aw-v1-p0-id-environment', '21a97456-bb10-4833-bb75-a14cabaad4c1'],
    [orchestrator, 'Orchestrator', 'aw-v1-p0-id-orchestrator', 'b9cfb747-b0f0-42c9-b0a8-31ec3c335a9a'],
    [events, 'EventsAndSessions', 'aw-v1-p0-id-events-and-sessions', '3ab39eda-7389-4b5e-b5b6-232d7671e012'],
  ]) {
    const identity = `${owner}__Database__WorkloadIdentity__`;
    assert.equal(resource.data[`ConnectionStrings__${owner}`],
      `Host=${host};Port=5432;Database=${database};Username=${role};SSL Mode=VerifyFull`);
    assert.equal(resource.data[`${identity}TenantId`], tenant);
    assert.equal(resource.data[`${identity}ClientId`], clientId);
    assert.equal(resource.data[`${identity}TokenFilePath`], tokenFile);
  }
  assert.equal(environment.data.Environment__Authentication__Authority, broker);
  assert.equal(environment.data.Environment__Authentication__Audience, broker);
  for (const resource of [events, orchestrator]) {
    assert.equal(resource.data.Identity__Issuer, broker);
    assert.equal(resource.data.Identity__Audience, broker);
  }
  assert.equal(orchestrator.data.Identity__SecretRedemption__Audience, broker);
});

test('P0 service addresses and source-backed provider defaults are exact', () => {
  const env = environment.data;
  assert.equal(env.ProjectsConfig__BaseAddress, serviceAddress('projects-config'));
  assert.equal(env.Environment__RuntimeBootstrap__OrchestratorOwnerAddress, serviceAddress('orchestrator'));
  assert.equal(env.Environment__RuntimeBootstrap__EventsOwnerAddress, serviceAddress('events-and-sessions'));
  assert.equal(env.Environment__RuntimeBootstrap__BrokerOwnerAddress, serviceAddress('identity-broker', 443));
  assert.equal(env.Environment__Sandbox__AgentSandbox__OptionsSchemaVersion, '2');
  assert.equal(env.Environment__Sandbox__AgentSandbox__Namespace, namespace);
  assert.equal(env.Environment__Sandbox__AgentSandbox__WorkspaceStorageProviderId, 'azure-files-csi');
  assert.equal(env.Environment__Sandbox__AgentSandbox__RuntimeClassName, 'kata-vm-isolation');
  assert.equal(env.Environment__Sandbox__AgentSandbox__ExpectedRuntimeHandler, 'kata');
  assert.equal(env.Environment__Storage__AzureFiles__OptionsSchemaVersion, '1');
  assert.equal(env.Environment__Storage__AzureFiles__Namespace, namespace);
  assert.equal(env.Environment__Storage__AzureFiles__StorageClassName, 'azurefile-csi');
  assert.match(readFileSync(path.join(root, 'services', 'environment',
    'Agentweaver.Environment', 'EnvironmentDbContext.cs'), 'utf8'),
  /public const string Schema = "environment";/);

  const orch = orchestrator.data;
  assert.equal(orch.Orchestrator__Schema, 'orchestrator');
  assert.equal(orch.ProjectsConfig__AuthorizationContext__OwnerBaseAddress, serviceAddress('projects-config'));
  assert.equal(orch.ProjectsConfig__AuthorizationContext__Audience, broker);
  assert.equal(orch.EventsAndSessions__Authorization__OwnerBaseAddress, serviceAddress('events-and-sessions'));
  assert.equal(orch.EventsAndSessions__Authorization__Audience, broker);
  assert.equal(orch.Identity__SecretRedemption__OwnerBaseAddress, serviceAddress('identity-broker', 443));

  const evt = events.data;
  assert.equal(evt.EventsAndSessions__Provider__DatabaseName, database);
  assert.equal(evt.EventsAndSessions__Provider__Schema, 'events_sessions');
  assert.equal(evt.EventsAndSessions__Provider__OptionsSchemaVersion, '1');
  assert.equal(evt.EventsAndSessions__Provider__OptionsRevision, 'native-postgres-v1');
  assert.equal(evt.EventsAndSessions__Provider__PollIntervalMilliseconds, '250');
  assert.equal(evt.EventsAndSessions__Provider__ReferenceRetentionDays, '365');
  assert.equal(evt.EventsAndSessions__Messaging__OptionsSchemaVersion, '1');
  assert.equal(evt.EventsAndSessions__Messaging__OptionsRevision, 'native-postgres-messaging-v1');
  assert.equal(evt.EventsAndSessions__Messaging__ClaimLeaseSeconds, '120');
  assert.equal(evt.EventsAndSessions__Messaging__MaximumMessageLifetimeDays, '1');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__OptionsSchemaVersion, '1');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__RateCard__MeterSource, 'copilot.nano_aiu');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__RateCard__Unit, 'AIC');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__RateCard__NanoUnitsPerUnit, '1000000000');
  assert.equal(evt.EventsAndSessions__Knowledge__BaseAddress, serviceAddress('knowledge'));
  assert.equal(evt.ProjectsConfig__BaseAddress, serviceAddress('projects-config'));
  assert.equal(evt.ProjectsConfig__AuthorizationContext__OwnerBaseAddress, serviceAddress('projects-config'));
  assert.equal(evt.ProjectsConfig__AuthorizationContext__Audience, broker);
  assert.equal(evt.EventsAndSessions__OrchestratorOwner__OwnerBaseAddress, serviceAddress('orchestrator'));
  assert.equal(evt.EventsAndSessions__OrchestratorOwner__Audience, broker);
});

test('nested Cilium selectors match the existing source policies and contain no env-unsafe keys', () => {
  assert.deepEqual(Object.keys(cilium.data), ['appsettings.Production.json']);
  const settings = JSON.parse(cilium.data['appsettings.Production.json']);
  assert.deepEqual(Object.keys(settings), ['Environment']);
  const options = settings.Environment.NetworkPolicy.Cilium;
  assert.deepEqual(Object.keys(options).sort(), ['EndpointSelectors', 'Namespace', 'OptionsSchemaVersion']);
  assert.equal(options.Namespace, namespace);
  assert.equal(options.OptionsSchemaVersion, 1);

  const policies = [
    'environment-agenthost-network.yaml',
    'service-owner-network.yaml',
    path.join('identity-broker', 'egress.yaml'),
  ].map(file => readFileSync(path.join(root, 'deploy', 'k8s', 'base', file), 'utf8')
    .replaceAll('\r\n', '\n'));
  const policyText = policies.join('\n');
  const workloads = new Set([...policyText.matchAll(/^\s*agentweaver\.io\/workload:\s*([a-z0-9-]+)\s*$/gm)]
    .map(match => match[1]));
  assert.deepEqual([...workloads].sort(), [
    'agenthost', 'environment', 'events-and-sessions', 'gateway', 'identity-broker',
    'knowledge', 'mcp', 'orchestrator', 'projects-config',
  ]);
  for (const value of policyText.matchAll(/^\s*(?:namespace|k8s:io\.kubernetes\.pod\.namespace):\s*(\S+)\s*$/gm))
    assert.ok(value[1] === namespace || value[1] === 'kube-system');
  assert.deepEqual(Object.keys(options.EndpointSelectors).sort(),
    [...workloads].map(workload => `${namespace}/${workload}`).sort());
  for (const workload of workloads) {
    assert.deepEqual(options.EndpointSelectors[`${namespace}/${workload}`], {
      'k8s:io.kubernetes.pod.namespace': namespace,
      'agentweaver.io/workload': workload,
    });
    assert.ok(policies.some(text =>
      text.includes(`k8s:io.kubernetes.pod.namespace: ${namespace}\n` +
        `            agentweaver.io/workload: ${workload}`)),
    `release-source policy must pin ${workload} in ${namespace}`);
  }
  assert.equal(environment.data.Environment__NetworkPolicy__Cilium__EndpointSelectors, undefined);
});

test('unknown P0 required values are omitted rather than invented', () => {
  const missingEnvironment = [
    ...['CpuRequest', 'MemoryRequest', 'ReconciliationTimeoutSeconds',
      'PollIntervalMilliseconds', 'StartupBudgets__ScheduledSeconds',
      'StartupBudgets__ImageReadySeconds', 'StartupBudgets__StartedSeconds',
      'StartupBudgets__ConfiguredSeconds', 'StartupBudgets__ReadySeconds',
      'StartupBudgets__TotalSeconds'].map(key => `Environment__Sandbox__AgentSandbox__${key}`),
    ...['MaximumCapacityGiB', 'ProvisioningTimeoutSeconds', 'PollIntervalMilliseconds']
      .map(key => `Environment__Storage__AzureFiles__${key}`),
  ];
  for (const key of missingEnvironment)
    assert.ok(!Object.hasOwn(environment.data, key), key);
  for (const key of ['ContainerImage', 'ContainerImagePlatform', 'ContainerImageCompressedPullBytes',
    'OptionsRevision']) {
    assert.ok(!Object.hasOwn(environment.data, `Environment__Sandbox__AgentSandbox__${key}`));
  }
  for (const key of ['ResourceId', 'ResourceGeneration', 'PlatformPolicyDocuments']) {
    assert.ok(!Object.keys(orchestrator.data).some(name =>
      name === `AgentGovernance__Policy__${key}` ||
      name.startsWith(`AgentGovernance__Policy__${key}__`)), key);
  }
  for (const key of [
    'EventsAndSessions__Provider__ResourceId',
    'EventsAndSessions__Provider__ResourceGeneration',
    'EventsAndSessions__SessionMaterial__ContainerUri',
  ]) {
    assert.ok(!Object.hasOwn(events.data, key), key);
  }
  assert.equal(events.data.EventsAndSessions__Cost__Copilot__OptionsSchemaVersion, '1');
  for (const key of [
    'ResourceId',
    'ResourceGeneration',
    'RateCard__Id',
    'RateCard__Version',
  ]) {
    assert.ok(!Object.hasOwn(events.data, `EventsAndSessions__Cost__Copilot__${key}`), key);
  }
  assert.ok(!Object.keys(events.data).some(key =>
    key.startsWith('EventsAndSessions__Cost__Copilot__RateCard__ModelMultipliers__')));
  assert.ok(!Object.values(events.data).includes(
    '/subscriptions/90ab3701-83f0-4ba1-a90a-f2e68683adab/resourceGroups/aw-v1-p0/providers/Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg'));
});
