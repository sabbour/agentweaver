// Turns one successful weekly v1 coverage artifact into per-surface line-coverage
// percentages, shields.io endpoint badges, a durable Markdown report, and a single
// open Copilot coverage-improvement issue.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const issueMarker = '<!-- agentweaver:v1-coverage-improvement -->';
export const surfaces = [
  ['dotnet', '.NET', 'v1 .NET coverage'],
  ['web', 'Web', 'v1 web coverage'],
  ['node', 'Node release tooling', 'v1 Node coverage'],
];
const minimumGapLines = 10;
const gapCount = 5;

/** Round down to one decimal place so a published value never overstates coverage. */
export function percent(covered, total) {
  if (!Number.isInteger(covered) || !Number.isInteger(total) || total <= 0 || covered < 0 || covered > total) {
    throw new Error(`Invalid line counts: ${covered}/${total}`);
  }
  return Math.floor((covered / total) * 1000) / 10;
}

function surface(units, totals = units.reduce((sum, unit) => [sum[0] + unit.covered, sum[1] + unit.total], [0, 0])) {
  const [covered, total] = totals;
  const gaps = units
    .filter((unit) => unit.total - unit.covered >= minimumGapLines)
    .map((unit) => ({ ...unit, pct: percent(unit.covered, unit.total), missed: unit.total - unit.covered }))
    .sort((left, right) => left.pct - right.pct || right.missed - left.missed || left.name.localeCompare(right.name))
    .slice(0, gapCount);
  return { covered, total, pct: percent(covered, total), gaps };
}

/** Read ReportGenerator's combined JsonSummary; classes are the unit for .NET gaps. */
export function dotnetCoverage(summary) {
  const units = summary.coverage.assemblies.flatMap((assembly) => assembly.classesinassembly.map((type) => ({
    name: `${assembly.name}: ${type.name}`, covered: type.coveredlines, total: type.coverablelines,
  }))).filter((unit) => unit.total > 0);
  return surface(units, [summary.summary.coveredlines, summary.summary.coverablelines]);
}

/** Read Vitest's json-summary; source files are the unit for web gaps. */
export function webCoverage(summary) {
  const units = Object.entries(summary).filter(([name]) => name !== 'total').map(([name, value]) => ({
    name: name.replaceAll('\\', '/').replace(/^.*?(?=apps\/web\/)/, ''), covered: value.lines.covered, total: value.lines.total,
  })).filter((unit) => unit.total > 0);
  return surface(units, [summary.total.lines.covered, summary.total.lines.total]);
}

/** Merge repeated LCOV records per source file; a line is covered when any test process hit it. */
export function nodeCoverage(lcov) {
  const files = new Map();
  let current;
  for (const line of lcov.split(/\r?\n/)) {
    if (line.startsWith('SF:')) {
      const name = line.slice(3).replaceAll('\\', '/').replace(/^.*?(?=scripts\/)/, '');
      current = files.get(name) ?? new Map();
      files.set(name, current);
    } else if (line.startsWith('DA:') && current) {
      const [number, hits] = line.slice(3).split(',');
      current.set(number, (current.get(number) ?? false) || Number(hits) > 0);
    } else if (line === 'end_of_record') {
      current = undefined;
    }
  }
  const units = [...files].map(([name, lines]) => ({
    name, covered: [...lines.values()].filter(Boolean).length, total: lines.size,
  })).filter((unit) => unit.total > 0);
  return surface(units);
}

/** Read and validate all three surfaces; any missing or empty report rejects publication. */
export function measure(directory) {
  const read = (file) => readFileSync(path.join(directory, file), 'utf8');
  const source = JSON.parse(read('source.json'));
  if (!/^[0-9a-f]{40}$/.test(source.testedSourceSha ?? '')) throw new Error('source.json has no tested source SHA');
  return {
    source,
    surfaces: {
      dotnet: dotnetCoverage(JSON.parse(read('dotnet/combined/Summary.json'))),
      web: webCoverage(JSON.parse(read('web/coverage-summary.json'))),
      node: nodeCoverage(read('node/lcov.info')),
    },
  };
}

