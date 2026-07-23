#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import {
  assertVersionMirrors,
  extractChangelogSection,
  releaseBranchVersion,
  validateReleasePreparation,
} from "./shared.mjs";

const root = process.cwd();
const expectedIndex = process.argv.indexOf("--expected");
const expected = expectedIndex >= 0 ? process.argv[expectedIndex + 1] : undefined;
const changesetsCli = path.join(root, "node_modules", "@changesets", "cli", "bin.js");

if (!expected || !/^\d+\.\d+\.\d+$/.test(expected)) {
  throw new Error("Use --expected X.Y.Z");
}

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

if (git("status", "--porcelain", "--untracked-files=all")) {
  throw new Error("Working tree must be clean before release preparation.");
}

const branch = git("branch", "--show-current");
if (releaseBranchVersion(branch) !== expected) {
  throw new Error(`release:prepare must run on release/v${expected}, not ${branch || "detached HEAD"}`);
}

assertVersionMirrors(root);

// Changesets owns package and lockfile updates; this wrapper owns VERSION.
execFileSync(process.execPath, [changesetsCli, "version"], {
  cwd: root,
  stdio: "inherit",
});

const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
validateReleasePreparation(expected, branch, packageJson.version);
fs.writeFileSync(path.join(root, "VERSION"), `${expected}\n`);

assertVersionMirrors(root);
extractChangelogSection(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), expected);
console.log(`Prepared v${expected}. Review VERSION, package.json, package-lock.json, CHANGELOG.md, and consumed .changeset files before committing.`);
