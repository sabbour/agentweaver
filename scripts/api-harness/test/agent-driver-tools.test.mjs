import { test } from 'node:test';
import assert from 'node:assert/strict';

import { computeDeterministicP0 } from '../agent-driver/tools.mjs';

test('computeDeterministicP0 passes only when successful pushbacks settled and the run ends at the confirmation gate', () => {
  const turns = [
    { action: 'init', response: { status: 200, body: { status: 'signed_in' } } },
    { action: 'create-project', response: { status: 201, body: { projectId: 'p1' } } },
    { action: 'submit-goal', response: { status: 201, body: { runId: 'r1' } } },
    {
      action: 'revise-spec (pushback)',
      response: {
        status: 200,
        body: {
          preRevisionSpec: { status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v1' } },
          postRevisionPolls: [{ status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v2' } }],
          finalSpec: { status: 'awaiting_confirmation', desiredOutcome: 'v2' },
          objectiveRevision: { appliedSuccessfully: true, specReachedSettledState: true },
        },
      },
    },
    {
      action: 'revise-spec (pushback)',
      response: {
        status: 200,
        body: {
          preRevisionSpec: { status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v2' } },
          postRevisionPolls: [{ status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v3' } }],
          finalSpec: { status: 'awaiting_confirmation', desiredOutcome: 'v3' },
          objectiveRevision: { appliedSuccessfully: true, specReachedSettledState: true },
        },
      },
    },
    {
      action: 'get-spec',
      response: {
        status: 200,
        body: {
          polls: [{ status: 200, responseBody: { status: 'awaiting_confirmation' } }],
          spec: { status: 'awaiting_confirmation', desiredOutcome: 'v3' },
        },
      },
    },
  ];

  const result = computeDeterministicP0(turns);
  assert.equal(result.objectivePass, true);
  assert.equal(result.allApiCallsSucceeded, true);
  assert.equal(result.pushbacksAppliedSuccessfully, 2);
  assert.equal(result.specReachedSettledStateAfterEachPushback, true);
  assert.equal(result.endedInSafeTerminalState, true);
  assert.equal(result.latestObservedSpecStatus, 'awaiting_confirmation');
});

test('computeDeterministicP0 ignores failed revise attempts even if the raw attempt count reached two', () => {
  const turns = [
    { action: 'init', response: { status: 200, body: { status: 'signed_in' } } },
    {
      action: 'revise-spec (pushback)',
      response: {
        status: 500,
        body: {
          preRevisionSpec: { status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v1' } },
          postRevisionPolls: [],
          finalSpec: null,
          objectiveRevision: { appliedSuccessfully: false, specReachedSettledState: false },
        },
      },
    },
    {
      action: 'revise-spec (pushback)',
      response: {
        status: 200,
        body: {
          preRevisionSpec: { status: 200, responseBody: { status: 'awaiting_confirmation', desiredOutcome: 'v1' } },
          postRevisionPolls: [{ status: 200, responseBody: { status: 'drafting', desiredOutcome: 'v1' } }],
          finalSpec: { status: 'drafting', desiredOutcome: 'v1' },
          objectiveRevision: { appliedSuccessfully: false, specReachedSettledState: false },
        },
      },
    },
    {
      action: 'get-spec',
      response: {
        status: 200,
        body: {
          polls: [{ status: 200, responseBody: { status: 'drafting' } }],
          spec: { status: 'drafting', desiredOutcome: 'v1' },
        },
      },
    },
  ];

  const result = computeDeterministicP0(turns);
  assert.equal(result.objectivePass, false);
  assert.equal(result.allApiCallsSucceeded, false);
  assert.equal(result.pushbacksAppliedSuccessfully, 0);
  assert.equal(result.specReachedSettledStateAfterEachPushback, false);
  assert.equal(result.endedInSafeTerminalState, false);
  assert.equal(result.latestObservedSpecStatus, 'drafting');
});
