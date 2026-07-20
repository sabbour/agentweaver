#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import { assertVersionMirrors, extractChangelogSection } from "./shared.mjs";
import fs from "node:fs";
import path from "node:path";
const root = process.cwd(); const sha = process.argv[2]; if (!sha) throw new Error("Usage: release:sync-dev -- <release-preparation-sha>");
const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
if (git("status", "--porcelain")) throw new Error("Working tree must be clean before syncing release metadata.");
const branch = git("branch", "--show-current"); if (!branch || branch === "dev" || branch === "main") throw new Error("Run release:sync-dev on a short-lived branch from current dev.");
git("fetch", "origin", "dev"); if (git("merge-base", "HEAD", "origin/dev") !== git("rev-parse", "origin/dev")) throw new Error("Current branch must be based on current origin/dev.");
const files = git("show", "--format=", "--name-only", sha).split("\n").filter(Boolean);
for (const required of ["VERSION", "package.json", "package-lock.json", "CHANGELOG.md"]) if (!files.includes(required)) throw new Error(`${sha} is not a release-preparation commit (missing ${required}).`);
if (!files.some((file) => file.startsWith(".changeset/"))) throw new Error(`${sha} does not consume changesets.`);
const version = assertVersionMirrors(root); extractChangelogSection(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), version);
execFileSync("git", ["cherry-pick", sha], { cwd: root, stdio: "inherit" });
console.log(`Cherry-picked release preparation ${sha} onto ${branch}. Open a PR to dev.`);
