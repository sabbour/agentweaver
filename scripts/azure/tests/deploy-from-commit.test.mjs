import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  CommitResolutionError,
  parseArgs,
  resolveCommitRef,
  run,
} from "../deploy-from-commit.mjs";

const TEST_REPO_ROOT = path.resolve("test-fixtures", "aw-deploy-from-commit-test-repo");
const TEST_CALLER_WORKTREE = path.join(TEST_REPO_ROOT, "caller-worktree");
const TEST_GIT_COMMON_DIR = path.join(TEST_REPO_ROOT, ".git");

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
      argv: ["origin/feature"],
      repoRoot: TEST_CALLER_WORKTREE,
      env: { REPO_APP_PRIVATE_KEY_FILE: sourceFile },
      exec: { capture: blocked("git/azure capture"), run: blocked("git/azure run") },
      git: { revParseCommit: blocked("git resolution") },
      log,
      resolveVariables: blocked("variable resolution"),
      deployCommittedSha: blocked("build/deploy"),
    }),
    (error) => {
      assert.match(error.message, expected);
      assert.doesNotMatch(error.message, /SENSITIVE|BEGIN .* PRIVATE KEY/);
      return true;
    },
  );
  assert.deepEqual(calls, []);
}

test("parseArgs requires exactly one SHA or ref", () => {
  assert.deepEqual(parseArgs(["origin/feature"]), {
    ref: "origin/feature",
    help: false,
  });
  assert.throws(() => parseArgs([]), /Usage/);
  assert.throws(() => parseArgs(["one", "two"]), /Unknown argument/);
  assert.throws(() => parseArgs(["origin/feature", "--allow-dirty"]), /Unknown argument/);
});

test("resolveCommitRef resolves after the general origin fetch", async () => {
  const calls = [];
  const exec = {
    capture: async (cmd, args) => {
      calls.push([cmd, args]);
      return { code: 0, stdout: "" };
    },
  };
  const git = {
    revParseCommit: async (ref) => ref === "origin/feature" ? "a".repeat(40) : null,
  };

  const commit = await resolveCommitRef("origin/feature", {
    repoRoot: TEST_REPO_ROOT,
    exec,
    git,
  });
  assert.equal(commit, "a".repeat(40));
  assert.deepEqual(calls[0][1], ["fetch", "origin", "--prune", "--tags"]);
});

test("resolveCommitRef fetches a specific unresolved ref and uses FETCH_HEAD", async () => {
  const captures = [];
  const exec = {
    capture: async (cmd, args) => {
      captures.push([cmd, args]);
      return { code: 0, stdout: "" };
    },
  };
  const git = {
    revParseCommit: async (ref) => ref === "FETCH_HEAD" ? "b".repeat(40) : null,
  };

  const commit = await resolveCommitRef("pull/123/head", {
    repoRoot: TEST_REPO_ROOT,
    exec,
    git,
  });
  assert.equal(commit, "b".repeat(40));
  assert.ok(captures.some(([, args]) => args.join(" ") === "fetch origin pull/123/head"));
});

test("resolveCommitRef refuses an unresolvable ref", async () => {
  const exec = {
    capture: async () => ({ code: 1, stdout: "", stderr: "missing" }),
  };
  const git = { revParseCommit: async () => null };
  await assert.rejects(
    resolveCommitRef("missing", { repoRoot: TEST_REPO_ROOT, exec, git }),
    CommitResolutionError,
  );
});

test("run deploys from a detached temporary worktree and removes it", async () => {
  const calls = [];
  const commit = "c".repeat(40);
  const exec = {
    run: async (cmd, args, opts) => {
      calls.push({ type: "run", cmd, args, opts });
      return { code: 0 };
    },
    capture: async (cmd, args, opts) => {
      calls.push({ type: "capture", cmd, args, opts });
      if (args[0] === "rev-parse" && args[1] === "--git-common-dir") {
        return { code: 0, stdout: TEST_GIT_COMMON_DIR };
      }
      return { code: 0, stdout: "" };
    },
  };
  const git = {
    revParseCommit: async (ref) => ref === "origin/feature" ? commit : null,
  };
  const mkdirCalls = [];
  let deployed;
  const result = await run({
    argv: ["origin/feature"],
    repoRoot: TEST_CALLER_WORKTREE,
    exec,
    git,
    log,
    fsImpl: { mkdirSync: (...args) => mkdirCalls.push(args) },
    resolveVariables: async ({ env, repoRoot }) => ({
      IMAGE_TAG: env.IMAGE_TAG,
      AGENTHOST_IMAGE_TAG: env.AGENTHOST_IMAGE_TAG,
      repoRoot,
    }),
    deployCommittedSha: async (cfg, opts) => {
      deployed = { cfg, opts };
      return { imageTag: opts.imageTag };
    },
  });

  const expectedWorktree = path.join(
    TEST_REPO_ROOT,
    ".worktrees",
    `deploy-from-commit-ccccccc-${process.pid}`,
  );
  assert.equal(deployed.opts.imageTag, "ccccccc");
  assert.equal(deployed.opts.verifyGitRef, commit);
  assert.equal(deployed.opts.cwd, expectedWorktree);
  assert.equal(deployed.cfg.repoRoot, expectedWorktree);
  assert.equal(result.worktreeRemoved, true);
  assert.equal(mkdirCalls.length, 1);
  assert.ok(calls.some((call) =>
    call.type === "run" &&
    call.cmd === "git" &&
    call.args.join(" ") === `worktree add --detach ${expectedWorktree} ${commit}`));
  assert.ok(calls.some((call) =>
    call.type === "capture" &&
    call.cmd === "git" &&
    call.args.join(" ") === `worktree remove --force ${expectedWorktree}`));
  assert.ok(!calls.some((call) => call.args.includes("checkout") || call.args.includes("switch")));
  assert.ok(!calls.some((call) => call.args[0] === "status"));
});

test("run removes the temporary worktree when deployment fails", async () => {
  const commit = "d".repeat(40);
  const calls = [];
  const exec = {
    run: async () => ({ code: 0 }),
    capture: async (cmd, args) => {
      calls.push([cmd, args]);
      if (args[0] === "rev-parse" && args[1] === "--git-common-dir") {
        return { code: 0, stdout: TEST_GIT_COMMON_DIR };
      }
      return { code: 0, stdout: "" };
    },
  };
  const git = { revParseCommit: async () => commit };

  await assert.rejects(
    run({
      argv: ["deadbeef"],
      repoRoot: TEST_REPO_ROOT,
      exec,
      git,
      log,
      fsImpl: { mkdirSync() {} },
      resolveVariables: async () => ({}),
      deployCommittedSha: async () => {
        throw new Error("deployment failed");
      },
    }),
    /deployment failed/,
  );
  assert.ok(calls.some(([, args]) => args[0] === "worktree" && args[1] === "remove"));
});

test("run rejects invalid, unreadable, and final reparse Repo App key inputs before collaborators", async (t) => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "deploy-commit-repo-app-key-"));
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
