#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import {
  assertVersionMirrors,
  applyExplicitReleaseTarget,
  latestPublishedVersion,
  parseReleaseTarget,
  validatePublishedReleaseState,
} from "./shared.mjs";
import { createNativeReleasePlan } from "./native-release-plan.mjs";

const root = process.cwd();
const target = parseReleaseTarget(process.argv.slice(2));

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

const currentVersion = assertVersionMirrors(root);
const publishedVersion = latestPublishedVersion(
  git("ls-remote", "--tags", "--refs", "origin", "refs/tags/v*"),
);
validatePublishedReleaseState(currentVersion, undefined, publishedVersion);

const { config, changesets, releasePlan } = await createNativeReleasePlan(root, { allowEmpty: true });
const release = releasePlan.releases?.find((item) => item.name === "agentweaver");
if (!release) {
  if (target !== undefined) {
    throw new Error("--target requires a pending native release plan for agentweaver.");
  }
  console.log("No pending changesets.");
  process.exit(0);
}

const targetInfo = applyExplicitReleaseTarget(releasePlan, target, currentVersion, publishedVersion, config);
if (release.type === "major" && release.newVersion !== "1.0.0") {
  throw new Error("A major changeset is prohibited before the intentional 1.0 release.");
}
validatePublishedReleaseState(currentVersion, release.newVersion, publishedVersion);

const includedChangesets = changesets
  .map((item) => typeof item === "string" ? item : item.id ?? "unknown")
  .join(", ") || "none";
const targetNote = targetInfo && targetInfo.nativeVersion !== release.newVersion
  ? `; native minimum ${targetInfo.nativeVersion}`
  : "";
console.log(`Planned release: ${release.oldVersion} -> ${release.newVersion} (${release.type}${targetNote})`);
console.log(`Included changesets: ${includedChangesets}`);
