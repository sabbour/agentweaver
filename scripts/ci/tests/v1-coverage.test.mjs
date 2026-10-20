import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  badge, dotnetCoverage, findOpenIssue, issueBody, issueMarker, nodeCoverage, percent, report, syncIssue, webCoverage,
} from '../v1-coverage-report.mjs';

const read = (file) => readFileSync(new URL(`../../../${file}`, import.meta.url), 'utf8');
const measured = (pct, covered, total, gaps = []) => ({ pct, covered, total, gaps });
const measurement = {
  surfaces: {
    dotnet: measured(82.9, 829, 1000, [{ name: 'Agentweaver.Gateway: Program', pct: 0, covered: 0, total: 40, missed: 40 }]),
    web: measured(75.6, 756, 1000),
    node: measured(88.1, 881, 1000),
  },
};
const context = { sha: 'a'.repeat(40), ref: 'v1', runUrl: 'https://github.com/o/r/actions/runs/7', artifact: 'v1-coverage-a-1', measuredAt: 'now' };

test('percentages round down and reject impossible or empty measurements', () => {
  assert.equal(percent(829, 1000), 82.9);
  assert.equal(percent(2, 3), 66.6);
  for (const [covered, total] of [[0, 0], [2, 1], [-1, 3], [1.5, 3]]) assert.throws(() => percent(covered, total), /Invalid/);
});

test('each surface keeps its own denominator and ranks the largest real gaps', () => {
  const dotnet = dotnetCoverage({
    summary: { coveredlines: 90, coverablelines: 120 },
    coverage: { assemblies: [{ name: 'A', classesinassembly: [
      { name: 'Covered', coveredlines: 60, coverablelines: 60 },
      { name: 'Gap', coveredlines: 0, coverablelines: 30 },
      { name: 'Tiny', coveredlines: 0, coverablelines: 5 },
    ] }] },
  });
  assert.equal(dotnet.pct, 75);
  assert.deepEqual(dotnet.gaps.map((gap) => gap.name), ['A: Gap']);

  const web = webCoverage({
    total: { lines: { covered: 15, total: 40 } },
    '/home/runner/work/x/x/apps/web/src/v1/App.tsx': { lines: { covered: 5, total: 30 } },
  });
  assert.equal(web.pct, 37.5);
  assert.equal(web.gaps[0].name, 'apps/web/src/v1/App.tsx');

  // Repeated LCOV records for one file are unioned, not double-counted.
  const lcov = ['SF:scripts/release/a.mjs', ...Array.from({ length: 20 }, (_, i) => `DA:${i + 1},${i < 5 ? 1 : 0}`), 'end_of_record',
    'SF:/w/scripts/release/a.mjs', 'DA:6,3', 'DA:1,0', 'end_of_record'].join('\n');
  const node = nodeCoverage(lcov);
  assert.deepEqual([node.covered, node.total, node.pct], [6, 20, 30]);
  assert.equal(node.gaps[0].name, 'scripts/release/a.mjs');
});

test('badges are shields.io endpoint documents carrying only the measured value', () => {
  assert.deepEqual(badge('v1 web coverage', measured(75.6, 1, 2)),
    { schemaVersion: 1, label: 'v1 web coverage', message: '75.6%', color: 'green' });
  assert.equal(badge('x', measured(59.9, 1, 2)).color, 'red');
  assert.equal(badge('x', measured(80, 1, 2)).message, '80.0%');
});

test('report and issue body carry provenance, gaps, and verifiable acceptance criteria', () => {
  for (const text of [report(measurement, context), issueBody(measurement, context)]) {
    assert.match(text, /\| \.NET \| 82\.9% \| 829 \/ 1000 \|/);
    assert.match(text, /\| Node release tooling \| 88\.1% \|/);
    assert.ok(text.includes(context.sha) && text.includes(context.runUrl) && text.includes(context.artifact));
  }
  const body = issueBody(measurement, context);
  assert.ok(body.startsWith(issueMarker));
  assert.match(body, /Add tests for `Agentweaver.Gateway: Program`: 0\.0% \(0\/40 lines, 40 uncovered\)/);
  assert.match(body, /No surface drops below its measured value \(\.NET 82\.9%, Web 75\.6%, Node release tooling 88\.1%\)/);
  assert.match(body, /No coverage exclusions/);
});

test('duplicate handling picks the oldest open marker issue and ignores pull requests and closed issues', () => {
  const issues = [
    { number: 9, state: 'open', body: issueMarker },
    { number: 3, state: 'closed', body: issueMarker },
    { number: 4, state: 'open', body: issueMarker, pull_request: {} },
    { number: 5, state: 'open', body: 'unrelated' },
    { number: 7, state: 'open', body: `${issueMarker}\nold` },
  ];
  assert.equal(findOpenIssue(issues).number, 7);
  assert.equal(findOpenIssue(issues.slice(1, 4)), undefined);
});

