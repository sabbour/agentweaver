import test from "node:test";
import assert from "node:assert/strict";
import { parseArgs, run } from "../release.mjs";

const log = { info() {} };

test("release accepts dry-run, resume, and acceptance manifest options", () => {
  assert.deepEqual(parseArgs(["--dry-run"]), {
    resumeTag: undefined,
    dryRun: true,
    help: false,
    featureManifestPath: undefined,
    resultPaths: [],
  });
  assert.deepEqual(parseArgs([
    "--resume", "v1.2.3",
    "--feature-manifest", "feature.json",
    "--result", "representative.json",
    "--result", "focused.json",
  ]), {
    resumeTag: "v1.2.3",
    dryRun: false,
    help: false,
    featureManifestPath: "feature.json",
    resultPaths: ["representative.json", "focused.json"],
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
  const acceptance = {
    runReleaseAcceptanceGate(args) {
      calls.push({ command: "acceptance", args });
      return { ok: true };
    },
  };

  const result = await run({
    argv: [
      "--resume", "v1.2.3",
      "--feature-manifest", "feature.json",
      "--result", "representative.json",
    ],
    log,
    publish,
    deployFromRelease,
    acceptance,
  });

  assert.equal(result.ok, true);
  assert.deepEqual(calls.map((call) => call.command), ["publish", "deploy", "acceptance"]);
  assert.deepEqual(calls[0].argv, ["--resume", "v1.2.3"]);
  assert.deepEqual(calls[1].argv, ["v1.2.3"]);
  assert.deepEqual(calls[1].validatedRelease, {
    tag: "v1.2.3",
    version: "1.2.3",
    commit: "abc",
  });
  assert.deepEqual(calls[2].args, {
    featureManifestPath: "feature.json",
    resultPaths: ["representative.json"],
  });
});

test("release deploys but fails closed until post-deployment acceptance evidence is supplied", async () => {
  const calls = [];
  await assert.rejects(
    run({
      argv: [],
      log,
      publish: {
        run: async () => {
          calls.push("publish");
          return { tag: "v1.2.3", version: "1.2.3", commit: "abc" };
        },
      },
      deployFromRelease: {
        run: async () => {
          calls.push("deploy");
          return { ok: true };
        },
      },
    }),
    /Deployment completed, but release acceptance remains blocked/,
  );
  assert.deepEqual(calls, ["publish", "deploy"]);
});
