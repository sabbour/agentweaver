// Unit tests for the Priya scenario's NON-GATING judgeContext and the --insecure
// guard. Run with:  node --test  (from scripts/persona-harness/)
//
// DRIVER/JUDGE SEPARATION: the driver no longer embeds subjective pass/fail for
// Priya's content. Instead the scenario exposes `judgeContext(evidence)`, which
// returns deterministic REFERENCE DATA (expected ticket IDs, the known duplicate
// pair, the raw batch, and what the judge should verify) for a downstream LLM/
// human judge. These tests prove that reference data is complete and correct so a
// judge handed the finding JSON alone can render a P1 verdict — they do NOT assert
// any pass/fail on drafted content (that is the judge's job, not the driver's).

import { test } from 'node:test';
import assert from 'node:assert/strict';

import priya from '../scenarios/priya-ticket-triage.mjs';
import { checkInsecureAllowed } from '../run-persona.mjs';

test('judgeContext exposes all five expected ticket IDs from the sample batch', () => {
  const ctx = priya.judgeContext({});
  assert.ok(Array.isArray(ctx.expectedTicketIds), 'expectedTicketIds should be an array');
  assert.deepEqual(
    [...ctx.expectedTicketIds].sort(),
    ['TICKET-4821', 'TICKET-4822', 'TICKET-4830', 'TICKET-4835', 'TICKET-4840'],
    'judge must be told exactly which tickets to look for',
  );
});

test('judgeContext names the known 4821<->4822 duplicate pair for the judge', () => {
  const ctx = priya.judgeContext({});
  assert.deepEqual([...ctx.knownDuplicatePair].sort(), ['TICKET-4821', 'TICKET-4822']);
  assert.ok(typeof ctx.duplicateRationale === 'string' && ctx.duplicateRationale.length > 0);
});

test('judgeContext embeds the raw ticket batch and a concrete verify checklist', () => {
  const ctx = priya.judgeContext({});
  assert.ok(ctx.rawTicketBatch.includes('TICKET-4835'), 'raw batch should be embedded verbatim for the judge');
  assert.ok(Array.isArray(ctx.judgeShouldVerify) && ctx.judgeShouldVerify.length >= 5);
  // The checklist must call out the specific things a shallow substring check could miss.
  const joined = ctx.judgeShouldVerify.join(' | ').toLowerCase();
  for (const needle of ['duplicate', 'severity', 'owning team', 'internal', 'customer']) {
    assert.ok(joined.includes(needle), `judge checklist should mention "${needle}"`);
  }
});

test('judgeContext is pure reference data — it computes no pass/fail on content', () => {
  const ctx = priya.judgeContext({});
  // No boolean "pass" anywhere in the returned object — the driver must not judge.
  const json = JSON.stringify(ctx);
  assert.ok(!/"pass"\s*:/.test(json), 'judgeContext must not embed any pass/fail verdict');
});

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
