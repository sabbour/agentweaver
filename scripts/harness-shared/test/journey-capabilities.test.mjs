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

test('journal uses the discovered MCP replay tool without inventing an API SSE route', () => {
  const adapters = {
    api: { operations: new Map() },
    mcp: { tools: new Map([['agentweaver_replayRunEvents', {}]]) },
    ui: { replayJournal() {} },
  };
  const required = requireJourneyCapabilities(adapters, 'journal');
  assert.deepEqual(required.apiOperations, []);
  assert.deepEqual(required.mcpTools, ['agentweaver_replayRunEvents']);
  adapters.mcp.tools.clear();
  assert.throws(() => requireJourneyCapabilities(adapters, 'journal'), /mcp:agentweaver_replayRunEvents/);
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
