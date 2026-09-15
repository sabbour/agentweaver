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

test("pull requests run image dry builds only for relevant paths", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");

  assert.match(trigger, /pull_request:\n\s+paths:/);
  assert.match(trigger, /- 'apps\/web\/\*\*'/);
  assert.match(trigger, /- 'apps\/Agentweaver\.\*\/\*\*'/);
  assert.match(trigger, /- 'packages\/\*\*'/);
  assert.match(trigger, /- 'Directory\.Build\.props'/);
  assert.match(trigger, /- '\*\*\/Dockerfile'/);
});

test("tag pushes publish release image tags before the GitHub Release exists", () => {
  const trigger = workflowSection("on:\n", "\npermissions:\n");

  assert.match(trigger, /tags:\n\s+- 'v\*'/);
  assert.doesNotMatch(trigger, /\n\s+release:/);
});

test("pull requests build but never push images", () => {
  const build = workflowSection("  build:\n", "\n          labels: |\n");

  assert.match(build, /github\.event_name == 'pull_request'/);
  assert.match(
    build,
    /push: \$\{\{ github\.event_name != 'pull_request' && \(github\.event_name != 'workflow_dispatch' \|\| inputs\.push\) \}\}/,
  );
  assert.match(
    build,
    /if: github\.event_name != 'pull_request' && \(github\.event_name != 'workflow_dispatch' \|\| inputs\.push\)/,
  );
});

test("pull request image filters catch Dockerfile and project-reference drift", () => {
  const changes = workflowSection("  changes:\n", "\n  build:\n");

  assert.match(
    changes,
    /if: \(github\.event_name == 'push' && !startsWith\(github\.ref, 'refs\/tags\/'\)\) \|\| github\.event_name == 'pull_request'/,
  );
  assert.equal(changes.match(/- '\*\*\/Dockerfile'/g)?.length, 4);
  assert.equal(changes.match(/- '\.github\/workflows\/publish-images\.yml'/g)?.length, 4);
  assert.equal(changes.match(/- 'scripts\/azure\/image-spec\.mjs'/g)?.length, 4);
  assert.equal(changes.match(/- 'scripts\/ci\/ghcr-plan\.mjs'/g)?.length, 4);
  assert.match(changes, /agentweaver-mcp:[\s\S]*- 'packages\/\*\*'[\s\S]*- 'apps\/Agentweaver\.Mcp\/\*\*'/);
});
