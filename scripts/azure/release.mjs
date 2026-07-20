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

// Shared "is this a real, final release tag" predicate. A release boundary is
// an annotated `vX.Y.Z` tag with NO suffix -- lightweight tags and prerelease
// tags like `v0.9.6-rc1` must NOT count as release boundaries, or they would
// pollute the changelog / release-note range. The identical regex string
// (`^v\d+\.\d+\.\d+$`) is used by scripts/gen-changelog.py so both tools agree
// on what counts as a release.
export const RELEASE_TAG_PATTERN = /^v\d+\.\d+\.\d+$/;

/** True only for a final `vX.Y.Z` release tag (no prerelease/build suffix). */
export function isReleaseTag(tag) {
  return RELEASE_TAG_PATTERN.test((tag ?? "").trim());
}

export class InvalidBumpError extends Error {}
export class DirtyWorkingTreeError extends Error {}
export class ReleaseResumeError extends Error {}

/** Parses `release` subcommand argv: bump, --resume <tag>, --dry-run, or help. */
export function parseArgs(argv = []) {
  let bump;
  let resumeTag;
  let dryRun = false;
  let help = false;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--dry-run") dryRun = true;
    else if (arg === "-h" || arg === "--help" || arg === "help") help = true;
    else if (arg === "--resume") {
      resumeTag = argv[index + 1];
      if (!resumeTag || resumeTag.startsWith("-")) {
        throw new Error("Missing release tag after --resume. Example: release --resume v1.2.3");
      }
      index += 1;
    }
    else if (!bump) bump = arg;
    else throw new Error(`Unknown argument: ${arg}. Run 'release --help' for usage.`);
  }
  if (bump && resumeTag) {
    throw new Error("Specify either a version bump or --resume <tag>, not both.");
  }
  return { bump, resumeTag, dryRun, help };
}

export const HELP_TEXT = `release -- Agentweaver semver release workflow

Usage:
  node scripts/azure/cli.mjs release <major|minor|patch> [--dry-run]
  node scripts/azure/cli.mjs release --resume vX.Y.Z [--dry-run]

Bumps VERSION, tags, pushes, creates a GitHub Release, then delegates image
build/retag/provenance/deploy/verify to the shared step engine (steps/20,
25, 30, 40) -- the same engine 'deploy' and 'upgrade' use. Never duplicates
build logic locally. --resume performs only the shared image/deploy/verify
steps after confirming the tag and GitHub Release already exist and match VERSION.

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

/** Resolves the previous release tag: the most recent final `vX.Y.Z` tag (prerelease/lightweight tags excluded), or '' if none exists. */
export async function previousTag({ cwd, capture }) {
  // Enumerate tags newest-first by semver and return the most recent FINAL
  // release tag. Using `git tag --list` (not `git describe --tags`) lets us
  // skip prerelease/lightweight tags (e.g. v0.9.6-rc1) that must not define a
  // release boundary -- see isReleaseTag / RELEASE_TAG_PATTERN.
  const result = await capture("git", ["tag", "--list", "--sort=-v:refname"], { cwd, allowFailure: true });
  if (result.code !== 0) return "";
  for (const line of result.stdout.split("\n")) {
    const tag = line.trim();
    if (isReleaseTag(tag)) return tag;
  }
  return "";
}

/** Resolves the final release tag immediately preceding `tag` in semver order. */
export async function previousTagBefore(tag, { cwd, capture }) {
  const result = await capture("git", ["tag", "--list", "--sort=-v:refname"], { cwd, allowFailure: true });
  if (result.code !== 0) return "";
  let foundTarget = false;
  for (const line of result.stdout.split("\n")) {
    const candidate = line.trim();
    if (candidate === tag) {
      foundTarget = true;
      continue;
    }
    if (foundTarget && isReleaseTag(candidate)) return candidate;
  }
  return "";
}

/** Validates that a failed release can safely resume its non-git steps. */
export async function validateResumeTarget(tag, version, { cwd, capture, repo = "sabbour/agentweaver" }) {
  if (!isReleaseTag(tag)) {
    throw new ReleaseResumeError(`Invalid resume tag '${tag}'. Expected a final release tag such as v1.2.3.`);
  }
  if (tag !== `v${version}`) {
    throw new ReleaseResumeError(
      `Cannot resume ${tag}: VERSION is ${version}, which does not match the tag. Check out the release commit or restore VERSION before resuming.`,
    );
  }
  const tagResult = await capture("git", ["rev-parse", "--verify", `${tag}^{commit}`], { cwd, allowFailure: true });
  if (tagResult.code !== 0) {
    throw new ReleaseResumeError(`Cannot resume ${tag}: the tag does not exist locally. Fetch tags and try again.`);
  }
  const releaseResult = await capture("gh", ["release", "view", tag, "--repo", repo], { cwd, allowFailure: true });
  if (releaseResult.code !== 0) {
    throw new ReleaseResumeError(
      `Cannot resume ${tag}: its GitHub Release does not exist. Run the normal release command only if no release was created.`,
    );
  }
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
 * Main entry point for the `release` subcommand: bump/tag/GitHub-release, or
 * resume an already-created release, then delegate to the shared step engine.
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

  const { bump, resumeTag, dryRun: dryRunFlag, help } = parseArgs(argv);
  const dryRun = dryRunFlag || process.env.DRY_RUN === "true";

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }
  if (!resumeTag && !VALID_BUMPS.includes(bump)) {
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
  const newVersion = resumeTag ? currentVersion : bumpVersion(currentVersion, bump);
  const newTag = resumeTag ?? `v${newVersion}`;
  if (resumeTag) {
    log.info(`==> Resuming release ${newTag}; VERSION is ${currentVersion}.`);
    await validateResumeTarget(newTag, currentVersion, { cwd: repoRoot, capture: exec.capture });
  } else {
    log.info(`==> Bumping version: ${currentVersion} -> ${newVersion} (${bump})`);
  }

  const lastTag = resumeTag
    ? await previousTagBefore(newTag, { cwd: repoRoot, capture: exec.capture })
    : await previousTag({ cwd: repoRoot, capture: exec.capture });
  const lastTagDate = await tagDate(lastTag, { cwd: repoRoot, capture: exec.capture });
  if (lastTag) log.info(`  Last tag: ${lastTag}`);
  else log.info("  (no previous tag found; treating first commit as baseline)");

  let changelog;
  if (!resumeTag) {
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
    changelog = dryRun ? "(dry-run: changelog not generated)" : await generateChangelog(lastTagDate, lastTag, { capture: exec.capture });
    log.info(changelog);

    log.info(`==> Creating GitHub release ${newTag}...`);
    await runOrLog(`gh release create ${newTag}`, () =>
      exec.run("gh", ["release", "create", newTag, "--title", newTag, "--notes", changelog]),
    );
  } else {
    log.info(`==> Tag and GitHub Release ${newTag} already exist; skipping release creation.`);
  }

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
