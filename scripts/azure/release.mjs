// release.mjs -- Publish a prepared stable release through the shared engine.
//
// Version preparation belongs to scripts/changesets/prepare-release.mjs. This
// module intentionally performs no version writes or commits: it validates the
// prepared ledger on the exact origin/main SHA, then tags, releases, deploys,
// and verifies that immutable source.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import * as gitDefault from "./lib/git.mjs";
import { resolveVariables, DEFAULT_REPO_ROOT } from "./variables.mjs";
import * as buildImagesDefault from "./steps/20-build-push-images.mjs";
import * as verifyProvenanceDefault from "./steps/25-verify-image-provenance.mjs";
import * as deployStepDefault from "./steps/30-deploy.mjs";
import * as verifyStepDefault from "./steps/40-verify.mjs";
import { assertVersionMirrors, extractChangelogSection } from "../changesets/shared.mjs";

export const RELEASE_TAG_PATTERN = /^v\d+\.\d+\.\d+$/;

export function isReleaseTag(tag) {
  return RELEASE_TAG_PATTERN.test((tag ?? "").trim());
}

export class DirtyWorkingTreeError extends Error {}
export class ReleaseResumeError extends Error {}

export function parseArgs(argv = []) {
  let resumeTag;
  let dryRun = false;
  let help = false;

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--dry-run") {
      dryRun = true;
    } else if (["-h", "--help", "help"].includes(arg)) {
      help = true;
    } else if (arg === "--resume") {
      resumeTag = argv[index + 1];
      if (!resumeTag || resumeTag.startsWith("-")) {
        throw new Error("Missing release tag after --resume.");
      }
      index += 1;
    } else {
      throw new Error(`Unknown argument: ${arg}. release accepts only --dry-run and --resume vX.Y.Z.`);
    }
  }

  return { resumeTag, dryRun, help };
}

export const HELP_TEXT = `release -- publish a prepared Agentweaver release

Usage:
  node scripts/azure/cli.mjs release [--dry-run]
  node scripts/azure/cli.mjs release --resume vX.Y.Z [--dry-run]

Validates the prepared VERSION/package.json/package-lock.json/CHANGELOG.md release on the exact origin/main SHA, then tags, publishes, deploys, and verifies it. It never bumps or writes a version.
`;

export async function isWorkingTreeClean({ cwd, capture }) {
  const unstaged = await capture("git", ["diff", "--quiet"], { cwd, allowFailure: true });
  const staged = await capture("git", ["diff", "--cached", "--quiet"], { cwd, allowFailure: true });
  return unstaged.code === 0 && staged.code === 0;
}

export async function previousTag({ cwd, capture }) {
  const result = await capture("git", ["tag", "--list", "--sort=-v:refname"], {
    cwd,
    allowFailure: true,
  });
  if (result.code !== 0) {
    return "";
  }

  return result.stdout.split("\n").map((item) => item.trim()).find(isReleaseTag) ?? "";
}

