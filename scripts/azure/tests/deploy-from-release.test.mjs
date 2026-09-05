import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  parseArgs,
  validatePublishedRelease,
  run,
} from "../deploy-from-release.mjs";

const mirrors = new Map([
  ["/repo/VERSION", "1.2.3\n"],
  ["/repo/package.json", '{"version":"1.2.3"}'],
  ["/repo/package-lock.json", '{"version":"1.2.3","packages":{"":{"version":"1.2.3"}}}'],
  ["/repo/CHANGELOG.md", "## 1.2.3\n\n- Release note\n"],
]);
const readFile = (file) => mirrors.get(file.replaceAll("\\", "/"));
const log = {
  info() {}, section() {}, field() {}, ok() {}, skip() {}, warn() {},
  error() {}, debug() {}, command() {},
};

function makeFinalReparsePath(scratchRoot) {
  const targetFile = path.join(scratchRoot, "target.pem");
  const sourceFile = path.join(scratchRoot, "source.pem");
  fs.writeFileSync(targetFile, "-----BEGIN PRIVATE KEY-----\nSENSITIVE\n-----END PRIVATE KEY-----");
  try {
    fs.symlinkSync(targetFile, sourceFile, "file");
  } catch {
    const targetDir = path.join(scratchRoot, "target-dir");
    fs.mkdirSync(targetDir);
    fs.symlinkSync(targetDir, sourceFile, "junction");
  }
  return sourceFile;
}

async function assertRepoAppKeyRejectedBeforeCollaborators(sourceFile, expected) {
  const calls = [];
  const blocked = (name) => async () => {
    calls.push(name);
    throw new Error(`${name} must not be called`);
  };

  await assert.rejects(
    run({
      argv: ["v1.2.3"],
      repoRoot: "/repo",
      env: { REPO_APP_PRIVATE_KEY_FILE: sourceFile },
      exec: {
        capture: blocked("git/azure capture"),
        run: blocked("git/azure run"),
        setDryRun: () => calls.push("dry-run"),
      },
      git: { revParseCommit: blocked("git resolution") },
      kubectl: new Proxy({}, { get: (_target, property) => blocked(`kubectl.${String(property)}`) }),
      log,
      readFile: blocked("release file read"),
      resolveVariables: blocked("variable resolution"),
      resolveGitHubRepository: blocked("GitHub repository resolution"),
      steps: {
        buildImages: { run: blocked("build") },
        deployStep: { run: blocked("deploy") },
        verifyProvenance: { run: blocked("provenance") },
        verifyStep: { run: blocked("verification") },
      },
    }),
    (error) => {
      assert.match(error.message, expected);
      assert.doesNotMatch(error.message, /SENSITIVE|BEGIN .* PRIVATE KEY/);
      return true;
    },
  );
  assert.deepEqual(calls, []);
}

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
    imageSource: "ghcr",
    ghcrToken: undefined,
  });
  assert.throws(() => parseArgs([]), /Usage/);
  assert.throws(() => parseArgs(["1.2.3"]), /Usage/);
});

test("deploy-from-release accepts --image-source ghcr and --ghcr-token", () => {
  assert.deepEqual(parseArgs(["v1.2.3", "--image-source", "ghcr", "--ghcr-token", "tok"]), {
    tag: "v1.2.3",
    dryRun: false,
    help: false,
    imageSource: "ghcr",
    ghcrToken: "tok",
  });
  assert.deepEqual(parseArgs(["v1.2.3", "--image-source=ghcr"]).imageSource, "ghcr");
  assert.throws(() => parseArgs(["v1.2.3", "--image-source", "bogus"]), /--image-source must be one of/);
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
    argv: ["v1.2.3", "--image-source", "acr-build"],
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

test("deploy-from-release --image-source ghcr wires GHCR_REF/OWNER/REPOSITORY/TOKEN into cfg", async () => {
  let capturedCfg;
  const steps = {
    buildImages: { run: async (cfg) => { capturedCfg = cfg; return {}; } },
    deployStep: { run: async () => ({}) },
    verifyProvenance: { run: async () => ({ results: [{ status: "ok" }] }) },
    verifyStep: { run: async () => ({ ok: true, pass: 1, fail: 0 }) },
  };
  const exec = fakeExec();
  exec.capture = async (cmd, args) => {
    if (cmd === "git" && args[0] === "tag") {
      return { code: 0, stdout: "v1.2.3\nv1.2.2\n" };
    }
    if (cmd === "kubectl") {
      return { code: 1, stdout: "", json: null };
    }
    return { code: 0, stdout: "" };
  };

  const result = await run({
    argv: ["v1.2.3", "--image-source", "ghcr", "--ghcr-token", "tok"],
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
    resolveGitHubRepository: async () => ({ owner: "sabbour", repo: "agentweaver" }),
    steps,
  });

  assert.equal(result.ok, true);
  assert.equal(capturedCfg.IMAGE_SOURCE, "ghcr");
  assert.equal(capturedCfg.GHCR_REF, "v1.2.3");
  assert.equal(capturedCfg.GHCR_OWNER, "sabbour");
  assert.equal(capturedCfg.GHCR_REPOSITORY, "agentweaver");
  assert.equal(capturedCfg.GHCR_TOKEN, "tok");
});

test("deploy-from-release --image-source ghcr fails closed without a GitHub origin remote", async () => {
  const exec = fakeExec();
  exec.capture = async (cmd, args) => {
    if (cmd === "git" && args[0] === "tag") {
      return { code: 0, stdout: "v1.2.3\nv1.2.2\n" };
    }
    return { code: 0, stdout: "" };
  };

  await assert.rejects(
    run({
      argv: ["v1.2.3", "--image-source", "ghcr"],
      repoRoot: "/repo",
      exec,
      log,
      readFile,
      validatedRelease: { tag: "v1.2.3", version: "1.2.3", commit: "abc" },
      resolveVariables: async ({ env }) => ({
        IMAGE_TAG: env.IMAGE_TAG,
        AGENTHOST_IMAGE_TAG: env.AGENTHOST_IMAGE_TAG,
        ACR_NAME: "acr",
        NAMESPACE: "agentweaver",
      }),
      resolveGitHubRepository: async () => null,
      steps: {
        buildImages: { run: async () => ({}) },
      },
    }),
    /GitHub origin remote/,
  );
});

test("run rejects invalid, unreadable, and final reparse Repo App key inputs before collaborators", async (t) => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "deploy-release-repo-app-key-"));
  try {
    await t.test("invalid PEM", async () => {
      const sourceFile = path.join(scratchRoot, "invalid.pem");
      fs.writeFileSync(sourceFile, "SENSITIVE-PRIVATE-KEY-MATERIAL");
      await assertRepoAppKeyRejectedBeforeCollaborators(
        sourceFile,
        /exactly one unencrypted.*private key pem block/i,
      );
    });

    await t.test("unreadable path", async () => {
      await assertRepoAppKeyRejectedBeforeCollaborators(
        path.join(scratchRoot, "missing.pem"),
        /could not be read/i,
      );
    });

    await t.test("final-component reparse point", async () => {
      await assertRepoAppKeyRejectedBeforeCollaborators(
        makeFinalReparsePath(scratchRoot),
        /must not be a symbolic link, junction, or reparse-point path/i,
      );
    });
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});
