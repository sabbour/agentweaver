// Checks that committed P0 runtime values are sourced and that unknown values stay visibly incomplete.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { SERVICE_NAMES, SERVICE_NETWORK_POLICY_NAMES, validateKustomizeInstall } from '../deploy.mjs';
import { parseKustomizeResources } from '../kustomize-runtime.mjs';
import { parse, parseAllDocuments } from 'yaml';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const namespace = 'agentweaver';
const tenant = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const managedDomain = '6ad4aaa13923a30001c83518.eastus2euap.aksapp.io';
const brokerHost = `agentweaver.${managedDomain}`;
const broker = `https://${brokerHost}/`;
const portalOrigin = `https://portal.${managedDomain}`;
const p1InitializationBlockedReason =
  'P1 initialization is blocked because the initializer requires an already-ready 127.0.0.1:15432 connection, but the documented bootstrap removes its temporary tunnel before returning and no documented continuation keeps or shares it.';
const postgresBootstrapBlockedReason =
  'Guarded PostgreSQL bootstrap did not run because the preflight resource-inventory guard rejected an unexpected type/name before Azure what-if; the Agentweaver database and roles remain uninitialized.';
const incompleteProvisioningReason =
  `${p1InitializationBlockedReason} ${postgresBootstrapBlockedReason}`;
const tokenFile = '/var/run/secrets/azure/tokens/azure-identity-token';
const host = 'agentweaver-pg.postgres.database.azure.com';
const database = 'agentweaver';
const runtimeActionIds = [
  'model.turn',
  'tool.read',
  'tool.write',
  'exec.shell',
  'exec.read',
  'exec.write',
  'exec.stop',
];
const approvedPlatformPolicy = [
  'apiVersion: governance.toolkit/v1',
  'version: "1.0"',
  'name: p1-platform-baseline',
  'scope: global',
  'default_action: deny',
  'rules:',
  ...runtimeActionIds.flatMap(actionId => {
    const ruleName = actionId.replaceAll('.', '-');
    return [
      `  - name: allow-${ruleName}`,
      `    condition: "action_id == '${actionId}'"`,
      '    action: allow',
      '    priority: 100',
    ];
  }),
  '',
].join('\n');
const serviceAddress = (name, port = 8443) =>
  `https://${name}.${namespace}.svc.cluster.local:${port}/`;
const manifest = (name) => JSON.parse(readFileSync(
  path.join(root, 'deploy', 'k8s', 'runtime-configs', `${name}.json`), 'utf8'));
const identityBroker = manifest('identity-broker-runtime-config');
const environment = manifest('environment-runtime-config');
const orchestrator = manifest('orchestrator-runtime-config');
const events = manifest('events-and-sessions-runtime-config');
const cilium = manifest('environment-cilium-config');
const gateway = manifest('gateway-runtime-config');
const mcp = manifest('mcp-runtime-config');
const web = manifest('web-runtime-config');
const knowledge = manifest('knowledge-runtime-config');
const projects = manifest('projects-config-runtime-config');

test('target-specific acceptance and migration ServiceAccounts use verified client IDs', () => {
  for (const [relativePath, name, clientId] of [
    ['acceptance/foundation-probe/serviceaccount.yaml', 'foundation-probe', '30efe293-b1c8-4cc7-93e3-913f14ed18dc'],
    ['migrations/identity-broker/serviceaccount.yaml', 'identity-broker-migration', '0cf2e9cd-6b0a-45cd-bbaf-fc674a194781'],
  ]) {
    const serviceAccount = parse(readFileSync(path.join(root, 'deploy', 'k8s', relativePath), 'utf8'));
    assert.equal(serviceAccount.metadata.name, name);
    assert.equal(serviceAccount.metadata.annotations['azure.workload.identity/client-id'], clientId);
  }
});

