import test from 'node:test';
import assert from 'node:assert/strict';
import { createStdioTransport } from '../mcp-client/transport-stdio.mjs';

test('stdio transport does not apply URL-based target-guard validation', async () => {
  // Regression test: the 'stdio' string is a transport-selector sentinel, not
  // a network target, and must never reach assertTargetAllowed's URL parser.
  await assert.doesNotReject(() =>
    createStdioTransport({ command: 'node', args: ['--version'], target: 'stdio' }));
});

test('stdio transport works when target is omitted entirely', async () => {
  await assert.doesNotReject(() => createStdioTransport({ command: 'node', args: ['--version'] }));
});

test('stdio transport still requires a server command', async () => {
  await assert.rejects(() => createStdioTransport({ command: '' }), /server command is required/);
});
