#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const LEARNINGS_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), 'learnings.md');

export const CATEGORIES = ['bug', 'environment-fact', 'scenario-design-note'];
export const SURFACES = ['api', 'ui', 'mcp', 'all'];
export const STATUSES = ['open', 'fixed'];

function todayIso() {
  return new Date().toISOString().slice(0, 10);
}

/** Parse existing `## Title` entries out of a learnings.md document (titles only, for dedup). */
export function parseEntryTitles(text) {
  const titles = [];
  for (const match of String(text ?? '').matchAll(/^##\s+(.+)$/gm)) titles.push(match[1].trim());
  return titles;
}

export function validateLearning({ title, category, surface, body, status = 'open', date } = {}) {
  const errors = [];
  if (!String(title ?? '').trim()) errors.push('title is required');
  if (!CATEGORIES.includes(category)) errors.push(`category must be one of: ${CATEGORIES.join(', ')}`);
  if (!SURFACES.includes(surface)) errors.push(`surface must be one of: ${SURFACES.join(', ')}`);
  if (!String(body ?? '').trim()) errors.push('body is required');
  if (!STATUSES.includes(status)) errors.push(`status must be one of: ${STATUSES.join(', ')}`);
  if (date && !/^\d{4}-\d{2}-\d{2}$/.test(date)) errors.push('date must be an ISO YYYY-MM-DD string');
  return { valid: errors.length === 0, errors };
}

export function formatEntry({ title, category, surface, body, status = 'open', date = todayIso() }) {
  return [
    `## ${title.trim()}`,
    '',
    `- date: ${date}`,
    `- category: ${category}`,
    `- surface: ${surface}`,
    `- status: ${status}`,
    '',
    body.trim(),
    '',
  ].join('\n');
}

/**
 * Append a new learning entry to learnings.md (or `filePath` if supplied, for tests).
 * Validates the entry and refuses an exact-title duplicate (case-insensitive).
 * Returns `{ appended: boolean, reason?: string }`.
 */
export function recordLearning(entry, { filePath = LEARNINGS_PATH } = {}) {
  const validation = validateLearning(entry);
  if (!validation.valid) throw new Error(`Invalid learning entry: ${validation.errors.join('; ')}`);

  const existing = fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : '';
  const existingTitles = parseEntryTitles(existing).map((title) => title.toLowerCase());
  if (existingTitles.includes(entry.title.trim().toLowerCase())) {
    return { appended: false, reason: `an entry titled "${entry.title.trim()}" already exists` };
  }

  const separator = existing.trim() ? '\n---\n\n' : '';
  fs.appendFileSync(filePath, separator + formatEntry(entry));
  return { appended: true };
}

async function main() {
  const args = process.argv.slice(2);
  const take = (flag) => {
    const index = args.indexOf(flag);
    if (index < 0) return null;
    const value = args[index + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`${flag} requires a value`);
    args.splice(index, 2);
    return value;
  };
  const entry = {
    title: take('--title'),
    category: take('--category'),
    surface: take('--surface'),
    body: take('--body'),
    status: take('--status') ?? 'open',
    date: take('--date') ?? undefined,
  };
  if (args.length) {
    throw new Error('usage: node record-learning.mjs --title <t> --category <bug|environment-fact|scenario-design-note> --surface <api|ui|mcp|all> --body <text> [--status open|fixed] [--date YYYY-MM-DD]');
  }
  const result = recordLearning(entry);
  if (result.appended) console.error(`Learning "${entry.title}" appended to ${path.relative(process.cwd(), LEARNINGS_PATH)}`);
  else console.error(`Skipped: ${result.reason}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 2; });
}