test('P0 runtime ConfigMaps use immutable snapshots except for the operator-updatable Events map', () => {
  for (const [name, resource] of [
    ['environment-runtime-config', environment],
    ['orchestrator-runtime-config', orchestrator],
    ['events-and-sessions-runtime-config', events],
    ['identity-broker-runtime-config-v2', identityBroker],
    ['projects-config-runtime-config', projects],
    ['environment-cilium-config', cilium],
  ]) {
    assert.equal(resource.apiVersion, 'v1');
    assert.equal(resource.kind, 'ConfigMap');
    assert.equal(resource.metadata.name, name);
    assert.equal(resource.metadata.namespace, namespace);
    assert.equal(resource.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
    assert.equal(resource.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'], undefined);
    assert.equal(resource.immutable, name !== 'events-and-sessions-runtime-config');
    assert.ok(Object.keys(resource.data).length > 0);
    for (const [key, value] of Object.entries(resource.data)) {
      assert.equal(typeof value, 'string', `${name}: ${key}`);
      assert.ok(value.trim(), `${name}: ${key} must not be blank`);
      assert.doesNotMatch(value, /CHANGEME|TODO|PLACEHOLDER|DUMMY|<[^>]+>/i,
        `${name}: ${key} must contain no unresolved runtime inputs`);
      assert.doesNotMatch(key, /(?:^|__)(?:Password|ClientSecret|CredentialValue|SecretKey)(?:__|$)/i,
        `${name}: ${key}`);
      assert.doesNotMatch(value, /(?:^|;)Password\s*=|sig=|SharedAccessSignature=/i, `${name}: ${key}`);
    }
  }
  for (const resource of [environment, orchestrator, events])
    assert.ok(Object.keys(resource.data).every(key => /^[A-Za-z][A-Za-z0-9_.-]*$/.test(key)));
  assert.equal(cilium.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
});

test('Identity Broker snapshot keeps the acceptance client and adds the versioned public Web client', () => {
  assert.deepEqual(Object.keys(identityBroker.data).sort(), [
    'ConnectionStrings__IdentityBroker',
    'IdentityBroker__Clients__0__ClientId',
    'IdentityBroker__Clients__0__DisplayName',
    'IdentityBroker__Clients__0__RedirectUris__0',
    'IdentityBroker__Clients__0__Resources__0',
    'IdentityBroker__Clients__0__Scopes__0',
    'IdentityBroker__Clients__0__Scopes__1',
    'IdentityBroker__Clients__0__Scopes__2',
    'IdentityBroker__Clients__0__Scopes__3',
    'IdentityBroker__Clients__0__Scopes__4',
    'IdentityBroker__Clients__0__Type',
    'IdentityBroker__Clients__1__ClientId',
    'IdentityBroker__Clients__1__DisplayName',
    'IdentityBroker__Clients__1__RedirectUris__0',
    'IdentityBroker__Clients__1__Resources__0',
    'IdentityBroker__Clients__1__Scopes__0',
    'IdentityBroker__Clients__1__Scopes__1',
    'IdentityBroker__Clients__1__Scopes__2',
    'IdentityBroker__Clients__1__Scopes__3',
    'IdentityBroker__Clients__1__Scopes__4',
    'IdentityBroker__Clients__1__Scopes__5',
    'IdentityBroker__Clients__1__Scopes__6',
    'IdentityBroker__Clients__1__Type',
    'IdentityBroker__DataProtectionKeyPath',
    'IdentityBroker__ExternalProvider__Authority',
    'IdentityBroker__ExternalProvider__ClientId',
    'IdentityBroker__Issuer',
    'IdentityBroker__SecretRedemption__Audience',
    'IdentityBroker__SecretRedemption__VaultHost',
    'IdentityBroker__SecretRedemption__VaultUri',
    'IdentityBroker__SecretRedemption__WorkloadIdentityClientId',
    'IdentityBroker__SecretRedemption__WorkloadIdentityTenantId',
    'IdentityBroker__SecretRedemption__WorkloadIdentityTokenFilePath',
    'IdentityBroker__WebOrigin',
  ].sort());
  assert.equal(identityBroker.metadata.name, 'identity-broker-runtime-config-v2');
  assert.equal(identityBroker.immutable, true);
  assert.equal(identityBroker.metadata.labels?.['agentweaver.io/acceptance-run'], undefined);
  assert.equal(identityBroker.data.IdentityBroker__Clients__0__Type, 'Public');
  assert.equal(identityBroker.data.IdentityBroker__Clients__0__RedirectUris__0,
    'http://127.0.0.1:49187/callback');
  assert.equal(identityBroker.data.IdentityBroker__Clients__0__Scopes__3, 'api.read');
  assert.equal(identityBroker.data.IdentityBroker__Clients__0__Scopes__4, 'offline_access');
  assert.equal(identityBroker.data.IdentityBroker__ExternalProvider__ClientId,
    '3b682024-2c59-4497-aff2-fc6335d12d97');
  assert.equal(identityBroker.data.IdentityBroker__Clients__1__ClientId, 'agentweaver-web');
  assert.equal(identityBroker.data.IdentityBroker__Clients__1__DisplayName, 'Agentweaver Web');
  assert.equal(identityBroker.data.IdentityBroker__Clients__1__Type, 'Public');
  assert.equal(identityBroker.data.IdentityBroker__Clients__1__RedirectUris__0,
    `${portalOrigin}/auth/callback`);
  assert.equal(identityBroker.data.IdentityBroker__WebOrigin, portalOrigin);
  assert.equal(identityBroker.data.IdentityBroker__SecretRedemption__WorkloadIdentityClientId,
    '4ee7e4e8-f5c8-445a-9fa4-5cf13afdb067');
  assert.equal(identityBroker.data.IdentityBroker__SecretRedemption__VaultHost,
    'agentweaver-kv-v1.vault.azure.net');
  assert.equal(identityBroker.data.IdentityBroker__SecretRedemption__VaultUri,
    'https://agentweaver-kv-v1.vault.azure.net/');
  assert.equal(identityBroker.data.IdentityBroker__Clients__1__Resources__0, broker);
  assert.deepEqual(Array.from({ length: 7 }, (_, index) =>
    identityBroker.data[`IdentityBroker__Clients__1__Scopes__${index}`]),
  ['openid', 'profile', 'email', 'api.read', 'offline_access', 'projects.admin', 'projects.orchestrator']);
  assert.equal(web.data.VITE_OAUTH_CLIENT_ID, 'agentweaver-web');
  assert.equal(web.data.VITE_OAUTH_SCOPES,
    'openid profile email api.read offline_access projects.admin projects.orchestrator');
  assert.notEqual(web.data.VITE_OAUTH_CLIENT_ID,
    identityBroker.data.IdentityBroker__Clients__0__ClientId);
});

test('Projects Config privileged setup contains only the approved issuer, tenant, and subject', () => {
  assert.deepEqual(
    Object.fromEntries(
      Object.entries(projects.data)
        .filter(([key]) => key.startsWith('ProjectsConfig__PrivilegedSetup__'))
        .sort(([left], [right]) => left.localeCompare(right)),
    ),
    {
      ProjectsConfig__PrivilegedSetup__ApprovedIssuer: broker,
      ProjectsConfig__PrivilegedSetup__ApprovedSubject: 'c4279823-a7c7-477e-982e-f4145bb0a83d',
      ProjectsConfig__PrivilegedSetup__ApprovedTenantId: tenant,
    },
  );
});

test('P0 PostgreSQL roles, projected identity keys, and issuer use the exact pre-provisioning inputs', () => {
  for (const [resource, owner, role, clientId] of [
    [environment, 'Environment', 'agentweaver-id-environment', '47e81803-63aa-4311-8990-98beef468a5c'],
    [orchestrator, 'Orchestrator', 'agentweaver-id-orchestrator', '039d7849-9fa0-4488-83ed-c52ec55007ea'],
    [events, 'EventsAndSessions', 'agentweaver-id-events-and-sessions', '23083988-bf80-45c7-84cf-891eb5ed4736'],
    [knowledge, 'Knowledge', 'agentweaver-id-knowledge', 'faadfa78-1bfc-4a85-a0e5-d9a913630a0f'],
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

test('Portal route and Web origin keep exact hostnames on the shared managed-domain listener', () => {
  const rootKustomization = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'kustomization.yaml'), 'utf8'));
  const route = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'web-application-p0.yaml'), 'utf8'));
  const gatewaySource = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'routing', 'identity-broker',
    'gateway.yaml'), 'utf8'));
  const portalHostname = new URL(portalOrigin).hostname;
  assert.ok(rootKustomization.resources.includes('routing/identity-broker/web-application-p0.yaml'));
  assert.deepEqual(route.spec.parentRefs, [{
    name: 'identity-broker', namespace: 'agentweaver-gateway', sectionName: 'https',
  }]);
  assert.deepEqual(route.spec.hostnames, [portalHostname]);
  assert.deepEqual(route.spec.rules[0].backendRefs, [{ name: 'web', port: 8080 }]);
  assert.equal(gatewaySource.metadata.namespace, 'agentweaver-gateway');
  assert.deepEqual(gatewaySource.spec.listeners.map(({ name, hostname }) => ({ name, hostname })), [{
    name: 'https', hostname: 'CHANGEME-MANAGED-WILDCARD-HOST',
  }]);
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
  assert.equal(env.Environment__Sandbox__AgentSandbox__CpuRequest, '1');
  assert.equal(env.Environment__Sandbox__AgentSandbox__MemoryRequest, '2Gi');
  assert.match(readFileSync(path.join(root, 'deploy', 'k8s', 'base', 'limitrange.yaml'), 'utf8')
    .replaceAll('\r\n', '\n'), /max:\n\s+cpu:\s+"1"\n\s+memory:\s+2Gi/);
  assert.equal(env.Environment__Sandbox__AgentSandbox__ReconciliationTimeoutSeconds, '600');
  assert.equal(env.Environment__Sandbox__AgentSandbox__PollIntervalMilliseconds, '250');
  assert.deepEqual({
    scheduled: env.Environment__Sandbox__AgentSandbox__StartupBudgets__ScheduledSeconds,
    imageReady: env.Environment__Sandbox__AgentSandbox__StartupBudgets__ImageReadySeconds,
    started: env.Environment__Sandbox__AgentSandbox__StartupBudgets__StartedSeconds,
    configured: env.Environment__Sandbox__AgentSandbox__StartupBudgets__ConfiguredSeconds,
    ready: env.Environment__Sandbox__AgentSandbox__StartupBudgets__ReadySeconds,
    total: env.Environment__Sandbox__AgentSandbox__StartupBudgets__TotalSeconds,
  }, {
    scheduled: '30', imageReady: '90', started: '30', configured: '30', ready: '30', total: '180',
  });
  assert.equal(env.Environment__Storage__AzureFiles__OptionsSchemaVersion, '1');
  assert.equal(env.Environment__Storage__AzureFiles__Namespace, namespace);
  assert.equal(env.Environment__Storage__AzureFiles__StorageClassName, 'azurefile-csi');
  assert.equal(env.Environment__Storage__AzureFiles__MaximumCapacityGiB, '100');
  assert.equal(env.Environment__Storage__AzureFiles__ProvisioningTimeoutSeconds, '600');
  assert.equal(env.Environment__Storage__AzureFiles__PollIntervalMilliseconds, '250');

  const policy = orchestrator.data;
  assert.equal(policy.AgentGovernance__Policy__ResourceId, 'agt-policy:p1-v1.0.0');
  assert.equal(policy.AgentGovernance__Policy__ResourceGeneration, '1');
  assert.equal(policy.AgentGovernance__Policy__OptionsRevision, 'p1-v1.0.0');
  assert.equal(policy.AgentGovernance__Policy__PlatformPolicyDocuments__0, approvedPlatformPolicy);
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
  assert.equal(evt.EventsAndSessions__Provider__ResourceId, 'events-sessions-provider:p1-v1.0.0');
  assert.equal(evt.EventsAndSessions__Provider__ResourceGeneration, '1');
  assert.equal(evt.EventsAndSessions__Provider__PollIntervalMilliseconds, '250');
  assert.equal(evt.EventsAndSessions__Provider__ReferenceRetentionDays, '365');
  assert.equal(evt.EventsAndSessions__Messaging__OptionsSchemaVersion, '1');
  assert.equal(evt.EventsAndSessions__Messaging__OptionsRevision, 'native-postgres-messaging-v1');
  assert.equal(evt.EventsAndSessions__Messaging__ClaimLeaseSeconds, '120');
  assert.equal(evt.EventsAndSessions__Messaging__MaximumMessageLifetimeDays, '1');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__OptionsSchemaVersion, '2');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__ResourceId, 'copilot-cost:p1-v1.0.0');
  assert.equal(evt.EventsAndSessions__Cost__Copilot__ResourceGeneration, '1');
  const copilotRateCard = JSON.parse(evt.EventsAndSessions__Cost__Copilot__RateCard__Json);
  assert.equal(copilotRateCard.schemaVersion, 1);
  assert.equal(copilotRateCard.id, 'github-copilot-published');
  assert.equal(copilotRateCard.version, '2026-10-17');
  assert.equal(Object.keys(copilotRateCard.modelTokenRates).length, 33);
  assert.equal(copilotRateCard.modelTokenRates['gpt-5.6-luna'].longContextThresholdTokens, 200000);
  assert.equal(copilotRateCard.modelTokenRates['claude-haiku-5.5'].longContextThresholdTokens, 100000);
  assert.equal(copilotRateCard.modelTokenRates['gpt-5-mini'].longContext, null);
  assert.equal(evt.EventsAndSessions__Knowledge__BaseAddress, serviceAddress('knowledge'));
  assert.equal(evt.ProjectsConfig__BaseAddress, serviceAddress('projects-config'));
  assert.equal(evt.ProjectsConfig__AuthorizationContext__OwnerBaseAddress, serviceAddress('projects-config'));
  assert.equal(evt.ProjectsConfig__AuthorizationContext__Audience, broker);
  assert.equal(evt.EventsAndSessions__OrchestratorOwner__OwnerBaseAddress, serviceAddress('orchestrator'));
  assert.equal(evt.EventsAndSessions__OrchestratorOwner__Audience, broker);
  assert.equal(evt.EventsAndSessions__SessionMaterial__ContainerUri,
    'https://agentweaverblob.blob.core.windows.net/session-material');

  const modelBindings = {
    reasoning: 'gpt-6.1-sol',
    coding: 'gpt-6-luna',
    'gpt-6.1-sol': 'gpt-6.1-sol',
    'gpt-6-luna': 'gpt-6-luna',
  };
  assert.equal(evt.AgentHost__ModelBindingsRevision, 'p1-v1.0.1');
  for (const [reference, modelId] of Object.entries(modelBindings)) {
    assert.equal(evt[`AgentHost__ModelBindings__${reference}__ModelId`], modelId);
    assert.equal(evt[`AgentHost__ModelBindings__${reference}__SourceMode`], 'HostedCopilot');
  }
  const agentHostManifest = parse(readFileSync(
    path.join(root, 'deploy', 'k8s', 'profiles', 'agenthost', 'runtime-config.example.yaml'), 'utf8'));
  assert.equal(agentHostManifest.immutable, true);
  assert.equal(agentHostManifest.metadata.annotations?.['agentweaver.io/runtime-config-status'], 'incomplete');
  assert.equal(agentHostManifest.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    `${incompleteProvisioningReason.slice(0, -1)}; AgentHost.ConfigureEndpoint and Authentication.Audience require the exact per-Sandbox HTTPS service endpoint; AgentHost.MaximumPendingTurns has no approved P0 value; Environment__Sandbox__AgentSandbox__AgentHost__TlsSecretName has no approved existing Secret name.`);
  const agentHost = JSON.parse(agentHostManifest.data['appsettings.json']).AgentHost;
  assert.equal(agentHost.ModelBindingsRevision, 'p1-v1.0.1');
  assert.deepEqual(agentHost.ModelBindings, {
    reasoning: { ModelId: 'gpt-6.1-sol', SourceMode: 'HostedCopilot' },
    coding: { ModelId: 'gpt-6-luna', SourceMode: 'HostedCopilot' },
    'gpt-6.1-sol': { ModelId: 'gpt-6.1-sol', SourceMode: 'HostedCopilot' },
    'gpt-6-luna': { ModelId: 'gpt-6-luna', SourceMode: 'HostedCopilot' },
  });
  assert.equal(agentHost.ConfigureEndpoint, '');
  assert.equal(agentHost.Authentication.Audience, '');
  assert.equal(agentHost.MaximumPendingTurns, undefined);
  assert.equal(agentHost.BrokerAddress, serviceAddress('identity-broker', 443));
  assert.equal(agentHost.OrchestratorAddress, serviceAddress('orchestrator'));
  assert.equal(agentHost.EnvironmentAddress, serviceAddress('environment'));
  assert.equal(agentHost.EventsAddress, serviceAddress('events-and-sessions'));
  assert.equal(agentHost.Authentication.Authority, broker);
  assert.equal(agentHost.Image.Platform, 'linux/amd64');
  assert.equal(environment.data['Environment__Sandbox__AgentSandbox__AgentHost__ConfigurationMapName'], undefined);
  assert.equal(environment.data['Environment__Sandbox__AgentSandbox__AgentHost__TlsSecretName'], undefined);
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
    assert.ok([namespace, 'kube-system', 'agentweaver-gateway'].includes(value[1]));
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

