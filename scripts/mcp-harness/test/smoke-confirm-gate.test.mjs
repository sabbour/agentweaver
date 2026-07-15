import test from 'node:test';
import assert from 'node:assert/strict';
import { classifySmokeStatus } from '../lib/smoke-confirm-gate.mjs';

const TERMINAL = new Set(['completed', 'failed', 'cancelled', 'archived']);

// ── terminal-status detection ──────────────────────────────────────────────

test('returns break for completed status', () => {
  assert.equal(classifySmokeStatus({ status: 'completed' }, { terminal: TERMINAL }), 'break');
});

test('returns break for failed status', () => {
  assert.equal(classifySmokeStatus({ status: 'failed' }, { terminal: TERMINAL }), 'break');
});

test('returns break for cancelled status', () => {
  assert.equal(classifySmokeStatus({ status: 'cancelled' }, { terminal: TERMINAL }), 'break');
});

test('returns break for archived status', () => {
  assert.equal(classifySmokeStatus({ status: 'archived' }, { terminal: TERMINAL }), 'break');
});

test('terminal check is case-insensitive', () => {
  assert.equal(classifySmokeStatus({ status: 'Completed' }, { terminal: TERMINAL }), 'break');
  assert.equal(classifySmokeStatus({ status: 'FAILED' }, { terminal: TERMINAL }), 'break');
});

// ── awaiting_confirmation gate ─────────────────────────────────────────────

test('returns confirm when coordinator_status is awaiting_confirmation and not yet confirmed', () => {
  const content = { status: 'running', coordinator_status: 'awaiting_confirmation' };
  assert.equal(classifySmokeStatus(content, { terminal: TERMINAL }), 'confirm');
});

test('awaiting_confirmation check is case-insensitive', () => {
  const content = { status: 'running', coordinator_status: 'Awaiting_Confirmation' };
  assert.equal(classifySmokeStatus(content, { terminal: TERMINAL }), 'confirm');
});

test('returns continue (not confirm) when alreadyConfirmed is true', () => {
  const content = { status: 'running', coordinator_status: 'awaiting_confirmation' };
  assert.equal(classifySmokeStatus(content, { terminal: TERMINAL, alreadyConfirmed: true }), 'continue');
});

test('terminal status takes precedence over coordinator_status awaiting_confirmation', () => {
  // Defensive: if top-level status is terminal, break regardless of coordinator_status.
  const content = { status: 'completed', coordinator_status: 'awaiting_confirmation' };
  assert.equal(classifySmokeStatus(content, { terminal: TERMINAL }), 'break');
});

// ── non-terminal, no gate ──────────────────────────────────────────────────

test('returns continue for a running status with no gate', () => {
  assert.equal(classifySmokeStatus({ status: 'running' }, { terminal: TERMINAL }), 'continue');
});

test('returns continue for a queued status', () => {
  assert.equal(classifySmokeStatus({ status: 'queued' }, { terminal: TERMINAL }), 'continue');
});

test('returns continue for null content', () => {
  assert.equal(classifySmokeStatus(null, { terminal: TERMINAL }), 'continue');
});

test('returns continue for empty content object', () => {
  assert.equal(classifySmokeStatus({}, { terminal: TERMINAL }), 'continue');
});

// ── smoke state-machine sequence ───────────────────────────────────────────

test('simulated state machine: polls through awaiting_confirmation then completes', () => {
  // Simulate: running → awaiting_confirmation → running (post-confirm) → completed
  const states = [
    { status: 'running', coordinator_status: '' },
    { status: 'running', coordinator_status: 'awaiting_confirmation' },
    { status: 'running', coordinator_status: '' },
    { status: 'completed', coordinator_status: '' },
  ];

  let confirmed = false;
  const actions = [];
  for (const content of states) {
    const action = classifySmokeStatus(content, { terminal: TERMINAL, alreadyConfirmed: confirmed });
    actions.push(action);
    if (action === 'confirm') confirmed = true;
  }

  assert.deepEqual(actions, ['continue', 'confirm', 'continue', 'break']);
  assert.equal(confirmed, true);
});

test('simulated state machine: does not re-confirm on repeated awaiting_confirmation', () => {
  // After confirming, a second awaiting_confirmation poll must not trigger a second confirm.
  const states = [
    { status: 'running', coordinator_status: 'awaiting_confirmation' },
    { status: 'running', coordinator_status: 'awaiting_confirmation' }, // repeated before server advances
    { status: 'completed', coordinator_status: '' },
  ];

  let confirmed = false;
  const actions = [];
  for (const content of states) {
    const action = classifySmokeStatus(content, { terminal: TERMINAL, alreadyConfirmed: confirmed });
    actions.push(action);
    if (action === 'confirm') confirmed = true;
  }

  assert.deepEqual(actions, ['confirm', 'continue', 'break']);
});
