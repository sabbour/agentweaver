// deploy-checkpoint.mjs -- Stage-level resume state for release deployments.
//
// A release deploy runs expensive, mostly-idempotent stages in sequence. When a
// late stage fails on a transient Azure error, re-running the whole command
// repeats every earlier stage -- including a four-image promotion that takes
// minutes and re-queries a registry that was already correct. This module
// records which stages completed, plus the values later stages need from them
// (notably the resolved AgentHost digest), so `--resume` can continue from the
// break instead of starting over.
//
// WHY THE STATE LIVES OUTSIDE THE REPO
// ------------------------------------
// `validatePublishedRelease` refuses to deploy when the working tree is dirty,
// and `isWorkingTreeClean` inspects untracked *and* ignored paths. A state file
// written anywhere inside the checkout would therefore make the very command
// that writes it refuse to run. It is stored under the user's home directory
// instead, keyed by the deployment target.
//
// Only verification-independent facts are persisted. Verification stages are
// deliberately NOT resumable: they are the proof that the deployment is
// correct, they are comparatively cheap, and skipping them on a resume could
// report success for a cluster nobody actually re-checked.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";

export const CHECKPOINT_VERSION = 1;

/** Stages that may be skipped when resuming, in execution order. */
export const RESUMABLE_STAGES = Object.freeze(["build", "deploy"]);

export function defaultCheckpointDir({ homedir = os.homedir() } = {}) {
  return path.join(homedir, ".agentweaver", "deploy-state");
}

/**
 * Identifies a deployment target. A checkpoint is only reusable for the exact
 * same release tag *and* the same destination; anything else must start clean,
 * otherwise a resume could skip a build that never happened for this target.
 */
export function checkpointKey({
  tag,
  subscriptionId = "",
  resourceGroup = "",
  acrName = "",
  clusterName = "",
  namespace = "",
  imageSource = "",
}) {
  if (!tag) throw new Error("checkpointKey requires a release tag.");
  const material = [
    tag,
    subscriptionId,
    resourceGroup,
    acrName,
    clusterName,
    namespace,
    imageSource,
  ].join("\u0000");
  const digest = crypto.createHash("sha256").update(material).digest("hex").slice(0, 16);
  const safeTag = tag.replace(/[^A-Za-z0-9._-]/g, "_");
  return `${safeTag}-${digest}`;
}

export function checkpointPath(key, { dir = defaultCheckpointDir() } = {}) {
  return path.join(dir, `${key}.json`);
}

export function loadCheckpoint(key, { dir = defaultCheckpointDir(), readFile = fs.readFileSync } = {}) {
  const file = checkpointPath(key, { dir });
  let raw;
  try {
    raw = readFile(file, "utf8");
  } catch {
    return null;
  }
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch {
    // A corrupt checkpoint must never block a deploy; treat it as absent so the
    // run simply starts from the beginning.
    return null;
  }
  if (!parsed || parsed.version !== CHECKPOINT_VERSION) return null;
  if (!parsed.stages || typeof parsed.stages !== "object") return null;
  return parsed;
}

export function saveCheckpoint(key, state, {
  dir = defaultCheckpointDir(),
  writeFile = fs.writeFileSync,
  mkdir = fs.mkdirSync,
} = {}) {
  mkdir(dir, { recursive: true });
  writeFile(checkpointPath(key, { dir }), `${JSON.stringify(state, null, 2)}\n`, "utf8");
  return state;
}

export function clearCheckpoint(key, { dir = defaultCheckpointDir(), rm = fs.rmSync } = {}) {
  try {
    rm(checkpointPath(key, { dir }), { force: true });
  } catch {
    // Losing a stale checkpoint is never worth failing a deployment over.
  }
}

/**
 * Records a completed stage. `result` must contain only data later stages need
 * (never credentials): for `build` that is the resolved image digests.
 */
export function recordStage(state, stage, result, { now = () => new Date().toISOString() } = {}) {
  return {
    ...state,
    updatedAt: now(),
    stages: {
      ...state.stages,
      [stage]: { completedAt: now(), result: result ?? null },
    },
  };
}

export function newCheckpoint({ tag, key, now = () => new Date().toISOString() }) {
  return { version: CHECKPOINT_VERSION, tag, key, updatedAt: now(), stages: {} };
}

export function completedStage(state, stage) {
  return state?.stages?.[stage] ?? null;
}
