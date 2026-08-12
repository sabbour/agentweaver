import {
  addNode,
  AUTHORABLE_WORKFLOW_NODE_TYPES,
  getEventTrigger,
  getScheduleTrigger,
  NODE_TYPE_LABELS,
  parseWorkflowYaml,
  setBranchTarget,
  setEventTrigger,
  setNodeField,
  setScheduleTrigger,
  WORKFLOW_NODE_TYPES,
} from '../utils/workflowYaml';
import { describe, expect, it } from 'vitest';
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

  it('keeps the shared authoring contract aligned for pull-request and publish actions', () => {
    expect(WORKFLOW_NODE_TYPES).toContain('open_pull_request');
    expect(WORKFLOW_NODE_TYPES).toContain('publish');
    expect(AUTHORABLE_WORKFLOW_NODE_TYPES).toContain('open_pull_request');
    expect(AUTHORABLE_WORKFLOW_NODE_TYPES).toContain('publish');
    expect(NODE_TYPE_LABELS.open_pull_request).toBe('Open pull request');
    expect(NODE_TYPE_LABELS.publish).toBe('Publish');
  });

  it('round-trips pull-request and publish nodes without dropping action fields', () => {
    const actionYaml = `
id: actions
name: Actions
start: open-pr
nodes:
  - id: open-pr
    type: open_pull_request
    label: Open Pull Request
    title: "Agentweaver: {outcome_summary}"
    body: "Run {run_id}"
    base: dev
    head: feature/generated
    draft: true
  - id: publish
    type: publish
    label: Publish
    agent: content-author
    prompt: Package the approved content.
edges:
  - from: open-pr
    to: publish
`;

    const edited = setNodeField(actionYaml, 'publish', 'label', 'Publish output');
    const parsed = parseWorkflowYaml(edited);

    expect(parsed.model?.nodes.map((node) => node.type)).toEqual(['open_pull_request', 'publish']);
    expect(edited).toContain('title: "Agentweaver: {outcome_summary}"');
    expect(edited).toContain('body: "Run {run_id}"');
    expect(edited).toContain('base: dev');
    expect(edited).toContain('head: feature/generated');
    expect(edited).toContain('draft: true');
    expect(edited).toContain('label: Publish output');
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

  it('round-trips an event trigger with OR conditions and exact comment commands', () => {
    const yaml = setEventTrigger(baseYaml, {
      event: 'issue_comment',
      eventName: 'github.issue_comment',
      conditions: [
        { predicate: 'commentMatches', values: ['/agentweaver:triage', '/agentweaver:rerun'], matchAny: true },
      ],
    });

    expect(yaml).toContain('type: event');
    expect(yaml).toContain('event_name: github.issue_comment');
    expect(yaml).toContain('pattern: ^/agentweaver:triage$');
    expect(yaml).toContain('pattern: ^/agentweaver:rerun$');

    expect(getEventTrigger(yaml)).toEqual({
      event: 'issue_comment',
      eventName: 'github.issue_comment',
      conditions: [
        { predicate: 'commentMatches', values: ['/agentweaver:triage', '/agentweaver:rerun'], matchAny: true },
      ],
    });
  });

  it('parses event triggers with single-value predicates', () => {
    const parsed = getEventTrigger(`
id: triage
name: Triage
start: done
nodes: []
edges: []
trigger:
  type: event
  event_name: github.pull_request
  if:
    - has_label:
        label: agentweaver:triage
    - base_branch:
        branch: main
    - is_not_labeled_with:
        label: skip-triage
`);

    expect(parsed).toEqual({
      event: 'pull_request',
      eventName: 'github.pull_request',
      conditions: [
        { predicate: 'hasLabel', values: ['agentweaver:triage'], matchAny: false },
        { predicate: 'baseBranch', values: ['main'], matchAny: false },
        { predicate: 'isNotLabeledWith', values: ['skip-triage'], matchAny: false },
      ],
    });
  });

  it('keeps a schedule and event trigger together while editing either one', () => {
    const scheduled = setScheduleTrigger(baseYaml, {
      interval: 'weekly',
      dayOfWeek: 'monday',
      timeOfDay: '09:00',
    });
    const combined = setEventTrigger(scheduled, {
      event: 'issues',
      eventName: 'github.issues.labeled',
      conditions: [
        { predicate: 'hasLabel', values: ['roadmap-review'], matchAny: false },
      ],
    });

    expect(combined).toContain('triggers:');
    expect(combined).toContain('type: schedule');
    expect(combined).toContain('type: event');
    expect(getEventTrigger(combined)?.eventName).toBe('github.issues.labeled');

    const scheduleOnly = setEventTrigger(combined, null);
    expect(scheduleOnly).toContain('trigger:');
    expect(scheduleOnly).toContain('type: schedule');
    expect(scheduleOnly).not.toContain('type: event');
  });

  it('reads and updates schedule triggers without dropping unknown workflow fields', () => {
    const yaml = `# workflow comment
id: scheduled
name: Scheduled
custom_field: preserved
start: done
triggers:
  - type: event
    event_name: github.issues
  - type: schedule
    interval: monthly
    day_of_month: 12
    time_of_day: "14:45"
nodes: []
edges: []
`;

    expect(getScheduleTrigger(yaml)).toEqual({
      interval: 'monthly',
      timeOfDay: '14:45',
      dayOfWeek: undefined,
      dayOfMonth: 12,
    });

    const updated = setScheduleTrigger(baseYaml, {
      interval: 'weekly',
      dayOfWeek: 'friday',
      timeOfDay: '08:15',
    });
    expect(getScheduleTrigger(updated)).toEqual({
      interval: 'weekly',
      timeOfDay: '08:15',
      dayOfWeek: 'friday',
      dayOfMonth: undefined,
    });
  });
});