export async function previousTagBefore(tag, { cwd, capture }) {
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

export async function validateMainSha({ cwd, capture }) {
  const fetched = await capture("git", ["fetch", "origin", "main"], { cwd, allowFailure: true });
  if (fetched.code !== 0) {
    throw new Error("Could not fetch origin/main.");
  }

  const head = await capture("git", ["rev-parse", "HEAD"], { cwd, allowFailure: true });
  const main = await capture("git", ["rev-parse", "origin/main"], { cwd, allowFailure: true });
  if (head.code !== 0 || main.code !== 0 || head.stdout !== main.stdout) {
    throw new Error("Release must run from the exact fetched origin/main SHA.");
  }

  return head.stdout;
}

export async function tagExists(tag, { cwd, capture }) {
  const result = await capture("git", ["rev-parse", "--verify", `${tag}^{commit}`], {
    cwd,
    allowFailure: true,
  });
  return result.code === 0;
}

export async function releaseExists(tag, { cwd, capture, repo = "sabbour/agentweaver" }) {
  const result = await capture("gh", ["release", "view", tag, "--repo", repo], {
    cwd,
    allowFailure: true,
  });
  return result.code === 0;
}

export async function run(opts = {}) {
  const {
    argv = [],
    repoRoot = DEFAULT_REPO_ROOT,
    exec = execDefault,
    log = logDefault,
    git = gitDefault,
    resolveVariables: resolveVariablesFn = resolveVariables,
    steps = {},
    readFile = fs.readFileSync,
  } = opts;
  const { resumeTag, dryRun: dryRunFlag, help } = parseArgs(argv);
  const dryRun = dryRunFlag || process.env.DRY_RUN === "true";

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  if (dryRun) {
    exec.setDryRun(true);
  }

  try {
    const clean = await isWorkingTreeClean({ cwd: repoRoot, capture: exec.capture });
    if (!clean) {
      throw new DirtyWorkingTreeError("Working tree has uncommitted changes. Commit or stash first.");
    }

    const version = assertVersionMirrors(repoRoot, { readFile });
    const tag = `v${version}`;
    if (resumeTag && resumeTag !== tag) {
      throw new ReleaseResumeError(`Cannot resume ${resumeTag}: prepared version is ${tag}.`);
    }

    const changelog = readFile(path.join(repoRoot, "CHANGELOG.md"), "utf8");
    const notes = extractChangelogSection(changelog, version);
    const mainSha = await validateMainSha({ cwd: repoRoot, capture: exec.capture });
    const tagAlreadyExists = await tagExists(tag, { cwd: repoRoot, capture: exec.capture });
    if (resumeTag && !tagAlreadyExists) {
      throw new ReleaseResumeError(`Cannot resume ${tag}: the annotated tag does not exist.`);
    }

    if (tagAlreadyExists) {
      const tagSha = await exec.capture("git", ["rev-list", "-n", "1", tag], {
        cwd: repoRoot,
        allowFailure: true,
      });
      if (tagSha.code !== 0 || tagSha.stdout !== mainSha) {
        throw new Error(`${tag} does not point at the exact origin/main SHA.`);
      }
    }

    const runOrLog = async (description, action) => {
      if (dryRun) {
        log.info(`  [dry-run] ${description}`);
        return;
      }
      await action();
    };

    if (!tagAlreadyExists) {
      await runOrLog(`Create annotated tag ${tag}`, () => {
        return exec.run("git", ["tag", "-a", tag, "-m", `Release ${tag}`], { cwd: repoRoot });
      });
      await runOrLog(`Push ${tag}`, () => exec.run("git", ["push", "origin", tag], { cwd: repoRoot }));
    }

    const githubReleaseExists = tagAlreadyExists && await releaseExists(tag, {
      cwd: repoRoot,
      capture: exec.capture,
    });
    if (!githubReleaseExists) {
      await runOrLog(`Create GitHub Release ${tag}`, () => {
        return exec.run("gh", ["release", "create", tag, "--title", tag, "--notes", notes], { cwd: repoRoot });
      });
    }

    const previous = tagAlreadyExists
      ? await previousTagBefore(tag, { cwd: repoRoot, capture: exec.capture })
      : await previousTag({ cwd: repoRoot, capture: exec.capture });
    const releaseEnv = {
      ...process.env,
      IMAGE_TAG: tag,
      AGENTHOST_IMAGE_TAG: tag,
      TARGET_GIT_REF: tag,
    };
    if (previous) {
      releaseEnv.PREVIOUS_IMAGE_TAG = previous;
    }

    const cfg = await resolveVariablesFn({ env: releaseEnv, repoRoot });
    const buildImages = steps.buildImages ?? buildImagesDefault;
    const verifyProvenance = steps.verifyProvenance ?? verifyProvenanceDefault;
    const deployStep = steps.deployStep ?? deployStepDefault;
    const verifyStep = steps.verifyStep ?? verifyStepDefault;

    const build = await buildImages.run(cfg, { exec, git });
    const provenance = await verifyProvenance.run({ ...cfg, VERIFY_GIT_REF: tag }, { exec, git });
    const deploy = await deployStep.run(cfg, { run: exec.run, capture: exec.capture, log, repoRoot });
    const verify = await verifyStep.run(cfg, { exec, log });

    return {
      ok: dryRun || verify.ok,
      version,
      tag,
      previousTag: previous,
      changelog: notes,
      build,
      provenance,
      deploy,
      verify,
      dryRun,
    };
  } finally {
    if (dryRun) {
      exec.setDryRun(false);
    }
  }
}
