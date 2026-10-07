#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import {
  assertVersionMirrors,
  applyExplicitReleaseTarget,
  latestPublishedVersion,
  parseReleaseTarget,
  validatePublishedReleaseState,
} from "./shared.mjs";
import { readNativeReleaseConfig } from "./native-release-plan.mjs";

const root = process.cwd();
const target = parseReleaseTarget(process.argv.slice(2));
const output = path.join(root, `.changeset-status-${process.pid}.json`);
const changesetsCli = path.join(root, "node_modules", "@changesets", "cli", "bin.js");

function git(...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

const currentVersion = assertVersionMirrors(root);
const publishedVersion = latestPublishedVersion(
  git("ls-remote", "--tags", "--refs", "origin", "refs/tags/v*"),
);
validatePublishedReleaseState(currentVersion, undefined, publishedVersion);

try {
  execFileSync(process.execPath, [changesetsCli, "status", "--output", output], {
    cwd: root,
    stdio: "inherit",
  });

  const status = JSON.parse(fs.readFileSync(output, "utf8"));
  const release = status.releases?.find((item) => item.name === "agentweaver");
  if (!release) {
    if (target !== undefined) {
      throw new Error("--target requires a pending native release plan for agentweaver.");
    }
    console.log("No pending changesets.");
    process.exit(0);
  }

  const { config } = await readNativeReleaseConfig(root, { reportWarnings: false });
  const targetInfo = applyExplicitReleaseTarget(status, target, currentVersion, publishedVersion, config);
  if (release.type === "major" && release.newVersion !== "1.0.0") {
    throw new Error("A major changeset is prohibited before the intentional 1.0 release.");
  }
  validatePublishedReleaseState(currentVersion, release.newVersion, publishedVersion);

  const changesets = (status.changesets ?? [])
    .map((item) => typeof item === "string" ? item : item.id ?? "unknown")
    .join(", ") || "none";
  const targetNote = targetInfo && targetInfo.nativeVersion !== release.newVersion
    ? `; native minimum ${targetInfo.nativeVersion}`
    : "";
  console.log(`Planned release: ${release.oldVersion} -> ${release.newVersion} (${release.type}${targetNote})`);
  console.log(`Included changesets: ${changesets}`);
} finally {
  if (fs.existsSync(output)) {
    fs.unlinkSync(output);
  }
}
