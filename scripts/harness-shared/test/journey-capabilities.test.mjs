import test from 'node:test';
import assert from 'node:assert/strict';
import { requireJourneyCapabilities } from '../journey-capabilities.mjs';

test('requires exact discovered operations and UI adapters for each selected P1 journey', () => {
  const operations = ['readRunStatus', 'readSessionStatus', 'readDecisions', 'answerGate'];
  const adapters = {
    api: { operations: new Map(operations.map(name => [name, {}])) },
    mcp: { tools: new Map(operations.map(name => [`agentweaver_${name}`, {}])) },
    ui: { gate() {} },
  };
  assert.deepEqual(requireJourneyCapabilities(adapters, 'gate-answer').apiOperations, operations);
  assert.throws(() => requireJourneyCapabilities(adapters, 'gate-approve'), /api:approveGate, mcp:agentweaver_approveGate/);
  adapters.mcp.tools.delete('agentweaver_answerGate');
  assert.throws(() => requireJourneyCapabilities(adapters, 'gate-answer'), /mcp:agentweaver_answerGate/);
  assert.throws(() => requireJourneyCapabilities({}, 'gate-answer'), /Discover/);
  assert.throws(() => requireJourneyCapabilities(adapters, 'unknown'), /Unknown/);
});

test('journal capabilities are surface-specific and use the Gateway replay operation or the actual MCP/UI surface', () => {
  const adapters = {
    api: { operations: new Map([['replayRunEvents', {}]]) },
    mcp: { tools: new Map([['agentweaver_replayRunEvents', {}]]) },
    ui: { replayJournal() {} },
  };
  const required = requireJourneyCapabilities(adapters, 'journal');
  assert.deepEqual(required.apiOperations, ['replayRunEvents']);
  assert.deepEqual(required.mcpTools, ['agentweaver_replayRunEvents']);
  const api = requireJourneyCapabilities({ api: adapters.api }, 'journal', 'api');
  assert.equal(api.journey, 'journal');
  assert.deepEqual(api.apiOperations, ['replayRunEvents']);
  const mcp = requireJourneyCapabilities({ mcp: adapters.mcp }, 'journal', 'mcp');
  assert.equal(mcp.journey, 'journal');
  assert.deepEqual(mcp.apiOperations, []);
  assert.deepEqual(mcp.mcpTools, ['agentweaver_replayRunEvents']);
  const ui = requireJourneyCapabilities({ ui: adapters.ui }, 'journal', 'ui');
  assert.equal(ui.journey, 'journal');
  assert.deepEqual(ui.apiOperations, []);
  assert.deepEqual(ui.mcpTools, []);

  assert.throws(() => requireJourneyCapabilities({ api: { operations: new Map() } }, 'journal', 'api'),
    /api:replayRunEvents/);
  adapters.mcp.tools.clear();
  assert.throws(() => requireJourneyCapabilities({ mcp: adapters.mcp }, 'journal', 'mcp'),
    /mcp:agentweaver_replayRunEvents/);
});

test('surface checks require only that surface adapter while preserving shared live dependencies', () => {
  const api = { operations: new Map([['readRunStatus', {}], ['readSessionTree', {}], ['sendMessage', {}]]) };
  const mcp = { tools: new Map([
    ['agentweaver_readRunStatus', {}],
    ['agentweaver_readSessionTree', {}],
    ['agentweaver_sendMessage', {}],
  ]) };
  assert.equal(requireJourneyCapabilities({ api, mcp }, 'message', 'api').journey, 'message');
  assert.equal(requireJourneyCapabilities({ api, mcp }, 'message', 'mcp').journey, 'message');
  assert.throws(() => requireJourneyCapabilities({ api, mcp }, 'message', 'ui'), /ui:sendMessage/);
});
