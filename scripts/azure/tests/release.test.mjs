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
    acceptanceBundlePath: undefined,
  });
  assert.deepEqual(parseArgs([
    "--resume", "v1.2.3",
    "--feature-manifest", "feature.json",
    "--acceptance-bundle", "bundle.json",
  ]), {
    resumeTag: "v1.2.3",
    dryRun: false,
    help: false,
    featureManifestPath: "feature.json",
    acceptanceBundlePath: "bundle.json",
  });
  assert.throws(() => parseArgs(["patch"]), /Unknown argument/);
});

test("release composes publication followed by deployment", async () => {
  const calls = [];
  const publish = {
    validatePreparedRelease: async () => ({
      tag: "v1.2.3",
      version: "1.2.3",
      commit: "abc",
      changelog: "notes",
    }),
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
    argv: [
      "--resume", "v1.2.3",
      "--feature-manifest", "feature.json",
      "--acceptance-bundle", "bundle.json",
    ],
    log,
    publish,
    deployFromRelease,
    resolveVariables: async () => ({
      SUBSCRIPTION_ID: "sub",
      RESOURCE_GROUP: "rg",
      CLUSTER_NAME: "cluster",
      NAMESPACE: "namespace",
    }),
    acceptance: {
      runReleaseDeclarationGate: ({ featureManifestPath, expectedDeployment }) => {
        calls.push({ command: "validate", featureManifestPath, expectedDeployment });
        return { ok: true };
      },
    },
  });

  assert.equal(result.ok, true);
  assert.deepEqual(calls.map((call) => call.command), ["validate", "publish", "deploy"]);
  assert.equal(calls[0].featureManifestPath, "feature.json");
  assert.deepEqual(calls[0].expectedDeployment, {
    version: "1.2.3",
    deployedRevision: "abc",
    deploymentIdentity: "azure:sub/rg/cluster/namespace",
  });
  assert.deepEqual(calls[1].argv, ["--resume", "v1.2.3"]);
  assert.deepEqual(calls[2].validatedRelease, {
    tag: "v1.2.3",
    version: "1.2.3",
    commit: "abc",
  });
  assert.deepEqual(calls[2].argv, [
    "v1.2.3",
    "--feature-manifest", "feature.json",
    "--acceptance-bundle", "bundle.json",
  ]);
});

test("release requires the pre-deploy feature declaration before publication", async () => {
  await assert.rejects(
    run({
      argv: [],
      log,
      publish: { run: async () => assert.fail("must not publish without declaration") },
      deployFromRelease: { run: async () => assert.fail("must not deploy without declaration") },
    }),
    /requires --feature-manifest/,
  );
});

test("release validates the feature declaration before publication", async () => {
  await assert.rejects(
    run({
      argv: ["--feature-manifest", "feature.json"],
      log,
      acceptance: {
        runReleaseDeclarationGate: () => {
          throw new Error("invalid release feature declaration");
        },
      },
      publish: {
        validatePreparedRelease: async () => ({
          tag: "v1.2.3",
          version: "1.2.3",
          commit: "abc",
          changelog: "notes",
        }),
        run: async () => assert.fail("must not publish an invalid declaration"),
      },
      resolveVariables: async () => ({
        SUBSCRIPTION_ID: "sub",
        RESOURCE_GROUP: "rg",
        CLUSTER_NAME: "cluster",
        NAMESPACE: "namespace",
      }),
      deployFromRelease: { run: async () => assert.fail("must not deploy an invalid declaration") },
    }),
    /invalid release feature declaration/,
  );
});
