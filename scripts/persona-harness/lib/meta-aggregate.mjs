// lib/meta-aggregate.mjs — Layer-2 META-AGGREGATION across a BATCH of judge verdicts.
//
// Per-run judgment in isolation is noisy; the higher-signal read comes from
// cross-referencing ALL the runs together (JUDGE.md, "Layer 2"). This module is a
// pure aggregator: it consumes the machine-readable verdict blocks an LLM judge
// produced (one per persona transcript, schema agentweaver.persona-judge-verdict/v1,
// see lib/judge.mjs) and rolls them up into invariants / divergences / recurring
// findings / capability gaps / drift. It makes NO subjective judgment of its own —
// it only tallies and cross-references what the judges already decided.
//
// CLI:  node lib/meta-aggregate.mjs verdicts/priya.json verdicts/jordan.json verdicts/maya.json
//       node lib/meta-aggregate.mjs verdicts/            # a directory of *.json verdicts
//       node lib/meta-aggregate.mjs verdicts/*.json --json rollup.json

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { VERDICT_SCHEMA } from './judge.mjs';

/** Normalize a free-text finding title into a grouping key so the same finding
 *  reported by different personas collapses together. */
export function findingKey(finding) {
  if (finding?.relatedIssue) return `issue:${String(finding.relatedIssue).replace(/^#/, '')}`;
  const t = String(finding?.title ?? '')
    .toLowerCase()
    .replace(/[^a-z0-9 ]+/g, ' ')
    .replace(/\b(the|a|an|of|to|in|on|for|can|silently|unrelated|already)\b/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return `title:${t}`;
}

function truthy(v) {
  return v === true || v === 'true' || v === 'yes';
}

function isPlainObject(v) {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}

function validateFinding(finding) {
  const errors = [];
  if (!isPlainObject(finding)) {
    return { ok: false, errors: ['finding must be an object'] };
  }
  if (typeof finding.title !== 'string' || !finding.title.trim()) {
    errors.push('finding.title must be a non-empty string');
  }
  if (typeof finding.kind !== 'string' || !finding.kind.trim()) {
    errors.push('finding.kind must be a non-empty string');
  }
  return { ok: errors.length === 0, errors };
}

function sanitizeVerdictFindings(verdict, opts = {}) {
  const warn = opts.warn ?? (() => {});
  const source = opts.source ?? verdict.persona ?? verdict.transcript ?? 'unknown';
  const findings = [];
  for (const [idx, finding] of (verdict.findings ?? []).entries()) {
    const validation = validateFinding(finding);
    if (!validation.ok) {
      warn(`skip ${source}: findings[${idx}] malformed (${validation.errors.join('; ')})`);
      continue;
    }
    findings.push(finding);
  }
  return { ...verdict, findings };
}

export function validateVerdict(verdict) {
  const errors = [];
  if (!isPlainObject(verdict)) {
    return { ok: false, errors: ['file is not a JSON object'] };
  }
  if (verdict.schema !== VERDICT_SCHEMA) {
    errors.push(`schema must equal ${VERDICT_SCHEMA}`);
  }
  if (typeof verdict.persona !== 'string' && typeof verdict.transcript !== 'string') {
    errors.push('verdict must include persona or transcript');
  }
  if (!isPlainObject(verdict.p0) || typeof verdict.p0.verdict !== 'string') {
    errors.push('p0.verdict must be a string');
  }
  if (!isPlainObject(verdict.p1) || typeof verdict.p1.verdict !== 'string') {
    errors.push('p1.verdict must be a string');
  }
  if (!isPlainObject(verdict.pushback)) {
    errors.push('pushback must be an object');
  } else {
    if (typeof verdict.pushback.count !== 'number') errors.push('pushback.count must be a number');
    if (typeof verdict.pushback.requirementMet !== 'boolean') errors.push('pushback.requirementMet must be a boolean');
  }
  if (!Array.isArray(verdict.findings)) errors.push('findings must be an array');
  if (!Array.isArray(verdict.cannotDetermine)) errors.push('cannotDetermine must be an array');
  return { ok: errors.length === 0, errors };
}

/**
 * Aggregate an array of judge verdicts (already-parsed objects).
 * Pure + deterministic — unit-testable without disk.
 * @param {any[]} verdicts
 */
export function aggregate(verdicts, opts = {}) {
  const personaOf = (v) => v.persona ?? v.transcript ?? 'unknown';
  const sanitizedVerdicts = verdicts.map((v) => sanitizeVerdictFindings(v, { warn: opts.warn, source: personaOf(v) }));
  const runs = sanitizedVerdicts.length;

  // ---- P0 objective tally ----
  const p0Failures = [];
  const p0Passes = [];
  for (const v of sanitizedVerdicts) {
    const verdict = String(v.p0?.verdict ?? '').toUpperCase();
    if (verdict.startsWith('FAIL')) p0Failures.push({ persona: personaOf(v), evidence: v.p0?.evidence ?? null });
    else if (verdict.startsWith('PASS')) p0Passes.push(personaOf(v));
  }

  // ---- P0 invariants: mechanics true in EVERY run ----
  const mechanicKeys = new Set();
  for (const v of sanitizedVerdicts) for (const k of Object.keys(v.p0?.mechanics ?? {})) mechanicKeys.add(k);
  const invariants = [];
  const mechanicDivergences = [];
  for (const k of mechanicKeys) {
    const vals = sanitizedVerdicts.map((v) => ({ persona: personaOf(v), val: v.p0?.mechanics?.[k] }));
    const present = vals.filter((x) => x.val !== undefined);
    if (present.length === 0) continue;
    const allTrue = present.every((x) => truthy(x.val));
    const allFalse = present.every((x) => !truthy(x.val));
    if (allTrue && present.length === runs) invariants.push({ mechanic: k, heldIn: present.length });
    else if (!allTrue && !allFalse) mechanicDivergences.push({ mechanic: k, values: present });
  }

  // ---- P1 subjective tally + divergence ----
  const p1 = { PASS: [], PARTIAL: [], FAIL: [] };
  for (const v of sanitizedVerdicts) {
    const verdict = String(v.p1?.verdict ?? '').toUpperCase();
    if (p1[verdict]) p1[verdict].push(personaOf(v));
  }
  const p1Divergent = new Set([p1.PASS.length ? 'PASS' : null, p1.PARTIAL.length ? 'PARTIAL' : null, p1.FAIL.length ? 'FAIL' : null].filter(Boolean)).size > 1;

  // ---- findings cross-reference (recurring across personas) ----
  const groups = new Map();
  for (const v of sanitizedVerdicts) {
    for (const f of v.findings ?? []) {
      const key = findingKey(f);
      if (!groups.has(key)) groups.set(key, { key, title: f.title, kind: f.kind, relatedIssue: f.relatedIssue ?? null, personas: new Set(), markedRecurring: false, evidence: [] });
      const g = groups.get(key);
      g.personas.add(personaOf(v));
      if (truthy(f.recurring)) g.markedRecurring = true;
      if (f.evidence) g.evidence.push({ persona: personaOf(v), evidence: f.evidence });
      if (!g.title && f.title) g.title = f.title;
    }
  }
  const allFindings = [...groups.values()].map((g) => ({
    key: g.key,
    title: g.title,
    kind: g.kind,
    relatedIssue: g.relatedIssue,
    personas: [...g.personas],
    recurring: g.personas.size >= 2 || g.markedRecurring,
    evidence: g.evidence,
  }));
  const recurringFindings = allFindings.filter((f) => f.recurring);
  const capabilityGaps = allFindings.filter((f) => /gap/i.test(String(f.kind)));
  const drift = allFindings.filter((f) => /drift/i.test(String(f.kind)));

  // ---- pushback requirement ----
  const pushback = sanitizedVerdicts.map((v) => ({ persona: personaOf(v), count: v.pushback?.count ?? null, requirementMet: v.pushback?.requirementMet ?? null }));
  const pushbackRequirementMetAll = pushback.every((p) => truthy(p.requirementMet) || (typeof p.count === 'number' && p.count >= 2));

  // ---- cannot-determine union ----
  const cannotDetermine = [];
  for (const v of sanitizedVerdicts) for (const c of v.cannotDetermine ?? []) if (c) cannotDetermine.push({ persona: personaOf(v), item: c });

  return {
    schema: 'agentweaver.persona-meta-aggregate/v1',
    runs,
    personas: sanitizedVerdicts.map(personaOf),
    p0: { passes: p0Passes, failures: p0Failures, allGreen: p0Failures.length === 0 && p0Passes.length === runs },
    invariants,
    divergences: {
      p0Mechanics: mechanicDivergences,
      p1Quality: p1Divergent ? { note: 'P1 verdict varied across personas — inconsistency is itself a P1 signal (JUDGE.md Layer 2).', breakdown: p1 } : null,
    },
    p1: { breakdown: p1, divergent: p1Divergent },
    recurringFindings,
    capabilityGaps,
    drift,
    pushback: { perPersona: pushback, requirementMetAll: pushbackRequirementMetAll },
    cannotDetermine,
  };
}

export function renderRollup(agg) {
  const L = [];
  L.push(`BATCH: ${agg.runs} run(s) — personas: ${agg.personas.join(', ')}`);
  L.push('');
  L.push(`P0 platform-correctness: ${agg.p0.allGreen ? 'ALL GREEN' : `${agg.p0.failures.length} FAILURE(S)`}`);
  for (const f of agg.p0.failures) L.push(`  - FAIL (${f.persona}): ${f.evidence ?? ''}`);
  L.push('');
  L.push('Invariants (held in EVERY run — candidate platform guarantees):');
  if (agg.invariants.length) for (const i of agg.invariants) L.push(`  - ${i.mechanic} (held in ${i.heldIn}/${agg.runs})`);
  else L.push('  - (none computed — verdicts carried no p0.mechanics block)');
  L.push('');
  L.push('Divergences (varied run-to-run — judgment-call space / P1 signal):');
  if (agg.divergences.p1Quality) L.push(`  - P1 quality: ${JSON.stringify(agg.divergences.p1Quality.breakdown)}`);
  for (const d of agg.divergences.p0Mechanics) L.push(`  - mechanic ${d.mechanic}: ${JSON.stringify(d.values)}`);
  if (!agg.divergences.p1Quality && !agg.divergences.p0Mechanics.length) L.push('  - (none)');
  L.push('');
  L.push('Recurring findings (surfaced by ≥2 personas or flagged recurring):');
  if (agg.recurringFindings.length) for (const f of agg.recurringFindings) L.push(`  - [${f.kind}] ${f.title}${f.relatedIssue ? ` (${f.relatedIssue})` : ''} — personas: ${f.personas.join(', ')}`);
  else L.push('  - (none)');
  L.push('');
  L.push('Capability / tool gaps:');
  if (agg.capabilityGaps.length) for (const f of agg.capabilityGaps) L.push(`  - ${f.title} — personas: ${f.personas.join(', ')}`);
  else L.push('  - (none)');
  L.push('');
  L.push('Drift (system behaviour != what a brief assumed):');
  if (agg.drift.length) for (const f of agg.drift) L.push(`  - ${f.title} — personas: ${f.personas.join(', ')}`);
  else L.push('  - (none)');
  L.push('');
  L.push(`Pushback ≥2 requirement met by all personas: ${agg.pushback.requirementMetAll ? 'YES' : 'NO'}`);
  for (const p of agg.pushback.perPersona) L.push(`  - ${p.persona}: count=${p.count}, requirementMet=${p.requirementMet}`);
  if (agg.cannotDetermine.length) {
    L.push('');
    L.push('CANNOT_DETERMINE (union):');
    for (const c of agg.cannotDetermine) L.push(`  - (${c.persona}) ${c.item}`);
  }
  return L.join('\n');
}

/** Expand file/dir args into a flat list of *.json verdict paths. */
export function collectVerdictPaths(args) {
  const out = [];
  for (const a of args) {
    if (!fs.existsSync(a)) continue;
    const st = fs.statSync(a);
    if (st.isDirectory()) {
      for (const f of fs.readdirSync(a)) if (f.endsWith('.json')) out.push(path.join(a, f));
    } else if (a.endsWith('.json')) {
      out.push(a);
    }
  }
  return out;
}

export function loadVerdicts(paths, opts = {}) {
  const warn = opts.warn ?? ((message) => console.error(message));
  const verdicts = [];
  for (const p of paths) {
    let parsed;
    try {
      parsed = JSON.parse(fs.readFileSync(p, 'utf8'));
    } catch (e) {
      warn(`skip ${p}: ${e.message}`);
      continue;
    }
    const validation = validateVerdict(parsed);
    if (!validation.ok) {
      warn(`skip ${p}: non-conforming verdict (${validation.errors.join('; ')})`);
      continue;
    }
    verdicts.push(sanitizeVerdictFindings(parsed, { warn, source: p }));
  }
  return verdicts;
}

// ---- CLI ----
function isMain() {
  return process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}
if (isMain()) {
  const args = process.argv.slice(2);
  const jsonIdx = args.indexOf('--json');
  let jsonOut = null;
  if (jsonIdx !== -1) {
    jsonOut = args[jsonIdx + 1];
    args.splice(jsonIdx, 2);
  }
  const paths = collectVerdictPaths(args);
  if (!paths.length) {
    console.error('usage: node lib/meta-aggregate.mjs <verdict.json | dir> ... [--json rollup.json]');
    console.error('  (verdict JSONs are the machine-readable blocks an LLM judge produced from lib/judge.mjs prompts)');
    process.exit(2);
  }
  const verdicts = loadVerdicts(paths);
  if (!verdicts.length) {
    console.error('no valid verdicts found');
    process.exit(2);
  }
  const agg = aggregate(verdicts);
  process.stdout.write(renderRollup(agg) + '\n');
  if (jsonOut) {
    fs.writeFileSync(jsonOut, JSON.stringify(agg, null, 2), 'utf8');
    console.error(`\nrollup JSON written to ${jsonOut}`);
  }
}
