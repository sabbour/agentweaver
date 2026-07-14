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

import { computeDeterministicP0, resolveOperation } from '../drive.mjs';

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

// resolveOperation: the "dynamic client built from swagger" path. It must resolve
// purely from whatever the spec declares — no persona/business-specific knowledge
// of what an operationId "means".
const SAMPLE_CACHED_SPEC = {
  endpoints: [
    {
      method: 'GET',
      path: '/api/projects/{projectId}/runs',
      operationId: 'listRuns',
      parameters: [
        { name: 'projectId', in: 'path', required: true },
        { name: 'status', in: 'query', required: false },
      ],
    },
    {
      method: 'POST',
      path: '/api/blueprints',
      operationId: 'createBlueprint',
      parameters: [],
    },
  ],
};

test('resolveOperation substitutes path params and appends query params', () => {
  const resolved = resolveOperation(SAMPLE_CACHED_SPEC, 'listRuns', { projectId: 'proj-1', status: 'active' });
  assert.equal(resolved.method, 'GET');
  assert.equal(resolved.path, '/api/projects/proj-1/runs?status=active');
});

test('resolveOperation works with no params for operations that need none', () => {
  const resolved = resolveOperation(SAMPLE_CACHED_SPEC, 'createBlueprint', {});
  assert.equal(resolved.method, 'POST');
  assert.equal(resolved.path, '/api/blueprints');
});

test('resolveOperation throws on unknown operationId (never guesses)', () => {
  assert.throws(() => resolveOperation(SAMPLE_CACHED_SPEC, 'doesNotExist', {}), /not found in the cached spec/);
});

test('resolveOperation throws when a required path param is missing', () => {
  assert.throws(() => resolveOperation(SAMPLE_CACHED_SPEC, 'listRuns', {}), /requires path param "projectId"/);
});
