import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import {
  assembleBriefGenerationPrompt,
  BRIEF_TEMPLATE_HEADINGS,
  normalizeTargetHint,
  parseExcludeList,
} from '../lib/generate-brief.mjs';

test('parseExcludeList trims, splits, and deduplicates entries', () => {
  assert.deepEqual(parseExcludeList(' fittrack, bookclub ,fittrack,, trailmix '), ['fittrack', 'bookclub', 'trailmix']);
});

test('normalizeTargetHint prefers blueprint, then category, else any', () => {
  assert.deepEqual(normalizeTargetHint({ blueprint: 'blueprint-content-authoring', category: 'forum' }), {
    kind: 'blueprint',
    value: 'blueprint-content-authoring',
  });
  assert.deepEqual(normalizeTargetHint({ category: 'forum' }), { kind: 'category', value: 'forum' });
  assert.deepEqual(normalizeTargetHint({ category: 'any' }), { kind: 'any', value: 'any' });
});

test('prompt assembly includes the exclusion list and novelty guardrails', () => {
  const prompt = assembleBriefGenerationPrompt({
    category: 'forum',
    exclude: ['fittrack', 'bookclub', 'trailmix'],
  });

  assert.match(prompt, /Target blueprint-category hint: `forum`/);
  assert.match(prompt, /Already-covered scenarios\/archetypes to avoid repeating: `fittrack`, `bookclub`, `trailmix`\./);
  assert.match(prompt, /Do NOT rename one of those apps or lightly reskin the same concept/);
});

test('prompt assembly handles any-category target explicitly', () => {
  const prompt = assembleBriefGenerationPrompt({ category: 'any', exclude: ['forumhub'] });
  assert.match(prompt, /Target blueprint\/category hint: `any`/);
  assert.doesNotMatch(prompt, /Target blueprint-category hint:/);
});

test('prompt assembly handles an empty exclusion list', () => {
  const prompt = assembleBriefGenerationPrompt({ blueprint: 'blueprint-software-development', exclude: [] });
  assert.match(prompt, /Already-covered scenarios\/archetypes: `\(none supplied\)`\./);
  assert.match(prompt, /avoid clichéd repeats/i);
});

test('prompt assembly includes the exact brief structure contract and agent-driver-safe instructions', () => {
  const prompt = assembleBriefGenerationPrompt({});

  assert.match(prompt, /output ONLY the final markdown brief/i);
  assert.match(prompt, /# Persona brief: <Persona Name> — <Short role\/context label>/);
  for (const heading of BRIEF_TEMPLATE_HEADINGS) {
    assert.match(prompt, new RegExp(heading.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  }
  assert.match(prompt, /format must match the checked-in hand-authored briefs exactly/i);
  assert.match(prompt, /stop at the outcome-spec confirmation gate; do not confirm the spec/i);
});

test('generate-brief CLI writes prompt text to an --out file', () => {
  const tmpRoot = fs.mkdtempSync(path.join(process.cwd(), '.generate-brief-test-'));
  const outFile = path.join(tmpRoot, 'prompt.txt');
  const cli = spawnSync(
    process.execPath,
    [path.join(process.cwd(), 'lib', 'generate-brief.mjs'), '--category', 'forum', '--exclude', 'fittrack,bookclub', '--out', outFile],
    { cwd: process.cwd(), encoding: 'utf8' },
  );

  try {
    assert.equal(cli.status, 0, `stderr:\n${cli.stderr}\nstdout:\n${cli.stdout}`);
    assert.equal(cli.stdout, '');
    assert.match(cli.stderr, /brief-generation prompt written to .*prompt\.txt/i);
    const written = fs.readFileSync(outFile, 'utf8');
    assert.match(written, /Target blueprint-category hint: `forum`/);
    assert.match(written, /`fittrack`, `bookclub`/);
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true });
  }
});
