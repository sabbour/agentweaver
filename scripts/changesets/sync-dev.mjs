#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import {
  assertVersionMirrors,
  extractChangelogSection,
  validateReleasePreparationFiles,
  validateSyncBranch,
} from "./shared.mjs";

const root = process.cwd();
const sha = process.argv[2];

if (!sha) {
  throw new Error("Usage: release:sync-dev -- <release-preparation-sha>");
}

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

if (git("status", "--porcelain")) {
  throw new Error("Working tree must be clean before syncing release metadata.");
}

const branch = git("branch", "--show-current");
validateSyncBranch(branch);
git("fetch", "origin", "dev");

if (git("merge-base", "HEAD", "origin/dev") !== git("rev-parse", "origin/dev")) {
  throw new Error("Current branch must be based on current origin/dev.");
}

const files = git("show", "--format=", "--name-only", sha).split("\n").filter(Boolean);
validateReleasePreparationFiles(sha, files);

const version = assertVersionMirrors(root);
const changelog = fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
extractChangelogSection(changelog, version);

execFileSync("git", ["cherry-pick", sha], { cwd: root, stdio: "inherit" });
console.log(`Cherry-picked release preparation ${sha} onto ${branch}. Open a PR to dev.`);
