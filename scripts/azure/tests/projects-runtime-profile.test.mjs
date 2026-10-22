// Keeps Projects runtime-value validation separate from Kustomize resource generation.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { renderProjectsProfile } from '../../../deploy/k8s/profiles/projects-config/render.mjs';

const exampleUrl = new URL('../../../deploy/k8s/profiles/projects-config/runtime-inputs.example.json', import.meta.url);
const example = JSON.parse(readFileSync(exampleUrl, 'utf8'));
const runtimeConfigUrl = new URL('../../../deploy/k8s/runtime-configs/projects-config-runtime-config.json', import.meta.url);
const sourceConfiguration = JSON.parse(readFileSync(runtimeConfigUrl, 'utf8')).data;

function fixture() {
  const input = structuredClone(example);
  delete input.privilegedSetupConfiguration;
  input.configuration.ProjectsConfig__Authentication__Authority =
    'https://agentweaver.test.eastus2euap.aksapp.io/';
  input.configuration.ProjectsConfig__Authentication__Audience = 'projects-test-audience';
  return input;
}

/** The model-binding copy is optional; tests of required runtime keys start without it. */
const isModelBindingKey = key => key.startsWith('AgentHost__ModelBindings');
function requiredFixture() {
  const input = fixture();
  for (const key of Object.keys(input.configuration)) if (isModelBindingKey(key)) delete input.configuration[key];
  return input;
}

test('example remains incomplete until Broker authentication values are supplied', () => {
  assert.throws(() => renderProjectsProfile(example), /explicit nonempty string/);
});

/** Supply test-only approved authority values; these do not provision or authorize an identity. */
function privilegedFixture() {
  const input = fixture();
  input.privilegedSetupConfiguration = {
    ConnectionStrings__ProjectsConfigAuthority: input.configuration.ConnectionStrings__ProjectsConfig
      .replace('Username=agentweaver-id-projects-config;', 'Username=approved_setup_authority;'),
    ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TenantId:
      input.configuration.ProjectsConfig__Database__WorkloadIdentity__TenantId,
    ProjectsConfig__AuthorityDatabase__WorkloadIdentity__ClientId: '11111111-2222-4333-8444-555555555555',
    ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TokenFilePath:
      input.configuration.ProjectsConfig__Database__WorkloadIdentity__TokenFilePath,
    ProjectsConfig__PrivilegedSetup__ApprovedIssuer: input.configuration.ProjectsConfig__Authentication__Authority,
    ProjectsConfig__PrivilegedSetup__ApprovedSubject: 'approved-test-subject',
    ProjectsConfig__PrivilegedSetup__ApprovedTenantId:
      input.configuration.ProjectsConfig__Database__WorkloadIdentity__TenantId,
  };
  return input;
}

test('explicit privileged setup settings are rendered without changing runtime identity or input', () => {
  const input = privilegedFixture();
  const before = structuredClone(input);
  assert.deepEqual(renderProjectsProfile(input), {
    data: { ...input.configuration, ...input.privilegedSetupConfiguration },
  });
  assert.deepEqual(input, before);
});

test('privileged setup settings are all-or-none and exclude secrets and foreign settings', () => {
  for (const key of Object.keys(privilegedFixture().privilegedSetupConfiguration)) {
    const missing = privilegedFixture();
    delete missing.privilegedSetupConfiguration[key];
    assert.throws(() => renderProjectsProfile(missing), /missing field/, key);
    for (const value of [null, '', ' padded ', 'bad\nvalue', 1]) {
      const input = privilegedFixture();
      input.privilegedSetupConfiguration[key] = value;
      assert.throws(() => renderProjectsProfile(input), /explicit nonempty string/, key);
    }
  }
  for (const key of ['Password', 'AccessToken', 'AZURE_CLIENT_SECRET']) {
    const input = privilegedFixture();
    input.privilegedSetupConfiguration[key] = 'controlled-secret';
    assert.throws(() => renderProjectsProfile(input), /unsupported field/);
  }
});

