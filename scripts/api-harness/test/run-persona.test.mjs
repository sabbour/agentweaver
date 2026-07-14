// Unit tests for run-persona.mjs's --insecure guard (preserved infra plumbing —
// unrelated to which scenario kind is driven). The persona-behavior judgeContext
// tests that used to live alongside this (Priya's ticket-triage reference data)
// were removed with scenarios/priya-ticket-triage.mjs: persona scenarios are no
// longer fixed scripts, so there is no static judgeContext left to unit test —
// content-quality assessment now happens entirely in the Judge subagent reading a
// dynamically-driven transcript. Run with: node --test (from scripts/api-harness/)

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { checkInsecureAllowed } from '../run-persona.mjs';

test('--insecure guard: staging and localhost are allowed', () => {
  assert.equal(checkInsecureAllowed('https://agentweaver.abc123.westus2.staging.aksapp.io', true, false), null);
  assert.equal(checkInsecureAllowed('https://localhost:8080', true, false), null);
  assert.equal(checkInsecureAllowed('http://127.0.0.1:5000', true, false), null);
});

test('--insecure guard: production host is blocked unless overridden', () => {
  const err = checkInsecureAllowed('https://agentweaver.example.com', true, false);
  assert.ok(err && /refusing to disable TLS/i.test(err), `expected a block message, got: ${err}`);
  // Explicit override lifts the block.
  assert.equal(checkInsecureAllowed('https://agentweaver.example.com', true, true), null);
  // Without --insecure there is nothing to guard.
  assert.equal(checkInsecureAllowed('https://agentweaver.example.com', false, false), null);
});
