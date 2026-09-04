import assert from 'node:assert/strict';
import test from 'node:test';
import { convertSequenceDiagram } from './mermaid-to-sequencespec.mjs';

test('converts calls, returns, self messages, activations, notes, and fragments', () => {
  const result = convertSequenceDiagram(`
sequenceDiagram
  autonumber
  actor User as Human reviewer
  participant API as Agentweaver API
  participant DB as Event store
  User->>+API: start
  API->>API: validate
  Note over API,DB: durable boundary
  alt found
    API-->>User: ready
  else missing
    loop retry
      API->>DB: poll
    end
  end
  API-->>-User: done
`, { title: 'Example' });

  assert.ok(result);
  assert.equal(result.warnings.length, 0);
  assert.equal(result.spec.kind, 'sequence');
  assert.equal(result.spec.autonumber, true);
  assert.deepEqual(result.spec.participants.map((p) => p.id), ['User', 'API', 'DB']);
  assert.equal(result.spec.steps[0].type, 'activation');
  assert.equal(result.spec.steps[1].type, 'message');
  assert.equal(result.spec.steps[2].from, 'API');
  assert.equal(result.spec.steps[2].to, 'API');
  assert.equal(result.spec.steps[4].operator, 'alt');
  assert.equal(result.spec.steps[4].sections.length, 2);
  assert.equal(result.spec.steps.at(-1).type, 'activation');
});

test('parses dashed cross messages without inventing participants', () => {
  const result = convertSequenceDiagram(`
sequenceDiagram
  participant A
  participant DB
  A--xDB: heartbeat stops
`);

  assert.ok(result);
  assert.deepEqual(result.spec.participants.map((p) => p.id), ['A', 'DB']);
  assert.equal(result.spec.steps[0].line, 'dashed');
  assert.equal(result.spec.steps[0].arrow, 'cross');
});
