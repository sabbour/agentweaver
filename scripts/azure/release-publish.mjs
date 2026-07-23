// release-publish.mjs -- Publish durable repository release identity.
//
// Version preparation belongs to scripts/changesets/prepare-release.mjs. This
// module intentionally performs no version writes or commits: it validates the
// prepared ledger on the exact origin/main SHA, then creates the annotated
// tag and GitHub Release. It performs no Azure operations.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import { DEFAULT_REPO_ROOT } from "./variables.mjs";
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
      throw new Error(`Unknown argument: ${arg}. publish-release accepts only --dry-run and --resume vX.Y.Z.`);
    }
  }

  return { resumeTag, dryRun, help };
}

export const HELP_TEXT = `publish-release -- publish a prepared Agentweaver repository release

Usage:
  node scripts/azure/cli.mjs publish-release [--dry-run]
  node scripts/azure/cli.mjs publish-release --resume vX.Y.Z [--dry-run]

Validates the prepared VERSION/package.json/package-lock.json/CHANGELOG.md
release on the exact origin/main SHA, then creates the annotated tag and
GitHub Release. It never bumps, writes, builds, or deploys.
`;

export async function isWorkingTreeClean({ cwd, capture }) {
  const unstaged = await capture("git", ["diff", "--quiet"], { cwd, allowFailure: true });
  const staged = await capture("git", ["diff", "--cached", "--quiet"], { cwd, allowFailure: true });
  const status = await capture("git", ["status", "--porcelain", "--untracked-files=all"], {
    cwd,
    allowFailure: true,
  });
  
  if (unstaged.code !== 0 || staged.code !== 0 || status.code !== 0 || status.stdout.length > 0) {
    return false;
  }

  const ignored = await capture("git", ["status", "--porcelain", "--ignored=matching"], {
    cwd,
    allowFailure: true,
  });

  if (ignored.code !== 0) {
    return false;
  }

  const unexpectedIgnored = getUnexpectedIgnoredFiles(ignored.stdout);
  return unexpectedIgnored.length === 0;
}

function getUnexpectedIgnoredFiles(stdout) {
  const allowedPatterns = [
    /(^|\/)node_modules\//,
    /(^|\/)dist\//,
    /(^|\/)bin\//,
    /(^|\/)obj\//,
    /(^|\/)TestResults\//,
    /(^|\/)\.vite\//,
    /^tests\/e2e\/playwright-report\//,
    /^tests\/e2e\/test-results\//,
    /^\.squad\//,
    /^\.idea\//,
    /^\.vscode\//,
    /^\.vs\//,
    /^\.security\//,
    /^\.worktrees\//,
    /^\.env(\.local)?$/,
    /^npm-debug\.log/,
    /^scripts\/azure\/params\..*\.json$/,
    /^scripts\/azure\/tests\/\.scratch-/,
    /^scripts\/azure\/steps\/\.rendered\//,
    /\.(user|suo|userprefs)$/,
    /\.tsbuildinfo$/
  ];

  return stdout
    .split("\n")
    .map(line => line.trim())
    .filter(line => line.startsWith("!! "))
    .map(line => line.slice(3))
    .filter(file => !allowedPatterns.some(pattern => pattern.test(file)));
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

export async function tagIsAnnotated(tag, { cwd, capture }) {
  const result = await capture("git", ["cat-file", "-t", tag], {
    cwd,
    allowFailure: true,
  });
  return result.code === 0 && result.stdout.trim() === "tag";
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
    const fetchedTags = await exec.capture("git", ["fetch", "origin", "--tags"], {
      cwd: repoRoot,
      allowFailure: true,
    });
    if (fetchedTags.code !== 0) {
      throw new Error("Could not fetch existing release tags from origin.");
    }
    const tagAlreadyExists = await tagExists(tag, { cwd: repoRoot, capture: exec.capture });
    if (resumeTag && !tagAlreadyExists) {
      throw new ReleaseResumeError(`Cannot resume ${tag}: the annotated tag does not exist.`);
    }

    if (tagAlreadyExists) {
      if (!(await tagIsAnnotated(tag, { cwd: repoRoot, capture: exec.capture }))) {
        throw new Error(`${tag} exists but is not an annotated release tag.`);
      }
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

    return {
      ok: true,
      version,
      tag,
      commit: mainSha,
      changelog: notes,
      dryRun,
    };
  } finally {
    if (dryRun) {
      exec.setDryRun(false);
    }
  }
}
