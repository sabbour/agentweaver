// deploy-from-release.mjs -- Deploy an existing published Agentweaver release.
//
// A release deployment is identified only by an existing annotated vX.Y.Z
// tag with a matching GitHub Release. The build context must be checked out at
// that exact tag commit so a semver image can never be built from unrelated
// local source.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import * as gitDefault from "./lib/git.mjs";
import * as kubectlDefault from "./lib/kubectl.mjs";
import { resolveVariables, DEFAULT_REPO_ROOT } from "./variables.mjs";
import * as buildImagesDefault from "./steps/20-build-push-images.mjs";
import * as verifyProvenanceDefault from "./steps/25-verify-image-provenance.mjs";
import * as deployStepDefault from "./steps/30-deploy.mjs";
import * as verifyStepDefault from "./steps/40-verify.mjs";
import {
  waitForWarmPoolReady,
  verifyWarmPoolImage,
} from "./deploy-from-local.mjs";
import {
  isReleaseTag,
  isWorkingTreeClean,
  releaseExists,
} from "./release-publish.mjs";
import {
  assertVersionMirrors,
  extractChangelogSection,
} from "../changesets/shared.mjs";

export class PublishedReleaseError extends Error {}

export function parseArgs(argv = []) {
  let tag;
  let dryRun = false;
  let help = false;

  for (const arg of argv) {
    if (arg === "--dry-run") {
      dryRun = true;
    } else if (["-h", "--help", "help"].includes(arg)) {
      help = true;
    } else if (!tag && !arg.startsWith("-")) {
      tag = arg;
    } else {
      throw new Error(`Unknown argument: ${arg}. Expected a single existing vX.Y.Z tag.`);
    }
  }

  if (!help && !isReleaseTag(tag)) {
    throw new Error("Usage: azure:deploy-from-release -- vX.Y.Z");
  }

  return { tag, dryRun, help };
}

export const HELP_TEXT = `deploy-from-release -- deploy an existing published Agentweaver release

Usage:
  node scripts/azure/cli.mjs deploy-from-release vX.Y.Z [--dry-run]

Requires an existing annotated git tag and matching GitHub Release. The
working tree must be clean and HEAD must equal the tag commit. Builds or
retags vX.Y.Z images, deploys them, verifies live provenance against the tag,
waits for the AgentHost warm pool, and runs health verification.
`;

export async function previousReleaseTag(tag, { cwd, capture }) {
  const result = await capture("git", ["tag", "--list", "--sort=-v:refname"], {
    cwd,
    allowFailure: true,
  });
  let foundTarget = false;
  for (const candidate of result.stdout.split("\n").map((item) => item.trim())) {
    if (candidate === tag) {
      foundTarget = true;
    } else if (foundTarget && isReleaseTag(candidate)) {
      return candidate;
    }
  }
  return "";
}

export async function validatePublishedRelease({
  tag,
  repoRoot,
  exec,
  readFile = fs.readFileSync,
}) {
  const clean = await isWorkingTreeClean({ cwd: repoRoot, capture: exec.capture });
  if (!clean) {
    throw new PublishedReleaseError("Working tree has uncommitted changes. Commit or stash before deploying a release.");
  }

  const fetched = await exec.capture("git", ["fetch", "origin", "--tags"], {
    cwd: repoRoot,
    allowFailure: true,
  });
  if (fetched.code !== 0) {
    throw new PublishedReleaseError("Could not fetch published release tags from origin.");
  }

  const objectType = await exec.capture("git", ["cat-file", "-t", tag], {
    cwd: repoRoot,
    allowFailure: true,
  });
  if (objectType.code !== 0 || objectType.stdout.trim() !== "tag") {
    throw new PublishedReleaseError(`${tag} must be an existing annotated release tag.`);
  }

  const tagCommitResult = await exec.capture("git", ["rev-parse", `${tag}^{commit}`], {
    cwd: repoRoot,
    allowFailure: true,
  });
  const headResult = await exec.capture("git", ["rev-parse", "HEAD"], {
    cwd: repoRoot,
    allowFailure: true,
  });
  const tagCommit = tagCommitResult.stdout.trim();
  if (tagCommitResult.code !== 0 || !tagCommit) {
    throw new PublishedReleaseError(`${tag} does not resolve to a commit.`);
  }
  if (headResult.code !== 0 || headResult.stdout.trim() !== tagCommit) {
    throw new PublishedReleaseError(
      `Release deployment must run from the exact ${tag} commit (${tagCommit.slice(0, 12)}).`,
    );
  }

  if (!(await releaseExists(tag, { cwd: repoRoot, capture: exec.capture }))) {
    throw new PublishedReleaseError(`${tag} has no matching published GitHub Release.`);
  }

  const version = assertVersionMirrors(repoRoot, { readFile });
  if (tag !== `v${version}`) {
    throw new PublishedReleaseError(`${tag} does not match the checked-out VERSION (${version}).`);
  }
  const changelog = readFile(path.join(repoRoot, "CHANGELOG.md"), "utf8");
  extractChangelogSection(changelog, version);

  return { tag, version, commit: tagCommit };
}

