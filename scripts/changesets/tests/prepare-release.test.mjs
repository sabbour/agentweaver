import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import {
  ensureReleaseBranchHasMainAncestry,
  releaseMainMergeCommand,
} from "../shared.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
let scratchIndex = 0;

function scratchRepo(t, name) {
  const root = path.join(here, `.scratch-${process.pid}-${scratchIndex++}-${name}`);
  fs.rmSync(root, { recursive: true, force: true });
  fs.mkdirSync(root, { recursive: true });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}

function git(repo, ...args) {
  return execFileSync("git", args, {
    cwd: repo,
    encoding: "utf8",
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: "Test User",
      GIT_AUTHOR_EMAIL: "test@example.com",
      GIT_COMMITTER_NAME: "Test User",
      GIT_COMMITTER_EMAIL: "test@example.com",
    },
  }).trim();
}

function writeFile(repo, file, content) {
  const fullPath = path.join(repo, file);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content);
}

function commitFile(repo, file, content, message) {
  writeFile(repo, file, content);
  git(repo, "add", file);
  git(repo, "commit", "-q", "-m", message);
}

function initRepo(t, name) {
  const root = scratchRepo(t, name);
  const remote = path.join(root, "origin.git");
  const repo = path.join(root, "repo");
  execFileSync("git", ["init", "--bare", "-q", remote], { encoding: "utf8" });
  execFileSync("git", ["init", "-q", "-b", "main", repo], { encoding: "utf8" });
  git(repo, "remote", "add", "origin", remote);
  git(repo, "config", "user.name", "Test User");
  git(repo, "config", "user.email", "test@example.com");
  git(repo, "config", "core.autocrlf", "false");
  return repo;
}

function pushBranches(repo, ...branches) {
  git(repo, "push", "-q", "origin", ...branches);
}

function createReleaseRepo(t, name, { mainAncestor = false } = {}) {
  const repo = initRepo(t, name);
  commitFile(repo, "README.md", "base\n", "base");

  if (mainAncestor) {
    git(repo, "checkout", "-q", "-b", "release/v0.1.0");
    commitFile(repo, "release.txt", "release\n", "release work");
    pushBranches(repo, "main", "release/v0.1.0");
    return repo;
  }

  git(repo, "checkout", "-q", "-b", "dev");
  commitFile(repo, "dev.txt", "dev\n", "dev work");
  git(repo, "checkout", "-q", "main");
  commitFile(repo, "main.txt", "main\n", "main work");
  git(repo, "checkout", "-q", "-b", "release/v0.1.0", "dev");
  commitFile(repo, "release.txt", "release\n", "release work");
  pushBranches(repo, "main", "release/v0.1.0");
  return repo;
}

test("release ancestry succeeds without a merge when origin/main is already an ancestor", (t) => {
  const repo = createReleaseRepo(t, "ancestor-present", { mainAncestor: true });
  const before = git(repo, "rev-parse", "HEAD");
  const messages = [];

  ensureReleaseBranchHasMainAncestry(repo, "release/v0.1.0", {
    log: (message) => messages.push(message),
  });

  assert.equal(git(repo, "rev-parse", "HEAD"), before);
  assert.deepEqual(messages, ["origin/main is already an ancestor of release/v0.1.0."]);
});

test("release ancestry creates a merge when origin/main is missing", (t) => {
  const repo = createReleaseRepo(t, "ancestor-missing");
  const messages = [];

  ensureReleaseBranchHasMainAncestry(repo, "release/v0.1.0", {
    log: (message) => messages.push(message),
  });

  assert.doesNotThrow(() => git(repo, "merge-base", "--is-ancestor", "origin/main", "HEAD"));
  assert.equal(git(repo, "log", "-1", "--pretty=%s"), "merge: resolve main into release/v0.1.0");
  assert.equal(git(repo, "rev-list", "--parents", "-n", "1", "HEAD").split(/\s+/).length, 3);
  assert.match(messages.join("\n"), /Created ancestry merge [0-9a-f]+/);
});

test("release ancestry opt-out fails with the exact merge command", (t) => {
  const repo = createReleaseRepo(t, "ancestor-opt-out");
  const before = git(repo, "rev-parse", "HEAD");

  assert.throws(
    () => ensureReleaseBranchHasMainAncestry(repo, "release/v0.1.0", { allowMerge: false }),
    new RegExp(releaseMainMergeCommand("release/v0.1.0").replace(/[.*+?^${}()|[\]\\]/g, "\\$&")),
  );
  assert.equal(git(repo, "rev-parse", "HEAD"), before);
});

test("release ancestry stops when -X ours cannot resolve a conflict", (t) => {
  const repo = initRepo(t, "ancestor-conflict");
  commitFile(repo, "file.txt", "base\n", "base");
  git(repo, "checkout", "-q", "-b", "dev");
  commitFile(repo, "dev.txt", "dev\n", "dev work");
  git(repo, "checkout", "-q", "main");
  git(repo, "mv", "file.txt", "main.txt");
  git(repo, "commit", "-q", "-m", "main rename");
  git(repo, "checkout", "-q", "-b", "release/v0.1.0", "dev");
  git(repo, "mv", "file.txt", "release.txt");
  git(repo, "commit", "-q", "-m", "release rename");
  pushBranches(repo, "main", "release/v0.1.0");

  assert.throws(
    () => ensureReleaseBranchHasMainAncestry(repo, "release/v0.1.0", { log: () => {} }),
    /Git stopped during: git merge -X ours origin\/main --no-ff -m "merge: resolve main into release\/v0\.1\.0"/,
  );
  assert.match(git(repo, "status", "--short"), /(UA|AU|DD)/);
});
