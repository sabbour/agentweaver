#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { applyReleasePlan } from "@changesets/apply-release-plan";
import {
  applyExplicitReleaseTarget,
  assertVersionMirrors,
  extractChangelogSection,
  latestPublishedVersion,
  parseReleaseTarget,
  releaseBranchVersion,
  synchronizePackageLockVersion,
  validatePublishedReleaseState,
  validateReleasePreparation,
  getUnexpectedIgnoredFiles,
  ensureReleaseBranchHasMainAncestry,
  isReleasePlanInputPath,
} from "./shared.mjs";
import { createNativeReleasePlan } from "./native-release-plan.mjs";

const root = process.cwd();
const args = process.argv.slice(2);
const expectedIndex = args.indexOf("--expected");
const expected = expectedIndex >= 0 ? args[expectedIndex + 1] : undefined;
const target = parseReleaseTarget(args);
const noAncestryMerge = args.includes("--no-ancestry-merge");

if (!expected || !/^\d+\.\d+\.\d+$/.test(expected)) {
  throw new Error("Use --expected X.Y.Z");
}

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

if (git("status", "--porcelain", "--untracked-files=all")) {
  throw new Error("Working tree must be clean before release preparation.");
}

const ignored = git("status", "--porcelain", "--ignored=matching");
const unexpectedIgnored = getUnexpectedIgnoredFiles(ignored);
if (unexpectedIgnored.length > 0) {
  throw new Error(`Working tree contains unexpected ignored files: ${unexpectedIgnored.join(", ")}`);
}

const branch = git("branch", "--show-current");
if (releaseBranchVersion(branch) !== expected) {
  throw new Error(`release:prepare must run on release/v${expected}, not ${branch || "detached HEAD"}`);
}

const currentVersion = assertVersionMirrors(root);
const publishedVersion = latestPublishedVersion(
  git("ls-remote", "--tags", "--refs", "origin", "refs/tags/v*"),
);
validatePublishedReleaseState(currentVersion, undefined, publishedVersion);

const { packages, config, releasePlan } = await createNativeReleasePlan(root);
const targetInfo = applyExplicitReleaseTarget(releasePlan, target, currentVersion, publishedVersion, config);
const release = releasePlan.releases.find((item) => item.name === "agentweaver");
if (!release) {
  throw new Error("No native release plan was calculated for agentweaver.");
}
if (release.type === "major" && release.newVersion !== "1.0.0") {
  throw new Error("A major changeset is prohibited before the intentional 1.0 release.");
}
validateReleasePreparation(expected, branch, release.newVersion);
validatePublishedReleaseState(currentVersion, release.newVersion, publishedVersion);

const preMergeHead = git("rev-parse", "HEAD");
const ancestry = ensureReleaseBranchHasMainAncestry(root, branch, { allowMerge: !noAncestryMerge });
if (ancestry.merged) {
  const changedInputs = git("diff", "--name-only", `${preMergeHead}..HEAD`)
    .split(/\r?\n/)
    .filter(isReleasePlanInputPath);
  if (changedInputs.length > 0) {
    throw new Error(
      `origin/main ancestry merge changed native release-plan inputs: ${changedInputs.join(", ")}. `
      + "No release files were applied; rerun release:prepare on the clean merged tree to recalculate the native plan.",
    );
  }
  throw new Error(
    `origin/main was merged into ${branch} at ${ancestry.commit}. `
    + "No release files were applied; rerun release:prepare on the clean merged tree to recalculate the native plan.",
  );
}

// Apply the validated native plan once so package versions, changelogs, and
// consumed changesets all come from the same Changesets calculation.
await applyReleasePlan(releasePlan, packages, config);

const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
validateReleasePreparation(expected, branch, packageJson.version);
synchronizePackageLockVersion(root, packageJson.version);
fs.writeFileSync(path.join(root, "VERSION"), `${expected}\n`);

assertVersionMirrors(root);
extractChangelogSection(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), expected);
const targetNote = targetInfo && targetInfo.nativeVersion !== release.newVersion
  ? ` (native minimum ${targetInfo.nativeVersion})`
  : "";
console.log(`Prepared v${expected}${targetNote}. Review VERSION, package.json, package-lock.json, CHANGELOG.md, and consumed .changeset files before committing.`);
