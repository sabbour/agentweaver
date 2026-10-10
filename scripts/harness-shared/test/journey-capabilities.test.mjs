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

test('journal requires API SSE support but never invents the deliberately unadvertised MCP SSE tool', () => {
  const adapters = {
    api: { operations: new Map([['replayRunEvents', {}], ['streamRunEvents', {}]]) },
    mcp: { tools: new Map([['agentweaver_replayRunEvents', {}]]) },
    ui: { replayJournal() {} },
  };
  const required = requireJourneyCapabilities(adapters, 'journal');
  assert.deepEqual(required.mcpTools, ['agentweaver_replayRunEvents']);
  adapters.api.operations.delete('streamRunEvents');
  assert.throws(() => requireJourneyCapabilities(adapters, 'journal'), /api:streamRunEvents/);
});