function fakeApi(existing, assigneesAfter = []) {
  const calls = [];
  const request = async (method, route, body) => {
    calls.push([method, route, body]);
    if (method === 'GET' && route.includes('/issues?')) return existing;
    if (method === 'GET') return { number: 7, html_url: 'u/7', assignees: assigneesAfter };
    return { number: body?.labels ? 8 : 7, html_url: 'u', assignees: existing[0]?.assignees ?? [] };
  };
  return { calls, request, log: { notice: () => {}, warning: (message) => calls.push(['WARN', message]) } };
}
const sync = (api, copilotRequest) => syncIssue({
  request: api.request, copilotRequest, repository: 'o/r', baseBranch: 'v1', title: 't', body: issueMarker, log: api.log,
});

test('issue sync updates an existing issue instead of creating a duplicate', async () => {
  const api = fakeApi([{ number: 7, state: 'open', body: issueMarker, assignees: [{ login: 'Copilot' }] }]);
  const result = await sync(api);
  assert.deepEqual([result.action, result.assignment], ['updated', 'already-assigned']);
  assert.ok(api.calls.some(([method, route]) => method === 'PATCH' && route === '/repos/o/r/issues/7'));
  assert.ok(!api.calls.some(([method, route]) => method === 'POST' && route === '/repos/o/r/issues'));
});

test('missing Copilot token creates the issue, warns, and comments instead of claiming assignment', async () => {
  const api = fakeApi([]);
  const result = await sync(api);
  assert.deepEqual([result.action, result.assignment], ['created', 'missing-token']);
  assert.ok(api.calls.some(([kind, message]) => kind === 'WARN' && /COPILOT_ASSIGN_TOKEN/.test(message)));
  assert.ok(api.calls.some(([method, route, body]) => method === 'POST' && route.endsWith('/comments') && /not assigned/.test(body.body)));
});

test('Copilot assignment uses the user token with a v1 agent assignment and verifies the assignee', async () => {
  const assigned = [];
  const copilot = async (method, route, body) => { assigned.push([method, route, body]); return {}; };
  const ok = await sync(fakeApi([], [{ login: 'Copilot' }]), copilot);
  assert.equal(ok.assignment, 'assigned');
  assert.deepEqual(assigned[0][2].assignees, ['copilot-swe-agent[bot]']);
  assert.deepEqual([assigned[0][2].agent_assignment.base_branch, assigned[0][2].agent_assignment.target_repo], ['v1', 'o/r']);
  await assert.rejects(sync(fakeApi([], []), copilot), /no Copilot assignee/);
});

test('normal CI never instruments tests and only the weekly workflow publishes', async () => {
  const { parse } = await import('yaml');
  const ci = parse(read('.github/workflows/v1-ci.yml'));
  assert.equal(ci.env.COLLECT_COVERAGE, '${{ inputs.collect_coverage == true }}');
  assert.equal(ci.permissions.contents, 'read');
  const coverage = parse(read('.github/workflows/v1-coverage.yml'));
  assert.deepEqual(Object.keys(coverage.on), ['workflow_dispatch']);
  assert.equal(coverage.on.workflow_dispatch.inputs.publish.default, false);
  assert.deepEqual(coverage.permissions, { contents: 'read' });
  assert.equal(coverage.jobs.measure.uses, './.github/workflows/v1-ci.yml');
  assert.equal(coverage.jobs.measure.with.collect_coverage, true);
  const publish = coverage.jobs.publish;
  assert.match(publish.if, /inputs.publish && needs.measure.result == 'success'/);
  assert.deepEqual(publish.permissions, { contents: 'write', issues: 'write' });
  const script = publish.steps.map((step) => step.run ?? '').join('\n');
  const download = publish.steps.find((step) => step.uses === 'actions/download-artifact@v4');
  assert.equal(download.with.pattern, 'v1-coverage-${{ github.sha }}-*', 'a publish-only rerun must find the earlier measurement attempt');
  assert.ok(script.includes('COVERAGE_ARTIFACT=v1-coverage-$GITHUB_SHA-$latest'));
  assert.match(script, /push origin HEAD:refs\/heads\/badges/);
  assert.doesNotMatch(script, /push origin [^\n]*v1\b/);
  assert.equal(publish.steps.at(-1).env.COPILOT_BASE_BRANCH, 'v1');
  assert.match(publish.steps.at(-1).env.COPILOT_ASSIGN_TOKEN, /secrets.COPILOT_ASSIGN_TOKEN/);
});

test('README badges read the published endpoint documents and link to the durable report', () => {
  const readme = read('README.md');
  for (const key of ['dotnet', 'web', 'node']) {
    assert.ok(readme.includes(`https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-${key}.json`), key);
  }
  assert.ok(readme.includes('(https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)'));
  assert.doesNotMatch(readme, /coverage-\d|\d+(\.\d+)?%25/);
});
