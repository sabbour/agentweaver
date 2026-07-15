import { describe, expect, it, vi } from 'vitest';
import { buildRunTimeline } from '../timeline/runTimelineSteps';
import type { RunStreamEvent } from '../api/sse';

const evt = (sequence: number, type: string, payload: Record<string, unknown>): RunStreamEvent =>
  ({ sequence, type: type as RunStreamEvent['type'], payload });

describe('buildRunTimeline', () => {
  it('groups tool calls and messages under the owning agent.intent step', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.turn.start', { turnId: 't1' }),
      evt(2, 'agent.intent', { intent: 'Read the code' }),
      evt(3, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } }),
      evt(4, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(5, 'agent.intent', { intent: 'Build the project' }),
      evt(6, 'tool.call', { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm run build' } }),
      evt(7, 'tool.error', { callId: 'c2', errorMessage: 'exit code 1' }),
      evt(8, 'agent.message', { messageId: 'm1', content: 'Build failed.' }),
      evt(9, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(2);
    expect(model.steps[0].intent).toBe('Read the code');
    expect(model.steps[1].intent).toBe('Build the project');

    // Tools nest under the intent that was open when they ran.
    expect(model.steps[0].tools).toHaveLength(1);
    expect(model.steps[0].tools[0].status).toBe('complete');
    expect(model.steps[1].tools).toHaveLength(1);
    expect(model.steps[1].tools[0].status).toBe('error');

    // Agent messages assemble under the current step.
    expect(model.steps[1].messages).toHaveLength(1);
    expect(model.steps[1].messages[0].text).toBe('Build failed.');
  });

  it('orders out-of-order events by sequence before grouping', () => {
    const model = buildRunTimeline([
      evt(4, 'tool.result', { callId: 'c1', content: 'done' }),
      evt(2, 'agent.intent', { intent: 'Do the thing' }),
      evt(3, 'tool.call', { callId: 'c1', toolName: 'grep_search', arguments: { pattern: 'foo' } }),
      evt(1, 'agent.turn.start', { turnId: 't1' }),
      evt(5, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(1);
    expect(model.steps[0].tools[0].status).toBe('complete');
  });

  it('assembles agent.message.delta chunks into one message and settles on agent.message', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Explain' }),
      evt(2, 'agent.message.delta', { messageId: 'm1', delta: 'Hello ' }),
      evt(3, 'agent.message.delta', { messageId: 'm1', delta: 'world' }),
      evt(4, 'agent.message', { messageId: 'm1', content: 'Hello world' }),
      evt(5, 'agent.turn.end', {}),
    ]);

    expect(model.steps[0].messages).toHaveLength(1);
    expect(model.steps[0].messages[0].text).toBe('Hello world');
    expect(model.steps[0].messages[0].streaming).toBe(false);
  });

  it('stores a message timestamp for relative-time rendering', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-14T12:45:00.000Z'));
    try {
      const eventTime = '2026-07-14T12:42:00.000Z';
      const model = buildRunTimeline([
        evt(1, 'agent.intent', { intent: 'Explain' }),
        evt(2, 'agent.message', { messageId: 'm1', content: 'Hello world', timestamp_utc: eventTime }),
        evt(3, 'agent.turn.end', {}),
      ]);

      expect(model.steps[0].messages[0].timestamp).toBe(new Date(eventTime).getTime());
    } finally {
      vi.useRealTimers();
    }
  });

  it('does not duplicate report_intent as a tool row', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Plan the work' }),
      evt(2, 'tool.call', { callId: 'r1', toolName: 'report_intent', arguments: { intent: 'Plan the work' } }),
      evt(3, 'agent.turn.end', {}),
    ]);

    expect(model.steps[0].tools).toHaveLength(0);
  });

  it('marks a step running until its turn ends', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Working now' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'a.ts' } }),
    ]);

    expect(model.steps[0].active).toBe(true);
    expect(model.steps[0].status).toBe('running');
    expect(model.running).toBe(true);
  });

  it('flags sandbox violations on tool errors', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Touch a file' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'write_file', arguments: { path: '/etc/passwd' } }),
      evt(3, 'tool.error', { callId: 'c1', errorMessage: 'path is outside the sandbox boundary' }),
      evt(4, 'agent.turn.end', {}),
    ]);

    expect(model.steps[0].tools[0].isSandboxViolation).toBe(true);
    expect(model.steps[0].status).toBe('warning');
  });

  it('closes the previous intent step when the next intent starts (no perpetual running)', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'First' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'a.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.intent', { intent: 'Second' }),
      evt(5, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(2);
    // Step 1 must settle to complete once step 2 opens.
    expect(model.steps[0].active).toBe(false);
    expect(model.steps[0].status).toBe('complete');
    expect(model.steps[1].active).toBe(false);
  });

  it('settles all steps on a terminal run event with no agent.turn.end', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Plan' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'a.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.intent', { intent: 'Execute' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm test' } }),
      evt(6, 'tool.error', { callId: 'c2', errorMessage: 'exit code 1' }),
      // Terminal singleton carries sequence 0 — must still close everything.
      evt(0, 'run.failed', {}),
    ]);

    expect(model.running).toBe(false);
    expect(model.steps.every((s) => !s.active)).toBe(true);
    expect(model.steps[0].status).toBe('complete');
    // Last step errored -> warning, not complete.
    expect(model.steps[1].status).toBe('warning');
  });

  it('settles a still-running tool (missing completion) when its step closes', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Run something' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'run_command', arguments: { command: 'npm run build' } }),
      // No tool.result for c1 — a missing/mismatched completion.
      evt(3, 'run.completed', {}),
    ]);

    expect(model.steps[0].tools).toHaveLength(1);
    expect(model.steps[0].tools[0].status).toBe('complete');
    expect(model.steps[0].status).toBe('complete');
    expect(model.running).toBe(false);
  });

  it('derives category, primary title and muted secondary per tool kind', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Work' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'run_command', arguments: { command: 'cd repo && npm test' } }),
      evt(3, 'tool.call', { callId: 'c2', toolName: 'read_file', arguments: { path: '/Users/me/repo/src/app.ts', view_range: [10, 30] } }),
      evt(4, 'tool.call', { callId: 'c3', toolName: 'grep_search', arguments: { pattern: 'useState' } }),
      evt(5, 'tool.call', { callId: 'c4', toolName: 'edit_file', arguments: { path: 'src/app.ts' } }),
      evt(6, 'agent.turn.end', {}),
    ]);

    const [cmd, read, search, edit] = model.steps[0].tools;
    expect(cmd.category).toBe('command');
    expect(cmd.title).toBe('Run command');
    expect(cmd.titleSecondary).toBe('cd repo && npm test');

    expect(read.category).toBe('read');
    // Home prefix stripped, line range appended.
    expect(read.title).toBe('View repo/src/app.ts:10-30');

    expect(search.category).toBe('search');
    expect(search.title).toBe('Searched useState');

    expect(edit.category).toBe('edit');
    expect(edit.title).toBe('Edit src/app.ts');
  });

  it('summarises result meta as lines/results by category', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Inspect' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'a.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'line one\nline two\nline three' }),
      evt(4, 'tool.call', { callId: 'c2', toolName: 'grep_search', arguments: { pattern: 'x' } }),
      evt(5, 'tool.result', { callId: 'c2', content: 'match a\nmatch b' }),
      evt(6, 'tool.call', { callId: 'c3', toolName: 'run_command', arguments: { command: 'echo hi' } }),
      evt(7, 'tool.result', { callId: 'c3', content: 'hi' }),
      evt(8, 'agent.turn.end', {}),
    ]);

    const [read, search, cmd] = model.steps[0].tools;
    expect(read.resultMeta).toBe('3 lines');
    expect(search.resultMeta).toBe('2 results');
    expect(cmd.resultMeta).toBe('1 line');
  });

  it('builds an expandable diff and delta meta for str_replace-style edits', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Refactor' }),
      evt(2, 'tool.call', {
        callId: 'c1',
        toolName: 'str_replace_editor',
        arguments: { path: 'src/app.ts', old_str: 'const a = 1;', new_str: 'const a = 1;\nconst b = 2;\nconst c = 3;' },
      }),
      evt(3, 'tool.result', { callId: 'c1', content: 'edited' }),
      evt(4, 'agent.turn.end', {}),
    ]);

    const edit = model.steps[0].tools[0];
    expect(edit.category).toBe('edit');
    expect(edit.expandable).toBe(true);
    expect(edit.diff).toContain('-const a = 1;');
    expect(edit.diff).toContain('+const b = 2;');
    // 1 removed, 3 added.
    expect(edit.resultMeta).toBe('+3 -1');
  });

  it('orders step children so a message that arrives after a tool renders after it', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Explore then narrate' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'a.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.message', { messageId: 'm1', content: 'Found the entrypoint.' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'grep_search', arguments: { pattern: 'foo' } }),
      evt(6, 'tool.result', { callId: 'c2', content: 'match' }),
      evt(7, 'agent.message', { messageId: 'm2', content: 'Done searching.' }),
      evt(8, 'agent.turn.end', {}),
    ]);

    const children = model.steps[0].children;
    // Reading order: tool → message → tool → message (not all-tools-then-all-messages).
    expect(children.map((c) => c.kind)).toEqual(['tool', 'message', 'tool', 'message']);
    expect(children[0].kind === 'tool' && children[0].tool.callId).toBe('c1');
    expect(children[1].kind === 'message' && children[1].message.text).toBe('Found the entrypoint.');
    expect(children[2].kind === 'tool' && children[2].tool.callId).toBe('c2');
    expect(children[3].kind === 'message' && children[3].message.text).toBe('Done searching.');
  });

  it('correlates id-less deltas into one message and settles it on the final agent.message', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Narrate' }),
      evt(2, 'agent.message.delta', { delta: 'Hello ' }),
      evt(3, 'agent.message.delta', { delta: 'world' }),
      evt(4, 'agent.message', { content: 'Hello world' }),
      evt(5, 'agent.turn.end', {}),
    ]);

    // A missing messageId must not spawn a message per delta plus a separate final message.
    expect(model.steps[0].messages).toHaveLength(1);
    expect(model.steps[0].messages[0].text).toBe('Hello world');
    expect(model.steps[0].messages[0].streaming).toBe(false);
    // And it appears exactly once in ordered children.
    expect(model.steps[0].children.filter((c) => c.kind === 'message')).toHaveLength(1);
  });

  it('keeps accumulated id-less deltas when the final agent.message content is empty', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Narrate' }),
      evt(2, 'agent.message.delta', { delta: 'Partial ' }),
      evt(3, 'agent.message.delta', { delta: 'text' }),
      evt(4, 'agent.message', { content: '' }),
      evt(5, 'agent.turn.end', {}),
    ]);

    expect(model.steps[0].messages).toHaveLength(1);
    expect(model.steps[0].messages[0].text).toBe('Partial text');
    expect(model.steps[0].messages[0].streaming).toBe(false);
  });

  it('caps an unbounded edit diff by line count while keeping the full +A -R delta accurate', () => {
    const newStr = Array.from({ length: 500 }, (_, i) => `line ${i}`).join('\n');
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Big edit' }),
      evt(2, 'tool.call', {
        callId: 'c1',
        toolName: 'str_replace_editor',
        arguments: { path: 'src/big.ts', old_str: '', new_str: newStr },
      }),
      evt(3, 'tool.result', { callId: 'c1', content: 'edited' }),
      evt(4, 'agent.turn.end', {}),
    ]);

    const edit = model.steps[0].tools[0];
    expect(edit.truncated).toBe(true);
    // The stored diff is capped to the 200-line budget.
    expect(edit.diff!.split('\n').length).toBeLessThanOrEqual(200);
    expect(edit.diffHiddenLines).toBeGreaterThan(0);
    // The delta still reflects all 500 added lines (counted before truncation).
    expect(edit.resultMeta).toBe('+500');
  });

  it('replaces the serialized work-plan JSON message with a short friendly summary', () => {
    const workPlanJson = JSON.stringify([
      { title: 'Research', scope: 'Gather sources', role: 'researcher', depends_on: [] },
      { title: 'Draft', scope: 'Write the piece', role: 'writer', depends_on: [] },
      { title: 'Editorial', scope: 'Polish', role: 'editor', depends_on: [] },
    ]);
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Decomposing work plan' }),
      evt(2, 'agent.message', { messageId: 'm1', content: workPlanJson }),
      evt(3, 'agent.turn.end', {}),
    ]);

    const step = model.steps[0];
    // The raw JSON wall must never render.
    expect(step.messages[0].text).not.toContain('"scope"');
    expect(step.messages[0].text).not.toContain('[{');
    // It is summarised with the subtask count instead.
    expect(step.messages[0].text).toBe('Decomposed the work into 3 subtasks.');
    // The ordered children mirror the summarised text (same object reference).
    const msgChild = step.children.find((c) => c.kind === 'message');
    expect(msgChild && msgChild.kind === 'message' && msgChild.message.text).toBe('Decomposed the work into 3 subtasks.');
  });

  it('does NOT rewrite a title/scope JSON array on a non-coordinator (child) scope', () => {
    // A child agent may legitimately emit a JSON array whose objects carry title+scope (e.g. an
    // example payload). With stripSerializedWorkPlan disabled the message must be left verbatim.
    const legitArray = JSON.stringify([
      { title: 'Endpoint A', scope: 'GET /a returns colors' },
      { title: 'Endpoint B', scope: 'POST /b adds a color' },
    ]);
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Drafting response' }),
      evt(2, 'agent.message', { messageId: 'm1', content: legitArray }),
      evt(3, 'agent.turn.end', {}),
    ], { stripSerializedWorkPlan: false });

    const step = model.steps[0];
    expect(step.messages[0].text).toBe(legitArray);
    expect(step.messages[0].text).not.toContain('Decomposed the work into');
  });

  it('formats an interim outcome-spec JSON message into friendly Markdown on ANY scope, including a child/subtask scope (#UI-bug-2)', () => {
    const outcomeSpecJson = JSON.stringify({
      desired_outcome: 'Ship a minimal preview app',
      scope: 'Implement only the web preview path.',
    });
    // stripSerializedWorkPlan: false mirrors a child/subtask scope (e.g. the "Working" step in
    // Ahmed's screenshot) — the outcome-spec reformat must still apply there, unlike the
    // coordinator-only serialized-work-plan strip above.
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Drafting the outcome plan' }),
      evt(2, 'agent.message', { messageId: 'm1', content: outcomeSpecJson }),
      evt(3, 'agent.turn.end', {}),
    ], { stripSerializedWorkPlan: false });

    const step = model.steps[0];
    expect(step.messages[0].text).not.toContain('"desired_outcome"');
    expect(step.messages[0].text).toContain('### Outcome plan');
    expect(step.messages[0].text).toContain('Ship a minimal preview app');
    expect(step.messages[0].text).toContain('Implement only the web preview path.');
  });

  it('collapses adjacent short continuation intents into one larger timeline step', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: "Now let's build the prototype with Vite + React" }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'write_file', arguments: { path: 'src/main.tsx' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'wrote 20 lines' }),
      evt(4, 'agent.intent', { intent: 'Now the storage module' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'write_file', arguments: { path: 'src/storage.ts' } }),
      evt(6, 'tool.result', { callId: 'c2', content: 'wrote 18 lines' }),
      evt(7, 'agent.intent', { intent: 'Now the styles file' }),
      evt(8, 'tool.call', { callId: 'c3', toolName: 'write_file', arguments: { path: 'src/styles.css' } }),
      evt(9, 'tool.result', { callId: 'c3', content: 'wrote 12 lines' }),
      evt(10, 'agent.intent', { intent: "Now let's create the README and .gitignore" }),
      evt(11, 'tool.call', { callId: 'c4', toolName: 'write_file', arguments: { path: 'README.md' } }),
      evt(12, 'tool.result', { callId: 'c4', content: 'wrote 8 lines' }),
      evt(13, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(1);
    expect(model.steps[0].intent).toBe('Build the prototype with Vite + React');
    expect(model.steps[0].tools).toHaveLength(4);
    expect(model.steps[0].children.filter((child) => child.kind === 'tool')).toHaveLength(4);
  });

  it('keeps distinct non-continuation intents as separate steps', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: 'Read the code' }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'read_file', arguments: { path: 'src/app.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'agent.intent', { intent: 'Build the project' }),
      evt(5, 'tool.call', { callId: 'c2', toolName: 'run_command', arguments: { command: 'npm run build' } }),
      evt(6, 'tool.result', { callId: 'c2', content: 'ok' }),
      evt(7, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(2);
    expect(model.steps[0].intent).toBe('Read the code');
    expect(model.steps[1].intent).toBe('Build the project');
  });

  it('does not collapse continuation intents when a step is already too large', () => {
    const model = buildRunTimeline([
      evt(1, 'agent.intent', { intent: "Now let's build the prototype" }),
      evt(2, 'tool.call', { callId: 'c1', toolName: 'write_file', arguments: { path: 'src/one.ts' } }),
      evt(3, 'tool.result', { callId: 'c1', content: 'ok' }),
      evt(4, 'tool.call', { callId: 'c2', toolName: 'write_file', arguments: { path: 'src/two.ts' } }),
      evt(5, 'tool.result', { callId: 'c2', content: 'ok' }),
      evt(6, 'tool.call', { callId: 'c3', toolName: 'write_file', arguments: { path: 'src/three.ts' } }),
      evt(7, 'tool.result', { callId: 'c3', content: 'ok' }),
      evt(8, 'tool.call', { callId: 'c4', toolName: 'write_file', arguments: { path: 'src/four.ts' } }),
      evt(9, 'tool.result', { callId: 'c4', content: 'ok' }),
      evt(10, 'tool.call', { callId: 'c5', toolName: 'write_file', arguments: { path: 'src/five.ts' } }),
      evt(11, 'tool.result', { callId: 'c5', content: 'ok' }),
      evt(12, 'agent.intent', { intent: 'Now the README' }),
      evt(13, 'tool.call', { callId: 'c6', toolName: 'write_file', arguments: { path: 'README.md' } }),
      evt(14, 'tool.result', { callId: 'c6', content: 'ok' }),
      evt(15, 'agent.turn.end', {}),
    ]);

    expect(model.steps).toHaveLength(2);
    expect(model.steps[0].tools).toHaveLength(5);
    expect(model.steps[1].intent).toBe('Now the README');
  });
});
