// release.mjs -- Semver release workflow, composed OVER the shared engine
// (steps/20, steps/25, steps/30, steps/40) -- NOT a literal port of
// release.sh's duplicated build logic. Mirrors release.ps1's delegation
// model: this module owns only the semver bump / git tag / GitHub release
// mechanics; all image build/retag/provenance/deploy/verify behavior is
// delegated to the exact same step modules every other command (deploy,
// upgrade) uses, so there is exactly one build-vs-retag decision tree in the
// whole codebase (image-spec.mjs + steps/20-build-push-images.mjs).
//
// Faithful to release.ps1/release.sh's release MECHANICS:
//   1. Validate clean working tree (git diff --quiet, staged + unstaged).
//   2. Read + bump VERSION (major/minor/patch).
//   3. Write VERSION, commit "chore(release): bump version to vX.Y.Z".
//   4. Create annotated tag vX.Y.Z.
//   5. Push the release commit and tag to origin.
//   6. Generate a changelog from merged PRs since the previous tag (via gh).
//   7. Create the GitHub Release (via gh).
//   8. Delegate to steps/20 (build/retag+provenance-stamp), passing
//      PREVIOUS_IMAGE_TAG=<last tag> and TARGET_GIT_REF=<new tag> exactly
//      like release.ps1 does, so 20's build-vs-retag decision has the same
//      baseline release.sh's now-removed duplicated logic used.
//   9. Delegate to steps/25 (provenance verification).
//  10. Delegate to steps/30 (deploy).
//  11. Delegate to steps/40 (post-deploy verification).
//
// DRY_RUN mode (matches release.ps1's -DryRun / release.sh's DRY_RUN=true):
// every git/gh mutation is skipped (logged instead), and lib/exec.mjs's
// global dry-run mode is enabled for the delegated steps so no `az`/`kubectl`
// mutation runs either.

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

export const VALID_BUMPS = Object.freeze(["major", "minor", "patch"]);

export class InvalidBumpError extends Error {}
export class DirtyWorkingTreeError extends Error {}

/** Parses `release` subcommand argv: positional bump arg + --dry-run/-h/--help. */
export function parseArgs(argv = []) {
  let bump;
  let dryRun = false;
  let help = false;
  for (const arg of argv) {
    if (arg === "--dry-run") dryRun = true;
    else if (arg === "-h" || arg === "--help" || arg === "help") help = true;
    else if (!bump) bump = arg;
    else throw new Error(`Unknown argument: ${arg}. Run 'release --help' for usage.`);
  }
  return { bump, dryRun, help };
}

export const HELP_TEXT = `release -- Agentweaver semver release workflow

Usage:
  node scripts/azure/cli.mjs release <major|minor|patch> [--dry-run]

Bumps VERSION, tags, pushes, creates a GitHub Release, then delegates image
build/retag/provenance/deploy/verify to the shared step engine (steps/20,
25, 30, 40) -- the same engine 'deploy' and 'upgrade' use. Never duplicates
build logic locally.

Environment variables:
  DRY_RUN=true   Same as --dry-run: print actions without making changes.
`;

/** Reads and validates the VERSION file's current semver. */
export function readCurrentVersion(repoRoot, { readFile = fs.readFileSync } = {}) {
  const raw = readFile(path.join(repoRoot, "VERSION"), "utf8");
  const trimmed = raw.replace(/\s+/g, "");
  if (!/^\d+\.\d+\.\d+$/.test(trimmed)) {
    throw new Error(`VERSION file contains invalid semver: '${trimmed}'`);
  }
  return trimmed;
}

/** Bumps a semver string per `major`/`minor`/`patch`. */
export function bumpVersion(currentVersion, bump) {
  if (!VALID_BUMPS.includes(bump)) {
    throw new InvalidBumpError(`argument must be one of: ${VALID_BUMPS.join(", ")}. Run with --help for usage.`);
  }
  const [major, minor, patch] = currentVersion.split(".").map(Number);
  switch (bump) {
    case "major":
      return `${major + 1}.0.0`;
    case "minor":
      return `${major}.${minor + 1}.0`;
    case "patch":
    default:
      return `${major}.${minor}.${patch + 1}`;
  }
}

