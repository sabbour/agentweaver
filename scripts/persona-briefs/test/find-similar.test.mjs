import assert from 'node:assert/strict';
import { test } from 'node:test';
import { findSimilar, findSimilarWithDiagnostics, scoreEntry, tokenize } from '../find-similar.mjs';

const FIXTURE_CATALOG = [
  {
    id: 'priya',
    description: 'Customer support lead triaging a messy ticket queue: grouping, severity, duplicates, missing info.',
    tags: ['support', 'ticket', 'triage', 'severity', 'customer', 'queue'],
    runsToCompletion: false,
    stopsAt: 'outcome-spec / plan-review confirmation gate (never confirms execution)',
  },
  {
    id: 'maya',
    description: 'Product-marketing strategist producing a sourced, confidence-rated competitive brief.',
    tags: ['marketing', 'competitive-analysis', 'strategy', 'brief', 'business'],
    runsToCompletion: false,
    stopsAt: 'outcome-spec / plan-review confirmation gate (never confirms execution)',
  },
  {
    id: 'jordan',
    description: 'Greenfield product developer taking an idea through delivery and live verification.',
    tags: ['greenfield', 'product', 'developer', 'deployment', 'delivery'],
    runsToCompletion: false,
    stopsAt: 'outcome-spec / plan-review confirmation gate (never confirms execution)',
  },
  {
    id: 'oracle',
    description: 'Product manager driving a prototype through the full lifecycle to live preview validation.',
    tags: ['product-manager', 'prototype', 'preview', 'full-lifecycle', 'validation'],
    runsToCompletion: true,
    stopsAt: 'after the full lifecycle reaches a genuine end',
  },
];

test('tokenize lowercases, splits on non-alphanumerics, and drops stopwords/short tokens', () => {
  assert.deepEqual(tokenize('Please test Ticket-Triage severity!'), ['please', 'test', 'ticket', 'triage', 'severity']);
});

test('scoreEntry weights tag matches higher than description-only matches', () => {
  const tagHit = scoreEntry(['support'], FIXTURE_CATALOG[0]);
  const descOnlyHit = scoreEntry(['grouping'], FIXTURE_CATALOG[0]);
  assert.ok(tagHit.score > descOnlyHit.score);
  assert.deepEqual(tagHit.matchedTokens, ['support']);
});

test('findSimilar ranks the closest fixture entry first for a support-triage query', () => {
  const matches = findSimilar('help me test ticket severity triage for support escalations', { entries: FIXTURE_CATALOG });
  assert.ok(matches.length > 0);
  assert.equal(matches[0].id, 'priya');
});

test('findSimilar ranks a marketing/competitive query to maya over unrelated entries', () => {
  const matches = findSimilar('need a competitive marketing brief with sourced business claims', { entries: FIXTURE_CATALOG });
  assert.equal(matches[0].id, 'maya');
});

test('findSimilar returns no matches for an unrelated query rather than a false positive', () => {
  const matches = findSimilar('xyzzy quantum flux capacitor calibration', { entries: FIXTURE_CATALOG });
  assert.deepEqual(matches, []);
});

test('findSimilar honors the limit option', () => {
  const matches = findSimilar('product delivery developer greenfield support ticket brief marketing', {
    entries: FIXTURE_CATALOG,
    limit: 1,
  });
  assert.equal(matches.length, 1);
});

test('findSimilar can require run-to-completion personas and exclude gate-stopping keyword matches', () => {
  const matches = findSimilar('greenfield product developer delivery with live preview validation', {
    entries: FIXTURE_CATALOG,
    requiresCompletion: true,
  });

  assert.ok(matches.length > 0);
  assert.equal(matches[0].id, 'oracle');
  assert.equal(matches[0].runsToCompletion, true);
  assert.ok(!matches.some((entry) => entry.id === 'jordan'));
});

test('findSimilarWithDiagnostics warns when the strongest keyword match stops at a gate', () => {
  const result = findSimilarWithDiagnostics('greenfield product developer delivery with live preview validation', {
    entries: FIXTURE_CATALOG,
    requiresCompletion: true,
  });

  assert.equal(result.matches[0].id, 'oracle');
  assert.ok(result.rejectedMatches.some((entry) => entry.id === 'jordan' && entry.runsToCompletion === false));
  assert.match(result.warnings.join('\n'), /jordan.*stops at a gate/i);
});

test('findSimilarWithDiagnostics makes completion-required dead ends explicit', () => {
  const result = findSimilarWithDiagnostics('greenfield developer delivery', {
    entries: FIXTURE_CATALOG.filter((entry) => entry.id !== 'oracle'),
    requiresCompletion: true,
  });

  assert.deepEqual(result.matches, []);
  assert.equal(result.rejectedMatches[0].id, 'jordan');
  assert.match(result.warnings.join('\n'), /requires a run-to-completion persona/i);
  assert.match(result.warnings.join('\n'), /no keyword-matched candidate declares runsToCompletion: true/i);
});

test('findSimilar loads the real checked-in catalog.json without throwing', () => {
  const matches = findSimilar('ticket triage severity support queue');
  assert.ok(Array.isArray(matches));
  assert.ok(matches.some((entry) => entry.id === 'priya'));
});
