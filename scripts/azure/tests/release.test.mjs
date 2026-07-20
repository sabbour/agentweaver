// release.test.mjs -- Tests for release.mjs: semver bump logic, dirty-tree
// rejection, VERSION file read/validate, and delegation call sequence to
// the shared step engine (steps/20 -> 25 -> 30 -> 40, NOT duplicated build
// logic). All git/gh/exec calls are injected fakes -- no real git, gh, az,
// or kubectl invocation.

import test from "node:test";
import assert from "node:assert/strict";
import {
  VALID_BUMPS,
  InvalidBumpError,
  DirtyWorkingTreeError,
  parseArgs,
  readCurrentVersion,
  bumpVersion,
  isWorkingTreeClean,
  previousTag,
  previousTagBefore,
  isReleaseTag,
  RELEASE_TAG_PATTERN,
  ReleaseResumeError,
  tagDate,
  generateChangelog,
  run,
} from "../release.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec() };
}

function fakeStep(name, calls, result = {}) {
  return {
    async run(cfg, opts) {
      calls.push({ step: name, cfg, opts });
      return result;
    },
  };
}

test("VALID_BUMPS: is major/minor/patch", () => {
  assert.deepEqual(VALID_BUMPS, ["major", "minor", "patch"]);
});

test("bumpVersion: patch/minor/major arithmetic", () => {
  assert.equal(bumpVersion("1.2.3", "patch"), "1.2.4");
  assert.equal(bumpVersion("1.2.3", "minor"), "1.3.0");
  assert.equal(bumpVersion("1.2.3", "major"), "2.0.0");
});

test("bumpVersion: throws InvalidBumpError on an invalid bump argument", () => {
  assert.throws(() => bumpVersion("1.2.3", "bogus"), InvalidBumpError);
});

test("parseArgs: parses positional bump + --dry-run", () => {
  assert.deepEqual(parseArgs(["patch", "--dry-run"]), { bump: "patch", resumeTag: undefined, dryRun: true, help: false });
});

test("parseArgs: parses --resume release tag", () => {
  assert.deepEqual(parseArgs(["--resume", "v1.2.3"]), { bump: undefined, resumeTag: "v1.2.3", dryRun: false, help: false });
  assert.throws(() => parseArgs(["patch", "--resume", "v1.2.3"]), /either a version bump or --resume/);
});

test("parseArgs: help via -h/--help/help", () => {
  assert.equal(parseArgs(["-h"]).help, true);
  assert.equal(parseArgs(["--help"]).help, true);
  assert.equal(parseArgs(["help"]).help, true);
});

test("parseArgs: throws on a second unexpected positional argument", () => {
  assert.throws(() => parseArgs(["patch", "minor"]), /Unknown argument/);
});

test("readCurrentVersion: reads and validates VERSION file contents", () => {
  const readFile = () => "1.4.2\n";
  assert.equal(readCurrentVersion("/repo", { readFile }), "1.4.2");
});

test("readCurrentVersion: throws on malformed VERSION contents", () => {
  const readFile = () => "not-a-version";
  assert.throws(() => readCurrentVersion("/repo", { readFile }), /invalid semver/);
});

test("isWorkingTreeClean: true only when both unstaged and staged diffs are clean", async () => {
  const cleanCapture = async () => ({ code: 0 });
  assert.equal(await isWorkingTreeClean({ cwd: "/repo", capture: cleanCapture }), true);

  const dirtyCapture = async (cmd, args) => ({ code: args.includes("--cached") ? 0 : 1 });
  assert.equal(await isWorkingTreeClean({ cwd: "/repo", capture: dirtyCapture }), false);
});

test("previousTag: returns '' when no tag exists", async () => {
  const capture = async () => ({ code: 128, stdout: "" });
  assert.equal(await previousTag({ cwd: "/repo", capture }), "");
});

test("previousTag: returns the trimmed tag on success", async () => {
  const capture = async () => ({ code: 0, stdout: "v1.2.3\n" });
  assert.equal(await previousTag({ cwd: "/repo", capture }), "v1.2.3");
});

test("isReleaseTag: matches only final vX.Y.Z tags (no suffix/prefix)", () => {
  assert.equal(isReleaseTag("v0.9.6"), true);
  assert.equal(isReleaseTag("v10.20.30"), true);
  assert.equal(isReleaseTag("  v1.2.3\n"), true); // trimmed
  assert.equal(isReleaseTag("v0.9.6-rc1"), false); // prerelease suffix
  assert.equal(isReleaseTag("v0.9.6+build"), false); // build metadata
  assert.equal(isReleaseTag("0.9.6"), false); // missing v prefix
  assert.equal(isReleaseTag("v1.2"), false); // not full semver
  assert.equal(isReleaseTag("release-1.2.3"), false);
  assert.equal(isReleaseTag(""), false);
  assert.equal(isReleaseTag(undefined), false);
  assert.equal(RELEASE_TAG_PATTERN.source, "^v\\d+\\.\\d+\\.\\d+$");
});