/** True if the working tree (staged + unstaged) is clean. Mirrors `git diff --quiet` + `git diff --cached --quiet`. */
export async function isWorkingTreeClean({ cwd, capture }) {
  const unstaged = await capture("git", ["diff", "--quiet"], { cwd, allowFailure: true });
  const staged = await capture("git", ["diff", "--cached", "--quiet"], { cwd, allowFailure: true });
  return unstaged.code === 0 && staged.code === 0;
}

/** Resolves the previous release tag (`git describe --tags --abbrev=0`), or '' if none exists. */
export async function previousTag({ cwd, capture }) {
  const result = await capture("git", ["describe", "--tags", "--abbrev=0"], { cwd, allowFailure: true });
  return result.code === 0 ? result.stdout.trim() : "";
}

/** Resolves the ISO-8601 author date of a tag, for the changelog baseline. */
export async function tagDate(tag, { cwd, capture }) {
  if (!tag) return "1970-01-01T00:00:00Z";
  const result = await capture("git", ["log", "-1", "--format=%aI", tag], { cwd, allowFailure: true });
  if (result.code !== 0 || !result.stdout.trim()) {
    throw new Error(`Could not determine date for previous tag ${tag}`);
  }
  return result.stdout.trim();
}

/** Generates a changelog from merged PRs since `sinceDate`, via `gh pr list`. Never throws -- falls back to a placeholder line. */
export async function generateChangelog(sinceDate, previousTagLabel, { capture, repo = "sabbour/agentweaver" } = {}) {
  const result = await capture(
    "gh",
    [
      "pr",
      "list",
      "--repo",
      repo,
      "--state",
      "merged",
      "--search",
      `merged:>${sinceDate}`,
      "--json",
      "number,title,mergedAt",
      "--jq",
      '.[] | "- \\(.title) (#\\(.number))"',
    ],
    { allowFailure: true },
  );
  const lines = result.code === 0 ? result.stdout.split("\n").map((l) => l.trim()).filter(Boolean) : [];
  if (lines.length === 0) {
    return `No pull requests found since ${previousTagLabel || "the beginning of history"}.`;
  }
  return lines.join("\n");
}

