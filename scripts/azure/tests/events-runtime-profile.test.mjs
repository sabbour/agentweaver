// Checks Events runtime values without generating Deployments, Services, or policies.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { renderEventsProfile } from '../../../deploy/k8s/profiles/events-and-sessions/render.mjs';

const exampleUrl = new URL('../../../deploy/k8s/profiles/events-and-sessions/runtime-inputs.example.json', import.meta.url);
const example = JSON.parse(readFileSync(exampleUrl, 'utf8'));
const runtimeConfigUrl = new URL('../../../deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json', import.meta.url);
const sourceConfiguration = JSON.parse(readFileSync(runtimeConfigUrl, 'utf8')).data;
const rateCardKey = 'EventsAndSessions__Cost__Copilot__RateCard__Json';
const testManagedHost = 'agentweaver.0123456789abcdef.eastus2euap.aksapp.io';

function fixture() {
  const configuration = Object.fromEntries(Object.entries(example.configuration)
    .map(([key, value]) => [key, value ?? sourceConfiguration[key]]));
  for (const [key, value] of Object.entries(configuration)) {
    if (typeof value === 'string') {
      configuration[key] = value.replaceAll('CHANGEME-MANAGED-HOST', testManagedHost);
    }
  }
  return {
    configuration,
  };
}

test('example remains incomplete until Broker, owner and audience values are supplied', () => {
  assert.throws(() => renderEventsProfile(example), /explicit nonempty string/);
});

test('validator returns runtime data only and applies the checked-in rate-card default', () => {
  const input = fixture();
  const before = structuredClone(input);
  const result = renderEventsProfile(input);

  assert.deepEqual(input, before);
  assert.deepEqual(result.data, {
    ...input.configuration,
    [rateCardKey]: sourceConfiguration[rateCardKey],
  });
  assert.equal(result.apiVersion, undefined);
  assert.equal(result.kind, undefined);
  assert.equal(result.data.EventsAndSessions__SessionMaterial__ContainerUri,
    'https://agentweaverblob.blob.core.windows.net/session-material');
  assert.equal(result.data.AgentHost__ModelBindings__reasoning__ModelId, 'gpt-6.1-sol');
  assert.equal(result.data.AgentHost__ModelBindings__coding__ModelId, 'gpt-6-luna');
});

test('required configuration rejects missing, empty and non-string values', () => {
  for (const key of Object.keys(fixture().configuration)) {
    const missing = fixture();
    delete missing.configuration[key];
    assert.throws(() => renderEventsProfile(missing), /missing field|incomplete or invalid/, key);

    for (const value of [null, '', ' ', 'bad\nvalue', 1]) {
      const invalid = fixture();
      invalid.configuration[key] = value;
      assert.throws(() => renderEventsProfile(invalid), Error, key);
    }
  }
});

test('database role, identity, schema and approved resource IDs remain exact', () => {
  for (const key of [
    'ConnectionStrings__EventsAndSessions',
    'EventsAndSessions__Database__WorkloadIdentity__TenantId',
    'EventsAndSessions__Database__WorkloadIdentity__ClientId',
    'EventsAndSessions__Database__WorkloadIdentity__TokenFilePath',
    'EventsAndSessions__Provider__DatabaseName',
    'EventsAndSessions__Provider__Schema',
  ]) {
    const input = fixture();
    input.configuration[key] = 'foreign';
    assert.throws(() => renderEventsProfile(input), /admitted Events runtime mapping/, key);
  }
  for (const role of ['foundation_probe_runtime', 'agentweaver-id-identity-broker', 'postgres', 'events_migration']) {
    const input = fixture();
    input.configuration.ConnectionStrings__EventsAndSessions =
      input.configuration.ConnectionStrings__EventsAndSessions.replace('agentweaver-id-events-and-sessions', role);
    assert.throws(() => renderEventsProfile(input), /runtime mapping/);
  }
  for (const [key, values] of [
    ['EventsAndSessions__Provider__ResourceGeneration', ['0', '-1', '1.5', '01', '9007199254740992']],
    ['EventsAndSessions__Provider__ResourceId', ['bad/path', 'a'.repeat(257)]],
    ['EventsAndSessions__Cost__Copilot__ResourceId', ['bad/path', 'a'.repeat(257)]],
  ]) {
    for (const value of values) {
      const input = fixture();
      input.configuration[key] = value;
      assert.throws(() => renderEventsProfile(input), Error, key);
    }
  }
});

test('model bindings require exact references, IDs, source modes and a revision', () => {
  for (const [key, value] of [
    ['AgentHost__ModelBindingsRevision', 'invalid revision'],
    ['AgentHost__ModelBindings__reasoning__ModelId', 'bad model ID'],
    ['AgentHost__ModelBindings__reasoning__SourceMode', 'UnknownSource'],
    ['AgentHost__ModelBindings__unexpected__Extra', 'value'],
  ]) {
    const input = fixture();
    input.configuration[key] = value;
    assert.throws(() => renderEventsProfile(input));
  }
  const input = fixture();
  delete input.configuration.AgentHost__ModelBindings__coding__SourceMode;
  assert.throws(() => renderEventsProfile(input), /incomplete or invalid/);
  const withoutRateCard = fixture();
  delete withoutRateCard.configuration[rateCardKey];
  assert.equal(renderEventsProfile(withoutRateCard).data[rateCardKey], sourceConfiguration[rateCardKey]);
  const invalidCard = fixture();
  invalidCard.configuration[rateCardKey] = '{';
  assert.throws(() => renderEventsProfile(invalidCard), /must be valid JSON/);
});

