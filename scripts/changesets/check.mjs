#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { assertVersionMirrors } from "./shared.mjs";

const root = process.cwd();
const baseIndex = process.argv.indexOf("--base");
const base = baseIndex >= 0 ? process.argv[baseIndex + 1] : "origin/dev";
if (!base) throw new Error("--base requires a git ref");
const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
const changed = git("diff", "--name-only", `${base}...HEAD`).split("\n").filter(Boolean);
const added = git("diff", "--name-only", "--diff-filter=A", `${base}...HEAD`).split("\n").filter((file) => file.startsWith(".changeset/") && file.endsWith(".md") && path.basename(file) !== "README.md");
const fragment = /^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]+)$/;
for (const file of added) {
  const content = fs.readFileSync(path.join(root, file), "utf8");
  const match = fragment.exec(content);
  if (!match) throw new Error(`${file}: expected Changesets frontmatter followed by user-facing prose`);
  const entries = [...match[1].matchAll(/^\s*["']?([^"':\s]+)["']?\s*:\s*(major|minor|patch)\s*$/gm)];
  if (entries.length !== 1 || entries[0][1] !== "agentweaver") throw new Error(`${file}: must contain exactly one agentweaver bump`);
  if (entries[0][2] === "major" && process.env.GITHUB_BASE_REF !== "release/v1.0.0") {
    throw new Error(`${file}: major changesets are prohibited before the intentional 1.0 release`);
  }
  if (!match[2].trim()) throw new Error(`${file}: release-note prose is required`);
}
assertVersionMirrors(root);
const relevant = changed.some((file) => /^(apps\/|packages\/|scripts\/azure\/|k8s\/)/.test(file));
const event = process.env.GITHUB_EVENT_PATH && fs.existsSync(process.env.GITHUB_EVENT_PATH) ? JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH, "utf8")) : {};
const labels = event.pull_request?.labels?.map((label) => label.name) ?? [];
const body = event.pull_request?.body ?? "";
const exempt = labels.includes("changeset:not-required") && /^Changeset exemption:\s*\S.+$/mi.test(body);
const metadataOnly = changed.length > 0 && changed.every((file) => /^(VERSION|package\.json|package-lock\.json|CHANGELOG\.md|\.changeset\/)/.test(file));
if (relevant && added.length === 0 && !exempt && !metadataOnly) console.warn("::warning::Relevant product changes have no changeset. Add one or use changeset:not-required with a Changeset exemption: rationale.");
if (exempt) console.log("Changeset exemption accepted.");
else console.log(added.length ? `Validated ${added.length} changeset fragment(s).` : "No changeset required by this advisory check.");
