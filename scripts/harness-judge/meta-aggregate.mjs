import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  FRUSTRATION_SCORES,
  META_AGGREGATE_SCHEMA,
  validateVerdict,
} from './verdict-schema.mjs';

function isPlainObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function truthy(value) {
  return value === true || value === 'true' || value === 'yes';
}

function numericFrustration(level) {
  return level in FRUSTRATION_SCORES ? FRUSTRATION_SCORES[level] : null;
}

export function findingKey(finding) {
  if (finding?.relatedIssue) return `issue:${String(finding.relatedIssue).replace(/^#/, '')}`;
  const normalized = String(finding?.title ?? '')
    .toLowerCase()
    .replace(/[^a-z0-9 ]+/g, ' ')
    .replace(/\b(the|a|an|of|to|in|on|for|can|already|same)\b/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return `title:${normalized}`;
}

function collectJsonFiles(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) collectJsonFiles(full, out);
    else if (entry.isFile() && entry.name.endsWith('.json')) out.push(full);
  }
}

export function collectVerdictPaths(args) {
  const out = [];
  for (const value of args) {
    if (!fs.existsSync(value)) continue;
    const stat = fs.statSync(value);
    if (stat.isDirectory()) collectJsonFiles(value, out);
    else if (value.endsWith('.json')) out.push(value);
  }
  return out;
}

