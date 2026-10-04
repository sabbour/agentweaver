import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { TEST_SHARDS } from './dotnet-test-shards.mjs';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const EXPECTED_DOTNET_SHARD_COUNT = TEST_SHARDS.length;

// Job outcomes that genuinely indicate the coverage run never happened for an
// area (as opposed to having run — possibly to a cancelled or partial end —
// and potentially leaving a partial artifact worth inspecting).
const NO_RUN_STATUSES = new Set(['skipped']);

export function readJsonIfExists(filePath) {
  if (!existsSync(filePath)) {
    return null;
  }
  try {
    return JSON.parse(readFileSync(filePath, 'utf8'));
  } catch {
    return null;
  }
}

export function formatRatio(covered, total) {
  if (!isValidMetricPair(covered, total)) {
    return 'n/a';
  }
  const pct = ((covered / total) * 100).toFixed(2);
  return `${covered}/${total} (${pct}%)`;
}

// A metric pair is only meaningful when both numbers are finite, non-negative,
// and covered does not exceed total. `formatRatio` falls back to "n/a" for
// anything else; completeness must use this same check rather than trusting
// an upstream status flag, so a malformed/schema-drifted report cannot be
// displayed as a confident "complete" result.
function isValidMetricPair(covered, total) {
  return Number.isFinite(covered) && Number.isFinite(total)
    && covered >= 0 && total > 0 && covered <= total;
}

// `dir` is the directory a workflow downloaded the `dotnet-coverage` artifact
// into (the uploaded `TestResults/coverage` tree), or null when no artifact
// was downloaded at all (the job failed before the upload step could run).
export function summarizeDotnet(dir, jobStatus) {
  if (NO_RUN_STATUSES.has(jobStatus)) {
    return { label: '.NET', status: 'not run', complete: false, rows: [], notes: ['Job did not run.'] };
  }
  const statusJson = dir ? readJsonIfExists(path.join(dir, 'status.json')) : null;
  if (!statusJson) {
    return {
      label: '.NET',
      status: jobStatus,
      complete: false,
      rows: [],
      notes: ['No status.json was found; the coverage job produced no usable report.'],
    };
  }
  const summaryJson = statusJson.combined
    ? readJsonIfExists(path.join(dir, 'combined', 'Summary.json'))
    : null;
  const rows = [];
  let metricsValid = false;
  if (summaryJson?.summary) {
    const totals = summaryJson.summary;
    const pairs = [
      ['Lines', totals.coveredlines, totals.coverablelines],
      ['Branches', totals.coveredbranches, totals.totalbranches],
      ['Methods', totals.coveredmethods, totals.totalmethods],
    ];
    metricsValid = pairs.every(([, covered, total]) => isValidMetricPair(covered, total));
    for (const [label, covered, total] of pairs) {
      rows.push([label, formatRatio(covered, total)]);
    }
  }
  const notes = [];
  notes.push(`Revision: \`${statusJson.revision ?? 'unknown'}\``);
  const completedCount = Array.isArray(statusJson.completed) ? statusJson.completed.length : 0;
  notes.push(`Shards verified: ${completedCount}/${EXPECTED_DOTNET_SHARD_COUNT}`);
  if (Array.isArray(statusJson.completed) && statusJson.completed.length > 0) {
    notes.push(`Completed shards: ${statusJson.completed.map((entry) => entry.id).join(', ')}`);
  }
  if (Array.isArray(statusJson.missing) && statusJson.missing.length > 0) {
    notes.push(`Missing/failed: ${statusJson.missing.join('; ')}`);
  }
  if (Array.isArray(statusJson.absentAssemblies) && statusJson.absentAssemblies.length > 0) {
    notes.push(`Assemblies absent from instrumentation: ${statusJson.absentAssemblies.join(', ')}`);
  }
  if (!statusJson.combined) {
    notes.push('No combined report was produced.');
  } else if (!metricsValid) {
    notes.push('Combined Summary.json is missing or malformed; totals above are not trustworthy.');
  }
  // Fail closed: trust the GitHub job conclusion and the upstream `complete`
  // flag, but also independently require a full shard set, no reported
  // failures, and a well-formed combined report. A schema-drifted or
  // downstream-corrupted artifact must never render as "complete" just
  // because `coverage.mjs` claimed success.
  const complete = jobStatus === 'success'
    && statusJson.complete === true
    && metricsValid
    && completedCount === EXPECTED_DOTNET_SHARD_COUNT
    && (!Array.isArray(statusJson.missing) || statusJson.missing.length === 0)
    && (!Array.isArray(statusJson.absentAssemblies) || statusJson.absentAssemblies.length === 0);
  return {
    label: '.NET',
    status: jobStatus,
    complete,
    rows,
    notes,
  };
}

