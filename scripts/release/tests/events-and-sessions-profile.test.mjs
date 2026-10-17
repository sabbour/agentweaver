// Verifies that operator model bindings and the deployment rate-card data render into the runtime ConfigMap.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { renderEventsProfile } from '../../../deploy/k8s/profiles/events-and-sessions/render.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const readJson = relativePath => JSON.parse(readFileSync(path.join(root, relativePath), 'utf8'));
const runtimeConfig = readJson('deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json');
const inputExample = readJson('deploy/k8s/profiles/events-and-sessions/runtime-inputs.example.json');

test('Events profile accepts config-only Copilot model additions and carries the default rate card', () => {
  const configuration = Object.fromEntries(Object.entries(inputExample.configuration)
    .map(([key, value]) => [key, value ?? runtimeConfig.data[key]]));
  configuration.AgentHost__ModelBindingsRevision = 'runtime-models-2026-10-17';
  configuration.AgentHost__ModelBindings__reasoning__ModelId = 'gpt-5-mini';
  configuration.AgentHost__ModelBindings__analyst__ModelId = 'claude-haiku-5.5';
  configuration.AgentHost__ModelBindings__analyst__SourceMode = 'HostedCopilot';

  const rendered = renderEventsProfile({
    ...inputExample,
    image: `ghcr.io/sabbour/agentweaver.eventsandsessions@sha256:${'a'.repeat(64)}`,
    imageUser: 10001,
    imageGroup: 10001,
    tlsSecretName: 'events-and-sessions',
    configuration,
  });
  const configMap = rendered.items.find(item => item.kind === 'ConfigMap');
  const deployment = rendered.items.find(item => item.kind === 'Deployment');

  assert.equal(configMap.data.AgentHost__ModelBindings__reasoning__ModelId, 'gpt-5-mini');
  assert.equal(configMap.data.AgentHost__ModelBindings__analyst__ModelId, 'claude-haiku-5.5');
  assert.equal(configMap.data.EventsAndSessions__Cost__Copilot__RateCard__Json,
    runtimeConfig.data.EventsAndSessions__Cost__Copilot__RateCard__Json);
  assert.equal(configMap.immutable, false);
  assert.equal(deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name,
    configMap.metadata.name);
  const rolloutHash = deployment.spec.template.metadata.annotations['agentweaver.io/runtime-config-sha256'];
  assert.match(rolloutHash, /^[a-f0-9]{64}$/);

  const updatedConfiguration = { ...configuration };
  const rateCardKey = 'EventsAndSessions__Cost__Copilot__RateCard__Json';
  const updatedCard = JSON.parse(runtimeConfig.data[rateCardKey]);
  updatedCard.version = '2026-10-18';
  updatedCard.snapshotVersion = '2026-10-18';
  updatedCard.retrievedOn = '2026-10-18';
  updatedConfiguration[rateCardKey] = JSON.stringify(updatedCard);
  const updated = renderEventsProfile({
    ...inputExample,
    image: `ghcr.io/sabbour/agentweaver.eventsandsessions@sha256:${'a'.repeat(64)}`,
    imageUser: 10001,
    imageGroup: 10001,
    tlsSecretName: 'events-and-sessions',
    configuration: updatedConfiguration,
  });
  const updatedDeployment = updated.items.find(item => item.kind === 'Deployment');
  assert.notEqual(
    updatedDeployment.spec.template.metadata.annotations['agentweaver.io/runtime-config-sha256'],
    rolloutHash);
});