test('P0 values use approved labels and omit unknown resource identities', () => {
  for (const key of ['ContainerImage', 'ContainerImagePlatform', 'ContainerImageCompressedPullBytes',
    'OptionsRevision']) {
    assert.ok(!Object.hasOwn(environment.data, `Environment__Sandbox__AgentSandbox__${key}`));
  }
  assert.equal(events.data.EventsAndSessions__Provider__ResourceId, 'events-sessions-provider:p1-v1.0.0');
  assert.equal(events.data.EventsAndSessions__Provider__ResourceGeneration, '1');
  assert.equal(events.data.EventsAndSessions__Cost__Copilot__OptionsSchemaVersion, '2');
  assert.equal(events.data.EventsAndSessions__Cost__Copilot__ResourceId, 'copilot-cost:p1-v1.0.0');
  assert.equal(events.data.EventsAndSessions__Cost__Copilot__ResourceGeneration, '1');
  assert.ok(!Object.keys(events.data).some(key =>
    key.startsWith('EventsAndSessions__Cost__Copilot__RateCard__ModelMultipliers__')));
  assert.equal(events.data.AgentHost__ModelBindingsRevision, 'p1-v1.0.1');
  assert.equal(events.data.AgentHost__ModelBindings__reasoning__ModelId, 'gpt-6.1-sol');
  assert.equal(events.data.AgentHost__ModelBindings__coding__ModelId, 'gpt-6-luna');
  assert.equal(events.data['AgentHost__ModelBindings__gpt-6.1-sol__ModelId'], 'gpt-6.1-sol');
  assert.equal(events.data['AgentHost__ModelBindings__gpt-6-luna__ModelId'], 'gpt-6-luna');
  assert.ok(!Object.values(events.data).includes(
    '/subscriptions/90ab3701-83f0-4ba1-a90a-f2e68683adab/resourceGroups/agentweaver/providers/Microsoft.DBforPostgreSQL/flexibleServers/agentweaver-pg'));
});

