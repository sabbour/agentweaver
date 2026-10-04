import test from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.join(HERE, "..", "..", "..");
const WORKFLOW = (await readFile(path.join(REPO_ROOT, ".github", "workflows", "ci.yml"), "utf8"))
  .replaceAll("\r\n", "\n");

function workflowSection(startMarker, endMarker) {
  const start = WORKFLOW.indexOf(startMarker);
  assert.notEqual(start, -1, `missing workflow marker: ${startMarker.trim()}`);
  const end = endMarker === null ? WORKFLOW.length : WORKFLOW.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(end, -1, `missing workflow marker: ${endMarker?.trim()}`);
  return WORKFLOW.slice(start, end);
}

test("coverage no longer lives in a separate workflow file", () => {
  assert.equal(
    existsSync(path.join(REPO_ROOT, ".github", "workflows", "coverage.yml")),
    false,
    "coverage.yml must be removed now that ci.yml collects coverage from its own test jobs",
  );
});

test("ci.yml gained a schedule and an opt-in collect_coverage dispatch input, with no new push/pull_request trigger", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");
  assert.match(trigger, /schedule:/);
  assert.match(trigger, /- cron: "0 5 \* \* 1"/);
  assert.match(trigger, /workflow_dispatch:\n\s+inputs:\n\s+collect_coverage:/);
  assert.match(trigger, /type: boolean/);
  assert.match(trigger, /default: false/);
  // No duplicate trigger was added: pull_request/push stay exactly as they
  // already were before coverage was threaded into this workflow.
  assert.match(trigger, /pull_request:\n/);
  assert.match(trigger, /push:\n\s+branches: \[dev, main\]/);
});

test("scheduled/dispatch coverage runs get their own concurrency group so an ordinary push can't cancel a coverage run", () => {
  assert.match(
    WORKFLOW,
    /group: ci-\$\{\{ github\.workflow \}\}-\$\{\{ github\.ref \}\}-\$\{\{ \(github\.event_name == 'schedule' \|\| github\.event_name == 'workflow_dispatch'\) && 'coverage' \|\| 'default' \}\}/,
  );
  assert.match(WORKFLOW, /cancel-in-progress: true/);
});

test("the changes job computes a collect_coverage output from schedule/dispatch only", () => {
  const changesJob = workflowSection("  changes:\n", "\n  release-main-ancestry:\n");
  assert.match(changesJob, /collect_coverage: \$\{\{ steps\.coverage-mode\.outputs\.collect_coverage \}\}/);
  assert.match(changesJob, /id: coverage-mode/);
  assert.match(changesJob, /github\.event_name \}\}" = "schedule"/);
  assert.match(changesJob, /workflow_dispatch' && inputs\.collect_coverage/);
});

test("collect_coverage forces the dotnet/web/node jobs to run without changing their path-filter behavior otherwise", () => {
  assert.match(
    WORKFLOW,
    /if: \(github\.event_name == 'pull_request' && github\.base_ref == 'dev'\) \|\| needs\.changes\.outputs\.dotnet == 'true' \|\| needs\.changes\.outputs\.collect_coverage == 'true'/,
    "dotnet-test-plan must also run when collect_coverage is true",
  );
  const nodeJob = workflowSection("  node-toolchain-tests:\n", "\n  web-tests:\n");
  assert.match(nodeJob, /needs\.changes\.outputs\.node-toolchain == 'true' \|\| needs\.changes\.outputs\.collect_coverage == 'true'/);
  const webJob = workflowSection("  web-tests:\n", "\n  docs-build:\n");
  assert.match(webJob, /needs\.changes\.outputs\.web == 'true' \|\| needs\.changes\.outputs\.collect_coverage == 'true'/);

  // The draft-PR skip must be bypassed ONLY when collect_coverage forces the
  // job to run, never for every non-pull_request event in general — the
  // latter would also newly run these jobs on an ordinary push to dev/main
  // regardless of collect_coverage, which is outside this redesign's scope.
  for (const job of [nodeJob, webJob]) {
    assert.match(job, /\(needs\.changes\.outputs\.collect_coverage == 'true' \|\| github\.event\.pull_request\.draft == false\)/);
    assert.doesNotMatch(job, /github\.event_name != 'pull_request'/);
  }
});

