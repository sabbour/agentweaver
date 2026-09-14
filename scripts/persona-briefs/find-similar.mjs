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

function annotateCompletion(entry) {
  const runsToCompletion = typeof entry.runsToCompletion === 'boolean' ? entry.runsToCompletion : null;
  const completionStatus = runsToCompletion === true
    ? 'runs-to-completion'
    : runsToCompletion === false
      ? 'stops-at-gate'
      : 'unknown';
  return {
    ...entry,
    runsToCompletion,
    completionStatus,
    stopsAt: entry.stopsAt ?? null,
  };
}

function rankMatches(description, { entries = null, catalogPath = CATALOG_PATH } = {}) {
  const queryTokens = [...new Set(tokenize(description))];
  const catalog = entries ?? loadCatalog(catalogPath);
  return catalog
    .map((entry) => ({ ...annotateCompletion(entry), ...scoreEntry(queryTokens, entry) }))
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score || a.id.localeCompare(b.id));
}

function describeGateStoppingCandidates(candidates) {
  return candidates
    .map((entry) => `${entry.id}${entry.stopsAt ? ` (${entry.stopsAt})` : ''}`)
    .join(', ');
}

/**
 * Rank catalog entries by relevance to a free-text description.
 * Returns entries with score > 0, sorted descending, each entry annotated with
 * `score`, `matchedTokens`, `runsToCompletion`, `completionStatus`, and `stopsAt`.
 * Set `requiresCompletion` to keep only personas that declare
 * `runsToCompletion: true`.
 */
export function findSimilar(description, {
  entries = null,
  catalogPath = CATALOG_PATH,
  limit = 5,
  requiresCompletion = false,
} = {}) {
  return findSimilarWithDiagnostics(description, { entries, catalogPath, limit, requiresCompletion }).matches;
}

/**
 * Like findSimilar(), but also returns rejected matches and human-facing warnings
 * for cases where keyword similarity diverges from run-to-completion suitability.
 */
export function findSimilarWithDiagnostics(description, {
  entries = null,
  catalogPath = CATALOG_PATH,
  limit = 5,
  requiresCompletion = false,
} = {}) {
  const ranked = rankMatches(description, { entries, catalogPath });
  const rejectedMatches = requiresCompletion
    ? ranked.filter((entry) => entry.runsToCompletion !== true)
    : [];
  const matches = (requiresCompletion
    ? ranked.filter((entry) => entry.runsToCompletion === true)
    : ranked).slice(0, limit);
  const warnings = [];
  const topKeywordMatch = ranked[0];

  if (topKeywordMatch?.runsToCompletion === false) {
    const gate = topKeywordMatch.stopsAt ? `; stopsAt: ${topKeywordMatch.stopsAt}` : '';
    warnings.push(
      `Top keyword match "${topKeywordMatch.id}" stops at a gate (runsToCompletion: false${gate}).` +
      (requiresCompletion
        ? ' It was excluded because completion was required.'
        : ' Use --requires-completion for scenarios that must execute through completion.'),
    );
  }

  if (requiresCompletion && ranked.length > 0 && matches.length === 0) {
    const rejected = describeGateStoppingCandidates(rejectedMatches);
    warnings.push(
      'This query requires a run-to-completion persona, but no keyword-matched candidate declares ' +
      `runsToCompletion: true.${rejected ? ` Gate-stopping/unknown candidates: ${rejected}.` : ''}`,
    );
  } else if (requiresCompletion && ranked.length === 0) {
    warnings.push(
      'This query requires a run-to-completion persona, but no keyword-matched candidates were found.',
    );
  }

  return {
    query: description,
    requirements: { requiresCompletion },
    matches,
    rejectedMatches: rejectedMatches.slice(0, limit),
    warnings,
  };
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
  const takeBoolean = (flag) => {
    const index = args.indexOf(flag);
    if (index < 0) return false;
    args.splice(index, 1);
    return true;
  };
  const description = take('--description');
  const limit = take('--limit');
  const requiresCompletion = takeBoolean('--requires-completion') || takeBoolean('--must-run-to-completion');
  if (args.length || !description) {
    throw new Error(
      'usage: node find-similar.mjs --description "<free text>" [--limit n] ' +
      '[--requires-completion|--must-run-to-completion]',
    );
  }
  const result = findSimilarWithDiagnostics(description, {
    limit: limit ? Number(limit) : undefined,
    requiresCompletion,
  });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  for (const warning of result.warnings) console.error(`Warning: ${warning}`);
  if (result.matches.length === 0 && requiresCompletion) {
    console.error('No completion-eligible matches found — choose a runsToCompletion persona or generate a new reviewed persona core with generate-core.mjs.');
  } else if (result.matches.length === 0) {
    console.error('No close matches found — consider generating a new persona core with generate-core.mjs.');
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 2; });
}
