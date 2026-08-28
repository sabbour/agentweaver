import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const WORKFLOW = (await readFile(path.join(HERE, "..", "..", "..", ".github", "workflows", "ci.yml"), "utf8"))
  .replaceAll("\r\n", "\n");

function workflowSection(startMarker, endMarker) {
  const start = WORKFLOW.indexOf(startMarker);
  assert.notEqual(start, -1, `missing workflow marker: ${startMarker.trim()}`);
  const end = WORKFLOW.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(end, -1, `missing workflow marker: ${endMarker.trim()}`);
  return WORKFLOW.slice(start, end);
}

test("UI harness changes select the required Node toolchain job", () => {
  const filter = workflowSection("            node-toolchain:\n", "            docs:\n");
  assert.match(filter, /- 'scripts\/ui-harness\/\*\*'/);
  assert.match(filter, /- 'scripts\/harness-shared\/\*\*'/);

  const job = workflowSection("  node-toolchain-tests:\n", "\n  web-tests:\n");
  assert.match(job, /if: needs\.changes\.outputs\.node-toolchain == 'true'/);
  assert.match(
    job,
    /run: node scripts\/ci\/validate\.mjs --profile ci --area node,harness/,
  );
});

test("path filters escalate on ci.yml only, never on every workflow file", () => {
  const filters = workflowSection("          filters: |\n", "\n  dotnet-tests:\n");

  // A `.github/workflows/**` glob makes an edit to any unrelated workflow
  // (agent-host-maintenance, docs-drift, publish-images, squad-*, ...) trip
  // every group and run the whole matrix. Only ci.yml drives these suites.
  assert.doesNotMatch(
    filters,
    /\.github\/workflows\/\*\*/,
    "path filters must not escalate on every workflow file",
  );

  // Every group must still escalate on ci.yml itself, so a change to the
  // pipeline can never leave a suite silently skipped.
  const groups = ["dotnet", "web", "node-toolchain", "docs", "diagrams"];
  assert.equal(
    filters.match(/- '\.github\/workflows\/ci\.yml'/g)?.length,
    groups.length,
    `each of the ${groups.length} filter groups must include .github/workflows/ci.yml`,
  );
});

test("no echo-only stub jobs remain in the pipeline", () => {
  // `Web lint` was a job whose only step echoed that lint had passed elsewhere.
  // It could never fail, and each run still billed a full minute.
  assert.doesNotMatch(WORKFLOW, /^\s+web-lint:$/m);
  assert.doesNotMatch(WORKFLOW, /name: Web lint/);
});

test(".NET tests run as stable shards with an exact-partition guard", () => {
  const jobs = workflowSection("  dotnet-test-plan:\n", "\n  node-toolchain-tests:\n");

  assert.match(jobs, /node scripts\/ci\/dotnet-test-shards\.mjs matrix/);
  assert.match(jobs, /dotnet-test-shards:/);
  assert.match(jobs, /strategy:\n\s+fail-fast: false\n\s+matrix: \$\{\{ fromJSON\(needs\.dotnet-test-plan\.outputs\.matrix\) \}\}/);
  assert.match(jobs, /node scripts\/ci\/dotnet-test-shards\.mjs verify/);
  assert.match(jobs, /name: \.NET tests/);
  assert.match(jobs, /--logger "trx;LogFileName=\$\{\{ matrix\.id \}\}\.trx"/);
  assert.doesNotMatch(jobs, /Run full \.NET test suite/);
});
