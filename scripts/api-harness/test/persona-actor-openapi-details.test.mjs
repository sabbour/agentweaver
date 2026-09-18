import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const actorPath = new URL('../../../.github/agents/persona-actor.agent.md', import.meta.url);

test('PersonaActor discovers a compact index before resolving only the selected operation', async () => {
  const actor = await readFile(actorPath, 'utf8');

  assert.match(actor, /openapi\/v1\.json/);
  assert.match(actor, /compact operation index/i);
  assert.match(actor, /method, path, tags, summary, and\s+`operationId`/i);
  assert.match(actor, /do \*\*not\*\* print complete path items or component objects/i);
  assert.match(actor, /parameters: expand\(parameters\)/);
  assert.match(actor, /requestBody: expand\(operation\.requestBody \?\? null\)/);
  assert.match(actor, /nested local `\$ref` values/i);
  assert.match(actor, /latest live response changes the next action/i);
  assert.match(actor, /Do not guess a path, method, parameter, or request shape/i);
});
