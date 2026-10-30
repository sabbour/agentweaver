import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";

const hook = fileURLToPath(new URL("../squad-coordinator-reminder.mjs", import.meta.url));

function runHook(input) {
  const result = spawnSync(process.execPath, [hook], {
    encoding: "utf8",
    input: JSON.stringify(input),
  });

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, "");
  return JSON.parse(result.stdout);
}

test("adds the reminder to a root session start", () => {
  const output = runHook({
    sessionId: "root-session",
    timestamp: 0,
    cwd: ".",
    source: "startup",
    initialPrompt: "Help with Squad",
  });

  assert.match(output.additionalContext, /^Squad reminder:/);
});

test("also reminds resumed root sessions without an initial prompt", () => {
  const output = runHook({
    sessionId: "resumed-session",
    timestamp: 0,
    cwd: ".",
    source: "resume",
  });

  assert.match(output.additionalContext, /^Squad reminder:/);
});

test("does not promote a subagent to coordinator", () => {
  assert.deepEqual(
    runHook({
      sessionId: "subagent-session",
      timestamp: 0,
      cwd: ".",
      source: "startup",
      initialPrompt: "Work as a specialist",
      agentId: "specialist-1",
    }),
    {},
  );
});

test("leaves malformed or unrelated hook input unchanged", () => {
  const result = spawnSync(process.execPath, [hook], {
    encoding: "utf8",
    input: "{",
  });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {});

  assert.deepEqual(runHook({ cwd: ".", source: "startup" }), {});
});
