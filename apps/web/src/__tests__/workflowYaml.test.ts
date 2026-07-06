import { describe, expect, it } from 'vitest';
import {
  AUTHORABLE_WORKFLOW_NODE_TYPES,
  addNode,
  parseWorkflowYaml,
  setBranchTarget,
} from '../utils/workflowYaml';

const baseYaml = `
id: sample
name: Sample
start: implement
nodes:
  - id: implement
    type: prompt
    label: Implement
  - id: done
    type: terminal
    label: Done
edges: []
`;

describe('workflowYaml', () => {
  it('keeps merge and scribe parseable but out of the authorable palette', () => {
    expect(AUTHORABLE_WORKFLOW_NODE_TYPES).toContain('build_test');
    expect(AUTHORABLE_WORKFLOW_NODE_TYPES).not.toContain('merge');
    expect(AUTHORABLE_WORKFLOW_NODE_TYPES).not.toContain('scribe');

    const parsed = parseWorkflowYaml(`
id: legacy
name: Legacy
start: merge
nodes:
  - id: merge
    type: merge
  - id: scribe
    type: scribe
edges:
  - from: merge
    to: scribe
`);

    expect(parsed.error).toBeNull();
    expect(parsed.model?.nodes.map((n) => n.type)).toEqual(['merge', 'scribe']);
  });

  it('adds build_test without a prompt and routes fixed verdict edges', () => {
    let yaml = addNode(baseYaml, {
      id: 'build-test',
      type: 'build_test',
      label: 'Build & Test',
      role: 'review',
      kind: 'live',
      agent: 'qa-engineer',
    });
    yaml = setBranchTarget(yaml, 'build-test', 'approved', 'done');
    yaml = setBranchTarget(yaml, 'build-test', 'request-changes', 'implement');

    const parsed = parseWorkflowYaml(yaml);
    const buildTest = parsed.model?.nodes.find((n) => n.id === 'build-test');

    expect(buildTest).toMatchObject({
      type: 'build_test',
      label: 'Build & Test',
      agent: 'qa-engineer',
      prompt: undefined,
    });
    expect(parsed.model?.edges).toEqual(expect.arrayContaining([
      { from: 'build-test', to: 'done', when: 'approved' },
      { from: 'build-test', to: 'implement', when: 'request-changes' },
    ]));
  });

  it('adds special check gates with gate_kind and branches preserved', () => {
    const yaml = addNode(baseYaml, {
      id: 'rai-check',
      type: 'check',
      label: 'RAI Check',
      role: 'review',
      kind: 'gate',
      gate_kind: 'rai',
      branches: ['revise', 'safety-failed', 'no-changes', 'review'],
    });

    const node = parseWorkflowYaml(yaml).model?.nodes.find((n) => n.id === 'rai-check');

    expect(node).toMatchObject({
      type: 'check',
      gate_kind: 'rai',
      kind: 'gate',
      branches: ['revise', 'safety-failed', 'no-changes', 'review'],
    });
  });
});
