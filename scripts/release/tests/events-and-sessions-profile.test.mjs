// Ensures the Events profile validates runtime data while Kustomize owns all workloads and policies.
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

test('Events runtime values preserve validated model additions and the default rate card', () => {
  const configuration = Object.fromEntries(Object.entries(inputExample.configuration)
    .map(([key, value]) => [key, value ?? runtimeConfig.data[key]]));
  configuration.AgentHost__ModelBindingsRevision = 'runtime-models-2026-10-17';
  configuration.AgentHost__ModelBindings__reasoning__ModelId = 'gpt-5-mini';
  configuration.AgentHost__ModelBindings__analyst__ModelId = 'gpt-5.4-mini';
  configuration.AgentHost__ModelBindings__analyst__SourceMode = 'HostedCopilot';

  const result = renderEventsProfile({ configuration });

  assert.equal(result.data.AgentHost__ModelBindings__reasoning__ModelId, 'gpt-5-mini');
  assert.equal(result.data.AgentHost__ModelBindings__analyst__ModelId, 'gpt-5.4-mini');
  assert.equal(result.data.EventsAndSessions__Cost__Copilot__RateCard__Json,
    runtimeConfig.data.EventsAndSessions__Cost__Copilot__RateCard__Json);
  assert.equal(result.items, undefined);
});

test('Events runtime profile input cannot generate a Deployment, Service, or policy', () => {
  const configuration = Object.fromEntries(Object.entries(inputExample.configuration)
    .map(([key, value]) => [key, value ?? runtimeConfig.data[key]]));

  assert.throws(() => renderEventsProfile({
    configuration,
    image: 'ghcr.io/sabbour/agentweaver.eventsandsessions:latest',
  }), /unsupported field/);
});
