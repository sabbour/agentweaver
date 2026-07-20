#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { assertVersionMirrors } from "./shared.mjs";
const root = process.cwd();
assertVersionMirrors(root);
const output = path.join(root, `.changeset-status-${process.pid}.json`);
try {
  execFileSync(process.execPath, [path.join(root, "node_modules", "@changesets", "cli", "bin.js"), "status", "--output", output], { cwd: root, stdio: "inherit" });
  const status = JSON.parse(fs.readFileSync(output, "utf8"));
  const release = status.releases?.find((item) => item.name === "agentweaver");
  if (!release) { console.log("No pending changesets."); process.exit(0); }
  if (release.type === "major" && release.newVersion !== "1.0.0") throw new Error("A major changeset is prohibited before the intentional 1.0 release.");
  console.log(`Planned release: ${release.oldVersion} -> ${release.newVersion} (${release.type})`);
  console.log(`Included changesets: ${(status.changesets ?? []).map((item) => typeof item === "string" ? item : item.id ?? "unknown").join(", ") || "none"}`);
} finally { if (fs.existsSync(output)) fs.unlinkSync(output); }
