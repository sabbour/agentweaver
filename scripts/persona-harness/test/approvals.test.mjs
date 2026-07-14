// test/approvals.test.mjs — deterministic approval-gate DETECTION (driver-only).
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  detectPendingApprovals,
  gateFromEvent,
  approvalKey,
  describeGate,
  APPROVAL_REQUIRED_TYPES,
} from '../lib/approvals.mjs';

const toolReq = (seq, requestId, extra = {}) => ({
  sequence: seq,
  type: 'tool.approval_required',
  payload: { requestId, displayId: requestId.slice(0, 8), toolName: 'web_fetch', url: 'https://example.com', message: 'needs approval', ...extra },
});
const toolResolved = (seq, requestId, approved = true) => ({
  sequence: seq,
  type: 'tool.approval_resolved',
  payload: { requestId, runId: 'r1', approved, expired: false },
});
const shellReq = (seq, commandHash, command) => ({
  sequence: seq,
  type: 'shell.approval_required',
  payload: { requestId: commandHash.slice(0, 8), commandHash, command, commandLength: command.length, message: 'shell needs approval' },
});
const childReq = (seq, requestId, childRunId) => ({
  sequence: seq,
  type: 'coordinator.child_approval_required',
  payload: { childRunId, subtaskId: 'sub-1', requestId, toolName: 'web_fetch', url: 'https://x.y', message: 'child gate' },
});

test('gateFromEvent extracts the fields the resolver needs for each gate kind', () => {
  const tool = gateFromEvent(toolReq(3, 'req-abc'));
  assert.equal(tool.kind, 'tool');
  assert.equal(tool.requestId, 'req-abc');
  assert.equal(tool.toolName, 'web_fetch');
  assert.equal(tool.key, 'request:req-abc');

  const shell = gateFromEvent(shellReq(5, 'DEADBEEF0011', 'rm -rf build'));
  assert.equal(shell.kind, 'shell');
  assert.equal(shell.commandHash, 'DEADBEEF0011');
  assert.equal(shell.command, 'rm -rf build');
  assert.equal(shell.key, 'shell:DEADBEEF0011');

  const child = gateFromEvent(childReq(2, 'req-child', 'child-77'));
  assert.equal(child.kind, 'coordinator-child');
  assert.equal(child.childRunId, 'child-77');
  assert.equal(child.key, 'request:req-child');

  assert.equal(gateFromEvent({ type: 'agent.message', payload: {} }), null);
});

test('approvalKey keys shell on commandHash and tool on requestId; unaddressable -> null', () => {
  assert.equal(approvalKey({ kind: 'shell', commandHash: 'H' }), 'shell:H');
  assert.equal(approvalKey({ kind: 'tool', requestId: 'R' }), 'request:R');
  assert.equal(approvalKey({ kind: 'shell' }), null);
  assert.equal(approvalKey({ kind: 'tool' }), null);
});

test('detectPendingApprovals returns a tool gate with no matching resolved event', () => {
  const events = [
    { sequence: 1, type: 'run.started', payload: {} },
    toolReq(2, 'req-1'),
  ];
  const { pending } = detectPendingApprovals(events);
  assert.equal(pending.length, 1);
  assert.equal(pending[0].requestId, 'req-1');
  assert.equal(pending[0].kind, 'tool');
});

test('detectPendingApprovals excludes a gate closed by a matching resolved event', () => {
  const events = [toolReq(2, 'req-1'), toolResolved(4, 'req-1', true)];
  const { pending, resolvedRequestIds } = detectPendingApprovals(events);
  assert.equal(pending.length, 0);
  assert.deepEqual(resolvedRequestIds, ['req-1']);
});

test('detectPendingApprovals excludes a gate the harness already drove (alreadyResolvedKeys)', () => {
  const events = [shellReq(2, 'HASH1', 'make deploy')];
  const before = detectPendingApprovals(events);
  assert.equal(before.pending.length, 1);
  const after = detectPendingApprovals(events, { alreadyResolvedKeys: ['shell:HASH1'] });
  assert.equal(after.pending.length, 0);
});

test('detectPendingApprovals dedupes re-emitted required events (latest wins) and orders by sequence', () => {
  const events = [
    toolReq(2, 'req-1', { message: 'first' }),
    childReq(3, 'req-2', 'child-1'),
    toolReq(6, 'req-1', { message: 'second' }), // re-emit
  ];
  const { pending } = detectPendingApprovals(events);
  assert.equal(pending.length, 2);
  // ordered by sequence: req-2 (seq 3) then req-1 (latest seq 6)
  assert.deepEqual(pending.map((g) => g.requestId), ['req-2', 'req-1']);
  const req1 = pending.find((g) => g.requestId === 'req-1');
  assert.equal(req1.message, 'second'); // freshest evidence retained
});

test('detectPendingApprovals handles coordinator-child + shell + tool together', () => {
  const events = [
    toolReq(1, 'req-tool'),
    childReq(2, 'req-child', 'c1'),
    shellReq(3, 'HASHX', 'terraform apply'),
    toolResolved(4, 'req-tool'), // tool resolved
  ];
  const { pending } = detectPendingApprovals(events);
  const keys = pending.map((g) => g.key).sort();
  assert.deepEqual(keys, ['request:req-child', 'shell:HASHX']);
});

test('describeGate produces an objective one-liner without a verdict', () => {
  assert.match(describeGate(gateFromEvent(shellReq(1, 'H', 'ls'))), /shell command gate/);
  assert.match(describeGate(gateFromEvent(toolReq(1, 'r'))), /tool gate/);
  assert.deepEqual(Object.keys(APPROVAL_REQUIRED_TYPES).sort(), [
    'coordinator.child_approval_required',
    'shell.approval_required',
    'tool.approval_required',
  ]);
});