export function loadVerdicts(paths, opts = {}) {
  const warn = opts.warn ?? ((message) => console.error(message));
  const verdicts = [];
  for (const file of paths) {
    let parsed;
    try {
      parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (error) {
      warn(`skip ${file}: ${error.message}`);
      continue;
    }
    const validation = validateVerdict(parsed);
    if (!validation.ok) {
      warn(`skip ${file}: non-conforming verdict (${validation.errors.join('; ')})`);
      continue;
    }
    verdicts.push(parsed);
  }
  return verdicts;
}

export function groupVerdicts(verdicts) {
  const groups = new Map();
  for (const verdict of verdicts) {
    const key = `${verdict.batchId}::${verdict.scenarioId}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(verdict);
  }
  return groups;
}

function summarizeVerdictBlock(verdicts, field, allowed) {
  const breakdown = Object.fromEntries(allowed.map((value) => [value, 0]));
  for (const verdict of verdicts) {
    const value = String(verdict?.[field]?.verdict ?? '');
    if (value in breakdown) breakdown[value] += 1;
  }
  return breakdown;
}

function summarizeSurface(verdicts, surface) {
  const frustrationObserved = verdicts
    .map((verdict) => ({
      level: verdict.frustration.level,
      score: numericFrustration(verdict.frustration.level),
      runId: verdict.runId,
    }))
    .filter((entry) => entry.score !== null);

  const frustrationLevels = {};
  for (const verdict of verdicts) {
    frustrationLevels[verdict.frustration.level] = (frustrationLevels[verdict.frustration.level] ?? 0) + 1;
  }

  return {
    surface,
    count: verdicts.length,
    personas: [...new Set(verdicts.map((verdict) => verdict.persona).filter(Boolean))],
    runIds: verdicts.map((verdict) => verdict.runId),
    p0: summarizeVerdictBlock(verdicts, 'p0', ['PASS', 'FAIL', 'CANNOT_DETERMINE']),
    p1: summarizeVerdictBlock(verdicts, 'p1', ['PASS', 'PARTIAL', 'FAIL', 'CANNOT_DETERMINE']),
    frustration: {
      levels: frustrationLevels,
      observedCount: frustrationObserved.length,
      notAssessedCount: verdicts.length - frustrationObserved.length,
      averageScore: frustrationObserved.length
        ? frustrationObserved.reduce((sum, entry) => sum + entry.score, 0) / frustrationObserved.length
        : null,
      maxLevel: frustrationObserved.length
        ? frustrationObserved.reduce((best, entry) => (entry.score > best.score ? entry : best), frustrationObserved[0]).level
        : 'not_assessed',
    },
    findings: verdicts.flatMap((verdict) => verdict.findings.map((finding) => ({ ...finding, runId: verdict.runId }))),
  };
}

function buildFindingGroups(verdicts) {
  const groups = new Map();
  for (const verdict of verdicts) {
    for (const finding of verdict.findings) {
      const key = findingKey(finding);
      if (!groups.has(key)) {
        groups.set(key, {
          key,
          title: finding.title,
          kind: finding.kind,
          relatedIssue: finding.relatedIssue ?? null,
          surfaces: new Set(),
          personas: new Set(),
          occurrences: [],
        });
      }
      const group = groups.get(key);
      group.surfaces.add(verdict.surface);
      if (verdict.persona) group.personas.add(verdict.persona);
      group.occurrences.push({
        surface: verdict.surface,
        runId: verdict.runId,
        evidence: finding.evidence ?? null,
      });
    }
  }
  return [...groups.values()].map((group) => ({
    ...group,
    surfaces: [...group.surfaces],
    personas: [...group.personas],
    recurringAcrossSurfaces: group.surfaces.size >= 2,
  }));
}

function correlateSurfaces(surfaceSummaries, verdicts) {
  const correlations = [];
  const api = surfaceSummaries.api;
  const ui = surfaceSummaries.ui;
  const mcp = surfaceSummaries.mcp;

  const hasCleanApi = !!api && api.p0.PASS > 0 && api.p0.FAIL === 0 && api.p1.FAIL === 0;
  const hasApiP0Failure = !!api && api.p0.FAIL > 0;
  const uiFrustrated = !!ui && (ui.frustration.averageScore ?? -1) > 0;
  const mcpFrustrated = !!mcp && (mcp.frustration.averageScore ?? -1) > 0;

  if (hasCleanApi && uiFrustrated) {
    correlations.push({
      kind: 'pure_ux_issue',
      summary: 'API surface stayed clean while UI runs showed frustration; frame this as an experience-layer issue first.',
      surfaces: ['api', 'ui'],
    });
  }
  if (hasApiP0Failure && uiFrustrated) {
    correlations.push({
      kind: 'backend_root_cause',
      summary: 'API P0 failures co-occurred with UI frustration; likely backend/platform root cause surfacing through UX.',
      surfaces: ['api', 'ui'],
    });
  }
  if (hasCleanApi && mcpFrustrated) {
    correlations.push({
      kind: 'protocol_or_integration_issue',
      summary: 'API surface stayed clean while MCP runs showed frustration; suspect MCP/protocol integration friction.',
      surfaces: ['api', 'mcp'],
    });
  }

  const p1Shapes = Object.entries(surfaceSummaries)
    .filter(([, summary]) => summary)
    .map(([surface, summary]) => ({ surface, dominant: Object.entries(summary.p1).sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'CANNOT_DETERMINE' }));
  const uniqueP1 = new Set(p1Shapes.map((shape) => shape.dominant));
  if (uniqueP1.size > 1) {
    correlations.push({
      kind: 'cross_surface_p1_divergence',
      summary: 'P1 quality verdicts diverged across surfaces for the same batch/scenario.',
      surfaces: p1Shapes.map((shape) => shape.surface),
      details: p1Shapes,
    });
  }

  const frustrationShapes = Object.entries(surfaceSummaries)
    .filter(([, summary]) => summary)
    .map(([surface, summary]) => ({ surface, level: summary.frustration.maxLevel }));
  const uniqueFrustration = new Set(frustrationShapes.map((shape) => shape.level));
  if (uniqueFrustration.size > 1) {
    correlations.push({
      kind: 'cross_surface_frustration_divergence',
      summary: 'Frustration signals diverged across surfaces for the same batch/scenario.',
      surfaces: frustrationShapes.map((shape) => shape.surface),
      details: frustrationShapes,
    });
  }

  if (new Set(verdicts.map((verdict) => verdict.targetRevision)).size > 1) {
    correlations.push({
      kind: 'mixed_target_revision',
      summary: 'This batch/scenario tuple spans multiple target revisions; compare cautiously and prefer reruns on a single revision.',
      surfaces: [...new Set(verdicts.map((verdict) => verdict.surface))],
    });
  }

  return correlations;
}

export function aggregateGroup(verdicts) {
  const sample = verdicts[0];
  const revisions = [...new Set(verdicts.map((verdict) => verdict.targetRevision))];
  const surfaceSummaries = {
    api: null,
    ui: null,
    mcp: null,
  };
  for (const surface of Object.keys(surfaceSummaries)) {
    const subset = verdicts.filter((verdict) => verdict.surface === surface);
    if (subset.length) surfaceSummaries[surface] = summarizeSurface(subset, surface);
  }

  const findings = buildFindingGroups(verdicts);
  const correlations = correlateSurfaces(surfaceSummaries, verdicts);
  const cannotDetermine = verdicts.flatMap((verdict) =>
    verdict.cannotDetermine.map((item) => ({ surface: verdict.surface, runId: verdict.runId, item })),
  );

  return {
    batchId: sample.batchId,
    scenarioId: sample.scenarioId,
    inputSeed: sample.inputSeed,
    targetRevisions: revisions,
    surfaces: surfaceSummaries,
    verdictCount: verdicts.length,
    findings,
    recurringFindings: findings.filter((finding) => finding.recurringAcrossSurfaces),
    correlations,
    cannotDetermine,
    personas: [...new Set(verdicts.map((verdict) => verdict.persona).filter(Boolean))],
  };
}

export function aggregate(verdicts) {
  const groups = [...groupVerdicts(verdicts).values()].map((group) => aggregateGroup(group));
  return {
    schema: META_AGGREGATE_SCHEMA,
    groupCount: groups.length,
    verdictCount: verdicts.length,
    groups,
    summary: {
      scenarios: groups.map((group) => ({ batchId: group.batchId, scenarioId: group.scenarioId, verdictCount: group.verdictCount })),
      correlationCounts: groups.flatMap((group) => group.correlations).reduce((acc, item) => {
        acc[item.kind] = (acc[item.kind] ?? 0) + 1;
        return acc;
      }, {}),
    },
  };
}

export function renderRollup(aggregateResult) {
  const lines = [];
  lines.push(`Cross-surface groups: ${aggregateResult.groupCount} (${aggregateResult.verdictCount} verdicts)`);
  for (const group of aggregateResult.groups) {
    lines.push('');
    lines.push(`BATCH ${group.batchId} / SCENARIO ${group.scenarioId}`);
    lines.push(`  inputSeed=${group.inputSeed} targetRevisions=${group.targetRevisions.join(', ')}`);
    for (const [surface, summary] of Object.entries(group.surfaces)) {
      if (!summary) continue;
      const avg = summary.frustration.averageScore == null ? 'n/a' : summary.frustration.averageScore.toFixed(2);
      lines.push(`  - ${surface}: runs=${summary.count} p0=${JSON.stringify(summary.p0)} p1=${JSON.stringify(summary.p1)} frustration(avg=${avg}, max=${summary.frustration.maxLevel})`);
    }
    if (group.correlations.length) {
      lines.push('  correlations:');
      for (const correlation of group.correlations) lines.push(`    * ${correlation.kind}: ${correlation.summary}`);
    } else {
      lines.push('  correlations: none');
    }
    if (group.recurringFindings.length) {
      lines.push('  recurring findings:');
      for (const finding of group.recurringFindings) {
        lines.push(`    * [${finding.kind}] ${finding.title} — surfaces: ${finding.surfaces.join(', ')}`);
      }
    }
  }
  return lines.join('\n');
}

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
    console.error('usage: node meta-aggregate.mjs <verdict.json | dir> ... [--json rollup.json]');
    process.exit(2);
  }
  const verdicts = loadVerdicts(paths);
  if (!verdicts.length) {
    console.error('no valid verdicts found');
    process.exit(2);
  }
  const agg = aggregate(verdicts);
  process.stdout.write(`${renderRollup(agg)}\n`);
  if (jsonOut) fs.writeFileSync(jsonOut, JSON.stringify(agg, null, 2), 'utf8');
}
