import test from "node:test";
import assert from "node:assert/strict";
import { parseArgs, run } from "../release.mjs";

const log = { info() {} };

test("release accepts only dry-run and resume options", () => {
  assert.deepEqual(parseArgs(["--dry-run"]), {
    resumeTag: undefined,
    dryRun: true,
    help: false,
  });
  assert.throws(() => parseArgs(["patch"]), /Unknown argument/);
});

test("release composes publication followed by deployment", async () => {
  const calls = [];
  const publish = {
    run: async ({ argv }) => {
      calls.push({ command: "publish", argv });
      return { tag: "v1.2.3", version: "1.2.3", commit: "abc" };
    },
  };
  const deployFromRelease = {
    run: async ({ argv, validatedRelease }) => {
      calls.push({ command: "deploy", argv, validatedRelease });
      return { ok: true, tag: "v1.2.3" };
    },
  };

  const result = await run({
    argv: ["--resume", "v1.2.3"],
    log,
    publish,
    deployFromRelease,
  });

  assert.equal(result.ok, true);
  assert.deepEqual(calls.map((call) => call.command), ["publish", "deploy"]);
  assert.deepEqual(calls[0].argv, ["--resume", "v1.2.3"]);
  assert.deepEqual(calls[1].argv, ["v1.2.3"]);
  assert.deepEqual(calls[1].validatedRelease, {
    tag: "v1.2.3",
    version: "1.2.3",
    commit: "abc",
  });
});
