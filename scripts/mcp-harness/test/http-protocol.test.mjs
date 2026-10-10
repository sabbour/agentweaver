import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { CallToolRequestSchema, ListToolsRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { McpHarnessClient } from '../mcp-client/client.mjs';

test('native SDK uses actual local HTTP JSON-RPC discovery, correlated results and explicit denial', { timeout: 15000 }, async () => {
  const ownerCalls = [];
  const requests = [];
  const server = new Server({ name: 'controlled-p1-protocol-fixture', version: '0.1.0' }, {
    capabilities: { tools: {} },
  });
  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools: [{
      name: 'agentweaver_getProject',
      inputSchema: { type: 'object', properties: { stale: { type: 'boolean' } } },
    }],
  }));
  server.setRequestHandler(CallToolRequestSchema, async request => {
    ownerCalls.push(request.params);
    const stale = request.params.arguments?.stale === true;
    return {
      content: [{ type: 'text', text: stale ? 'stale_request' : 'controlled_project' }],
      structuredContent: {
        status: stale ? 409 : 200,
        ownerResponse: stale ? { code: 'stale_request' } : {
          projectId: 'controlled-project', inputTokens: 12, token: 'credential-canary',
        },
      },
      isError: stale,
    };
  });
  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: () => randomUUID(), enableJsonResponse: true,
  });
  await server.connect(transport);
  const authorization = ['Bearer', 'controlled-protocol-fixture'].join(' ');
  const http = createServer((request, response) => {
    requests.push({ method: request.method, url: request.url, authorization: request.headers.authorization });
    if (request.headers.authorization !== authorization
      || request.headers['x-agentweaver-tenant'] !== 'controlled-tenant') {
      response.writeHead(401);
      response.end();
      return;
    }
    void transport.handleRequest(request, response).catch(error => {
      if (!response.headersSent) response.writeHead(500);
      response.end();
      http.emit('fixture-error', error);
    });
  });
  const fixtureErrors = [];
  http.on('fixture-error', error => fixtureErrors.push(error));
  http.listen(0, '127.0.0.1');
  await once(http, 'listening');
  const target = `http://127.0.0.1:${http.address().port}/mcp`;
  const options = {
    target, tenantId: 'controlled-tenant',
    authProvider: { origin: new URL(target).origin, getAuthorization: async () => authorization },
  };
  let client;
  try {
    client = await McpHarnessClient.connect(options);
    assert.equal((await client.discoverTools())[0].name, 'agentweaver_getProject');
    const success = await client.callTool('agentweaver_getProject');
    assert.equal(success.result.structuredContent.status, 200);
    assert.equal(success.result.structuredContent.ownerResponse.inputTokens, 12);
    const denial = await client.callTool('agentweaver_getProject', { stale: true });
    assert.equal(denial.result.isError, true);
    assert.equal(denial.result.structuredContent.status, 409);
    assert.equal(ownerCalls.length, 2);
    const callIds = client.protocol.filter(item => item.direction === 'request'
      && item.message.method === 'tools/call').map(item => item.message.id);
    const replyIds = client.protocol.filter(item => item.direction === 'response')
      .map(item => item.message.id);
    const initialize = client.protocol.find(item => item.direction === 'request'
      && item.message.method === 'initialize');
    assert.ok(initialize);
    assert.ok(replyIds.includes(initialize.message.id));
    assert.equal(callIds.length, 2);
    assert.ok(callIds.every(id => replyIds.includes(id)));
    assert.doesNotMatch(JSON.stringify({ calls: client.calls, protocol: client.protocol }), /credential-canary/);
    await assert.rejects(McpHarnessClient.connect({
      ...options,
      authProvider: { ...options.authProvider, getAuthorization: async () => ['Bearer', 'wrong-fixture'].join(' ') },
    }));
    assert.ok(requests.some(request => request.authorization !== authorization));
    assert.equal(fixtureErrors.length, 0);
  } finally {
    const failures = [];
    for (const close of [
      () => client?.close(), () => server.close(),
      () => new Promise((resolve, reject) => {
        http.close(error => error ? reject(error) : resolve());
        http.closeAllConnections();
      }),
    ]) {
      try {
        await close();
      } catch (error) {
        failures.push(error);
      }
    }
    if (failures.length) throw new AggregateError(failures, 'Controlled MCP fixture cleanup failed.');
  }
});