test('privileged setup never substitutes runtime, migration or foreign database authority', () => {
  const key = 'ConnectionStrings__ProjectsConfigAuthority';
  for (const mutate of [
    input => { input.privilegedSetupConfiguration[key] = input.configuration.ConnectionStrings__ProjectsConfig; },
    input => { input.privilegedSetupConfiguration[key] += ';Password=controlled-secret'; },
    input => { input.privilegedSetupConfiguration[key] = input.privilegedSetupConfiguration[key].replace('approved_setup_authority', 'postgres'); },
    input => { input.privilegedSetupConfiguration[key] = input.privilegedSetupConfiguration[key].replace('approved_setup_authority', 'projects_migration'); },
    input => { input.privilegedSetupConfiguration[key] = input.privilegedSetupConfiguration[key].replace('Host=agentweaver-pg', 'Host=foreign-pg'); },
    input => { input.privilegedSetupConfiguration[key] = input.privilegedSetupConfiguration[key].replace('VerifyFull', 'Require'); },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__AuthorityDatabase__WorkloadIdentity__ClientId = input.configuration.ProjectsConfig__Database__WorkloadIdentity__ClientId; },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__AuthorityDatabase__WorkloadIdentity__ClientId = 'not-a-guid'; },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TenantId = 'foreign-tenant'; },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TokenFilePath = '/foreign/token'; },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__PrivilegedSetup__ApprovedIssuer = 'https://foreign.test/'; },
    input => { input.privilegedSetupConfiguration.ProjectsConfig__PrivilegedSetup__ApprovedTenantId = 'foreign-tenant'; },
  ]) {
    const input = privilegedFixture();
    mutate(input);
    assert.throws(() => renderProjectsProfile(input), /Privileged setup requires/);
  }
});
test('validator returns only runtime data and does not mutate its input', () => {
  const input = fixture();
  const before = structuredClone(input);
  const result = renderProjectsProfile(input);

  assert.deepEqual(input, before);
  assert.deepEqual(result, { data: input.configuration });
  assert.equal(result.apiVersion, undefined);
  assert.deepEqual(Object.keys(result.data), Object.keys(input.configuration));
});

test('every required runtime field rejects missing, empty, padded, control-bearing and non-string values', () => {
  for (const key of Object.keys(requiredFixture().configuration)) {
    const missing = fixture();
    delete missing.configuration[key];
    assert.throws(() => renderProjectsProfile(missing), /missing field/, key);

    for (const value of [null, '', ' ', ' padded ', 'bad\nvalue', 'bad\u0000value', 1]) {
      const invalid = fixture();
      invalid.configuration[key] = value;
      assert.throws(() => renderProjectsProfile(invalid), /explicit nonempty string/, key);
    }
  }
});

test('database, workload identity, role and token path stay on the admitted P0 mapping', () => {
  for (const key of [
    'ConnectionStrings__ProjectsConfig',
    'ProjectsConfig__Database__WorkloadIdentity__TenantId',
    'ProjectsConfig__Database__WorkloadIdentity__ClientId',
    'ProjectsConfig__Database__WorkloadIdentity__TokenFilePath',
  ]) {
    const input = fixture();
    input.configuration[key] = 'foreign';
    assert.throws(() => renderProjectsProfile(input), /admitted Projects runtime mapping/, key);
  }
  for (const role of [
    'foundation_probe_runtime',
    'agentweaver-id-events-and-sessions',
    'agentweaver-id-identity-broker',
    'postgres',
    'projects_migration',
  ]) {
    const input = fixture();
    input.configuration.ConnectionStrings__ProjectsConfig =
      input.configuration.ConnectionStrings__ProjectsConfig.replace('agentweaver-id-projects-config', role);
    assert.throws(() => renderProjectsProfile(input), /runtime mapping/, role);
  }
  assert.equal(
    sourceConfiguration.ProjectsConfig__Database__WorkloadIdentity__ClientId,
    fixture().configuration.ProjectsConfig__Database__WorkloadIdentity__ClientId,
  );
});

