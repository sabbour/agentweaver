import assert from 'node:assert/strict';
import test from 'node:test';

import { McpHarnessClient } from '../mcp-client/client.mjs';

function fakeSdk() {
  const calls = [];
  return {
    calls,
    listTools: async () => ({ tools: [{ name: 'project_list' }, { name: 'project_delete' }] }),
    callTool: async (request) => {
      calls.push(request);
      return { structuredContent: { status: 'deleted' } };
    },
  };
}

test('dynamic persona action surface removes project_delete when it owns no project', async () => {
  const sdk = fakeSdk();
  const client = new McpHarnessClient(sdk, {}, { ownedProjectId: null });
  assert.deepEqual((await client.discoverTools()).map((tool) => tool.name), ['project_list']);
  assert.deepEqual((await client.discoverAllTools()).map((tool) => tool.name), ['project_list', 'project_delete']);
});

test('dynamic persona cannot bypass deletion ownership with an explicit caller project id', async () => {
  const sdk = fakeSdk();
  const client = new McpHarnessClient(sdk, {}, { ownedProjectId: 'harness-owned' });
  const denied = await client.callTool('project_delete', { project_id: 'caller-project' });
  assert.equal(denied.isError, true);
  assert.equal(sdk.calls.length, 0);

  const allowed = await client.callTool('project_delete', { project_id: 'harness-owned' });
  assert.equal(allowed.isError, false);
  assert.equal(sdk.calls.length, 1);
});

test('client returns only recursively redacted tool evidence', async () => {
  const canary = 'token-canary-client-77';
  const sdk = {
    callTool: async () => ({
      content: [{ type: 'text', text: `Bearer ${canary}` }],
      structuredContent: {
        headers: { Authorization: `Bearer ${canary}` },
        nested: { url: `https://user:${canary}@example.test/path?q=${canary}#${canary}` },
      },
    }),
  };
  const client = new McpHarnessClient(sdk, {});
  const result = await client.callTool('inspect', { token: canary });
  assert.doesNotMatch(JSON.stringify(result), new RegExp(canary));
  assert.match(result.structuredContent.nested.url, /^https:\/\/example\.test\/path$/);
});
