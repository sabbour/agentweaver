// Unit tests for drive.mjs's generic, persona-agnostic P0 mechanics check.
//
// This intentionally does NOT test any named business action (submit-goal,
// revise-spec, get-spec, ...) — those no longer exist. `call` is a raw method/
// path/body passthrough, so `computeDeterministicP0` only asks one persona-
// agnostic question: did every recorded API call succeed? Whether any pushback/
// objection was substantively grounded is P1 content quality, judged by the Judge
// subagent from the full transcript — never counted here. Run with:
// node --test (from scripts/api-harness/)

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { computeDeterministicP0 } from '../drive.mjs';

test('computeDeterministicP0 passes when every recorded call succeeded', () => {
  const turns = [
    { action: 'init', request: null, response: null }, // system turn, no request/response pair
    { action: 'GET /api/blueprints', request: { method: 'GET', path: '/api/blueprints' }, response: { status: 200 } },
    {
      action: 'POST /api/projects',
      request: { method: 'POST', path: '/api/projects', body: { name: 'x' } },
      response: { status: 201 },
    },
    {
      action: 'GET /api/runs/run-1/events',
      request: { method: 'GET', path: '/api/runs/run-1/events' },
      response: { status: 200 },
    },
  ];

  const result = computeDeterministicP0(turns);
  assert.equal(result.objectivePass, true);
  assert.equal(result.allApiCallsSucceeded, true);
  assert.equal(result.totalCalls, 3);
});

test('computeDeterministicP0 fails when any recorded call did not succeed', () => {
  const turns = [
    { action: 'GET /api/blueprints', request: { method: 'GET', path: '/api/blueprints' }, response: { status: 200 } },
    {
      action: 'POST /api/projects/bad-id/orchestrations',
      request: { method: 'POST', path: '/api/projects/bad-id/orchestrations', body: { goal: 'x' } },
      response: { status: 404 },
    },
  ];

  const result = computeDeterministicP0(turns);
  assert.equal(result.objectivePass, false);
  assert.equal(result.allApiCallsSucceeded, false);
  assert.equal(result.totalCalls, 2);
});

test('computeDeterministicP0 with no recorded calls does not falsely pass', () => {
  const result = computeDeterministicP0([]);
  assert.equal(result.objectivePass, false);
  assert.equal(result.totalCalls, 0);
});
