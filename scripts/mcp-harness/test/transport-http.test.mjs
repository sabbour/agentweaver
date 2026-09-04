import assert from 'node:assert/strict';
import http from 'node:http';
import test from 'node:test';

import { noRedirectFetch } from '../mcp-client/transport-http.mjs';

async function listen(handler) {
  const server = http.createServer(handler);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return server;
}

test('MCP HTTP requests reject same-origin redirects without forwarding headers or bodies', async (t) => {
  const received = [];
  const server = await listen((req, res) => {
    if (req.url === '/mcp') {
      res.writeHead(307, { location: '/not-mcp' });
      res.end();
      return;
    }
    req.on('data', (chunk) => received.push(chunk));
    req.on('end', () => {
      received.push(req.headers.authorization ?? null);
      res.end();
    });
  });
  t.after(() => server.close());
  const { port } = server.address();
  await assert.rejects(
    noRedirectFetch(`http://127.0.0.1:${port}/mcp`, {
      method: 'POST',
      headers: { Authorization: 'Bearer redirect-canary' },
      body: '{"secret":"body-canary"}',
    }),
    /fetch failed|redirect/i,
  );
  assert.deepEqual(received, []);
});

test('MCP HTTP requests reject cross-origin redirects without forwarding headers or bodies', async (t) => {
  const received = [];
  const sink = await listen((req, res) => {
    req.on('data', (chunk) => received.push(chunk));
    req.on('end', () => {
      received.push(req.headers.authorization ?? null);
      res.end();
    });
  });
  const source = await listen((_req, res) => {
    res.writeHead(307, { location: `http://127.0.0.1:${sink.address().port}/not-mcp` });
    res.end();
  });
  t.after(() => source.close());
  t.after(() => sink.close());
  await assert.rejects(
    noRedirectFetch(`http://127.0.0.1:${source.address().port}/mcp`, {
      method: 'POST',
      headers: { Authorization: 'Bearer cross-origin-canary' },
      body: '{"secret":"cross-origin-body"}',
    }),
    /fetch failed|redirect/i,
  );
  assert.deepEqual(received, []);
});
