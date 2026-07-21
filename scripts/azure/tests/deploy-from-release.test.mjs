import test from "node:test";
import assert from "node:assert/strict";
import {
  parseArgs,
  validatePublishedRelease,
  run,
} from "../deploy-from-release.mjs";

const mirrors = new Map([
  ["/repo/VERSION", "1.2.3\n"],
  ["/repo/package.json", '{"version":"1.2.3"}'],
  ["/repo/package-lock.json", '{"packages":{"":{"version":"1.2.3"}}}'],
  ["/repo/CHANGELOG.md", "## 1.2.3\n\n- Release note\n"],
]);
const readFile = (file) => mirrors.get(file.replaceAll("\\", "/"));
const log = {
  info() {}, section() {}, field() {}, ok() {}, skip() {}, warn() {},
  error() {}, debug() {}, command() {},
};

function fakeExec({ head = "abc", tagCommit = "abc", annotated = true, release = true } = {}) {
  const calls = [];
  return {
    calls,
    setDryRun() {},
    async run(cmd, args) {
      calls.push({ cmd, args });
      return { code: 0, stdout: "" };
    },
    async capture(cmd, args) {
      calls.push({ cmd, args });
      if (cmd === "gh") return { code: release ? 0 : 1, stdout: "" };
      if (args[0] === "diff") return { code: 0, stdout: "" };
      if (args[0] === "fetch") return { code: 0, stdout: "" };
      if (args[0] === "cat-file") return { code: annotated ? 0 : 1, stdout: annotated ? "tag" : "" };
      if (args[0] === "rev-parse" && args[1] === "HEAD") return { code: 0, stdout: head };
      if (args[0] === "rev-parse") return { code: 0, stdout: tagCommit };
      if (args[0] === "tag") return { code: 0, stdout: "v1.2.3\nv1.2.2\n" };
      return { code: 0, stdout: "" };
    },
  };
}

test("deploy-from-release requires one vX.Y.Z tag", () => {
  assert.deepEqual(parseArgs(["v1.2.3"]), {
    tag: "v1.2.3",
    dryRun: false,
    help: false,
  });
  assert.throws(() => parseArgs([]), /Usage/);
  assert.throws(() => parseArgs(["1.2.3"]), /Usage/);
});

test("published release validation requires an annotated tag", async () => {
  await assert.rejects(
    validatePublishedRelease({
      tag: "v1.2.3",
      repoRoot: "/repo",
      exec: fakeExec({ annotated: false }),
      readFile,
    }),
    /annotated release tag/,
  );
});

test("published release validation requires exact tag checkout", async () => {
  await assert.rejects(
    validatePublishedRelease({
      tag: "v1.2.3",
      repoRoot: "/repo",
      exec: fakeExec({ head: "def" }),
      readFile,
    }),
    /exact v1.2.3 commit/,
  );
});

test("published release validation requires a GitHub Release", async () => {
  await assert.rejects(
    validatePublishedRelease({
      tag: "v1.2.3",
      repoRoot: "/repo",
      exec: fakeExec({ release: false }),
      readFile,
    }),
    /no matching published GitHub Release/,
  );
});

test("deploy-from-release builds, deploys, verifies provenance, waits, then verifies health", async () => {
  const order = [];
  const steps = {
    buildImages: { run: async () => { order.push("build"); return {}; } },
    deployStep: { run: async () => { order.push("deploy"); return {}; } },
    verifyProvenance: {
      run: async () => {
        order.push("provenance");
        return { results: [{ status: "ok" }] };
      },
    },
    verifyStep: {
      run: async () => {
        order.push("health");
        return { ok: true, pass: 1, fail: 0 };
      },
    },
  };
  const exec = fakeExec();
  exec.capture = async (cmd, args) => {
    if (cmd === "git" && args[0] === "tag") {
      return { code: 0, stdout: "v1.2.3\nv1.2.2\n" };
    }
    if (cmd === "kubectl") {
      order.push("warm-pool");
      return { code: 1, stdout: "", json: null };
    }
    return { code: 0, stdout: "" };
  };

  const result = await run({
    argv: ["v1.2.3"],
    repoRoot: "/repo",
    exec,
    log,
    readFile,
    validatedRelease: { tag: "v1.2.3", version: "1.2.3", commit: "abc" },
    resolveVariables: async ({ env }) => ({
      IMAGE_TAG: env.IMAGE_TAG,
      AGENTHOST_IMAGE_TAG: env.AGENTHOST_IMAGE_TAG,
      ACR_NAME: "acr",
      ACR_LOGIN_SERVER: "acr.azurecr.io",
      NAMESPACE: "agentweaver",
    }),
    steps,
  });

  assert.equal(result.ok, true);
  assert.deepEqual(order, ["build", "deploy", "provenance", "warm-pool", "health"]);
});
