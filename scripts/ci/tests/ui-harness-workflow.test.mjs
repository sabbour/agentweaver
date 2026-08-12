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