export async function run(opts = {}) {
  const {
    argv = [],
    repoRoot = DEFAULT_REPO_ROOT,
    exec = execDefault,
    log = logDefault,
    git = gitDefault,
    kubectl = kubectlDefault,
    resolveVariables: resolveVariablesFn = resolveVariables,
    steps = {},
    readFile = fs.readFileSync,
    validatedRelease,
  } = opts;
  const parsed = parseArgs(argv);
  const dryRun = parsed.dryRun || process.env.DRY_RUN === "true";

  if (parsed.help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  if (dryRun) {
    exec.setDryRun(true);
  }

  try {
    const release = validatedRelease ?? await validatePublishedRelease({
      tag: parsed.tag,
      repoRoot,
      exec,
      readFile,
    });
    const tag = release.tag;
    const previous = await previousReleaseTag(tag, {
      cwd: repoRoot,
      capture: exec.capture,
    });
    const releaseEnv = {
      ...process.env,
      IMAGE_TAG: tag,
      AGENTHOST_IMAGE_TAG: tag,
      TARGET_GIT_REF: release.commit ?? tag,
    };
    if (previous) {
      releaseEnv.PREVIOUS_IMAGE_TAG = previous;
    }

    const cfg = {
      ...(await resolveVariablesFn({ env: releaseEnv, repoRoot })),
      TARGET_GIT_REF: release.commit ?? tag,
      PREVIOUS_IMAGE_TAG: previous || undefined,
      repoRoot,
    };
    const buildImages = steps.buildImages ?? buildImagesDefault;
    const deployStep = steps.deployStep ?? deployStepDefault;
    const verifyProvenance = steps.verifyProvenance ?? verifyProvenanceDefault;
    const verifyStep = steps.verifyStep ?? verifyStepDefault;

    log.section(`Deploying published release ${tag}`);
    const build = await buildImages.run(cfg, { exec, git, kubectl });
    const deploy = await deployStep.run(cfg, {
      run: exec.run,
      capture: exec.capture,
      log,
      repoRoot,
    });
    const provenance = await verifyProvenance.run(
      { ...cfg, VERIFY_GIT_REF: release.commit ?? tag },
      { exec, git, kubectl },
    );
    const warmPoolStatus = await waitForWarmPoolReady(cfg.NAMESPACE, { exec, log });
    const warmPoolImageCheck = warmPoolStatus.skipped
      ? { ok: true, pods: [], mismatched: [] }
      : await verifyWarmPoolImage(cfg.NAMESPACE, tag, {
          kubectl,
          log,
          exec,
          acrName: cfg.ACR_NAME,
        });
    if (!warmPoolImageCheck.ok) {
      throw new Error(
        `${warmPoolImageCheck.mismatched.length} warm-pool pod(s) do not run the ${tag} release image.`,
      );
    }
    const verify = await verifyStep.run(cfg, { exec, log });

    return {
      ok: dryRun || verify.ok,
      tag,
      version: release.version,
      commit: release.commit,
      previousTag: previous,
      build,
      deploy,
      provenance,
      warmPool: { ...warmPoolStatus, imageCheck: warmPoolImageCheck },
      verify,
      dryRun,
    };
  } finally {
    if (dryRun) {
      exec.setDryRun(false);
    }
  }
}
