import assert from 'node:assert/strict';
import { test } from 'node:test';
import { findSimilar, scoreEntry, tokenize } from '../find-similar.mjs';

const FIXTURE_CATALOG = [
  {
    id: 'priya',
    description: 'Customer support lead triaging a messy ticket queue: grouping, severity, duplicates, missing info.',
    tags: ['support', 'ticket', 'triage', 'severity', 'customer', 'queue'],
  },
  {
    id: 'maya',
    description: 'Product-marketing strategist producing a sourced, confidence-rated competitive brief.',
    tags: ['marketing', 'competitive-analysis', 'strategy', 'brief', 'business'],
  },
  {
    id: 'jordan',
    description: 'Greenfield product developer taking an idea through delivery and live verification.',
    tags: ['greenfield', 'product', 'developer', 'deployment', 'delivery'],
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

test('findSimilar loads the real checked-in catalog.json without throwing', () => {
  const matches = findSimilar('ticket triage severity support queue');
  assert.ok(Array.isArray(matches));
  assert.ok(matches.some((entry) => entry.id === 'priya'));
});
