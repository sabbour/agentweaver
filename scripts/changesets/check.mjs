#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import {
  assertVersionMirrors,
  hasChangesetExemption,
  isReleaseMetadataOnly,
  isReleaseRelevant,
  parseChangesetFragment,
} from "./shared.mjs";

const root = process.cwd();
const baseIndex = process.argv.indexOf("--base");
const base = baseIndex >= 0 ? process.argv[baseIndex + 1] : "origin/dev";

if (!base) {
  throw new Error("--base requires a git ref");
}

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

function changedFiles(diffFilter) {
  const args = ["diff", "--name-only"];
  if (diffFilter) {
    args.push(`--diff-filter=${diffFilter}`);
  }
  args.push(`${base}...HEAD`);
  return git(...args).split("\n").filter(Boolean);
}

const changed = changedFiles();
const added = changedFiles("A").filter((file) => {
  return file.startsWith(".changeset/") && file.endsWith(".md") && path.basename(file) !== "README.md";
});

for (const file of added) {
  const content = fs.readFileSync(path.join(root, file), "utf8");
  const allowMajor = process.env.GITHUB_BASE_REF === "release/v1.0.0";

  try {
    parseChangesetFragment(content, { allowMajor });
  } catch (error) {
    throw new Error(`${file}: ${error.message}`);
  }
}

assertVersionMirrors(root);

const event = process.env.GITHUB_EVENT_PATH && fs.existsSync(process.env.GITHUB_EVENT_PATH)
  ? JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, "utf8"))
  : {};
const labels = event.pull_request?.labels?.map((label) => label.name) ?? [];
const body = event.pull_request?.body ?? "";
const exempt = hasChangesetExemption(labels, body);
const needsChangeset = isReleaseRelevant(changed) && !isReleaseMetadataOnly(changed);

if (needsChangeset && added.length === 0 && !exempt) {
  throw new Error("Relevant product changes have no changeset. Add one with `npm run changeset`, or use the changeset:not-required label with a `Changeset exemption: <rationale>` line in the PR body.");
}

if (exempt) {
  console.log("Changeset exemption accepted.");
} else if (added.length) {
  console.log(`Validated ${added.length} changeset fragment(s).`);
} else {
  console.log("No changeset required by this advisory check.");
}
