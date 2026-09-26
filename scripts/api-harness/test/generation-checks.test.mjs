// Unit tests for the generated-artifact validators (lib/generation-checks.mjs).
//
// These are the automated guards for the class of bug a human had to catch by hand:
//   • issue #311 — a generated roster leaking a reserved system role;
//   • structurally-broken generated workflows (dangling edges, unrouted check
//     branches, unsupported node types, unknown node types).
//
// Every positive fixture asserts a KNOWN-GOOD artifact passes; every negative fixture
// asserts a KNOWN-BAD artifact fails — so the checks would meaningfully catch a
// regression rather than rubber-stamp anything.
//
// Run: node --test test/   (from scripts/persona-harness)

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  analyzeConservativeFan,
  isReservedRole,
  findReservedRoleLeaks,
  validateWorkflowYaml,
  workflowNodeRoles,
} from '../lib/generation-checks.mjs';

const SAFE_FAN_YAML = `
id: generated-research
name: Generated research
start: fan
nodes:
  - id: fan
    type: fan_out
  - id: customers
    type: prompt
    prompt: Research customer signals and write only reports/customer-signals.md.
    independent: true
    declared_output_paths:
      - reports/customer-signals.md
  - id: technical
    type: prompt
    prompt: Research technical feasibility and write only reports/technical-feasibility.md.
    independent: true
    declared_output_paths:
      - reports/technical-feasibility.md
  - id: join
    type: fan_in
    target: fan
  - id: synthesis
    type: prompt
    prompt: Synthesize the ordered research.
  - id: done
    type: terminal
edges:
  - from: fan
    to: customers
  - from: fan
    to: technical
  - from: customers
    to: join
  - from: technical
    to: join
  - from: join
    to: synthesis
  - from: synthesis
    to: done
`;

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

test('validateWorkflowYaml rejects serial nodes with sequential-edge guidance', () => {
  const bad = `
id: s
name: S
start: seq
nodes:
  - id: seq
    type: serial
    steps: [a]
  - id: a
    type: prompt
edges: []
`;
  const v = validateWorkflowYaml(bad);
  assert.equal(v.valid, false);
  assert.ok(v.errors.some((e) => e.includes("unsupported node type 'serial'")), v.errors.join('; '));
  assert.ok(v.errors.some((e) => e.includes('ordinary workflow edges')), v.errors.join('; '));
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

test('analyzeConservativeFan accepts explicit independent disjoint research outputs', () => {
  const result = analyzeConservativeFan(SAFE_FAN_YAML);

  assert.equal(result.mode, 'fan');
  assert.equal(result.safe, true);
  assert.deepEqual(result.branchIds, ['customers', 'technical']);
  assert.deepEqual(result.outputPaths, [
    'reports/customer-signals.md',
    'reports/technical-feasibility.md',
  ]);
});

test('analyzeConservativeFan accepts ordinary sequential generation', () => {
  const result = analyzeConservativeFan(VALID_WORKFLOW);

  assert.equal(result.mode, 'sequential');
  assert.equal(result.safe, true);
});

for (const [name, transform, expected] of [
  ['unknown scope', (yaml) => yaml.replace(/    declared_output_paths:\n      - reports\/technical-feasibility\.md\n/, ''), 'no declared_output_paths'],
  ['case-normalized overlap', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- REPORTS/CUSTOMER-SIGNALS.MD'), 'overlapping output paths'],
  ['file-directory prefix overlap', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- reports/customer-signals.md/source.md'), 'overlapping output paths'],
  ['broad scope', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- docs'), 'unknown, dynamic, broad, or shared'],
  ['shared manifest', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- package.json'), 'unknown, dynamic, broad, or shared'],
  ['generated artifact', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- generated/report.md'), 'unknown, dynamic, broad, or shared'],
  ['hidden shared output', (yaml) => yaml.replaceAll('reports/technical-feasibility.md', '.shared/technical-feasibility.md'), 'unknown, dynamic, broad, or shared'],
  ['code output', (yaml) => yaml.replaceAll('reports/technical-feasibility.md', 'reports/technical-feasibility.ts'), 'unknown, dynamic, broad, or shared'],
  ['dynamic scope', (yaml) => yaml.replace('- reports/technical-feasibility.md', '- reports/${topic}.md'), 'unknown, dynamic, broad, or shared'],
  ['basename reference', (yaml) => yaml.replace('Research technical feasibility', 'Incorporate findings from customer-signals.md while researching technical feasibility'), 'appears to depend on a sibling'],
  ['sibling id reference', (yaml) => yaml.replace('Research technical feasibility', 'Use results from customers while researching technical feasibility'), 'appears to depend on a sibling'],
  ['undeclared prompt target', (yaml) => yaml.replace('and write only reports/technical-feasibility.md.', 'and write only reports/technical-feasibility.md. Also write reports/shared-summary.md.'), 'references undeclared output paths'],
]) {
  test(`analyzeConservativeFan rejects ${name}`, () => {
    const result = analyzeConservativeFan(transform(SAFE_FAN_YAML));

    assert.equal(result.mode, 'fan');
    assert.equal(result.safe, false);
    assert.ok(result.errors.some((error) => error.includes(expected)), result.errors.join('; '));
  });
}

for (const [name, transform, expected] of [
  ['malformed continuation', (yaml) => yaml.replace(
    '  - from: join\n    to: synthesis',
    '  - from: join\n    to: synthesis\n  - from: join\n    to: done',
  ), 'exactly one unconditional continuation'],
  ['extra join input', (yaml) => yaml.replace(
    '  - from: join\n    to: synthesis',
    '  - from: synthesis\n    to: join\n  - from: join\n    to: synthesis',
  ), 'inputs must exactly match'],
  ['conditional fan edge', (yaml) => yaml.replace(
    '  - from: fan\n    to: technical',
    '  - from: fan\n    to: technical\n    when: approved',
  ), 'unconditional branches'],
  ['cyclic continuation', (yaml) => yaml.replace(
    '  - from: synthesis\n    to: done',
    '  - from: synthesis\n    to: done\n  - from: synthesis\n    to: synthesis',
  ), 'contains a cycle'],
]) {
  test(`analyzeConservativeFan rejects ${name}`, () => {
    const result = analyzeConservativeFan(transform(SAFE_FAN_YAML));

    assert.equal(result.mode, 'invalid');
    assert.equal(result.safe, false);
    assert.ok(result.errors.some((error) => error.includes(expected)), result.errors.join('; '));
  });
}

test('analyzeConservativeFan rejects prohibited generated orchestration nodes', () => {
  const result = analyzeConservativeFan(SAFE_FAN_YAML.replace('type: prompt', 'type: coordinator_composed'));

  assert.equal(result.mode, 'invalid');
  assert.equal(result.safe, false);
});
