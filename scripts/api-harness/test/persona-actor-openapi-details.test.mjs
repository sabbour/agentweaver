import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const actorPath = new URL('../../../.github/agents/persona-actor.agent.md', import.meta.url);
const harnessPath = new URL('../../../.github/agents/harness.agent.md', import.meta.url);
const skillPath = new URL('../SKILL.md', import.meta.url);
const oracleAdapterPath = new URL('../../persona-briefs/surfaces/oracle.api.md', import.meta.url);

test('API harness guidance discovers a compact index before resolving only the selected operation', async () => {
  const [actor, harness, skill, oracleAdapter] = await Promise.all([
    readFile(actorPath, 'utf8'),
    readFile(harnessPath, 'utf8'),
    readFile(skillPath, 'utf8'),
    readFile(oracleAdapterPath, 'utf8'),
  ]);

  assert.match(actor, /openapi\/v1\.json/);
  assert.match(actor, /compact operation index/i);
  assert.match(actor, /method, path, tags, summary, and\s+`operationId`/i);
  assert.match(actor, /do \*\*not\*\* print complete path items or component objects/i);
  assert.match(actor, /parameters: expand\(parameters\)/);
  assert.match(actor, /requestBody: expand\(operation\.requestBody \?\? null\)/);
  assert.match(actor, /nested local `\$ref` values/i);
  assert.match(actor, /latest live response changes the next action/i);
  assert.match(actor, /Do not guess a path, method, parameter, or request shape/i);

  for (const guidance of [harness, skill, oracleAdapter]) {
    assert.match(guidance, /openapi\/v1\.json/);
    assert.match(guidance, /compact.*(?:method\/path|method,? ?path).*tags.*summary.*operationId/is);
    assert.match(guidance, /selects?\s+an\s+operation.*(?:latest real response|persona goal)/is);
    assert.match(guidance, /(?:only|selected operation's).*parameters.*(?:resolved local|request-schema)/is);
    assert.doesNotMatch(guidance, /openapi\/v1\.yaml/);
  }
});
