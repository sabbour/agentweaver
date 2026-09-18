import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const WORKFLOW = (await readFile(path.join(HERE, "..", "..", "..", ".github", "workflows", "publish-images.yml"), "utf8"))
  .replaceAll("\r\n", "\n");

function workflowSection(startMarker, endMarker) {
  const start = WORKFLOW.indexOf(startMarker);
  assert.notEqual(start, -1, `missing workflow marker: ${startMarker.trim()}`);
  const end = WORKFLOW.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(end, -1, `missing workflow marker: ${endMarker.trim()}`);
  return WORKFLOW.slice(start, end);
}

test("pull requests never trigger image builds", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");

  assert.doesNotMatch(trigger, /pull_request:/);
  assert.match(trigger, /workflow_dispatch:/);
});

test("tag pushes publish release image tags before the GitHub Release exists", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");

  assert.match(trigger, /tags:\n\s+- 'v\*'/);
  assert.doesNotMatch(trigger, /\n\s+release:/);
});

test("tag pushes rebuild with semver identity instead of retagging a sha image", () => {
  const build = workflowSection("  build:\n", "\n          labels: |\n");

  assert.match(build, /startsWith\(github\.ref, 'refs\/tags\/v'\)/);
  assert.match(build, /IMAGE_TAG=\$\{\{ needs\.plan\.outputs\.primary_tag \}\}/);
  assert.doesNotMatch(build, /imagetools/);
  assert.doesNotMatch(build, /steps\.existing/);
});

test("manual dry runs can suppress image publication", () => {
  const build = workflowSection("  build:\n", "\n          labels: |\n");

  assert.match(
    build,
    /push: \$\{\{ github\.event_name != 'workflow_dispatch' \|\| inputs\.push \}\}/,
  );
  assert.match(
    build,
    /if: github\.event_name != 'workflow_dispatch' \|\| inputs\.push/,
  );
});

test("push image filters catch Dockerfile and project-reference drift", () => {
  const changes = workflowSection("  changes:\n", "\n  build:\n");

  assert.match(
    changes,
    /if: github\.event_name == 'push' && !startsWith\(github\.ref, 'refs\/tags\/'\)/,
  );
  assert.equal(changes.match(/- '\*\*\/Dockerfile'/g)?.length, 4);
  assert.equal(changes.match(/- '\.github\/workflows\/publish-images\.yml'/g)?.length, 4);
  assert.equal(changes.match(/- 'scripts\/azure\/image-spec\.mjs'/g)?.length, 4);
  assert.equal(changes.match(/- 'scripts\/ci\/ghcr-plan\.mjs'/g)?.length, 4);
  assert.match(changes, /agentweaver-mcp:[\s\S]*- 'packages\/\*\*'[\s\S]*- 'apps\/Agentweaver\.Mcp\/\*\*'/);
});