test('managed issuer, exact private owner services and Blob container inputs are enforced', () => {
  for (const issuer of [
    'https://foreign.example/',
    'https://agentweaver.test.aksapp.io:8443/',
    'https://user@agentweaver.test.aksapp.io/',
    'https://agentweaver.test.aksapp.io/?query=x',
  ]) {
    const input = fixture();
    input.configuration.Identity__Issuer = issuer;
    assert.throws(() => renderEventsProfile(input), /Identity issuer/);
  }
  for (const key of [
    'EventsAndSessions__Knowledge__BaseAddress',
    'ProjectsConfig__BaseAddress',
    'EventsAndSessions__OrchestratorOwner__OwnerBaseAddress',
  ]) {
    const input = fixture();
    input.configuration[key] = 'https://foreign.example:8443/';
    assert.throws(() => renderEventsProfile(input), /exact private/);
  }
  const mismatchedProjects = fixture();
  mismatchedProjects.configuration.ProjectsConfig__AuthorizationContext__OwnerBaseAddress =
    'https://projects-config.agentweaver.svc.cluster.local:9443/';
  assert.throws(() => renderEventsProfile(mismatchedProjects), /same exact Projects owner/);
  for (const uri of [
    'http://agentweaverblob.blob.core.windows.net/session-material',
    'https://foreign.blob.core.windows.net/session-material',
    'https://agentweaverblob.blob.core.windows.net/session-material?sig=secret',
    'https://agentweaverblob.blob.core.windows.net/session-material/extra',
  ]) {
    const input = fixture();
    input.configuration.EventsAndSessions__SessionMaterial__ContainerUri = uri;
    assert.throws(() => renderEventsProfile(input), /one explicit container/);
  }
});

test('BYOK provider fields and the Azure rate card match the AgentHost model map shape', () => {
  const byok = {
    'AgentHost__ModelBindings__azure-gpt__ModelId': 'gpt-5.4',
    'AgentHost__ModelBindings__azure-gpt__SourceMode': 'Byok',
    'AgentHost__ModelBindings__azure-gpt__Provider__Type': 'openai',
    'AgentHost__ModelBindings__azure-gpt__Provider__BaseUrl': 'https://example-resource.openai.azure.com/openai/v1',
    'AgentHost__ModelBindings__azure-gpt__Provider__WireApi': 'completions',
    'AgentHost__ModelBindings__azure-gpt__SupportedReasoningEfforts__0': 'low',
    'AgentHost__ModelBindings__azure-gpt__SupportedReasoningEfforts__1': 'high',
    EventsAndSessions__Cost__Azure__ResourceId: 'azure-openai:example-resource',
    EventsAndSessions__Cost__Azure__ResourceGeneration: '1',
    EventsAndSessions__Cost__Azure__OptionsRevision: 'byok-1',
    EventsAndSessions__Cost__Azure__OptionsSchemaVersion: '1',
    EventsAndSessions__Cost__Azure__PricingModel: 'StandardTokens',
    EventsAndSessions__Cost__Azure__RateCard__Id: 'azure-retail-prices.standard.swedencentral',
    EventsAndSessions__Cost__Azure__RateCard__Version: '2026-11-09',
    EventsAndSessions__Cost__Azure__RateCard__MeterSource: 'byok.tokens',
    EventsAndSessions__Cost__Azure__RateCard__Unit: 'USD',
    EventsAndSessions__Cost__Azure__RateCard__NanoUnitsPerUnit: '1000000',
    'EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__input': '2.5',
    'EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__cachedInput': '0.25',
    'EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__output': '15',
  };
  const input = fixture();
  Object.assign(input.configuration, byok);
  assert.equal(renderEventsProfile(input).data['AgentHost__ModelBindings__azure-gpt__Provider__WireApi'], 'completions');

  for (const [key, value] of [
    ['AgentHost__ModelBindings__azure-gpt__Provider__WireApi', undefined],
    ['AgentHost__ModelBindings__azure-gpt__Provider__BaseUrl', 'https://key@example.test/openai/v1'],
    ['AgentHost__ModelBindings__azure-gpt__Provider__AzureApiVersion', '2025-04-01-preview'],
    ['AgentHost__ModelBindings__azure-gpt__Provider__Headers__api-key', 'secret'],
    ['AgentHost__ModelBindings__azure-gpt__SupportedReasoningEfforts__1', 'minimal'],
    ['AgentHost__ModelBindings__reasoning__Provider__Type', 'openai'],
    ['EventsAndSessions__Cost__Azure__RateCard__Unit', 'EUR'],
    ['EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__output', '0'],
    ['EventsAndSessions__Cost__Azure__PricingModel', undefined],
  ]) {
    const invalid = fixture();
    Object.assign(invalid.configuration, byok);
    if (value === undefined) delete invalid.configuration[key];
    else invalid.configuration[key] = value;
    assert.throws(() => renderEventsProfile(invalid), Error, key);
  }
});
