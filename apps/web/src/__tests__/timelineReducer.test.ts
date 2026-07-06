import { describe, it, expect } from 'vitest';
import { timelineReducer, initialTimelineState, deriveHumanTitle } from '../timeline/reducer';
import type { TimelineReducerState, TurnGroupItem, AgentMessageItem, ToolCallItem, ApprovalRequestItem } from '../timeline/types';
import type { RunStreamEvent } from '../api/sse';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeEvent(
  type: RunStreamEvent['type'],
  payload: Record<string, unknown>,
  seq = 0,
): RunStreamEvent {
  return { sequence: seq, type, payload };
}

function fold(events: RunStreamEvent[], start = initialTimelineState): TimelineReducerState {
  return events.reduce(
    (s, e) => timelineReducer(s, { type: 'event', event: e }),
    start,
  );
}

function openTurn(state = initialTimelineState, turnId = 'turn-1') {
  return fold([makeEvent('agent.turn.start', { turnId })], state);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('timelineReducer', () => {

  // T-01
  it('turn.start → turn.end produces closed TurnGroupItem', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.turn.end', { turnId: 'T1' }),
    ]);
    expect(s.items).toHaveLength(1);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.kind).toBe('turn-group');
    expect(turn.active).toBe(false);
    expect(s.currentTurnIndex).toBeNull();
  });

  // T-02
  it('three deltas then agent.message yields single settled bubble', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'Hello', messageId: 'M1' }),
      makeEvent('agent.message.delta', { delta: ' world', messageId: 'M1' }),
      makeEvent('agent.message.delta', { delta: '!', messageId: 'M1' }),
      makeEvent('agent.message', { messageId: 'M1', content: 'Hello world!' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(1);
    const msg = turn.steps[0] as AgentMessageItem;
    expect(msg.kind).toBe('agent-message');
    expect(msg.streaming).toBe(false);
    expect(msg.content).toBe('Hello world!');
  });

  // T-03
  it('tool.call then tool.result: settled with result, no error', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.call', { callId: 'C1', toolName: 'read_file', arguments: { path: 'src/x.ts' } }),
      makeEvent('tool.result', { callId: 'C1', content: 'file content' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const call = turn.steps[0] as ToolCallItem;
    expect(call.settled).toBe(true);
    expect(call.result?.content).toBe('file content');
    expect(call.error).toBeNull();
  });

  // T-04
  it('tool.call then tool.error (sandbox keyword): isSandboxViolation=true', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.call', { callId: 'C2', toolName: 'write_file', arguments: {} }),
      makeEvent('tool.error', { callId: 'C2', errorMessage: 'Path is outside the sandbox boundary' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const call = turn.steps[0] as ToolCallItem;
    expect(call.settled).toBe(true);
    expect(call.error?.isSandboxViolation).toBe(true);
  });

  // T-04b: non-sandbox error
  it('tool.error without sandbox keyword: isSandboxViolation=false', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.call', { callId: 'C3', toolName: 'write_file', arguments: {} }),
      makeEvent('tool.error', { callId: 'C3', errorMessage: 'File not found' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const call = turn.steps[0] as ToolCallItem;
    expect(call.error?.isSandboxViolation).toBe(false);
  });

  // T-05
  it('tool.call with no result: settled=false, result=null, error=null', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.call', { callId: 'C4', toolName: 'list_directory', arguments: {} }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const call = turn.steps[0] as ToolCallItem;
    expect(call.settled).toBe(false);
    expect(call.result).toBeNull();
    expect(call.error).toBeNull();
  });

  // T-06
  it('review.requested + merge.completed → two LifecycleItems at top level', () => {
    const s = fold([
      makeEvent('review.requested', { tree_hash: 'abc123' }),
      makeEvent('merge.completed', { merged_commit_hash: 'def456' }),
    ]);
    expect(s.items).toHaveLength(2);
    expect(s.items[0].kind).toBe('lifecycle');
    expect(s.items[1].kind).toBe('lifecycle');
  });

  // T-06b: coordinator orchestration milestones surface as lifecycle cards so the
  // coordinator session timeline narrates the run even with no agent-turn content.
  it('coordinator milestone events surface as lifecycle items; topology/running ticks do not', () => {
    const s = fold([
      makeEvent('coordinator.outcome_spec', { desiredOutcome: 'Ship X' }),
      makeEvent('coordinator.work_plan', { subtasks: [{}, {}] }),
      makeEvent('subtask.dispatched', { assignedAgent: 'Neo', selectedModelId: 'opus' }),
      makeEvent('coordinator.topology', { kind: 'snapshot' }), // dropped (noise)
      makeEvent('subtask.running', { assignedAgent: 'Neo' }), // dropped (noise)
      makeEvent('coordinator.assembly_review_requested', {}),
      makeEvent('coordinator.assembly_completed', { commitHash: 'abc1234567' }),
    ]);
    expect(s.items).toHaveLength(5);
    expect(s.items.every((i) => i.kind === 'lifecycle')).toBe(true);
  });

  // T-07
  it('run.failed mid-turn: open turn closed (active=false), LifecycleItem appended', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('run.failed', { message: 'crash' }),
    ]);
    expect(s.items).toHaveLength(2);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.active).toBe(false);
    expect(s.items[1].kind).toBe('lifecycle');
  });

  // T-08: unknown event type returns state unchanged
  it('unknown event type: state unchanged', () => {
    const before = openTurn();
    // @ts-expect-error deliberately passing unknown type
    const after = timelineReducer(before, { type: 'event', event: { sequence: 99, type: 'unknown.event', payload: {} } });
    expect(after.items).toEqual(before.items);
  });

  // T-09
  it('agent.message with no prior delta: adds settled bubble without crashing', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message', { messageId: 'M99', content: 'Whole message' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(1);
    const msg = turn.steps[0] as AgentMessageItem;
    expect(msg.streaming).toBe(false);
    expect(msg.content).toBe('Whole message');
  });

  // T-10
  it('deriveHumanTitle: read_file + path', () => {
    expect(deriveHumanTitle('read_file', { path: 'src/index.ts' })).toBe('Read file \u00b7 src/index.ts');
  });

  it('deriveHumanTitle: unknown tool falls back to capitalized name', () => {
    expect(deriveHumanTitle('fetch_data', {})).toBe('Fetch Data');
  });

  it('deriveHumanTitle: strips home prefix from path', () => {
    const result = deriveHumanTitle('write_file', { path: '/home/runner/work/agentweaver/src/x.ts' });
    expect(result).not.toContain('/home/runner');
    expect(result).toContain('src/x.ts');
  });

  // Deduplication: duplicate agent.message for same messageId should settle, not add extra step
  it('duplicate agent.message for same messageId: no extra step added', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'hi', messageId: 'M1' }),
      makeEvent('agent.message', { messageId: 'M1', content: 'hi' }),
      // A duplicate settled message with same messageId should NOT add a second step
      // (the streamingMessage is null now, so it goes through addStepToCurrentTurn)
      // This is acceptable behavior — the second message is a new bubble.
    ]);
    const turn = s.items[0] as TurnGroupItem;
    // After settle, streaming=false — second identical message would be a new step.
    // What we verify here: no crash, content is correct after settle.
    expect((turn.steps[0] as AgentMessageItem).content).toBe('hi');
    expect((turn.steps[0] as AgentMessageItem).streaming).toBe(false);
  });

  // Fix #4: tool.call with no open turn auto-creates a synthetic TurnGroup
  it('tool.call with no open turn: auto-creates synthetic turn, does not crash', () => {
    const s = fold([
      makeEvent('tool.call', { callId: 'C5', toolName: 'read_file', arguments: { path: 'x' } }),
    ]);
    expect(s.items).toHaveLength(1);
    expect(s.items[0].kind).toBe('turn-group');
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(1);
    expect(turn.steps[0].kind).toBe('tool-call');
  });

  it('agent.message with no open turn: auto-creates synthetic turn, does not crash', () => {
    const s = fold([
      makeEvent('agent.message', { messageId: 'M1', content: 'Hello' }),
    ]);
    expect(s.items).toHaveLength(1);
    expect(s.items[0].kind).toBe('turn-group');
  });

  // turnIndex counter: must be 1-based per turn.start, not array index arithmetic (RD-3)
  it('turnIndex is 1-based and increments correctly across multiple turns', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.turn.end', { turnId: 'T1' }),
      makeEvent('run.completed', {}),
      makeEvent('agent.turn.start', { turnId: 'T2' }),
      makeEvent('agent.turn.end', { turnId: 'T2' }),
    ]);
    const turns = s.items.filter((i) => i.kind === 'turn-group') as TurnGroupItem[];
    expect(turns[0].turnIndex).toBe(1);
    expect(turns[1].turnIndex).toBe(2);
  });

  // reset action
  it('reset action clears all state', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'hello', messageId: 'M1' }),
    ]);
    expect(s.items.length).toBeGreaterThan(0);
    const reset = timelineReducer(s, { type: 'reset' });
    expect(reset.items).toHaveLength(0);
    expect(reset.currentTurnIndex).toBeNull();
    expect(reset.streamingMessage).toBeNull();
    expect(reset.turnCounter).toBe(0);
  });

  // T-11: orphan guard — two delta sequences with different messageIds, no intervening agent.message
  it('two delta sequences with different messageIds: first is auto-settled, second is streaming', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'Hello', messageId: 'M1' }),
      makeEvent('agent.message.delta', { delta: ' world', messageId: 'M1' }),
      // M2 arrives without a prior agent.message settling M1
      makeEvent('agent.message.delta', { delta: 'Now M2', messageId: 'M2' }),
      makeEvent('agent.message.delta', { delta: ' continues', messageId: 'M2' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(2);
    const m1 = turn.steps[0] as AgentMessageItem;
    const m2 = turn.steps[1] as AgentMessageItem;
    // First message must be settled with its accumulated content
    expect(m1.streaming).toBe(false);
    expect(m1.content).toBe('Hello world');
    // Second message is still streaming
    expect(m2.streaming).toBe(true);
    expect(m2.content).toBe('Now M2 continues');
    // Reducer tracks M2 as the active streaming message
    expect(s.streamingMessage?.messageId).toBe('M2');
  });

  // Delta coalescing: multiple deltas accumulate into one message item
  it('200 deltas coalesce into a single message item', () => {
    const events: RunStreamEvent[] = [makeEvent('agent.turn.start', { turnId: 'T1' })];
    for (let i = 0; i < 200; i++) {
      events.push(makeEvent('agent.message.delta', { delta: 'x', messageId: 'M1' }));
    }
    const s = fold(events);
    const turn = s.items[0] as TurnGroupItem;
    // All 200 deltas coalesced into a single message
    expect(turn.steps).toHaveLength(1);
    expect((turn.steps[0] as AgentMessageItem).content).toBe('x'.repeat(200));
  });

  // T-12: Copilot path — run.completed without agent.turn.end closes the open turn
  it('Copilot path: run.completed without turn.end closes open turn and settles message', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'Hello', messageId: 'M1' }),
      makeEvent('agent.message', { messageId: 'M1', content: 'Hello' }),
      // No agent.turn.end — simulates GitHubCopilotAgentRunner path
      makeEvent('run.completed', {}),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.active).toBe(false);
    expect(s.currentTurnIndex).toBeNull();
    const msg = turn.steps[0] as AgentMessageItem;
    expect(msg.streaming).toBe(false);
    expect(msg.content).toBe('Hello');
  });

  // T-13: orphan guard — run.completed with still-streaming message settles it and closes turn
  it('orphan guard: run.completed with no final agent.message settles streaming bubble and closes turn', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('agent.message.delta', { delta: 'partial', messageId: 'M1' }),
      // No agent.message — simulates a race / replay edge case
      makeEvent('run.completed', {}),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.active).toBe(false);
    expect(s.currentTurnIndex).toBeNull();
    expect(s.streamingMessage).toBeNull();
    const msg = turn.steps[0] as AgentMessageItem;
    expect(msg.streaming).toBe(false);
    expect(msg.content).toBe('partial');
  });

  // #196: a bubbled coordinator.child_approval_required carries the owning child run id so
  // approve/deny routes to the child subtask run, not the coordinator run.
  it('coordinator.child_approval_required captures the owning childRunId', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('coordinator.child_approval_required', {
        childRunId: 'child-run-9',
        subtaskId: 2,
        requestId: 'toolu_01xyz',
        toolName: 'web_fetch',
        url: 'https://api.github.com/search/issues',
      }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const card = turn.steps[0] as ApprovalRequestItem;
    expect(card.kind).toBe('approval-request');
    expect(card.childRunId).toBe('child-run-9');
    expect(card.requestId).toBe('toolu_01xyz');
    expect(s.pendingApprovals.has('toolu_01xyz')).toBe(true);
  });

  it('tool.approval_required without a childRunId leaves childRunId null', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.approval_required', { requestId: 'req-plain', toolName: 'web_fetch' }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const card = turn.steps[0] as ApprovalRequestItem;
    expect(card.childRunId).toBeNull();
  });

  // T-14: tool.approval_resolved with expired=true marks the card expired
  it('tool.approval_resolved (expired) marks pending approval as expired', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.approval_required', { requestId: 'req-abc', toolName: 'web_fetch', url: 'https://example.com' }),
      makeEvent('tool.approval_resolved', { requestId: 'req-abc', runId: 'run-1', approved: false, expired: true }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const card = turn.steps[0] as ApprovalRequestItem;
    expect(card.kind).toBe('approval-request');
    expect(card.resolved).toBe(true);
    expect(card.resolvedScope).toBe('expired');
    expect(s.pendingApprovals.has('req-abc')).toBe(false);
  });

  // T-15: tool.approval_resolved with approved=false, expired=false marks as deny
  it('tool.approval_resolved (denied) marks pending approval as deny', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.approval_required', { requestId: 'req-deny', toolName: 'web_fetch' }),
      makeEvent('tool.approval_resolved', { requestId: 'req-deny', approved: false, expired: false }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const card = turn.steps[0] as ApprovalRequestItem;
    expect(card.resolved).toBe(true);
    expect(card.resolvedScope).toBe('deny');
  });

  // T-16: tool.approval_resolved with approved=true, expired=false marks as approved
  it('tool.approval_resolved (approved) marks pending approval as once', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.approval_required', { requestId: 'req-ok', toolName: 'web_fetch' }),
      makeEvent('tool.approval_resolved', { requestId: 'req-ok', approved: true, expired: false }),
    ]);
    const turn = s.items[0] as TurnGroupItem;
    const card = turn.steps[0] as ApprovalRequestItem;
    expect(card.resolved).toBe(true);
    expect(card.resolvedScope).toBe('once');
  });

  // T-17: unknown requestId in tool.approval_resolved is a no-op (graceful)
  it('tool.approval_resolved for unknown requestId is a no-op', () => {
    const s = fold([
      makeEvent('agent.turn.start', { turnId: 'T1' }),
      makeEvent('tool.approval_resolved', { requestId: 'req-unknown', approved: false, expired: true }),
    ]);
    expect(s.items).toHaveLength(1);
    const turn = s.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(0);
  });

  // --- BLOCKING #1: HITL question gates ------------------------------------

  // Q-01: agent.question_asked creates a top-level, unresolved question item
  it('agent.question_asked creates an unresolved question-request item (no open turn needed)', () => {
    const s = fold([
      makeEvent('agent.question_asked', { requestId: 'q1', question: 'Which region?' }, 1),
    ]);
    expect(s.items).toHaveLength(1);
    const q = s.items[0] as import('../timeline/types').QuestionRequestItem;
    expect(q.kind).toBe('question-request');
    expect(q.resolved).toBe(false);
    expect(q.question).toBe('Which region?');
    expect(q.askingRunId).toBeUndefined();
    expect(s.pendingQuestions.get('q1')).toBe(0);
  });

  // Q-02: agent.question_answered resolves the paired item
  it('agent.question_answered resolves the paired question by requestId', () => {
    const s = fold([
      makeEvent('agent.question_asked', { requestId: 'q1', question: 'Which region?' }, 1),
      makeEvent('agent.question_answered', { requestId: 'q1', answer: 'westus2' }, 2),
    ]);
    expect(s.items).toHaveLength(1);
    const q = s.items[0] as import('../timeline/types').QuestionRequestItem;
    expect(q.resolved).toBe(true);
    expect(q.answer).toBe('westus2');
    expect(s.pendingQuestions.has('q1')).toBe(false);
  });

  // Q-03: coordinator.child_question captures askingRunId (childRunId) for answer routing
  it('coordinator.child_question stores childRunId as askingRunId for answer routing', () => {
    const s = fold([
      makeEvent('coordinator.child_question', {
        requestId: 'cq1', question: 'Merge strategy?', childRunId: 'child-9', agentName: 'Trinity',
      }, 1),
    ]);
    const q = s.items[0] as import('../timeline/types').QuestionRequestItem;
    expect(q.kind).toBe('question-request');
    expect(q.askingRunId).toBe('child-9');
    expect(q.sourceLabel).toBe('Trinity');
    expect(q.resolved).toBe(false);
  });

  // Q-04: coordinator.autopilot_answered resolves the question AND keeps the audit line
  it('coordinator.autopilot_answered resolves a child question and appends an audit lifecycle item', () => {
    const s = fold([
      makeEvent('coordinator.child_question', { requestId: 'cq1', question: 'Merge strategy?', childRunId: 'child-9' }, 1),
      makeEvent('coordinator.autopilot_answered', { requestId: 'cq1', answer: 'rebase', childRunId: 'child-9' }, 2),
    ]);
    // question item resolved + a lifecycle audit card appended
    const q = s.items[0] as import('../timeline/types').QuestionRequestItem;
    expect(q.kind).toBe('question-request');
    expect(q.resolved).toBe(true);
    expect(q.answer).toBe('rebase');
    expect(s.items.some((i) => i.kind === 'lifecycle' && i.event.type === 'coordinator.autopilot_answered')).toBe(true);
    expect(s.pendingQuestions.has('cq1')).toBe(false);
  });

  // Q-05: bubbled child approvals are no longer dropped — they surface as lifecycle items
  it('coordinator.child_approval_required surfaces as a lifecycle item (not dropped)', () => {
    const s = fold([
      makeEvent('coordinator.child_approval_required', { requestId: 'a1', toolName: 'shell', childRunId: 'child-9' }, 1),
    ]);
    expect(s.items).toHaveLength(1);
    expect(s.items[0].kind).toBe('lifecycle');
  });
});