/** Build a shields.io endpoint document from a measured surface. */
export function badge(label, measured) {
  const color = measured.pct >= 80 ? 'brightgreen' : measured.pct >= 70 ? 'green' : measured.pct >= 60 ? 'yellow' : 'red';
  return { schemaVersion: 1, label, message: `${measured.pct.toFixed(1)}%`, color };
}

function table(measurement) {
  return [
    '| Surface | Line coverage | Covered / coverable lines |',
    '| --- | ---: | ---: |',
    ...surfaces.map(([key, name]) => {
      const value = measurement.surfaces[key];
      return `| ${name} | ${value.pct.toFixed(1)}% | ${value.covered} / ${value.total} |`;
    }),
  ].join('\n');
}

function provenance(context) {
  return [
    `- Source: \`${context.sha}\` on \`${context.ref}\``,
    `- Run: ${context.runUrl}`,
    `- Artifact: \`${context.artifact}\` (retained 30 days)`,
    `- Measured: ${context.measuredAt}`,
  ].join('\n');
}

/** Durable report stored beside the badges; the README badges link here. */
export function report(measurement, context) {
  return [
    '# Latest v1 coverage measurement',
    '',
    'Generated by the weekly `v1 coverage` workflow. Each surface is measured separately:',
    'line coverage is covered executable lines divided by coverable lines in that surface\'s',
    'instrumented scope. The surfaces are not combined into one percentage.',
    '',
    table(measurement),
    '',
    provenance(context),
    '',
  ].join('\n');
}

/** Issue body with measured gaps, concrete test targets, and verifiable acceptance criteria. */
export function issueBody(measurement, context) {
  const gaps = surfaces.flatMap(([key, name]) => {
    const value = measurement.surfaces[key];
    if (value.gaps.length === 0) return [];
    return [`### ${name}`, '', ...value.gaps.map((gap) =>
      `- [ ] Add tests for \`${gap.name}\`: ${gap.pct.toFixed(1)}% (${gap.covered}/${gap.total} lines, ${gap.missed} uncovered).`), ''];
  });
  return [
    issueMarker,
    'The weekly v1 coverage measurement found the gaps below. Add focused tests on a branch',
    'from `v1` that exercise real behavior in the listed code. Do not change production behavior.',
    '',
    '## Measurement',
    '',
    table(measurement),
    '',
    provenance(context),
    '',
    '## Tests to add',
    '',
    ...gaps,
    '## Acceptance criteria',
    '',
    '- [ ] New or extended tests cover at least three listed items and assert observable behavior, not only execution.',
    '- [ ] Each targeted item gains line coverage relative to the numbers above.',
    `- [ ] No surface drops below its measured value (${surfaces.map(([key, name]) => `${name} ${measurement.surfaces[key].pct.toFixed(1)}%`).join(', ')}).`,
    '- [ ] No coverage exclusions, ignore pragmas, or skipped tests are added to raise the numbers.',
    '- [ ] The `v1 foundation CI` pull request checks pass, and a manual `v1 coverage` run on the branch reports the improvement.',
    '',
    'This issue is updated in place by later weekly runs while it stays open.',
    '',
  ].join('\n');
}

/** Pick the oldest open marker issue so weekly runs update one issue instead of creating duplicates. */
export function findOpenIssue(issues) {
  return issues
    .filter((issue) => issue.state === 'open' && !issue.pull_request && issue.body?.includes(issueMarker))
    .sort((left, right) => left.number - right.number)[0];
}

const copilotLogins = new Set(['copilot-swe-agent', 'copilot-swe-agent[bot]', 'Copilot']);
const isCopilot = (assignee) => copilotLogins.has(assignee.login);

/**
 * Create or update the single improvement issue, then assign Copilot with a user token.
 * GITHUB_TOKEN cannot assign Copilot, so a missing user token produces a visible warning, not success.
 */
