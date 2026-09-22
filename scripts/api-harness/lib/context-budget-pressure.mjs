import { setTimeout as delay } from 'node:timers/promises';

import { prepareAiExecutionContext, replacementExecutionContext } from './seams.mjs';

const PROFILE = { maxItems: 1, maxTokens: 128 };
const DATASETS = [
  {
    id: 'item_limit',
    memories: [
      'item-limit-first-' + 'a'.repeat(48),
      'item-limit-second-' + 'b'.repeat(48),
      'item-limit-third-' + 'c'.repeat(48),
    ],
  },
  {
    id: 'token_budget_omission',
    memories: ['token-budget-' + 't'.repeat(900)],
  },
  {
    id: 'mandatory_context_budget_exceeded',
    decision: 'mandatory-budget-' + 'd'.repeat(900),
  },
];

function bodyOf(response) {
  return response.transientResponseBody ?? response.responseBody ?? {};
}

function assertStatus(response, expected, action) {
  if (response.status !== expected) {
    throw new Error(`${action} failed with status ${response.status}: ${JSON.stringify(response.responseBody)}`);
  }
}

async function createProject(client, dataset, signal) {
  const suffix = `${Date.now().toString(36)}-${dataset.id}`;
  const response = await client.post('/api/projects', {
    name: `Context budget ${dataset.id}`,
    origin: 'blank',
    working_directory: `context-budget-${suffix}`,
    blueprint_id: 'blueprint-software-development',
  }, { signal });
  assertStatus(response, 201, `${dataset.id} project creation`);
  return bodyOf(response).project_id;
}

async function createApprovedMemory(client, projectId, content, signal) {
  const created = await client.post(`/api/projects/${projectId}/agents/Coordinator/memory`, {
    type: 'learning',
    importance: 'high',
    content,
  }, { signal });
  assertStatus(created, 201, 'memory creation');
  const memoryId = bodyOf(created).id;
  const promoted = await client.post(
    `/api/projects/${projectId}/agents/Coordinator/memory/${memoryId}/promote`,
    {},
    { signal },
  );
  assertStatus(promoted, 200, 'memory promotion');
}

async function createMandatoryDecision(client, projectId, content, signal) {
  const response = await client.post(`/api/projects/${projectId}/decisions`, {
    agent_name: 'Coordinator',
    type: 'architectural',
    title: 'Mandatory context-budget boundary',
    content,
  }, { signal });
  assertStatus(response, 201, 'mandatory decision creation');
}

async function startOrchestration(client, projectId, datasetId, signal) {
  const context = await prepareAiExecutionContext(client, 'orchestration', projectId, { signal });
  if (!context.ready) {
    throw new Error(`orchestration execution context was not ready for ${datasetId}`);
  }
  const path = `/api/projects/${projectId}/orchestrations`;
  const body = {
    goal: `Validate deterministic ${datasetId} context-budget behavior without executing project work.`,
    start_mode: 'defineOutcome',
  };
  let response = await client.post(path, body, { headers: context.headers, signal });
  const replacement = replacementExecutionContext(response, 'orchestration');
  if (replacement?.ready) response = await client.post(path, body, { headers: replacement.headers, signal });
  assertStatus(response, 201, `${datasetId} orchestration start`);
  return bodyOf(response).runId;
}

function eventPayload(event) {
  return event?.payload?.payload ?? event?.payload ?? {};
}

async function pollEvidence(client, runId, datasetId, signal, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const eventsResponse = await client.get(`/api/runs/${runId}/events`, { signal });
    if (eventsResponse.ok) {
      const events = bodyOf(eventsResponse);
      const composition = events.find((event) => event.type === 'memory.context_composition');
      const failure = events.find((event) => event.type === 'run.failed');
      if (datasetId === 'item_limit' && composition) {
        const payload = eventPayload(composition);
        if (payload.omittedMemoryCount >= 1 && payload.omissionCauses?.includes('item_limit')) {
          return { event: composition, assertion: 'item_limit' };
        }
      }
      if (datasetId === 'token_budget_omission' && composition) {
        const payload = eventPayload(composition);
        if (payload.omittedMemoryCount >= 1 && payload.omissionCauses?.includes('budget')) {
          return { event: composition, assertion: 'budget' };
        }
      }
      if (datasetId === 'mandatory_context_budget_exceeded' && failure) {
        const payload = eventPayload(failure);
        if (payload.errorCode === 'mandatory_context_budget_exceeded') {
          return { event: failure, assertion: 'mandatory_context_budget_exceeded' };
        }
      }
    }
    await delay(500, undefined, { signal });
  }
  throw new Error(`${datasetId} did not emit the expected context-budget evidence within ${timeoutMs}ms.`);
}

async function cleanupDataset(client, lifecycle) {
  const errors = [];
  if (lifecycle.runId) {
    const cancelled = await client.post(`/api/runs/${lifecycle.runId}/cancel`, {});
    if (!cancelled.ok && cancelled.status !== 404) {
      errors.push(`run ${lifecycle.runId} cancellation failed with status ${cancelled.status}`);
    }
  }
  if (lifecycle.projectId) {
    const deleted = await client.del(`/api/projects/${lifecycle.projectId}?confirm=true`);
    if (!deleted.ok && deleted.status !== 404) {
      errors.push(`project ${lifecycle.projectId} deletion failed with status ${deleted.status}`);
    }
  }
  if (errors.length > 0) throw new AggregateError(errors.map((error) => new Error(error)), 'Dataset cleanup failed.');
}

async function runDataset(client, dataset, { signal, timeoutMs }) {
  const lifecycle = { projectId: null, runId: null };
  let primaryError = null;
  try {
    lifecycle.projectId = await createProject(client, dataset, signal);
    for (const content of dataset.memories ?? []) {
      await createApprovedMemory(client, lifecycle.projectId, content, signal);
    }
    if (dataset.decision) {
      await createMandatoryDecision(client, lifecycle.projectId, dataset.decision, signal);
    }
    lifecycle.runId = await startOrchestration(client, lifecycle.projectId, dataset.id, signal);
    const evidence = await pollEvidence(client, lifecycle.runId, dataset.id, signal, timeoutMs);
    return { dataset: dataset.id, projectId: lifecycle.projectId, runId: lifecycle.runId, ...evidence };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    try {
      await cleanupDataset(client, lifecycle);
    } catch (cleanupError) {
      const messages = cleanupError.errors?.map((error) => error.message) ?? [cleanupError.message];
      if (primaryError) primaryError.cleanupErrors = messages;
      else throw cleanupError;
    }
  }
}

export async function runContextBudgetPressure(client, options = {}) {
  const results = [];
  for (const dataset of DATASETS) {
    results.push(await runDataset(client, dataset, {
      signal: options.signal,
      timeoutMs: options.timeoutMs ?? 120_000,
    }));
  }
  return { profile: PROFILE, results };
}

export const contextBudgetPressureProfile = PROFILE;