test('only the managed Broker HTTPS root and recognized runtime keys are accepted', () => {
  for (const authority of [
    'http://agentweaver.test.aksapp.io/',
    'https://foreign.example/',
    'https://agentweaver.test.aksapp.io:8443/',
    'https://user@agentweaver.test.aksapp.io/',
    'https://agentweaver.test.aksapp.io/?query=x',
    'https://agentweaver.test.aksapp.io/#fragment',
    'https://agentweaver.test.aksapp.io/path',
    'https://agentweaver.test.aksapp.io%2f/',
  ]) {
    const input = fixture();
    input.configuration.ProjectsConfig__Authentication__Authority = authority;
    assert.throws(() => renderProjectsProfile(input), /Projects authority/);
  }
  for (const key of [
    'Password',
    'ClientSecret',
    'ConnectionStrings__ProjectsConfigMigration',
    'ProjectsConfig__Migration__WorkloadIdentity__ClientId',
    'ProjectsConfig__SkillContentContainerUri',
    'ProjectsConfig__ProviderCatalog__Registrations__0__Id',
    'SSL_CERT_FILE',
    'AZURE_CLIENT_SECRET',
  ]) {
    const input = fixture();
    input.configuration[key] = 'forbidden';
    assert.throws(() => renderProjectsProfile(input), /unsupported field/, key);
  }
  const extraRootInput = fixture();
  extraRootInput.image = 'ghcr.io/example/image:latest';
  assert.throws(() => renderProjectsProfile(extraRootInput), /unsupported field/);
});

/** Add a read-only model-binding eligibility copy with one hosted, one BYOK and one disabled binding. */
function bindingFixture() {
  const input = requiredFixture();
  Object.assign(input.configuration, {
    AgentHost__ModelBindingsRevision: 'models-r7',
    AgentHost__ModelBindings__reasoning__SourceMode: 'HostedCopilot',
    AgentHost__ModelBindings__reasoning__Enabled: 'true',
    AgentHost__ModelBindings__coder__SourceMode: 'Byok',
    AgentHost__ModelBindings__coder__Enabled: 'true',
    AgentHost__ModelBindings__coder__SupportedReasoningEfforts__0: 'high',
    AgentHost__ModelBindings__coder__SupportedReasoningEfforts__1: 'low',
    AgentHost__ModelBindings__retired__SourceMode: 'Byok',
    AgentHost__ModelBindings__retired__Enabled: 'false',
    AgentHost__ModelBindings__retired__SupportedReasoningEfforts__0: 'medium',
  });
  return input;
}

test('model-binding eligibility copy is optional and renders unchanged when valid', () => {
  assert.deepEqual(renderProjectsProfile(requiredFixture()).data, requiredFixture().configuration);
  const input = bindingFixture();
  assert.deepEqual(renderProjectsProfile(input), { data: input.configuration });
  const exampleBindings = Object.fromEntries(
    Object.entries(fixture().configuration).filter(([key]) => isModelBindingKey(key)));
  const deployedBindings = Object.fromEntries(
    Object.entries(sourceConfiguration).filter(([key]) => isModelBindingKey(key)));
  assert.ok(Object.keys(deployedBindings).length > 0);
  assert.deepEqual(exampleBindings, deployedBindings);
  assert.deepEqual(renderProjectsProfile(fixture()), { data: fixture().configuration });
});