// Runtime maps must match their service contracts without unresolved inputs.
test('Gateway, Web, Knowledge, and Projects ConfigMaps contain complete sourced values', () => {
  const newMaps = [
    ['gateway-runtime-config', gateway],
    ['mcp-runtime-config', mcp],
    ['web-runtime-config', web],
    ['knowledge-runtime-config', knowledge],
    ['projects-config-runtime-config', projects],
  ];
  for (const [name, resource] of newMaps) {
    assert.equal(resource.apiVersion, 'v1');
    assert.equal(resource.kind, 'ConfigMap');
    assert.equal(resource.metadata.name, name);
    assert.equal(resource.metadata.namespace, namespace);
    assert.equal(resource.immutable, true);
    assert.ok(Object.keys(resource.data).length > 0);
    for (const [key, value] of Object.entries(resource.data)) {
      assert.equal(typeof value, 'string', `${name}: ${key}`);
      assert.doesNotMatch(value, /CHANGEME|TODO|PLACEHOLDER|DUMMY|<[^>]+>/i,
        `${name}: ${key} must contain no unresolved runtime inputs`);
      assert.doesNotMatch(key, /(?:^|__)(?:Password|ClientSecret|CredentialValue|SecretKey)(?:__|$)/i,
        `${name}: ${key}`);
      assert.doesNotMatch(value, /(?:^|;)Password\s*=|sig=|SharedAccessSignature=/i, `${name}: ${key}`);
    }
  }

  assert.deepEqual(Object.keys(gateway.data).sort(), [
    'Gateway__Owners__Events',
    'Gateway__Owners__IdentityBrokerAddress',
    'Gateway__Owners__Knowledge',
    'Gateway__Owners__Orchestrator',
    'Gateway__Owners__Projects',
    'Gateway__WebOrigin',
    'Identity__Audience',
    'Identity__Issuer',
  ].sort());
  assert.equal(gateway.data.Identity__Issuer, broker);
  assert.equal(gateway.data.Identity__Audience, broker);
  assert.equal(gateway.data.Gateway__Owners__Projects, serviceAddress('projects-config'));
  assert.equal(gateway.data.Gateway__Owners__Orchestrator, serviceAddress('orchestrator'));
  assert.equal(gateway.data.Gateway__Owners__Knowledge, serviceAddress('knowledge'));
  assert.equal(gateway.data.Gateway__Owners__Events, serviceAddress('events-and-sessions'));
  assert.equal(gateway.data.Gateway__Owners__IdentityBrokerAddress,
    serviceAddress('identity-broker', 443));
  assert.equal(gateway.data.Gateway__WebOrigin, portalOrigin);
  assert.equal(gateway.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
  assert.equal(gateway.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    undefined);

  assert.deepEqual(Object.keys(mcp.data).sort(), [
    'Gateway__BaseAddress', 'Identity__Audience', 'Identity__Issuer',
  ].sort());
  assert.deepEqual(mcp.data, {
    Identity__Issuer: broker,
    Identity__Audience: broker,
    Gateway__BaseAddress: serviceAddress('gateway'),
  });
  assert.equal(mcp.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
  assert.equal(mcp.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    undefined);

  assert.deepEqual(Object.keys(web.data).sort(), [
    'VITE_GATEWAY_URL', 'VITE_IDENTITY_BROKER_ISSUER', 'VITE_IDENTITY_BROKER_URL',
    'VITE_OAUTH_CLIENT_ID', 'VITE_OAUTH_SCOPES',
  ].sort());
  assert.equal(web.data.VITE_GATEWAY_URL, '/api/v1');
  assert.equal(web.data.VITE_IDENTITY_BROKER_URL, broker);
  assert.equal(web.data.VITE_IDENTITY_BROKER_ISSUER, broker);
  assert.equal(web.data.VITE_OAUTH_CLIENT_ID, 'agentweaver-web');
  assert.equal(web.data.VITE_OAUTH_SCOPES,
    'openid profile email api.read offline_access projects.admin projects.orchestrator');
  assert.equal(web.data.VITE_OAUTH_REDIRECT_URI, undefined);
  assert.equal(web.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
  assert.equal(web.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    undefined);

  assert.deepEqual(Object.keys(knowledge.data).sort(), [
    'ConnectionStrings__Knowledge',
    'Identity__Audience',
    'Identity__Issuer',
    'Knowledge__Database__WorkloadIdentity__ClientId',
    'Knowledge__Database__WorkloadIdentity__TenantId',
    'Knowledge__Database__WorkloadIdentity__TokenFilePath',
    'Knowledge__Events__Audience',
    'Knowledge__Events__BaseAddress',
    'Knowledge__Provider__DatabaseName',
    'Knowledge__Provider__OptionsRevision',
    'Knowledge__Provider__OptionsSchemaVersion',
    'Knowledge__Provider__ResourceGeneration',
    'Knowledge__Provider__ResourceId',
    'Knowledge__Provider__Schema',
    'ProjectsConfig__BaseAddress',
  ].sort());
  assert.equal(knowledge.data.ConnectionStrings__Knowledge,
    `Host=${host};Port=5432;Database=${database};Username=agentweaver-id-knowledge;SSL Mode=VerifyFull`);
  assert.equal(knowledge.data.Knowledge__Database__WorkloadIdentity__TenantId, tenant);
  assert.equal(knowledge.data.Knowledge__Database__WorkloadIdentity__ClientId,
    'faadfa78-1bfc-4a85-a0e5-d9a913630a0f');
  assert.equal(knowledge.data.Knowledge__Database__WorkloadIdentity__TokenFilePath, tokenFile);
  assert.equal(knowledge.data.Identity__Issuer, broker);
  assert.equal(knowledge.data.Identity__Audience, broker);
  assert.equal(knowledge.data.ProjectsConfig__BaseAddress, serviceAddress('projects-config'));
  assert.equal(knowledge.data.Knowledge__Events__BaseAddress, serviceAddress('events-and-sessions'));
  assert.equal(knowledge.data.Knowledge__Events__Audience, broker);
  assert.equal(knowledge.data.Knowledge__Provider__ResourceId, 'agentweaver.knowledge');
  assert.equal(knowledge.data.Knowledge__Provider__DatabaseName, database);
  assert.equal(knowledge.data.Knowledge__Provider__ResourceGeneration, '1');
  assert.equal(knowledge.data.Knowledge__Provider__Schema, 'knowledge');
  assert.equal(knowledge.data.Knowledge__Provider__OptionsRevision, 'native-postgres-memory-v1');
  assert.equal(knowledge.data.Knowledge__Provider__OptionsSchemaVersion, '1');
  assert.equal(knowledge.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
  assert.equal(knowledge.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    undefined);

  assert.deepEqual(projects.data, {
    ConnectionStrings__ProjectsConfig:
      `Host=${host};Port=5432;Database=${database};Username=agentweaver-id-projects-config;SSL Mode=VerifyFull`,
    ProjectsConfig__Database__WorkloadIdentity__TenantId: tenant,
    ProjectsConfig__Database__WorkloadIdentity__ClientId: 'b65f060f-364b-45ea-adf4-f704c176f33e',
    ProjectsConfig__Database__WorkloadIdentity__TokenFilePath: tokenFile,
    ProjectsConfig__Authentication__Authority: broker,
    ProjectsConfig__Authentication__Audience: broker,
    ProjectsConfig__PrivilegedSetup__ApprovedIssuer: broker,
    ProjectsConfig__PrivilegedSetup__ApprovedTenantId: tenant,
    ProjectsConfig__PrivilegedSetup__ApprovedSubject: 'c4279823-a7c7-477e-982e-f4145bb0a83d',
  });
  assert.equal(projects.metadata.annotations?.['agentweaver.io/runtime-config-status'], undefined);
  assert.equal(projects.metadata.annotations?.['agentweaver.io/runtime-config-incomplete-reason'],
    undefined);
  assert.equal(Object.keys(projects.data).some(key => key.includes('BrokerRunBinding')), false);
});

// Keep the base policies in Kustomize and bound external names to their required ports.
test('new Events and Projects Cilium policies admit only source-backed callers and exact destinations', () => {
  const readPolicy = name => readFileSync(
    path.join(root, 'deploy', 'k8s', 'base', `${name}.yaml`), 'utf8').replaceAll('\r\n', '\n');
  const base = readFileSync(path.join(root, 'deploy', 'k8s', 'base', 'kustomization.yaml'), 'utf8');
  const policyContracts = [
    {
      name: 'events-and-sessions',
      callers: ['agenthost', 'orchestrator', 'gateway', 'knowledge', 'environment'],
      owners: ['knowledge', 'projects-config', 'orchestrator'],
      fqdns: [
        brokerHost,
        'login.microsoftonline.com',
        'agentweaverblob.blob.core.windows.net',
        'agentweaverblob.privatelink.blob.core.windows.net',
        'agentweaver-pg.postgres.database.azure.com',
        'agentweaver-pg.privatelink.postgres.database.azure.com',
        'knowledge.agentweaver.svc.cluster.local',
        'projects-config.agentweaver.svc.cluster.local',
        'orchestrator.agentweaver.svc.cluster.local',
      ],
      fqdnRules: [
        {
          hosts: [
            brokerHost,
            'login.microsoftonline.com',
            'agentweaverblob.blob.core.windows.net',
          ],
          port: '443',
        },
        { hosts: ['agentweaver-pg.postgres.database.azure.com'], port: '5432' },
      ],
    },
    {
      name: 'projects-config',
      callers: ['identity-broker', 'orchestrator', 'gateway', 'events-and-sessions',
        'knowledge', 'environment'],
      owners: [],
      fqdns: [
        brokerHost,
        'login.microsoftonline.com',
        'api.github.com',
        'raw.githubusercontent.com',
        'agentweaver-pg.postgres.database.azure.com',
        'agentweaver-pg.privatelink.postgres.database.azure.com',
      ],
      fqdnRules: [
        {
          hosts: [
            brokerHost,
            'login.microsoftonline.com',
            'api.github.com',
            'raw.githubusercontent.com',
          ],
          port: '443',
        },
        { hosts: ['agentweaver-pg.postgres.database.azure.com'], port: '5432' },
      ],
    },
  ];

  for (const contract of policyContracts) {
    const policy = readPolicy(`${contract.name}-network`);
    assert.match(base, new RegExp(`- ${contract.name}-network\\.yaml`));
    assert.match(policy, new RegExp(`name: ${contract.name}-network`));
    assert.match(policy, new RegExp(`namespace: ${namespace}`));
    assert.match(policy, new RegExp(`agentweaver.io/workload: ${contract.name}`));
    const ingress = policy.split('  ingress:\n')[1].split('  egress:\n')[0];
    const egress = policy.split('  egress:\n')[1];
    const callers = [...ingress.matchAll(/agentweaver\.io\/workload:\s*([a-z0-9-]+)/g)]
      .map(match => match[1]);
    assert.deepEqual(callers.sort(), [...contract.callers].sort(), `${contract.name} ingress callers`);
    assert.equal((ingress.match(/k8s:io\.kubernetes\.pod\.namespace: agentweaver/g) ?? []).length,
      contract.callers.length);
    const owners = [...egress.matchAll(/agentweaver\.io\/workload:\s*([a-z0-9-]+)/g)]
      .map(match => match[1]);
    assert.deepEqual(owners.sort(), [...contract.owners].sort(), `${contract.name} egress owners`);
    const fqdns = [...new Set([...policy.matchAll(/matchName:\s*([A-Za-z0-9.-]+)/g)]
      .map(match => match[1]))].sort();
    assert.deepEqual(fqdns, [...contract.fqdns].sort(), `${contract.name} permitted DNS names`);
    assert.deepEqual([...new Set([...policy.matchAll(/port:\s*"(\d+)"/g)]
      .map(match => match[1]))].sort(), ['443', '53', '5432', '8443']);
    const fqdnRules = egress.split(/\n    - /).filter(rule => rule.startsWith('toFQDNs:\n'))
      .map(rule => ({
        hosts: [...rule.matchAll(/matchName:\s*([A-Za-z0-9.-]+)/g)].map(match => match[1]),
        port: rule.match(/port:\s*"(\d+)"/)?.[1],
      }));
    assert.deepEqual(fqdnRules, contract.fqdnRules, `${contract.name} FQDN ports`);
    assert.doesNotMatch(policy, /toEntities:|toCIDR(?:Set)?:|matchPattern:|world|0\.0\.0\.0\/0|\*/i);
  }
});

// Keep new external egress scoped to the P0 issuer, database, and workload call graph.
test('runtime owner policies allow only configured issuer, database, and Kubernetes API paths', () => {
  const policyFiles = [
    'service-owner-network.yaml',
    'events-and-sessions-network.yaml',
    'projects-config-network.yaml',
    'environment-agenthost-network.yaml',
    path.join('identity-broker', 'egress.yaml'),
  ];
  const policies = policyFiles.flatMap(file => parseAllDocuments(readFileSync(
    path.join(root, 'deploy', 'k8s', 'base', file), 'utf8'))
    .map(document => document.toJS()));
  const byName = new Map(policies.map(policy => [policy.metadata.name, policy]));
  const getPolicy = name => {
    const policy = byName.get(name);
    assert.ok(policy, `${name} is included in the P0 policy sources`);
    return policy;
  };
  const workloads = policy => policy.spec.endpointSelector.matchExpressions
    ?.find(expression => expression.key === 'agentweaver.io/workload').values ??
      [policy.spec.endpointSelector.matchLabels['agentweaver.io/workload']];
  const endpoints = rule => rule.toEndpoints.map(endpoint =>
    endpoint.matchLabels['agentweaver.io/workload']).sort();
  const ports = rule => rule.toPorts.flatMap(group => group.ports)
    .map(({ port, protocol }) => ({ port, protocol }));
  const issuer = getPolicy('broker-issuer-egress');
  assert.deepEqual([...workloads(issuer)].sort(),
    ['agenthost', 'environment', 'gateway', 'knowledge', 'mcp', 'orchestrator']);
  assert.deepEqual(issuer.spec.egress[0].toPorts[0].rules.dns.map(({ matchName }) => matchName),
    [
      brokerHost,
      'gateway.agentweaver.svc.cluster.local',
      'projects-config.agentweaver.svc.cluster.local',
      'orchestrator.agentweaver.svc.cluster.local',
      'knowledge.agentweaver.svc.cluster.local',
      'events-and-sessions.agentweaver.svc.cluster.local',
      'environment.agentweaver.svc.cluster.local',
      'identity-broker.agentweaver.svc.cluster.local',
    ]);
  assert.deepEqual(issuer.spec.egress[1].toFQDNs, [{ matchName: brokerHost }]);
  assert.deepEqual(ports(issuer.spec.egress[1]), [{ port: '443', protocol: 'TCP' }]);
  const issuerGateway = issuer.spec.egress[2];
  assert.deepEqual(issuerGateway.toEndpoints, [{
    matchLabels: {
      'k8s:io.kubernetes.pod.namespace': 'agentweaver-gateway',
      'k8s:gateway.networking.k8s.io/gateway-name': 'identity-broker',
    },
  }]);
  assert.deepEqual(ports(issuerGateway), [{ port: '443', protocol: 'TCP' }]);

  const events = getPolicy('events-and-sessions-network');
  const eventsIssuerGateway = events.spec.egress.find(rule => rule.toEndpoints
    ?.some(endpoint => endpoint.matchLabels['k8s:io.kubernetes.pod.namespace'] === 'agentweaver-gateway'));
  assert.deepEqual(eventsIssuerGateway.toEndpoints, issuerGateway.toEndpoints);
  assert.deepEqual(ports(eventsIssuerGateway), [{ port: '443', protocol: 'TCP' }]);
  assert.deepEqual(events.spec.egress[0].toPorts[0].rules.dns
    .filter(({ matchName }) => matchName.endsWith('.agentweaver.svc.cluster.local'))
    .map(({ matchName }) => matchName), [
    'knowledge.agentweaver.svc.cluster.local',
    'projects-config.agentweaver.svc.cluster.local',
    'orchestrator.agentweaver.svc.cluster.local',
  ]);

  const projects = getPolicy('projects-config-network');
  const projectsIssuerGateway = projects.spec.egress.find(rule => rule.toEndpoints
    ?.some(endpoint => endpoint.matchLabels['k8s:io.kubernetes.pod.namespace'] === 'agentweaver-gateway'));
  assert.deepEqual(projectsIssuerGateway.toEndpoints, issuerGateway.toEndpoints);
  assert.deepEqual(ports(projectsIssuerGateway), [{ port: '443', protocol: 'TCP' }]);

  const database = getPolicy('database-workload-identity-egress');
  assert.deepEqual([...workloads(database)].sort(), ['environment', 'knowledge', 'orchestrator']);
  assert.deepEqual(database.spec.egress[0].toPorts[0].rules.dns
    .map(({ matchName }) => matchName).sort(), [
    'agentweaver-pg.postgres.database.azure.com',
    'agentweaver-pg.privatelink.postgres.database.azure.com',
    'login.microsoftonline.com',
  ]);
  assert.deepEqual(database.spec.egress.slice(1).map(rule => ({
    host: rule.toFQDNs[0].matchName,
    port: rule.toPorts[0].ports[0].port,
  })).sort((left, right) => left.host.localeCompare(right.host)), [
    { host: 'agentweaver-pg.postgres.database.azure.com', port: '5432' },
    { host: 'login.microsoftonline.com', port: '443' },
  ]);

  const environment = getPolicy('environment-agenthost-egress');
  const environmentOwners = environment.spec.egress.find(rule => rule.toEndpoints);
  assert.deepEqual(endpoints(environmentOwners), [
    'agenthost', 'events-and-sessions', 'identity-broker', 'orchestrator', 'projects-config',
  ]);
  assert.deepEqual(ports(environmentOwners), [{ port: '8443', protocol: 'TCP' }]);
  const api = environment.spec.egress.find(rule => rule.toEntities);
  assert.deepEqual(api.toEntities, ['kube-apiserver']);
  assert.deepEqual(ports(api), [{ port: '443', protocol: 'TCP' }]);

  const broker = getPolicy('identity-broker-egress');
  assert.deepEqual(broker.spec.egress[0].toPorts[0].rules.dns
    .filter(({ matchName }) => matchName.endsWith('.agentweaver.svc.cluster.local'))
    .map(({ matchName }) => matchName), [
    'projects-config.agentweaver.svc.cluster.local',
    'orchestrator.agentweaver.svc.cluster.local',
    'environment.agentweaver.svc.cluster.local',
  ]);
  const brokerOwners = broker.spec.egress.find(rule => rule.toEndpoints
    ?.some(endpoint => endpoint.matchLabels['agentweaver.io/workload'] === 'projects-config'));
  assert.deepEqual(endpoints(brokerOwners), ['environment', 'orchestrator', 'projects-config']);
  assert.deepEqual(ports(brokerOwners), [{ port: '8443', protocol: 'TCP' }]);
  const environmentBrokerIngress = broker.spec.ingress.find(rule => rule.fromEndpoints
    ?.some(endpoint => endpoint.matchLabels['agentweaver.io/workload'] === 'environment'));
  assert.deepEqual(environmentBrokerIngress.fromEndpoints, [{
    matchLabels: {
      'k8s:io.kubernetes.pod.namespace': namespace,
      'agentweaver.io/workload': 'environment',
    },
  }]);
  assert.deepEqual(ports(environmentBrokerIngress), [{ port: '8443', protocol: 'TCP' }]);

  const orchestrator = getPolicy('orchestrator-owner-network');
  const orchestratorBrokerIngress = orchestrator.spec.ingress.find(rule => rule.fromEndpoints
    ?.some(endpoint => endpoint.matchLabels['agentweaver.io/workload'] === 'identity-broker'));
  assert.deepEqual(orchestratorBrokerIngress.fromEndpoints.find(endpoint =>
    endpoint.matchLabels['agentweaver.io/workload'] === 'identity-broker'), {
    matchLabels: {
      'k8s:io.kubernetes.pod.namespace': namespace,
      'agentweaver.io/workload': 'identity-broker',
    },
  });
  assert.deepEqual(ports(orchestratorBrokerIngress), [{ port: '8443', protocol: 'TCP' }]);

  const environmentOwnerBrokerIngress = getPolicy('environment-agenthost-ingress').spec.ingress[0];
  assert.deepEqual(environmentOwnerBrokerIngress.fromEndpoints.find(endpoint =>
    endpoint.matchLabels['agentweaver.io/workload'] === 'identity-broker'), {
    matchLabels: {
      'k8s:io.kubernetes.pod.namespace': namespace,
      'agentweaver.io/workload': 'identity-broker',
    },
  });
  assert.deepEqual(ports(environmentOwnerBrokerIngress), [{ port: '8443', protocol: 'TCP' }]);

  const agenthost = getPolicy('agenthost-egress');
  assert.deepEqual(endpoints(agenthost.spec.egress[0]), [
    'environment', 'events-and-sessions', 'identity-broker', 'orchestrator',
  ]);
  assert.deepEqual(ports(agenthost.spec.egress[0]), [{ port: '8443', protocol: 'TCP' }]);
});

// The install root is the complete P0 source bundle; credentials and Gateway placement stay external.
test('P0 Kustomize install renders every workload, identity, runtime map, and network policy', () => {
  const rendered = spawnSync('kubectl', ['kustomize', path.join(root, 'deploy', 'k8s')], {
    cwd: root,
    encoding: 'utf8',
  });
  assert.equal(rendered.error, undefined, rendered.error?.message);
  assert.equal(rendered.status, 0, rendered.stderr);
  const installation = parseKustomizeResources(rendered.stdout);
  const resources = installation.items;
  const namesFor = kind => new Set(resources.filter(resource => resource.kind === kind)
    .map(resource => resource.metadata.name));
  const configMapNames = namesFor('ConfigMap');
  const requiredServiceConfigMaps = Object.entries(SERVICE_NAMES)
    .filter(([id]) => id !== 'Agentweaver.AgentHost')
    .map(([id, name]) => id === 'Agentweaver.Identity.Broker'
      ? 'identity-broker-runtime-config-v2'
      : `${name}-runtime-config`);
  for (const name of [...requiredServiceConfigMaps, 'environment-cilium-config',
    'agenthost-runtime-v1', 'projects-config-provider-catalog']) {
    assert.ok(configMapNames.has(name), `${name} must be rendered by the P0 prerequisite overlay`);
  }

  const ciliumPolicyNames = namesFor('CiliumNetworkPolicy');
  const requiredCiliumPolicies = Object.values(SERVICE_NETWORK_POLICY_NAMES)
    .flatMap(names => Array.isArray(names) ? names : [names]);
  for (const name of requiredCiliumPolicies)
    assert.ok(ciliumPolicyNames.has(name), `${name} must be rendered by the P0 prerequisite overlay`);
  assert.deepEqual([...namesFor('NetworkPolicy')].sort(), ['allow-dns-egress', 'default-deny-all']);
  const sharedServices = Object.values(SERVICE_NAMES).filter(name => name !== 'agenthost').sort();
  assert.deepEqual([...namesFor('Deployment')].sort(), sharedServices);
  assert.deepEqual([...namesFor('Service')].sort(), sharedServices);
  assert.deepEqual([...namesFor('ServiceAccount')].sort(), sharedServices);
  const serviceAccounts = new Map(resources.filter(resource => resource.kind === 'ServiceAccount')
    .map(resource => [resource.metadata.name, resource]));
  for (const [name, runtime, key] of [
    ['identity-broker', manifest('identity-broker-runtime-config'), 'IdentityBroker__SecretRedemption__WorkloadIdentityClientId'],
    ['environment', manifest('environment-runtime-config'), 'Environment__Database__WorkloadIdentity__ClientId'],
    ['events-and-sessions', manifest('events-and-sessions-runtime-config'), 'EventsAndSessions__Database__WorkloadIdentity__ClientId'],
    ['knowledge', manifest('knowledge-runtime-config'), 'Knowledge__Database__WorkloadIdentity__ClientId'],
    ['orchestrator', manifest('orchestrator-runtime-config'), 'Orchestrator__Database__WorkloadIdentity__ClientId'],
    ['projects-config', manifest('projects-config-runtime-config'), 'ProjectsConfig__Database__WorkloadIdentity__ClientId'],
  ]) {
    assert.equal(serviceAccounts.get(name)?.metadata.annotations?.['azure.workload.identity/client-id'],
      runtime.data[key], `${name} ServiceAccount client ID must come from its runtime ConfigMap`);
  }

  const disallowedKinds = new Set([
    'Secret', 'Gateway', 'BackendTLSPolicy', 'Job',
  ]);
  assert.deepEqual(resources.filter(resource => disallowedKinds.has(resource.kind))
    .map(resource => `${resource.kind}/${resource.metadata.name}`), []);

  const brokerPolicy = resources.find(resource =>
    resource.kind === 'CiliumNetworkPolicy' && resource.metadata.name === 'identity-broker-egress');
  assert.ok(brokerPolicy);
  assert.deepEqual([...new Set([...JSON.stringify(brokerPolicy).matchAll(/matchName["\s:]+([A-Za-z0-9.-]+)/g)]
    .map(match => match[1]))], [
    'login.microsoftonline.com',
    'projects-config.agentweaver.svc.cluster.local',
    'orchestrator.agentweaver.svc.cluster.local',
    'environment.agentweaver.svc.cluster.local',
    'agentweaver-kv-v1.vault.azure.net',
    'agentweaver-pg.postgres.database.azure.com',
  ]);
  const unresolvedInputs = [...new Set([...JSON.stringify(installation).matchAll(/CHANGEME-[A-Za-z0-9-]+/g)]
    .map(match => match[0]))].sort();
  assert.deepEqual(unresolvedInputs, []);

  const kustomization = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'kustomization.yaml'), 'utf8'));
  // Kustomize pins and the linux/amd64 compressed-byte count must match the immutable v1.0.6 publication receipt.
  const expectedPins = [
    ['agentweaver-agenthost', 'e15fab2de04014b77d58a3ba5f08c729ad9f4f49880cbdbc5995c1093fe77fe6'],
    ['agentweaver.identity.broker', '8841568368ebdea5c3f7c1db481ca7a6f68db3d5e965632710f0cf31ff49e384'],
    ['agentweaver.eventsandsessions', '9bdffac08b291455e3686e33f1e1d008392a7c7504fdcc096b39277429698814'],
    ['agentweaver.gateway', 'a1b30970ed3c1375450ca1ce8eb1e409c2b3acf7f53092fc83fbd4b0d62ee67c'],
    ['agentweaver.mcp', 'eb523a0505a9f5b61cb04c09fec0af3345ff50d4f256194709df8676a61a3b1d'],
    ['agentweaver-web', 'ac1c3f8cad01e18f9306375d3218d93f15960fa13b68acfeb40dab1031d5d4d8'],
    ['agentweaver.orchestrator', '91e67a2ac41c31797ff63d137d1d8e0e42d59004964f8fb9ff0f715fb29c1075'],
    ['agentweaver.environment', 'd45fef1a20f1e0f138ecee43889505efd64c92eb93e719d3b2f591c8236fe7e2'],
    ['agentweaver.knowledge', '939862dfde4d6931167131871b8ec7272a8ec3bbf1e42c14d909f645adc54b9f'],
    ['agentweaver.projects.config', 'e011dba619857b4cffcc0fd14f64f31de01ef9c16e5ea1965ede5f211dd14160'],
  ].map(([repository, digest]) => ({
    name: `ghcr.io/sabbour/${repository}`,
    newName: `ghcr.io/sabbour/${repository}`,
    digest: `sha256:${digest}`,
  }));
  assert.deepEqual(kustomization.images, expectedPins);
  const environment = resources.find(resource =>
    resource.kind === 'Deployment' && resource.metadata.name === 'environment');
  const environmentEnv = new Map(environment.spec.template.spec.containers[0].env
    .map(({ name, value }) => [name, value]));
  assert.equal(environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImage'),
    `${expectedPins[0].newName}@${expectedPins[0].digest}`);
  assert.equal(environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform'), 'linux/amd64');
  assert.equal(environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes'),
    '321486521');
  const agentHostConfigMap = resources.find(resource =>
    resource.kind === 'ConfigMap' && resource.metadata.name === 'agenthost-runtime-v1');
  const agentHost = JSON.parse(agentHostConfigMap.data['appsettings.json']).AgentHost;
  assert.equal(agentHost.Image.Digest, expectedPins[0].digest);
  assert.equal(agentHost.Image.Platform,
    environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImagePlatform'));
  assert.equal(agentHost.Image.CompressedPullBytes,
    Number(environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes')));
  assert.equal(agentHostConfigMap.metadata.annotations?.['agentweaver.io/runtime-config-status'], 'incomplete');

  const images = Object.entries(SERVICE_NAMES).map(([id, name]) => ({
    id,
    name,
    image: resources.find(resource => resource.kind === 'Deployment' && resource.metadata.name === name)
      ?.spec.template.spec.containers[0].image,
    ...(name === 'agenthost' ? {
      sandboxImage: environmentEnv.get('Environment__Sandbox__AgentSandbox__ContainerImage'),
      sandboxImageDigest: agentHost.Image.Digest,
      compressedPullBytes: agentHost.Image.CompressedPullBytes,
    } : {}),
  }));
  const providerCatalog = {
    configMapName: 'projects-config-provider-catalog',
    sourceCommit: resources.find(resource => resource.kind === 'Deployment' &&
      resource.metadata.name === 'projects-config')
      .spec.template.metadata.annotations['agentweaver.io/provider-catalog-source'],
  };
  assert.deepEqual(validateKustomizeInstall(installation, images, namespace, providerCatalog)
    .incompleteRuntimeConfigMaps, []);

  for (const name of [
    'environment', 'events-and-sessions', 'gateway', 'identity-broker', 'knowledge', 'mcp', 'orchestrator',
  ]) {
    const pod = resources.find(resource =>
      resource.kind === 'Deployment' && resource.metadata.name === name).spec.template.spec;
    const volumeName = name === 'identity-broker' ? 'projects-ca' : 'backend-ca';
    const caVolume = pod.volumes.find(volume => volume.name === volumeName);
    assert.equal(caVolume.configMap.name, 'service-backend-ca', `${name} backend CA ConfigMap`);
    assert.equal(caVolume.configMap.optional, false, `${name} backend CA is required`);
    assert.deepEqual(caVolume.configMap.items, [{ key: 'ca.crt', path: 'ca.crt' }]);
    assert.ok(pod.containers[0].volumeMounts.some(mount =>
      mount.name === volumeName && mount.mountPath === '/var/run/service-backend-ca' &&
      mount.readOnly === true), `${name} backend CA mount`);
  }

  const unresolvedBroker = structuredClone(installation);
  unresolvedBroker.items.find(resource => resource.kind === 'ConfigMap' &&
    resource.metadata.name === 'identity-broker-runtime-config-v2')
    .data.IdentityBroker__SecretRedemption__VaultHost = 'CHANGEME-KEYVAULT-HOST';
  assert.throws(() => validateKustomizeInstall(unresolvedBroker, images, namespace, providerCatalog),
    /ConfigMap\/identity-broker-runtime-config-v2 contains an unresolved runtime input/);
});
