import test from 'node:test';
import assert from 'node:assert/strict';
import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';

test('MCP HTTP targets use host-agnostic transport validation and exact path semantics', () => {
  assert.doesNotThrow(() => validateNetworkTarget('https://arbitrary.example/mcp', { exactPath: '/mcp' }));
  assert.doesNotThrow(() => validateNetworkTarget('http://localhost:5000/mcp', { exactPath: '/mcp' }));
  assert.throws(() => validateNetworkTarget('http://remote.example/mcp', { exactPath: '/mcp' }), /HTTPS/);
  assert.throws(() => validateNetworkTarget('https://remote.example/mcp/', { exactPath: '/mcp' }), /exactly/);
});
