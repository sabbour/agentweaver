import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const WORKFLOW = (await readFile(path.join(HERE, "..", "..", "..", ".github", "workflows", "coverage.yml"), "utf8"))
  .replaceAll("\r\n", "\n");

function workflowSection(startMarker, endMarker) {
  const start = WORKFLOW.indexOf(startMarker);
  assert.notEqual(start, -1, `missing workflow marker: ${startMarker.trim()}`);
  const end = endMarker === null ? WORKFLOW.length : WORKFLOW.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(end, -1, `missing workflow marker: ${endMarker?.trim()}`);
  return WORKFLOW.slice(start, end);
}

test("coverage only runs on schedule, manual dispatch, and its own scoped pull_request paths", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");
  assert.doesNotMatch(trigger, /^\s+push:/m);
  assert.match(trigger, /schedule:/);
  assert.match(trigger, /workflow_dispatch: \{\}/);
  // The pull_request trigger must stay narrowly scoped to this workflow's own
  // files, never to ordinary product paths that would double instrumented
  // coverage onto every PR.
  const prTrigger = workflowSection("  pull_request:\n", "\n\npermissions:\n");
  assert.match(prTrigger, /- '\.github\/workflows\/coverage\.yml'/);
  assert.match(prTrigger, /- 'scripts\/ci\/coverage\.mjs'/);
  assert.match(prTrigger, /- 'scripts\/ci\/coverage-summary\.mjs'/);
  assert.doesNotMatch(prTrigger, /apps\/web/);
  assert.doesNotMatch(prTrigger, /\*\*\/\*\.cs/);
});

test("every area job runs its real coverage command without continue-on-error", () => {
  const dotnetJob = workflowSection("  dotnet-coverage:\n", "\n  web-coverage:\n");
  const webJob = workflowSection("  web-coverage:\n", "\n  node-coverage:\n");
  const nodeJob = workflowSection("  node-coverage:\n", "\n  coverage-summary:\n");

  for (const job of [dotnetJob, webJob, nodeJob]) {
    assert.doesNotMatch(job, /continue-on-error: true/, "a partial/failed coverage run must fail its job");
  }
  assert.match(dotnetJob, /run: npm run coverage:dotnet/);
  assert.match(webJob, /run: npm run coverage:web/);
  assert.match(nodeJob, /run: npm run coverage:node/);
});

test("every area uploads its report unconditionally with bounded retention", () => {
  const dotnetJob = workflowSection("  dotnet-coverage:\n", "\n  web-coverage:\n");
  const webJob = workflowSection("  web-coverage:\n", "\n  node-coverage:\n");
  const nodeJob = workflowSection("  node-coverage:\n", "\n  coverage-summary:\n");

  for (const job of [dotnetJob, webJob, nodeJob]) {
    assert.match(job, /uses: actions\/upload-artifact@v4/);
    assert.match(job, /if: always\(\)\n\s+uses: actions\/upload-artifact@v4/, "upload must run even if the coverage step failed");
    assert.match(job, /retention-days: \$\{\{ fromJSON\(env\.COVERAGE_ARTIFACT_RETENTION_DAYS\) \}\}/);
    // A coverage command that exits zero but drops/moves its output path must
    // still fail the job — "warn" would let a missing report pass silently.
    assert.match(job, /if-no-files-found: error/, "a missing coverage report must fail the upload step, not just warn");
  }
  assert.match(dotnetJob, /path: TestResults\/coverage/);
  assert.match(webJob, /path: apps\/web\/coverage/);
  assert.match(nodeJob, /path: coverage\/node/);
  assert.match(WORKFLOW, /COVERAGE_ARTIFACT_RETENTION_DAYS: 14/);
});

test("bubblewrap is installed and required for the Kata runtime shard inside coverage:dotnet", () => {
  const dotnetJob = workflowSection("  dotnet-coverage:\n", "\n  web-coverage:\n");
  assert.match(dotnetJob, /Install bubblewrap/);
  assert.match(dotnetJob, /bwrap --unshare-user --unshare-pid/);
  assert.match(dotnetJob, /AGENTWEAVER_REQUIRE_BWRAP: "1"/);
});

test("step-level timeouts leave headroom below each job's timeout so a hung run still uploads", () => {
  const dotnetJob = workflowSection("  dotnet-coverage:\n", "\n  web-coverage:\n");
  const webJob = workflowSection("  web-coverage:\n", "\n  node-coverage:\n");
  const nodeJob = workflowSection("  node-coverage:\n", "\n  coverage-summary:\n");

  assert.match(dotnetJob, /timeout-minutes: 140/);
  assert.match(dotnetJob, /Run instrumented \.NET coverage\n\s+timeout-minutes: 130/);
  assert.match(webJob, /timeout-minutes: 30/);
  assert.match(webJob, /Run instrumented web coverage\n\s+timeout-minutes: 20/);
  assert.match(nodeJob, /timeout-minutes: 30/);
  assert.match(nodeJob, /Run instrumented Node coverage\n\s+timeout-minutes: 20/);
});

test("the summary job reports every area's real job result without gating the workflow", () => {
  const summaryJob = workflowSection("  coverage-summary:\n", null);
  assert.match(summaryJob, /needs: \[dotnet-coverage, web-coverage, node-coverage\]/);
  assert.match(summaryJob, /if: always\(\)/);
  assert.match(summaryJob, /--dotnet-status "\$\{\{ needs\.dotnet-coverage\.result \}\}"/);
  assert.match(summaryJob, /--web-status "\$\{\{ needs\.web-coverage\.result \}\}"/);
  assert.match(summaryJob, /--node-status "\$\{\{ needs\.node-coverage\.result \}\}"/);
  assert.match(summaryJob, /--out "\$GITHUB_STEP_SUMMARY"/);
  assert.match(summaryJob, /continue-on-error: true\n\s+with:\n\s+name: dotnet-coverage/);
});