test("previousTag: skips prerelease/lightweight tags and returns the newest final release tag", async () => {
  // `git tag --list --sort=-v:refname` yields newest-first; a prerelease tag
  // sorts above its final release but must be ignored as a release boundary.
  const capture = async (cmd, args) => {
    assert.deepEqual(args, ["tag", "--list", "--sort=-v:refname"]);
    return { code: 0, stdout: "v0.9.7-rc2\nv0.9.7-rc1\nv0.9.6\nnightly\nv0.9.5\n" };
  };
  assert.equal(await previousTag({ cwd: "/repo", capture }), "v0.9.6");
});

test("previousTag: returns '' when no final release tag exists", async () => {
  const capture = async () => ({ code: 0, stdout: "v0.9.6-rc1\nnightly\n" });
  assert.equal(await previousTag({ cwd: "/repo", capture }), "");
});

test("previousTagBefore: returns the final release tag before the supplied tag", async () => {
  const capture = async () => ({ code: 0, stdout: "v1.0.1\nv1.0.0\nv0.9.9\n" });
  assert.equal(await previousTagBefore("v1.0.1", { cwd: "/repo", capture }), "v1.0.0");
});

test("tagDate: returns epoch fallback when tag is falsy", async () => {
  assert.equal(await tagDate("", { cwd: "/repo", capture: async () => ({ code: 0, stdout: "" }) }), "1970-01-01T00:00:00Z");
});

test("tagDate: throws when git log cannot resolve the tag's date", async () => {
  const capture = async () => ({ code: 1, stdout: "" });
  await assert.rejects(tagDate("v1.2.3", { cwd: "/repo", capture }), /Could not determine date/);
});

test("generateChangelog: falls back to a placeholder line when gh returns nothing", async () => {
  const capture = async () => ({ code: 0, stdout: "" });
  const changelog = await generateChangelog("2024-01-01T00:00:00Z", "v1.2.2", { capture });
  assert.match(changelog, /No pull requests found since v1.2.2/);
});

test("generateChangelog: never throws even if gh fails", async () => {
  const capture = async () => ({ code: 1, stdout: "" });
  const changelog = await generateChangelog("2024-01-01T00:00:00Z", "", { capture });
  assert.match(changelog, /No pull requests found/);
});

function makeGitExec({ dirty = false } = {}) {
  const runCalls = [];
  const captureCalls = [];
  let dryRunFlag = false;
  return {
    calls: { run: runCalls, capture: captureCalls },
    setDryRun(v) {
      dryRunFlag = v;
    },
    isDryRun() {
      return dryRunFlag;
    },
    async run(cmd, args, opts) {
      runCalls.push({ cmd, args, opts });
      return { code: 0 };
    },
    async capture(cmd, args, opts) {
      captureCalls.push({ cmd, args, opts });
      if (cmd === "git" && args[0] === "diff") return { code: dirty ? 1 : 0, stdout: "" };
      if (cmd === "git" && args[0] === "tag" && args[1] === "--list") return { code: 0, stdout: "v1.0.0\n" };
      if (cmd === "git" && args[0] === "describe") return { code: 0, stdout: "v1.0.0\n" };
      if (cmd === "git" && args[0] === "log") return { code: 0, stdout: "2024-01-01T00:00:00Z\n" };
      if (cmd === "gh" && args[0] === "pr") return { code: 0, stdout: "- Some fix (#42)\n" };
      return { code: 0, stdout: "" };
    },
  };
}

test("run: throws DirtyWorkingTreeError when the working tree is dirty", async () => {
  const exec = makeGitExec({ dirty: true });
  const writeFile = () => {};
  await assert.rejects(
    run({ argv: ["patch"], exec, log: noopLog(), writeFile, readFile: () => "1.0.0\n" }),
    DirtyWorkingTreeError,
  );
});

test("run: throws InvalidBumpError for an unrecognized bump argument", async () => {
  const exec = makeGitExec();
  await assert.rejects(run({ argv: ["bogus"], exec, log: noopLog() }), InvalidBumpError);
});

test("run: --help returns without doing any git/gh work", async () => {
  const exec = makeGitExec();
  const result = await run({ argv: ["--help"], exec, log: noopLog() });
  assert.equal(result.help, true);
  assert.equal(exec.calls.run.length, 0);
});