test('model-binding copy rejects provider, credential and malformed eligibility fields', () => {
  const cases = [
    ['ModelId', input => { input.configuration.AgentHost__ModelBindings__coder__ModelId = 'gpt-x'; }, /unsupported model binding field/],
    ['provider URL', input => { input.configuration.AgentHost__ModelBindings__coder__Provider__BaseUrl = 'https://models.example/'; }, /unsupported model binding field/],
    ['provider header', input => { input.configuration.AgentHost__ModelBindings__coder__Provider__Headers__Authorization = 'Bearer x'; }, /unsupported model binding field/],
    ['credential reference', input => { input.configuration.AgentHost__ModelBindings__coder__CredentialReference = 'kv://secret'; }, /unsupported model binding field/],
    ['prompt capacity', input => { input.configuration.AgentHost__ModelBindings__coder__PromptCapacityTokens = '1000'; }, /unsupported model binding field/],
    ['bad reference', input => { input.configuration['AgentHost__ModelBindings__bad ref__SourceMode'] = 'Byok'; }, /unsupported model binding field/],
    ['revision without bindings', input => {
      for (const key of Object.keys(input.configuration)) if (key.startsWith('AgentHost__ModelBindings__')) delete input.configuration[key];
    }, /at least one model binding/],
    ['bindings without revision', input => { delete input.configuration.AgentHost__ModelBindingsRevision; }, /valid model-bindings revision/],
    ['malformed revision', input => { input.configuration.AgentHost__ModelBindingsRevision = 'bad revision'; }, /valid model-bindings revision/],
    ['unknown source mode', input => { input.configuration.AgentHost__ModelBindings__coder__SourceMode = 'Foundry'; }, /'coder' is incomplete or invalid/],
    ['missing enabled', input => { delete input.configuration.AgentHost__ModelBindings__coder__Enabled; }, /'coder' is incomplete or invalid/],
    ['non-boolean enabled', input => { input.configuration.AgentHost__ModelBindings__coder__Enabled = 'True'; }, /'coder' is incomplete or invalid/],
    ['missing source mode', input => { delete input.configuration.AgentHost__ModelBindings__coder__SourceMode; }, /'coder' is incomplete or invalid/],
    ['unknown effort', input => { input.configuration.AgentHost__ModelBindings__coder__SupportedReasoningEfforts__1 = 'max'; }, /'coder' is incomplete or invalid/],
    ['duplicate effort', input => { input.configuration.AgentHost__ModelBindings__coder__SupportedReasoningEfforts__1 = 'high'; }, /'coder' is incomplete or invalid/],
    ['effort index gap', input => {
      delete input.configuration.AgentHost__ModelBindings__coder__SupportedReasoningEfforts__0;
    }, /'coder' is incomplete or invalid/],
    ['non-string effort', input => { input.configuration.AgentHost__ModelBindings__coder__SupportedReasoningEfforts__1 = 1; }, /'coder' is incomplete or invalid/],
    // .NET reads ':' and '__' as hierarchy separators, so such a reference would not reach the service intact.
    ['colon in reference', input => { input.configuration['AgentHost__ModelBindings__model:alias__SourceMode'] = 'Byok'; }, /unsupported model binding field/],
    ['double underscore in reference', input => { input.configuration.AgentHost__ModelBindings__model__alias__SourceMode = 'Byok'; }, /unsupported model binding field/],
  ];
  for (const [name, mutate, expected] of cases) {
    const input = bindingFixture();
    mutate(input);
    assert.throws(() => renderProjectsProfile(input), expected, name);
  }
});

test('model-binding copy accepts every effort shape AgentHost accepts', () => {
  // AgentHost ignores hosted binding efforts and accepts a BYOK binding with none; the copy must not fail on either.
  const input = bindingFixture();
  delete input.configuration.AgentHost__ModelBindings__retired__SupportedReasoningEfforts__0;
  input.configuration.AgentHost__ModelBindings__reasoning__SupportedReasoningEfforts__0 = 'low';
  input.configuration['AgentHost__ModelBindings__gpt-6.1_sol__SourceMode'] = 'HostedCopilot';
  input.configuration['AgentHost__ModelBindings__gpt-6.1_sol__Enabled'] = 'true';
  assert.deepEqual(renderProjectsProfile(input), { data: input.configuration });
});
