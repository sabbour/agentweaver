// Unit tests for the generated-artifact validators (lib/generation-checks.mjs).
//
// These are the automated guards for the class of bug a human had to catch by hand:
//   • issue #311 — a generated roster leaking a reserved system role;
//   • structurally-broken generated workflows (dangling edges, unrouted check
//     branches, unknown node types, serial steps that reference nothing).
//
// Every positive fixture asserts a KNOWN-GOOD artifact passes; every negative fixture
// asserts a KNOWN-BAD artifact fails — so the checks would meaningfully catch a
// regression rather than rubber-stamp anything.
//
// Run: node --test test/   (from scripts/persona-harness)

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  isReservedRole,
  findReservedRoleLeaks,
  validateWorkflowYaml,
  workflowNodeRoles,
} from '../lib/generation-checks.mjs';

// ── Reserved-role denylist (mirror of ReservedRoles.cs) ─────────────────────────

test('isReservedRole matches every reserved id and display-name variant', () => {
  for (const v of ['Scribe', 'scribe', 'Ralph', 'ralph', 'Rai', 'rai', 'rai-reviewer', 'Coordinator', 'coordinator']) {
    assert.equal(isReservedRole(v), true, `${v} should be reserved`);
  }
  // Work Monitor: id form + spaced/underscored display-name variants must all normalize.
  for (const v of ['work-monitor', 'Work Monitor', 'work_monitor', 'WORK MONITOR']) {
    assert.equal(isReservedRole(v), true, `${v} should normalize to a reserved role`);
  }
});

test('isReservedRole allows ordinary domain roles', () => {
  for (const v of ['backend-engineer', 'Frontend Engineer', 'qa', 'release-engineer', 'product-analyst', '', null, undefined]) {
    assert.equal(isReservedRole(v), false, `${v} should NOT be reserved`);
  }
});

test('findReservedRoleLeaks catches a reserved role in a generated roster (issue #311)', () => {
  const leak = findReservedRoleLeaks({
    roster: ['backend-engineer', 'qa', 'Scribe'], // Scribe leaked into a domain roster
    bespoke_roles: [{ id: 'release-engineer', title: 'Release Engineer' }],
  });
  assert.equal(leak.offenders.length, 1);
  assert.equal(leak.offenders[0], 'Scribe');
});

test('findReservedRoleLeaks catches a reserved bespoke role by id OR title', () => {
  const byId = findReservedRoleLeaks({ roster: ['backend'], bespoke_roles: [{ id: 'work-monitor', title: 'Backlog Watcher' }] });
  assert.deepEqual(byId.offenders, ['work-monitor']);
  const byTitle = findReservedRoleLeaks({ roster: ['backend'], bespoke_roles: [{ id: 'watcher', title: 'Coordinator' }] });
  assert.deepEqual(byTitle.offenders, ['Coordinator']);
});

test('findReservedRoleLeaks passes a clean domain roster', () => {
  const clean = findReservedRoleLeaks({
    roster: ['product-analyst', 'backend-engineer', 'frontend-engineer', 'qa', 'release-engineer'],
    bespoke_roles: [{ id: 'release-engineer', title: 'Release Engineer' }],
  });
  assert.deepEqual(clean.offenders, []);
});

// ── Workflow YAML structural validation (mirror of WorkflowDefinitionLoader) ────

const VALID_WORKFLOW = `
id: deliver-and-review
name: Deliver and Review
start: design
nodes:
  - id: design
    type: prompt
    role: architect
  - id: implement
    type: prompt
    role: backend-engineer
  - id: test
    type: build_test
  - id: gate
    type: check
    branches: [pass, fail]
  - id: deploy
    type: prompt
    role: release-engineer
  - id: done
    type: terminal
edges:
  - { from: design, to: implement }
  - { from: implement, to: test }
  - { from: test, to: gate }
  - { from: gate, to: deploy, when: pass }
  - { from: gate, to: implement, when: fail }
  - { from: deploy, to: done }
`;

test('validateWorkflowYaml accepts a well-formed workflow with a routed check gate', () => {
  const v = validateWorkflowYaml(VALID_WORKFLOW);
  assert.equal(v.valid, true, `expected valid; errors: ${v.errors.join('; ')}`);
  assert.equal(v.nodeCount, 6);
});

test('validateWorkflowYaml rejects a dangling edge (target node does not exist)', () => {
  const bad = VALID_WORKFLOW.replace('{ from: deploy, to: done }', '{ from: deploy, to: nonexistent }');
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(v.errors.some((e) => e.includes("unknown target node 'nonexistent'")), v.errors.join('; '));
});

test('validateWorkflowYaml rejects a check node with an unrouted verdict', () => {
  // Declare a 'fail' branch but remove its outgoing edge — the exact FR-016 rule.
  const bad = VALID_WORKFLOW.replace('  - { from: gate, to: implement, when: fail }\n', '');
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(
    v.errors.some((e) => e.includes("check node 'gate' declares verdict 'fail'")),
    v.errors.join('; '),
  );
});

test('validateWorkflowYaml rejects a serial node referencing a nonexistent step', () => {
  const bad = `
id: s
name: S
start: seq
nodes:
  - id: seq
    type: serial
    steps: [a, ghost]
  - id: a
    type: prompt
edges: []
`;
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(v.errors.some((e) => e.includes("unknown step 'ghost'")), v.errors.join('; '));
});

test('validateWorkflowYaml rejects an unknown node type and a missing start reference', () => {
  const badType = validateWorkflowYaml(`
id: x
name: X
start: a
nodes:
  - id: a
    type: wizardry
edges: []
`);
  assert.equal(badType.valid, false);
  assert.ok(badType.errors.some((e) => e.includes("unknown type 'wizardry'")));

  const badStart = validateWorkflowYaml(`
id: x
name: X
start: nope
nodes:
  - id: a
    type: prompt
edges: []
`);
  assert.equal(badStart.valid, false);
  assert.ok(badStart.errors.some((e) => e.includes("'start' references unknown node 'nope'")));
});

test('validateWorkflowYaml rejects empty / missing required fields', () => {
  const empty = validateWorkflowYaml('');
  assert.equal(empty.valid, false);
  const noId = validateWorkflowYaml('name: X\nstart: a\nnodes:\n  - id: a\n    type: prompt\nedges: []\n');
  assert.equal(noId.valid, false);
  assert.ok(noId.errors.some((e) => e.includes("missing required field 'id'")));
});

test('validateWorkflowYaml rejects a stage missing its id (backend mirror)', () => {
  const bad = `${VALID_WORKFLOW}
stages:
  - label: Ready
    order: 0
`;
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(v.errors.some((e) => e.includes("a stage is missing its required 'id'")), v.errors.join('; '));
});

test('validateWorkflowYaml rejects a stage missing its label (backend mirror)', () => {
  const bad = `${VALID_WORKFLOW}
stages:
  - id: ready
    order: 0
`;
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(v.errors.some((e) => e.includes("stage 'ready' is missing its required 'label'")), v.errors.join('; '));
});

test('workflowNodeRoles extracts declared role/agent ids, and reserved leakage is caught', () => {
  const wf = `
id: w
name: W
start: a
nodes:
  - id: a
    type: prompt
    role: backend-engineer
  - id: b
    type: prompt
    agent: Scribe
edges: [{ from: a, to: b }]
`;
  const roles = workflowNodeRoles(wf);
  assert.deepEqual(roles.sort(), ['Scribe', 'backend-engineer']);
  const leaks = findReservedRoleLeaks({ workflowRoles: roles });
  assert.deepEqual(leaks.offenders, ['Scribe']);
});