test("run: bumps VERSION, tags, and delegates to steps 20 -> 25 -> 30 -> 40 in order (not duplicated build logic)", async () => {
  const exec = makeGitExec();
  const calls = [];
  const steps = {
    buildImages: fakeStep("buildImages", calls, { IMAGE_TAG: "v1.0.1" }),
    verifyProvenance: fakeStep("verifyProvenance", calls, { ok: true }),
    deployStep: fakeStep("deployStep", calls, { HOST: "example.com" }),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 5, fail: 0 }),
  };
  let writtenVersion;
  const writeFile = (_path, content) => {
    writtenVersion = content;
  };
  const resolveVariablesFn = async ({ env }) => ({ IMAGE_TAG: env.IMAGE_TAG, PREVIOUS_IMAGE_TAG: env.PREVIOUS_IMAGE_TAG });

  const result = await run({
    argv: ["patch"],
    repoRoot: "C:\\repo",
    exec,
    log: noopLog(),
    git: {},
    resolveVariables: resolveVariablesFn,
    steps,
    readFile: () => "1.0.0\n",
    writeFile,
  });

  assert.equal(result.ok, true);
  assert.equal(result.version, "1.0.1");
  assert.equal(result.tag, "v1.0.1");
  assert.equal(writtenVersion, "1.0.1\n");
  assert.deepEqual(calls.map((c) => c.step), ["buildImages", "verifyProvenance", "deployStep", "verifyStep"]);
  // steps/25 must be called with VERIFY_GIT_REF pinned to the new release tag.
  assert.equal(calls[1].cfg.VERIFY_GIT_REF, "v1.0.1");
  // steps/20 receives the previous tag as PREVIOUS_IMAGE_TAG.
  assert.equal(calls[0].cfg.PREVIOUS_IMAGE_TAG, "v1.0.0");

  const gitCommandNames = exec.calls.run.map((c) => `${c.cmd} ${c.args[0]}`);
  assert.ok(gitCommandNames.includes("git tag"));
  assert.ok(gitCommandNames.includes("git push"));
  assert.ok(gitCommandNames.some((c) => c.startsWith("gh")));
});

test("run: --dry-run performs no git/gh mutations and toggles exec dry-run around the delegated steps", async () => {
  const exec = makeGitExec();
  const calls = [];
  const dryRunObserved = [];
  const steps = {
    buildImages: {
      async run(cfg, opts) {
        dryRunObserved.push(exec.isDryRun());
        calls.push({ step: "buildImages" });
        return {};
      },
    },
    verifyProvenance: fakeStep("verifyProvenance", calls, {}),
    deployStep: fakeStep("deployStep", calls, {}),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 1, fail: 0 }),
  };
  const resolveVariablesFn = async () => ({});

  const result = await run({
    argv: ["patch", "--dry-run"],
    repoRoot: "C:\\repo",
    exec,
    log: noopLog(),
    git: {},
    resolveVariables: resolveVariablesFn,
    steps,
    readFile: () => "1.0.0\n",
    writeFile: () => {
      throw new Error("writeFile must not be called during dry-run");
    },
  });

  assert.equal(result.dryRun, true);
  assert.equal(exec.calls.run.length, 0); // no git mutations were issued via exec.run
  assert.ok(dryRunObserved.includes(true)); // dry-run mode was active during delegated steps
  assert.equal(exec.isDryRun(), false); // restored afterwards
});

test("run: --resume skips version/tag/GitHub-release creation and delegates deployment steps", async () => {
  const exec = makeGitExec();
  const originalCapture = exec.capture;
  exec.capture = async (cmd, args, opts) => {
    if (cmd === "git" && args[0] === "tag" && args[1] === "--list") {
      return { code: 0, stdout: "v1.0.1\nv1.0.0\n" };
    }
    if (cmd === "git" && args[0] === "rev-parse") return { code: 0, stdout: "abc123\n" };
    if (cmd === "gh" && args[0] === "release" && args[1] === "view") return { code: 0, stdout: "v1.0.1\n" };
    return originalCapture(cmd, args, opts);
  };
  const calls = [];
  const steps = {
    buildImages: fakeStep("buildImages", calls, {}),
    verifyProvenance: fakeStep("verifyProvenance", calls, {}),
    deployStep: fakeStep("deployStep", calls, {}),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 2, fail: 0 }),
  };

  const result = await run({
    argv: ["--resume", "v1.0.1"],
    repoRoot: "C:\\repo",
    exec,
    log: noopLog(),
    git: {},
    resolveVariables: async ({ env }) => env,
    steps,
    readFile: () => "1.0.1\n",
    writeFile: () => {
      throw new Error("resume must not write VERSION");
    },
  });

  assert.equal(result.ok, true);
  assert.equal(result.tag, "v1.0.1");
  assert.equal(result.previousTag, "v1.0.0");
  assert.deepEqual(calls.map((call) => call.step), ["buildImages", "verifyProvenance", "deployStep", "verifyStep"]);
  assert.equal(calls[0].cfg.TARGET_GIT_REF, "v1.0.1");
  assert.equal(calls[0].cfg.PREVIOUS_IMAGE_TAG, "v1.0.0");
  assert.equal(exec.calls.run.length, 0);
});

test("run: --resume rejects a tag that does not match VERSION before deploying", async () => {
  const exec = makeGitExec();
  const calls = [];
  const steps = {
    buildImages: fakeStep("buildImages", calls),
    verifyProvenance: fakeStep("verifyProvenance", calls),
    deployStep: fakeStep("deployStep", calls),
    verifyStep: fakeStep("verifyStep", calls),
  };

  await assert.rejects(
    run({
      argv: ["--resume", "v1.0.1"],
      repoRoot: "C:\\repo",
      exec,
      log: noopLog(),
      steps,
      readFile: () => "1.0.0\n",
    }),
    (error) => error instanceof ReleaseResumeError && /VERSION is 1\.0\.0/.test(error.message),
  );
  assert.equal(calls.length, 0);
  assert.equal(exec.calls.run.length, 0);
});