// `dir` is the directory a workflow downloaded the web/node coverage artifact
// into (the uploaded `coverage/` tree containing `coverage-summary.json`).
export function summarizeJs(label, dir, jobStatus) {
  if (NO_RUN_STATUSES.has(jobStatus)) {
    return { label, status: 'not run', complete: false, rows: [], notes: ['Job did not run.'] };
  }
  const summaryJson = dir ? readJsonIfExists(path.join(dir, 'coverage-summary.json')) : null;
  if (!summaryJson?.total) {
    return {
      label,
      status: jobStatus,
      complete: false,
      rows: [],
      notes: ['No coverage-summary.json was found; the coverage job produced no usable report.'],
    };
  }
  const { total } = summaryJson;
  const pairs = [
    ['Lines', total.lines?.covered, total.lines?.total],
    ['Branches', total.branches?.covered, total.branches?.total],
    ['Functions', total.functions?.covered, total.functions?.total],
  ];
  const metricsValid = pairs.every(([, covered, totalValue]) => isValidMetricPair(covered, totalValue));
  const rows = pairs.map(([metric, covered, totalValue]) => [metric, formatRatio(covered, totalValue)]);
  const fileCount = Object.keys(summaryJson).filter((key) => key !== 'total').length;
  const notes = [`Instrumented source files: ${fileCount}`];
  if (!metricsValid) {
    notes.push('coverage-summary.json totals are missing or malformed; totals above are not trustworthy.');
  }
  // Fail closed: a successful job with a well-formed, fully numeric summary is
  // the only "complete" case. A malformed/zero-denominator report must never
  // render as complete just because the job exited zero.
  return { label, status: jobStatus, complete: jobStatus === 'success' && metricsValid, rows, notes };
}

function statusBadge(section) {
  if (section.status === 'not run') {
    return '⬜ not run';
  }
  return section.complete ? '✅ complete' : '⚠️ partial/failed';
}

function renderSection(section) {
  const lines = [`### ${section.label} — ${statusBadge(section)}`, ''];
  if (section.rows.length > 0) {
    lines.push('| Metric | Covered/Total |', '| --- | --- |');
    for (const [metric, value] of section.rows) {
      lines.push(`| ${metric} | ${value} |`);
    }
    lines.push('');
  }
  for (const note of section.notes) {
    lines.push(`- ${note}`);
  }
  lines.push('');
  return lines.join('\n');
}

export function buildMarkdown({ sha, dotnet, web, node }) {
  const sections = [dotnet, web, node];
  const overall = sections.every((section) => section.status === 'not run')
    ? 'No coverage families ran.'
    : sections.every((section) => section.complete || section.status === 'not run')
      ? 'All attempted families are complete.'
      : 'At least one family is partial, failed, or did not run — see below.';
  return [
    '## Cross-surface coverage report',
    '',
    `Measured against commit \`${sha}\`. Line/branch/function/method coverage describes`,
    'executed instrumentation only, not behavioral completeness. No pass/fail threshold',
    'is applied here; use these figures to find uncovered paths, not as a quality gate.',
    '',
    `**Summary:** ${overall}`,
    '',
    ...sections.map(renderSection),
  ].join('\n');
}

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith('--')) {
      continue;
    }
    const key = arg.slice(2);
    const value = argv[i + 1];
    options[key] = value;
    i += 1;
  }
  return options;
}

function resolveOptionalDir(value) {
  if (!value) {
    return null;
  }
  const resolved = path.resolve(REPO_ROOT, value);
  return existsSync(resolved) ? resolved : null;
}

function main(argv) {
  const options = parseArgs(argv);
  const sha = options.sha ?? 'unknown';
  const markdown = buildMarkdown({
    sha,
    dotnet: summarizeDotnet(resolveOptionalDir(options['dotnet-dir']), options['dotnet-status'] ?? 'unknown'),
    web: summarizeJs('Web', resolveOptionalDir(options['web-dir']), options['web-status'] ?? 'unknown'),
    node: summarizeJs('Node', resolveOptionalDir(options['node-dir']), options['node-status'] ?? 'unknown'),
  });
  const out = options.out;
  if (!out || out === '-') {
    process.stdout.write(`${markdown}\n`);
    return;
  }
  if (out === process.env.GITHUB_STEP_SUMMARY) {
    appendFileSync(out, `${markdown}\n`);
    return;
  }
  writeFileSync(out, `${markdown}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}