/**
 * Main entry point for the `release` subcommand: bump/tag/GitHub-release,
 * then delegate to the shared build/provenance/deploy/verify step engine.
 *
 * @param {object} [opts]
 * @param {string[]} [opts.argv]
 * @param {string} [opts.repoRoot]
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {typeof gitDefault} [opts.git]
 * @param {typeof resolveVariables} [opts.resolveVariables]
 * @param {object} [opts.steps] Injectable step modules (buildImages, verifyProvenance, deployStep, verifyStep) for testing.
 * @param {typeof fs.readFileSync} [opts.readFile]
 * @param {typeof fs.writeFileSync} [opts.writeFile]
 */
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
    writeFile = fs.writeFileSync,
  } = opts;

  const buildImages = steps.buildImages ?? buildImagesDefault;
  const verifyProvenance = steps.verifyProvenance ?? verifyProvenanceDefault;
  const deployStep = steps.deployStep ?? deployStepDefault;
  const verifyStep = steps.verifyStep ?? verifyStepDefault;

  const { bump, dryRun: dryRunFlag, help } = parseArgs(argv);
  const dryRun = dryRunFlag || process.env.DRY_RUN === "true";

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }
  if (!VALID_BUMPS.includes(bump)) {
    throw new InvalidBumpError(`argument must be one of: ${VALID_BUMPS.join(", ")}. Run with --help for usage.`);
  }

  const runOrLog = async (description, fn) => {
    if (dryRun) {
      log.info(`  [dry-run] ${description}`);
      return undefined;
    }
    return fn();
  };

  log.section("Agentweaver release");
  log.info("==> Checking working tree...");
  const clean = await isWorkingTreeClean({ cwd: repoRoot, capture: exec.capture });
  if (!clean) {
    throw new DirtyWorkingTreeError("Working tree has uncommitted changes. Commit or stash first.");
  }

  const currentVersion = readCurrentVersion(repoRoot, { readFile });
  const newVersion = bumpVersion(currentVersion, bump);
  const newTag = `v${newVersion}`;
  log.info(`==> Bumping version: ${currentVersion} -> ${newVersion} (${bump})`);

  const lastTag = await previousTag({ cwd: repoRoot, capture: exec.capture });
  const lastTagDate = await tagDate(lastTag, { cwd: repoRoot, capture: exec.capture });
  if (lastTag) log.info(`  Last tag: ${lastTag}`);
  else log.info("  (no previous tag found; treating first commit as baseline)");

  const versionFilePath = path.join(repoRoot, "VERSION");
  log.info("==> Writing VERSION file...");
  await runOrLog(`Write ${newVersion} to VERSION`, async () => writeFile(versionFilePath, `${newVersion}\n`));

  log.info("==> Committing version bump...");
  await runOrLog("git add VERSION", () => exec.run("git", ["add", versionFilePath], { cwd: repoRoot }));
  await runOrLog(`git commit -m "chore(release): bump version to ${newTag}"`, () =>
    exec.run("git", ["commit", "-m", `chore(release): bump version to ${newTag}`], { cwd: repoRoot }),
  );

  log.info(`==> Creating annotated tag ${newTag}...`);
  await runOrLog(`git tag -a ${newTag}`, () => exec.run("git", ["tag", "-a", newTag, "-m", `Release ${newTag}`], { cwd: repoRoot }));

  log.info("==> Pushing release commit and tag to origin...");
  await runOrLog("git push origin HEAD", () => exec.run("git", ["push", "origin", "HEAD"], { cwd: repoRoot }));
  await runOrLog(`git push origin ${newTag}`, () => exec.run("git", ["push", "origin", newTag], { cwd: repoRoot }));

  log.info(`==> Generating changelog from merged PRs since ${lastTagDate}...`);
  const changelog = dryRun ? "(dry-run: changelog not generated)" : await generateChangelog(lastTagDate, lastTag, { capture: exec.capture });
  log.info(changelog);

  log.info(`==> Creating GitHub release ${newTag}...`);
  await runOrLog(`gh release create ${newTag}`, () =>
    exec.run("gh", ["release", "create", newTag, "--title", newTag, "--notes", changelog]),
  );

  // --- Delegate to the shared step engine (never duplicate build logic) ----
  log.info("");
  log.info(`==> Processing images for ${newTag} (previous: ${lastTag || "none"})...`);
  const releaseEnv = {
    ...process.env,
    IMAGE_TAG: newTag,
    AGENTHOST_IMAGE_TAG: newTag,
    TARGET_GIT_REF: newTag,
  };
  if (lastTag) releaseEnv.PREVIOUS_IMAGE_TAG = lastTag;
  else delete releaseEnv.PREVIOUS_IMAGE_TAG;

  if (dryRun) exec.setDryRun(true);
  try {
    const cfg = await resolveVariablesFn({ env: releaseEnv, repoRoot });

    log.info("Step 1/4: Building + pushing images...");
    const buildResult = await buildImages.run(cfg, { exec, git });

    log.info("");
    log.info("Step 2/4: Verifying image provenance...");
    const provenanceResult = await verifyProvenance.run({ ...cfg, VERIFY_GIT_REF: newTag }, { exec, git });

    log.info("");
    log.info("Step 3/4: Deploying the release tag...");
    const deployResult = await deployStep.run(cfg, { run: exec.run, capture: exec.capture, log, repoRoot });

    log.info("");
    log.info("Step 4/4: Verifying deployment...");
    const verifyResult = await verifyStep.run(cfg, { exec, log });

    log.info("");
    log.section(`RELEASE ${newTag} COMPLETE`);
    log.field("GitHub Release", `https://github.com/sabbour/agentweaver/releases/tag/${newTag}`);
    log.field("Image tag", newTag);
    log.field("Verification", `${verifyResult.pass}/${verifyResult.pass + verifyResult.fail} checks passed`);

    return {
      ok: dryRun || verifyResult.ok,
      version: newVersion,
      tag: newTag,
      previousTag: lastTag,
      changelog,
      build: buildResult,
      provenance: provenanceResult,
      deploy: deployResult,
      verify: verifyResult,
      dryRun,
    };
  } finally {
    if (dryRun) exec.setDryRun(false);
  }
}