export async function syncIssue({ request, copilotRequest, repository, baseBranch, title, body, log }) {
  const issues = await request('GET', `/repos/${repository}/issues?state=open&labels=area:testing&per_page=100`);
  let issue = findOpenIssue(issues);
  let action;
  if (issue) {
    issue = await request('PATCH', `/repos/${repository}/issues/${issue.number}`, { title, body });
    action = 'updated';
  } else {
    issue = await request('POST', `/repos/${repository}/issues`, { title, body, labels: ['type:chore', 'area:testing'] });
    action = 'created';
  }
  log.notice(`Coverage improvement issue ${action}: ${issue.html_url}`);
  if ((issue.assignees ?? []).some(isCopilot)) {
    log.notice('Copilot is already assigned; the open assignment continues with the updated body.');
    return { issue, action, assignment: 'already-assigned' };
  }
  if (!copilotRequest) {
    const message = 'COPILOT_ASSIGN_TOKEN is not configured, so Copilot was not assigned. Add a user token secret as described in docs/guide/testing.md.';
    log.warning(message);
    await request('POST', `/repos/${repository}/issues/${issue.number}/comments`, { body: `⚠️ ${message}` });
    return { issue, action, assignment: 'missing-token' };
  }
  await copilotRequest('POST', `/repos/${repository}/issues/${issue.number}/assignees`, {
    assignees: ['copilot-swe-agent[bot]'],
    agent_assignment: { target_repo: repository, base_branch: baseBranch, custom_instructions: '', custom_agent: '', model: '' },
  });
  const assigned = await request('GET', `/repos/${repository}/issues/${issue.number}`);
  if (!(assigned.assignees ?? []).some(isCopilot)) {
    throw new Error(`Copilot assignment was accepted but ${assigned.html_url} has no Copilot assignee`);
  }
  log.notice(`Copilot assigned to ${assigned.html_url}`);
  return { issue: assigned, action, assignment: 'assigned' };
}

function client(token) {
  return async (method, route, body) => {
    const response = await fetch(`${process.env.GITHUB_API_URL ?? 'https://api.github.com'}${route}`, {
      method,
      headers: {
        accept: 'application/vnd.github+json',
        authorization: `Bearer ${token}`,
        'x-github-api-version': '2022-11-28',
        ...(body ? { 'content-type': 'application/json' } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) throw new Error(`${method} ${route} failed with ${response.status}: ${await response.text()}`);
    return response.json();
  };
}

function context() {
  const server = process.env.GITHUB_SERVER_URL ?? 'https://github.com';
  return {
    sha: process.env.GITHUB_SHA,
    ref: process.env.GITHUB_REF_NAME,
    runUrl: `${server}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`,
    artifact: process.env.COVERAGE_ARTIFACT,
    measuredAt: new Date().toISOString(),
  };
}

async function main([command, directory, output]) {
  const measurement = measure(directory);
  const details = context();
  if (measurement.source.testedSourceSha !== details.sha) {
    throw new Error(`Artifact measured ${measurement.source.testedSourceSha}, not ${details.sha}`);
  }
  if (command === 'badges') {
    mkdirSync(output, { recursive: true });
    for (const [key, , label] of surfaces) {
      writeFileSync(path.join(output, `coverage-${key}.json`), `${JSON.stringify(badge(label, measurement.surfaces[key]))}\n`);
    }
    writeFileSync(path.join(output, 'coverage.json'), `${JSON.stringify({ ...details, surfaces: measurement.surfaces }, null, 2)}\n`);
    writeFileSync(path.join(output, 'coverage.md'), report(measurement, details));
  } else if (command === 'issue') {
    const log = {
      notice: (message) => console.log(`::notice::${message}`),
      warning: (message) => console.log(`::warning::${message}`),
    };
    const result = await syncIssue({
      request: client(process.env.GITHUB_TOKEN),
      copilotRequest: process.env.COPILOT_ASSIGN_TOKEN ? client(process.env.COPILOT_ASSIGN_TOKEN) : undefined,
      repository: process.env.GITHUB_REPOSITORY,
      baseBranch: process.env.COPILOT_BASE_BRANCH,
      title: 'Improve v1 test coverage (weekly measurement)',
      body: issueBody(measurement, details),
      log,
    });
    if (process.env.GITHUB_STEP_SUMMARY) {
      writeFileSync(process.env.GITHUB_STEP_SUMMARY,
        `## Coverage improvement issue\n\n${result.issue.html_url} was ${result.action}; Copilot assignment: \`${result.assignment}\`.\n`,
        { flag: 'a' });
    }
  } else {
    throw new Error('Usage: v1-coverage-report.mjs badges|issue <coverage-directory> [badge-directory]');
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
}
