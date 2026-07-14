#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const CATALOG_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), 'catalog.json');

const STOPWORDS = new Set([
  'a', 'an', 'the', 'and', 'or', 'of', 'to', 'for', 'with', 'that', 'this', 'is', 'are',
  'in', 'on', 'as', 'be', 'it', 'from', 'their', 'who', 'you', 'your', 'i', 'want', 'need',
]);

export function tokenize(text) {
  return String(text ?? '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((token) => token.length > 1 && !STOPWORDS.has(token));
}

export function loadCatalog(catalogPath = CATALOG_PATH) {
  const raw = fs.readFileSync(catalogPath, 'utf8');
  const parsed = JSON.parse(raw);
  if (!Array.isArray(parsed?.entries)) throw new Error(`Catalog at ${catalogPath} is missing an "entries" array`);
  return parsed.entries;
}

/**
 * Cheap keyword/tag overlap scoring: no LLM call. Scores each catalog entry by how
 * many distinct query tokens appear in its id, description, and tags (tag matches
 * weighted higher since they're curated signal), normalized by query token count.
 */
export function scoreEntry(queryTokens, entry) {
  const tagTokens = new Set((entry.tags ?? []).flatMap((tag) => tokenize(tag)));
  const textTokens = new Set([
    ...tokenize(entry.id),
    ...tokenize(entry.description),
  ]);
  let score = 0;
  const matched = new Set();
  for (const token of queryTokens) {
    if (tagTokens.has(token)) { score += 2; matched.add(token); }
    else if (textTokens.has(token)) { score += 1; matched.add(token); }
  }
  return { score, matchedTokens: [...matched] };
}

/**
 * Rank catalog entries by relevance to a free-text description.
 * Returns entries with score > 0, sorted descending, each entry annotated with
 * `score` and `matchedTokens`. No entries are dropped for zero overlap results
 * except by the caller-supplied `limit`.
 */
export function findSimilar(description, { entries = null, catalogPath = CATALOG_PATH, limit = 5 } = {}) {
  const queryTokens = [...new Set(tokenize(description))];
  const catalog = entries ?? loadCatalog(catalogPath);
  return catalog
    .map((entry) => ({ ...entry, ...scoreEntry(queryTokens, entry) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score || a.id.localeCompare(b.id))
    .slice(0, limit);
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
  const description = take('--description');
  const limit = take('--limit');
  if (args.length || !description) throw new Error('usage: node find-similar.mjs --description "<free text>" [--limit n]');
  const matches = findSimilar(description, { limit: limit ? Number(limit) : undefined });
  process.stdout.write(`${JSON.stringify({ query: description, matches }, null, 2)}\n`);
  if (matches.length === 0) {
    console.error('No close matches found — consider generating a new persona core with generate-core.mjs.');
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 2; });
}
