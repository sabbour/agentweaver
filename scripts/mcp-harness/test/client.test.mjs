import test from 'node:test';
import assert from 'node:assert/strict';
import { McpHarnessClient } from '../mcp-client/client.mjs';
import { createHttpTransport } from '../mcp-client/transport-http.mjs';

test('MCP transport rejects missing or foreign broker/tenant metadata before any request', async () => {
  let requests = 0;
  const options = {
    target: 'https://controlled-mcp.test/mcp', tenantId: 'controlled-tenant',
    authProvider: { origin: 'https://controlled-mcp.test', getAuthorization: async () => 'not-used' },
    fetchImpl: async () => { requests++; throw new Error('No request is permitted.'); },
  };
  for (const invalid of [
    { authProvider: null },
    { authProvider: { ...options.authProvider, origin: 'https://foreign.test' } },
    { tenantId: '' },
  ]) {
    await assert.rejects(createHttpTransport({ ...options, ...invalid }), /origin-bound broker auth provider/);
  }
  assert.equal(requests, 0);
});

const tool = name => ({ name: `agentweaver_${name}`, inputSchema: { type: 'object' } });
const ownerResult = (status, ownerResponse, isError = status >= 400) => ({
  content: [{ type: 'text', text: `HTTP ${status}` }], structuredContent: { status, ownerResponse }, isError,
});

test('every live tool page is discovered and repeated cursors invalidate the menu', async () => {
  let repeated = false;
  const client = new McpHarnessClient({
    listTools: async ({ cursor }) => cursor
      ? { tools: [tool('getProject')], ...(repeated ? { nextCursor: cursor } : {}) }
      : { tools: [tool('listProjects')], nextCursor: 'second' },
  });
  assert.equal((await client.discoverTools()).length, 2);
  repeated = true;
  await assert.rejects(client.discoverTools(), /repeated/);
  assert.equal(client.tools.size, 0);
});

test('invalid, empty, foreign or duplicated tool catalogs are not success-shaped fallbacks', async () => {
  for (const tools of [undefined, [], [{ name: 'project_delete', inputSchema: { type: 'object' } }],
    [tool('getProject'), tool('getProject')]]) {
    const client = new McpHarnessClient({ listTools: async () => ({ tools }) });
    await assert.rejects(client.discoverTools());
    assert.equal(client.tools.size, 0);
  }
});

test('actual owner status and denial remain intact while credential fields stay out of retained evidence', async () => {
  let result = ownerResult(200, { projectId: 'p', token: 'credential-canary', inputTokens: 10 });
  const client = new McpHarnessClient({
    listTools: async () => ({ tools: [tool('getProject')] }),
    callTool: async () => result,
  });
  await client.discoverTools();
  const success = await client.callTool('agentweaver_getProject');
  assert.equal(success.transientResult.structuredContent.ownerResponse.token, 'credential-canary');
  assert.equal(success.result.structuredContent.ownerResponse.inputTokens, 10);
  assert.doesNotMatch(JSON.stringify(client.calls), /credential-canary/);
  result = ownerResult(409, { code: 'stale_request' });
  assert.equal((await client.callTool('agentweaver_getProject')).result.isError, true);
  result = ownerResult(202, { accepted: true });
  assert.equal((await client.callTool('agentweaver_getProject')).result.structuredContent.status, 202);
  result = ownerResult(200, { accepted: false }, false);
  await assert.rejects(client.callTool('agentweaver_getProject'), /accepted owner/);
  await assert.rejects(client.callTool('project_delete'), /not been discovered/);
});

test('missing owner results and protocol failures remain explicit failures', async () => {
  const client = new McpHarnessClient({
    listTools: async () => ({ tools: [tool('getProject')] }),
    callTool: async () => ({ content: [] }),
  });
  await client.discoverTools();
  await assert.rejects(client.callTool('agentweaver_getProject'), /owner-result/);
  assert.equal(client.calls[0].result, null);
  assert.ok(client.calls[0].error);
});
