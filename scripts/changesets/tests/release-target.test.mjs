import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { applyReleasePlan } from "@changesets/apply-release-plan";
import { applyExplicitReleaseTarget } from "../shared.mjs";
import { createNativeReleasePlan } from "../native-release-plan.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, "../../..");
const prepareScript = path.resolve(here, "../prepare-release.mjs");
const planScript = path.resolve(here, "../plan-release.mjs");
let scratchIndex = 0;

function scratchRoot(t, name) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `agentweaver-release-${process.pid}-${scratchIndex++}-${name}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}

function git(repo, ...args) {
  return execFileSync("git", args, {
    cwd: repo,
    encoding: "utf8",
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: "Release Test",
      GIT_AUTHOR_EMAIL: "release-test@example.invalid",
      GIT_COMMITTER_NAME: "Release Test",
      GIT_COMMITTER_EMAIL: "release-test@example.invalid",
    },
  }).trim();
}

function writeFile(repo, file, content) {
  const fullPath = path.join(repo, file);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content);
}

function createUnrelatedCommit(repo, message) {
  const tree = execFileSync("git", ["mktree"], {
    cwd: repo,
    encoding: "utf8",
    input: "",
  }).trim();
  return execFileSync("git", ["commit-tree", tree, "-m", message], {
    cwd: repo,
    encoding: "utf8",
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: "Release Test",
      GIT_AUTHOR_EMAIL: "release-test@example.invalid",
      GIT_COMMITTER_NAME: "Release Test",
      GIT_COMMITTER_EMAIL: "release-test@example.invalid",
    },
  }).trim();
}

function createReleaseRepo(t, name, {
  currentVersion = "0.34.0",
  publishedVersion = "0.34.0",
  bump = "patch",
  mainChangeset,
  baseBranch = "main",
  branch = "release/v0.34.2",
} = {}) {
  const root = scratchRoot(t, name);
  const repo = path.join(root, "repo");
  const remote = path.join(root, "origin.git");
  fs.mkdirSync(repo, { recursive: true });
  execFileSync("git", ["init", "-q", "-b", baseBranch, repo], { encoding: "utf8" });
  execFileSync("git", ["init", "--bare", "-q", remote], { encoding: "utf8" });
  git(repo, "config", "user.name", "Release Test");
  git(repo, "config", "user.email", "release-test@example.invalid");
  git(repo, "config", "core.autocrlf", "false");

  writeFile(repo, "VERSION", `${currentVersion}\n`);
  writeFile(repo, "package.json", `${JSON.stringify({
    name: "agentweaver",
    version: currentVersion,
    private: true,
  }, null, 2)}\n`);
  writeFile(repo, "package-lock.json", `${JSON.stringify({
    name: "agentweaver",
    version: currentVersion,
    lockfileVersion: 3,
    requires: true,
    packages: {
      "": { name: "agentweaver", version: currentVersion },
    },
  }, null, 2)}\n`);
  writeFile(repo, "CHANGELOG.md", "# agentweaver\n");
  writeFile(repo, ".changeset/config.json", `${JSON.stringify({
    changelog: "@changesets/cli/changelog",
    commit: false,
    fixed: [],
    linked: [],
    access: "restricted",
    baseBranch: "dev",
    updateInternalDependencies: "patch",
    bumpVersionsWithWorkspaceProtocolOnly: false,
    changedFilePatterns: ["**"],
    format: false,
    privatePackages: { version: true, tag: false },
  }, null, 2)}\n`);
  writeFile(repo, ".changeset/fixture-child-patch.md", [
    "---",
    `"agentweaver": ${bump}`,
    "---",
    "",
    "Patch Agentweaver.",
    "",
  ].join("\n"));

  git(repo, "add", "--all");
  git(repo, "commit", "-q", "-m", "fixture release source");
  git(repo, "tag", `v${publishedVersion}`);
  git(repo, "remote", "add", "origin", remote);
  git(repo, "push", "-q", "origin", baseBranch, "--tags");
  if (branch !== baseBranch) {
    git(repo, "checkout", "-q", "-b", branch);
  }
  if (mainChangeset) {
    git(repo, "checkout", "-q", baseBranch);
    writeFile(repo, ".changeset/origin-main-minor.md", [
      "---",
      `"agentweaver": ${mainChangeset}`,
      "---",
      "",
      "Release input added on origin/main.",
      "",
    ].join("\n"));
    git(repo, "add", ".changeset/origin-main-minor.md");
    git(repo, "commit", "-q", "-m", "add release input on main");
    git(repo, "push", "-q", "origin", baseBranch);
    if (branch !== baseBranch) {
      git(repo, "checkout", "-q", branch);
    }
  }
  return repo;
}

function runPrepare(repo, args) {
  try {
    return {
      stdout: execFileSync(process.execPath, [prepareScript, ...args], {
        cwd: repo,
        encoding: "utf8",
      }),
      stderr: "",
      status: 0,
    };
  } catch (error) {
    return {
      stdout: error.stdout?.toString() ?? "",
      stderr: error.stderr?.toString() ?? "",
      status: error.status ?? 1,
    };
  }
}

function runPlan(repo, args) {
  try {
    return {
      stdout: execFileSync(process.execPath, [planScript, ...args], {
        cwd: repo,
        encoding: "utf8",
      }),
      stderr: "",
      status: 0,
    };
  } catch (error) {
    return {
      stdout: error.stdout?.toString() ?? "",
      stderr: error.stderr?.toString() ?? "",
      status: error.status ?? 1,
    };
  }
}

function sourceSnapshot(repo) {
  const files = [
    "VERSION",
    "package.json",
    "package-lock.json",
    "CHANGELOG.md",
    ".changeset/fixture-child-patch.md",
  ];
  return {
    head: git(repo, "rev-parse", "HEAD"),
    files: Object.fromEntries(files.map((file) => [
      file,
      fs.existsSync(path.join(repo, file)) ? fs.readFileSync(path.join(repo, file), "utf8") : null,
    ])),
  };
}

test("release preparation defaults to the native Changesets bump", (t) => {
  const repo = createReleaseRepo(t, "default", { branch: "release/v0.34.1" });
  const result = runPrepare(repo, ["--expected", "0.34.1"]);

  assert.equal(result.status, 0, result.stderr);
  const packageJson = JSON.parse(fs.readFileSync(path.join(repo, "package.json"), "utf8"));
  const lockJson = JSON.parse(fs.readFileSync(path.join(repo, "package-lock.json"), "utf8"));
  assert.equal(fs.readFileSync(path.join(repo, "VERSION"), "utf8"), "0.34.1\n");
  assert.equal(packageJson.version, "0.34.1");
  assert.equal(lockJson.version, "0.34.1");
  assert.equal(lockJson.packages[""].version, "0.34.1");
  assert.match(fs.readFileSync(path.join(repo, "CHANGELOG.md"), "utf8"), /^## 0\.34\.1/m);
  assert.equal(fs.existsSync(path.join(repo, ".changeset/fixture-child-patch.md")), false);
});

test("release planner ignores an unrelated local dev ref and does not mutate sources", (t) => {
  const repo = createReleaseRepo(t, "planner");
  git(repo, "push", "-q", "origin", "main:dev");
  git(repo, "fetch", "-q", "origin");
  git(repo, "update-ref", "refs/heads/dev", createUnrelatedCommit(repo, "unrelated local dev"));
  assert.notEqual(git(repo, "rev-parse", "dev"), git(repo, "rev-parse", "origin/dev"));
  assert.throws(() => execFileSync("git", ["merge-base", "dev", "origin/dev"], {
    cwd: repo,
    stdio: "ignore",
  }));
  fs.symlinkSync(
    path.join(projectRoot, "node_modules"),
    path.join(repo, "node_modules"),
    process.platform === "win32" ? "junction" : "dir",
  );
  const sourceStatus = git(repo, "status", "--porcelain", "--ignored");

  const defaultPlan = runPlan(repo, []);
  assert.equal(defaultPlan.status, 0, defaultPlan.stderr);
  assert.match(defaultPlan.stdout, /Planned release: 0\.34\.0 -> 0\.34\.1 \(patch\)/);
  assert.doesNotMatch(defaultPlan.stdout, /native minimum/);
  const explicitPlan = runPlan(repo, ["--target", "0.34.2"]);
  assert.equal(explicitPlan.status, 0, explicitPlan.stderr);
  assert.match(explicitPlan.stdout, /Planned release: 0\.34\.0 -> 0\.34\.2 \(patch; native minimum 0\.34\.1\)/);
  assert.equal(git(repo, "status", "--porcelain", "--ignored"), sourceStatus);
  assert.equal(fs.readdirSync(repo).some((name) => name.startsWith(".changeset-status-")), false);
});

test("release planner preserves no-pending output and rejects a target without a plan", (t) => {
  const repo = createReleaseRepo(t, "planner-no-pending");
  fs.rmSync(path.join(repo, ".changeset/fixture-child-patch.md"));
  git(repo, "add", "--all");
  git(repo, "commit", "-q", "-m", "remove pending changeset");
  fs.symlinkSync(
    path.join(projectRoot, "node_modules"),
    path.join(repo, "node_modules"),
    process.platform === "win32" ? "junction" : "dir",
  );
  const sourceStatus = git(repo, "status", "--porcelain", "--ignored");

  const noPlan = runPlan(repo, []);
  assert.equal(noPlan.status, 0, noPlan.stderr);
  assert.equal(noPlan.stdout, "No pending changesets.\n");

  const targeted = runPlan(repo, ["--target", "0.34.2"]);
  assert.notEqual(targeted.status, 0);
  assert.match(targeted.stderr, /--target requires a pending native release plan for agentweaver/);
  assert.equal(git(repo, "status", "--porcelain", "--ignored"), sourceStatus);
});

test("explicit patch target updates the same native plan's package, changelog, dependency, and mirrors", (t) => {
  const repo = createReleaseRepo(t, "explicit-target");
  const result = runPrepare(repo, ["--expected", "0.34.2", "--target", "0.34.2"]);

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Prepared v0\.34\.2 \(native minimum 0\.34\.1\)/);
  const packageJson = JSON.parse(fs.readFileSync(path.join(repo, "package.json"), "utf8"));
  const lockJson = JSON.parse(fs.readFileSync(path.join(repo, "package-lock.json"), "utf8"));
  const changelog = fs.readFileSync(path.join(repo, "CHANGELOG.md"), "utf8");
  assert.equal(fs.readFileSync(path.join(repo, "VERSION"), "utf8"), "0.34.2\n");
  assert.equal(packageJson.version, "0.34.2");
  assert.equal(lockJson.version, "0.34.2");
  assert.equal(lockJson.packages[""].version, "0.34.2");
  assert.match(changelog, /^## 0\.34\.2/m);
  assert.equal(fs.existsSync(path.join(repo, ".changeset/fixture-child-patch.md")), false);

  git(repo, "add", "--all");
  git(repo, "commit", "-q", "-m", "prepared release");
  const beforeRepeat = sourceSnapshot(repo);
  const repeated = runPrepare(repo, ["--expected", "0.34.2", "--target", "0.34.2"]);
  assert.notEqual(repeated.status, 0);
  assert.match(repeated.stderr, /No unreleased changesets found/);
  assert.deepEqual(sourceSnapshot(repo), beforeRepeat);
});

test("native dependent graph updates workspace versions and dependency ranges", async (t) => {
  const root = scratchRoot(t, "dependent-graph");
  const repo = path.join(root, "repo");
  fs.mkdirSync(repo, { recursive: true });
  execFileSync("git", ["init", "-q", "-b", "main", repo], { encoding: "utf8" });
  git(repo, "config", "user.name", "Release Test");
  git(repo, "config", "user.email", "release-test@example.invalid");
  git(repo, "config", "core.autocrlf", "false");
  writeFile(repo, "package.json", `${JSON.stringify({
    name: "fixture-root",
    version: "1.0.0",
    private: true,
    workspaces: ["packages/*"],
  }, null, 2)}\n`);
  writeFile(repo, "package-lock.json", `${JSON.stringify({
    name: "fixture-root",
    version: "1.0.0",
    lockfileVersion: 3,
    requires: true,
    packages: {
      "": { name: "fixture-root", version: "1.0.0" },
      "packages/agentweaver": { name: "agentweaver", version: "0.34.0" },
      "packages/product-client": { name: "product-client", version: "1.0.0" },
      "packages/fixture-lib": { name: "fixture-lib", version: "1.0.0" },
    },
  }, null, 2)}\n`);
  writeFile(repo, ".changeset/config.json", `${JSON.stringify({
    changelog: "@changesets/cli/changelog",
    commit: false,
    fixed: [],
    linked: [],
    access: "restricted",
    baseBranch: "main",
    updateInternalDependencies: "patch",
    bumpVersionsWithWorkspaceProtocolOnly: false,
    changedFilePatterns: ["**"],
    format: false,
    privatePackages: { version: true, tag: false },
  }, null, 2)}\n`);
  writeFile(repo, "packages/agentweaver/package.json", `${JSON.stringify({
    name: "agentweaver",
    version: "0.34.0",
    private: true,
    dependencies: { "fixture-lib": "1.0.0" },
  }, null, 2)}\n`);
  writeFile(repo, "packages/product-client/package.json", `${JSON.stringify({
    name: "product-client",
    version: "1.0.0",
    private: true,
    dependencies: { agentweaver: "0.34.0" },
  }, null, 2)}\n`);
  writeFile(repo, "packages/fixture-lib/package.json", `${JSON.stringify({
    name: "fixture-lib",
    version: "1.0.0",
  }, null, 2)}\n`);
  writeFile(repo, ".changeset/fixture-lib-patch.md", [
    "---",
    '"fixture-lib": patch',
    "---",
    "",
    "Patch the fixture library.",
    "",
  ].join("\n"));
  writeFile(repo, ".changeset/agentweaver-patch.md", [
    "---",
    '"agentweaver": patch',
    "---",
    "",
    "Patch Agentweaver.",
    "",
  ].join("\n"));
  git(repo, "add", "--all");
  git(repo, "commit", "-q", "-m", "fixture workspace release");

  const { packages, config, releasePlan } = await createNativeReleasePlan(repo);
  const productRelease = releasePlan.releases.find((item) => item.name === "agentweaver");
  assert.equal(productRelease?.newVersion, "0.34.1");
  assert.deepEqual(productRelease.changesets, ["agentweaver-patch"]);
  assert.equal(releasePlan.releases.find((item) => item.name === "fixture-lib")?.newVersion, "1.0.1");
  assert.equal(releasePlan.releases.find((item) => item.name === "product-client")?.newVersion, "1.0.1");
  applyExplicitReleaseTarget(releasePlan, "0.34.2", "0.34.0", "0.34.0", config);
  await applyReleasePlan(releasePlan, packages, config);

  const product = JSON.parse(fs.readFileSync(path.join(repo, "packages/agentweaver/package.json"), "utf8"));
  const client = JSON.parse(fs.readFileSync(path.join(repo, "packages/product-client/package.json"), "utf8"));
  const dependency = JSON.parse(fs.readFileSync(path.join(repo, "packages/fixture-lib/package.json"), "utf8"));
  assert.equal(product.version, "0.34.2");
  assert.equal(product.dependencies["fixture-lib"], "1.0.1");
  assert.equal(client.version, "1.0.1");
  assert.equal(client.dependencies.agentweaver, "0.34.2");
  assert.equal(dependency.version, "1.0.1");
  assert.match(fs.readFileSync(path.join(repo, "packages/agentweaver/CHANGELOG.md"), "utf8"), /^## 0\.34\.2/m);
  assert.equal(fs.existsSync(path.join(repo, ".changeset/fixture-lib-patch.md")), false);
  assert.equal(fs.existsSync(path.join(repo, ".changeset/agentweaver-patch.md")), false);
});

test("invalid target fails before release files, changesets, or branch ancestry can change", (t) => {
  const repo = createReleaseRepo(t, "invalid-target", { mainChangeset: "minor" });
  const before = sourceSnapshot(repo);
  const cases = [
    ["0.35.0", /native release series/],
    ["0.34.02", /no leading zeroes/],
    ["0.9007199254740992.0", /safe integer components/],
  ];
  for (const [target, error] of cases) {
    const result = runPrepare(repo, ["--expected", "0.34.2", "--target", target]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, error);
    assert.deepEqual(sourceSnapshot(repo), before);
    assert.equal(git(repo, "status", "--porcelain"), "");
  }
});

test("ancestry merge that adds a minor changeset stops before applying the pre-merge plan", (t) => {
  const repo = createReleaseRepo(t, "ancestry-input-change", { mainChangeset: "minor" });
  const beforeMerge = sourceSnapshot(repo);
  const result = runPrepare(repo, ["--expected", "0.34.2", "--target", "0.34.2"]);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ancestry merge changed native release-plan inputs: \.changeset\/origin-main-minor\.md/);
  const afterMerge = sourceSnapshot(repo);
  assert.notEqual(afterMerge.head, beforeMerge.head);
  assert.deepEqual(afterMerge.files, beforeMerge.files);
  assert.equal(fs.existsSync(path.join(repo, ".changeset/origin-main-minor.md")), true);
  assert.equal(git(repo, "status", "--porcelain"), "");

  const rerun = runPrepare(repo, ["--expected", "0.34.2"]);
  assert.notEqual(rerun.status, 0);
  assert.match(rerun.stderr, /Changesets calculated 0\.35\.0; expected 0\.34\.2/);
  assert.deepEqual(sourceSnapshot(repo), afterMerge);
  assert.equal(git(repo, "status", "--porcelain"), "");
});

test("expected remains an assertion and does not select or force a native version", (t) => {
  const repo = createReleaseRepo(t, "expected-assertion");
  const before = sourceSnapshot(repo);
  const result = runPrepare(repo, ["--expected", "0.34.2"]);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Changesets calculated 0\.34\.1; expected 0\.34\.2/);
  assert.deepEqual(sourceSnapshot(repo), before);
});

test("explicit targets cannot move a native major release beyond the intentional 1.0.0 transition", (t) => {
  const repo = createReleaseRepo(t, "major-target", {
    bump: "major",
    branch: "release/v1.0.2",
  });
  const before = sourceSnapshot(repo);
  const result = runPrepare(repo, ["--expected", "1.0.2", "--target", "1.0.2"]);

  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /major changeset is prohibited before the intentional 1\.0 release/);
  assert.deepEqual(sourceSnapshot(repo), before);
});

test("dirty tree, stale published baseline, and expected branch guards remain ahead of application", (t) => {
  const dirtyRepo = createReleaseRepo(t, "dirty-tree");
  writeFile(dirtyRepo, "README.md", "dirty\n");
  const dirtyBefore = sourceSnapshot(dirtyRepo);
  const dirty = runPrepare(dirtyRepo, ["--expected", "0.34.2", "--target", "0.34.2"]);
  assert.notEqual(dirty.status, 0);
  assert.match(dirty.stderr, /Working tree must be clean/);
  assert.deepEqual(sourceSnapshot(dirtyRepo), dirtyBefore);

  const staleRepo = createReleaseRepo(t, "stale-baseline", {
    currentVersion: "0.33.2",
    publishedVersion: "0.34.0",
  });
  const staleBefore = sourceSnapshot(staleRepo);
  const stale = runPrepare(staleRepo, ["--expected", "0.34.2", "--target", "0.34.2"]);
  assert.notEqual(stale.status, 0);
  assert.match(stale.stderr, /missing the previous release preparation forward-port/);
  assert.deepEqual(sourceSnapshot(staleRepo), staleBefore);

  const wrongBranchRepo = createReleaseRepo(t, "wrong-branch", { branch: "release/v0.34.1" });
  const wrongBranchBefore = sourceSnapshot(wrongBranchRepo);
  const wrongBranch = runPrepare(wrongBranchRepo, ["--expected", "0.34.2", "--target", "0.34.2"]);
  assert.notEqual(wrongBranch.status, 0);
  assert.match(wrongBranch.stderr, /release\/v0\.34\.2/);
  assert.deepEqual(sourceSnapshot(wrongBranchRepo), wrongBranchBefore);
});