test("each coverage-collecting job runs exactly one test command, with coverage flags appended rather than a second invocation", () => {
  const dotnetShardsJob = workflowSection("  dotnet-test-shards:\n", "\n  dotnet-tests:\n");
  // Both the retry-wrapped Kata step and the plain step must call the SAME
  // CLI with the SAME conditional flag — never two different commands.
  const dotnetRunLines = [...dotnetShardsJob.matchAll(/run-dotnet-test-shard\.mjs[^\n]*/g)].map((m) => m[0]);
  assert.equal(dotnetRunLines.length, 2, "exactly the Kata retry step and the plain step should invoke the shard runner");
  for (const line of dotnetRunLines) {
    assert.match(line, /--shard "\$\{\{ matrix\.id \}\}"/);
    assert.match(line, /\$\{\{ env\.COLLECT_COVERAGE == 'true' && '--collect-coverage' \|\| '' \}\}/);
  }
  assert.doesNotMatch(dotnetShardsJob, /run: dotnet test/, "the shard job must delegate to the Node CLI, not inline dotnet test");

  const nodeJob = workflowSection("  node-toolchain-tests:\n", "\n  web-tests:\n");
  const nodeRunLines = [...nodeJob.matchAll(/validate\.mjs[^\n]*/g)].map((m) => m[0]);
  assert.equal(nodeRunLines.length, 1, "exactly one validate.mjs invocation for node/harness");
  assert.match(nodeRunLines[0], /--area node,harness/);
  assert.match(nodeRunLines[0], /\$\{\{ env\.COLLECT_COVERAGE == 'true' && '--collect-coverage' \|\| '' \}\}/);

  const webJob = workflowSection("  web-tests:\n", "\n  docs-build:\n");
  const webRunLines = [...webJob.matchAll(/validate\.mjs[^\n]*/g)].map((m) => m[0]);
  assert.equal(webRunLines.length, 1, "exactly one validate.mjs invocation for web");
  assert.match(webRunLines[0], /--area web/);
  assert.match(webRunLines[0], /\$\{\{ env\.COLLECT_COVERAGE == 'true' && '--collect-coverage' \|\| '' \}\}/);
});

test("coverage artifacts are only uploaded when collect_coverage is true, with bounded retention", () => {
  const dotnetShardsJob = workflowSection("  dotnet-test-shards:\n", "\n  dotnet-tests:\n");
  assert.match(dotnetShardsJob, /name: Upload \.NET shard coverage report\n\s+if: always\(\) && env\.COLLECT_COVERAGE == 'true'/);
  assert.match(dotnetShardsJob, /name: dotnet-coverage-\$\{\{ matrix\.id \}\}/);

  const nodeJob = workflowSection("  node-toolchain-tests:\n", "\n  web-tests:\n");
  assert.match(nodeJob, /name: Upload Node coverage report\n\s+if: always\(\) && env\.COLLECT_COVERAGE == 'true'/);

  const webJob = workflowSection("  web-tests:\n", "\n  docs-build:\n");
  assert.match(webJob, /name: Upload web coverage report\n\s+if: always\(\) && env\.COLLECT_COVERAGE == 'true'/);

  assert.match(WORKFLOW, /COVERAGE_ARTIFACT_RETENTION_DAYS: 14/);
  const retentionLines = [...WORKFLOW.matchAll(/retention-days: \$\{\{ fromJSON\(env\.COVERAGE_ARTIFACT_RETENTION_DAYS\) \}\}/g)];
  assert.ok(retentionLines.length >= 4, "every coverage artifact upload should use the bounded retention");
});

test("dotnet-coverage-combine and coverage-summary jobs exist, are gated on collect_coverage, and never re-run tests", () => {
  const combineJob = workflowSection("  dotnet-coverage-combine:\n", "\n  coverage-summary:\n");
  assert.match(combineJob, /needs: \[changes, dotnet-test-shards\]/);
  assert.match(combineJob, /if: always\(\) && needs\.changes\.outputs\.collect_coverage == 'true'/);
  assert.match(combineJob, /pattern: dotnet-coverage-\*/);
  assert.match(combineJob, /run: node scripts\/ci\/coverage-combine\.mjs --downloaded-dir downloaded\/dotnet-shards --out TestResults\/coverage/);
  assert.doesNotMatch(combineJob, /dotnet test/, "combine job must only merge already-produced reports, never re-run tests");

  const summaryJob = workflowSection("  coverage-summary:\n", null);
  assert.match(summaryJob, /needs: \[changes, dotnet-coverage-combine, web-tests, node-toolchain-tests\]/);
  assert.match(summaryJob, /if: always\(\) && needs\.changes\.outputs\.collect_coverage == 'true'/);
  assert.match(summaryJob, /node scripts\/ci\/coverage-summary\.mjs/);
  assert.match(summaryJob, /--dotnet-status "\$\{\{ needs\.dotnet-coverage-combine\.result \}\}"/);
  assert.match(summaryJob, /--web-status "\$\{\{ needs\.web-tests\.result \}\}"/);
  assert.match(summaryJob, /--node-status "\$\{\{ needs\.node-toolchain-tests\.result \}\}"/);
  assert.match(summaryJob, /--out "\$GITHUB_STEP_SUMMARY"/);
});

test("bubblewrap setup for the Kata runtime shard is untouched by the coverage changes", () => {
  const dotnetShardsJob = workflowSection("  dotnet-test-shards:\n", "\n  dotnet-tests:\n");
  assert.match(dotnetShardsJob, /Install bubblewrap/);
  assert.match(dotnetShardsJob, /bwrap --unshare-user --unshare-pid/);
  assert.match(dotnetShardsJob, /AGENTWEAVER_REQUIRE_BWRAP: "1"/);
});
