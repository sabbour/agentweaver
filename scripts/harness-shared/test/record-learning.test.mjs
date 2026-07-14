import assert from 'node:assert/strict';
import { test } from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { formatEntry, parseEntryTitles, recordLearning, validateLearning } from '../record-learning.mjs';

function tempFile() {
  return path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'learnings-')), 'learnings.md');
}

test('validateLearning requires all mandatory fields with allowed enum values', () => {
  assert.equal(validateLearning({ title: 't', category: 'bug', surface: 'mcp', body: 'b' }).valid, true);
  assert.equal(validateLearning({ category: 'bug', surface: 'mcp', body: 'b' }).valid, false);
  assert.equal(validateLearning({ title: 't', category: 'nope', surface: 'mcp', body: 'b' }).valid, false);
  assert.equal(validateLearning({ title: 't', category: 'bug', surface: 'nope', body: 'b' }).valid, false);
  assert.equal(validateLearning({ title: 't', category: 'bug', surface: 'mcp', body: '' }).valid, false);
  assert.equal(validateLearning({ title: 't', category: 'bug', surface: 'mcp', body: 'b', status: 'nope' }).valid, false);
  assert.equal(validateLearning({ title: 't', category: 'bug', surface: 'mcp', body: 'b', date: 'not-a-date' }).valid, false);
});

test('formatEntry renders the documented heading + metadata shape', () => {
  const rendered = formatEntry({ title: 'A title', category: 'bug', surface: 'api', body: 'Body text.', date: '2026-01-02' });
  assert.match(rendered, /^## A title\n/);
  assert.match(rendered, /- date: 2026-01-02/);
  assert.match(rendered, /- category: bug/);
  assert.match(rendered, /- surface: api/);
  assert.match(rendered, /- status: open/);
  assert.match(rendered, /Body text\./);
});

test('parseEntryTitles extracts every ## heading from a document', () => {
  const doc = '# Harness learnings\n\n## First title\n\nbody\n\n---\n\n## Second title\n\nbody\n';
  assert.deepEqual(parseEntryTitles(doc), ['First title', 'Second title']);
});

test('recordLearning appends a new entry to an empty or missing file', () => {
  const filePath = tempFile();
  const result = recordLearning({ title: 'New fact', category: 'environment-fact', surface: 'all', body: 'Detail here.' }, { filePath });
  assert.equal(result.appended, true);
  const content = fs.readFileSync(filePath, 'utf8');
  assert.match(content, /## New fact/);
  assert.match(content, /Detail here\./);
});

test('recordLearning refuses an exact-title duplicate (case-insensitive) and leaves the file unchanged', () => {
  const filePath = tempFile();
  recordLearning({ title: 'Duplicate check', category: 'bug', surface: 'ui', body: 'first body' }, { filePath });
  const before = fs.readFileSync(filePath, 'utf8');
  const result = recordLearning({ title: 'duplicate CHECK', category: 'bug', surface: 'ui', body: 'second body' }, { filePath });
  assert.equal(result.appended, false);
  assert.match(result.reason, /already exists/);
  const after = fs.readFileSync(filePath, 'utf8');
  assert.equal(after, before);
});

test('recordLearning supports multiple distinct appends separated by a divider', () => {
  const filePath = tempFile();
  recordLearning({ title: 'Entry one', category: 'bug', surface: 'api', body: 'one' }, { filePath });
  recordLearning({ title: 'Entry two', category: 'scenario-design-note', surface: 'ui', body: 'two' }, { filePath });
  const content = fs.readFileSync(filePath, 'utf8');
  assert.deepEqual(parseEntryTitles(content), ['Entry one', 'Entry two']);
  assert.match(content, /---/);
});

test('recordLearning throws on an invalid entry instead of writing anything', () => {
  const filePath = tempFile();
  assert.throws(() => recordLearning({ title: '', category: 'bug', surface: 'api', body: 'x' }, { filePath }), /Invalid learning entry/);
  assert.equal(fs.existsSync(filePath), false);
});
