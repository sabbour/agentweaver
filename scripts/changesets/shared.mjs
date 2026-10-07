import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

export const SEMVER = /^\d+\.\d+\.\d+$/;
const RELEASE_TARGET_SEMVER = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/;
const FRAGMENT_PATTERN = /^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]+)$/;
const BUMP_PATTERN = /^\s*["']?([^"':\s]+)["']?\s*:\s*(major|minor|patch)\s*$/gm;
const RELEASE_METADATA_PATTERN = /^(VERSION|package\.json|package-lock\.json|CHANGELOG\.md|\.changeset\/)/;
const RELEASE_RELEVANT_PATTERN = /^(apps\/|packages\/|scripts\/azure\/|k8s\/)/;
// Matches test-only paths that never ship product behavior even though they live
// under a release-relevant prefix (e.g. apps/web/src/__tests__/foo.test.tsx).
// Excluding these avoids demanding a changeset for a PR that only touches tests.
const TEST_ONLY_PATTERN = /(^|\/)(__tests__|tests)\/|\.(test|spec)\.[^/]+$/;

export function readVersionMirrors(repoRoot, { readFile = fs.readFileSync } = {}) {
  const version = readFile(path.join(repoRoot, "VERSION"), "utf8").trim();
  const packageJson = JSON.parse(readFile(path.join(repoRoot, "package.json"), "utf8"));
  const lockJson = JSON.parse(readFile(path.join(repoRoot, "package-lock.json"), "utf8"));
  const packageVersion = packageJson.version;
  const lockTopLevelVersion = lockJson.version;
  const lockRootPackageVersion = lockJson.packages?.[""]?.version;

  for (const [name, value] of Object.entries({
    VERSION: version,
    "package.json": packageVersion,
    "package-lock.json.version": lockTopLevelVersion,
    'package-lock.json.packages[""].version': lockRootPackageVersion,
  })) {
    if (!SEMVER.test(value ?? "")) {
      throw new Error(`${name} contains invalid semver: '${value ?? "missing"}'`);
    }
  }

  return {
    version,
    packageVersion,
    lockTopLevelVersion,
    lockRootPackageVersion,
    lockVersion: lockRootPackageVersion,
  };
}

export function assertVersionMirrors(repoRoot, options) {
  const mirrors = readVersionMirrors(repoRoot, options);
  if (
    mirrors.version !== mirrors.packageVersion
    || mirrors.version !== mirrors.lockTopLevelVersion
    || mirrors.version !== mirrors.lockRootPackageVersion
  ) {
    throw new Error(
      `Version mirrors disagree: VERSION=${mirrors.version}, package.json=${mirrors.packageVersion}, `
      + `package-lock.json.version=${mirrors.lockTopLevelVersion}, `
      + `package-lock.json.packages[""].version=${mirrors.lockRootPackageVersion}`,
    );
  }

  return mirrors.version;
}

export function compareSemver(left, right) {
  if (!SEMVER.test(left ?? "") || !SEMVER.test(right ?? "")) {
    throw new Error(`Cannot compare invalid semver values: '${left ?? "missing"}' and '${right ?? "missing"}'`);
  }

  const leftParts = left.split(".").map(Number);
  const rightParts = right.split(".").map(Number);
  for (let index = 0; index < leftParts.length; index += 1) {
    if (leftParts[index] !== rightParts[index]) {
      return leftParts[index] < rightParts[index] ? -1 : 1;
    }
  }
  return 0;
}

function isReleaseTargetSemver(version) {
  return RELEASE_TARGET_SEMVER.test(version ?? "")
    && version.split(".").every((part) => Number.isSafeInteger(Number(part)));
}

export function parseReleaseTarget(args) {
  const targetIndex = args.indexOf("--target");
  if (args.some((arg) => arg.startsWith("--target="))) {
    throw new Error("Use --target X.Y.Z");
  }
  if (args.lastIndexOf("--target") !== targetIndex) {
    throw new Error("Use --target only once.");
  }
  if (targetIndex < 0) {
    return undefined;
  }

  const target = args[targetIndex + 1];
  if (!target || target.startsWith("--")) {
    throw new Error("Use --target X.Y.Z");
  }
  return target;
}

export function applyExplicitReleaseTarget(
  releasePlan,
  target,
  currentVersion,
  publishedVersion,
  config = { fixed: [], linked: [] },
) {
  if (target === undefined) {
    return undefined;
  }
  if (!isReleaseTargetSemver(target)) {
    throw new Error(
      `--target must be a stable SemVer version in X.Y.Z format with no leading zeroes `
      + `and safe integer components, not '${target}'.`,
    );
  }

  const release = releasePlan.releases.find((item) => item.name === "agentweaver");
  if (!release) {
    throw new Error("--target requires a pending native release plan for agentweaver.");
  }
  if (!SEMVER.test(release.newVersion ?? "")) {
    throw new Error(`Changesets calculated unsupported native release version '${release.newVersion ?? "missing"}'.`);
  }
  const relatedReleases = new Set(["agentweaver"]);
  const requiredFixedPackages = new Set();
  const releaseGroups = [
    ...(config.fixed ?? []).map((packages) => ({ kind: "fixed", packages })),
    ...(config.linked ?? []).map((packages) => ({ kind: "linked", packages })),
  ];
  const releasesByName = new Map(releasePlan.releases.map((item) => [item.name, item]));
  let expanded;
  do {
    expanded = false;
    for (const group of releaseGroups) {
      if (!group.packages.some((name) => relatedReleases.has(name))) {
        continue;
      }
      for (const name of group.packages) {
        if (group.kind === "fixed") {
          requiredFixedPackages.add(name);
        } else if (!releasesByName.has(name)) {
          continue;
        }
        if (!relatedReleases.has(name)) {
          relatedReleases.add(name);
          expanded = true;
        }
      }
    }
  } while (expanded);

  const groupedReleases = [...relatedReleases].map((name) => {
    const groupedRelease = releasesByName.get(name);
    if (!groupedRelease && requiredFixedPackages.has(name)) {
      throw new Error(`Changesets fixed release group is missing '${name}' from the native release plan.`);
    }
    return groupedRelease;
  }).filter(Boolean);

  for (const groupedRelease of groupedReleases) {
    if (!SEMVER.test(groupedRelease.newVersion ?? "")) {
      throw new Error(
        `Changesets calculated unsupported native release version for '${groupedRelease.name}': `
        + `'${groupedRelease.newVersion ?? "missing"}'.`,
      );
    }
    if (compareSemver(target, groupedRelease.oldVersion) <= 0) {
      throw new Error(
        `--target ${target} must be newer than current version ${groupedRelease.oldVersion} `
        + `for '${groupedRelease.name}'.`,
      );
    }
  }
  if (publishedVersion && compareSemver(target, publishedVersion) <= 0) {
    throw new Error(`--target ${target} must be newer than latest published v${publishedVersion}.`);
  }

  for (const groupedRelease of groupedReleases) {
    const [nativeMajor, nativeMinor] = groupedRelease.newVersion.split(".");
    const [targetMajor, targetMinor] = target.split(".");
    if (targetMajor !== nativeMajor || targetMinor !== nativeMinor) {
      throw new Error(
        `--target ${target} must stay in the native release series ${nativeMajor}.${nativeMinor}.x `
        + `for '${groupedRelease.name}'.`,
      );
    }
    if (compareSemver(target, groupedRelease.newVersion) < 0) {
      throw new Error(
        `--target ${target} is below Changesets' required native bump ${groupedRelease.newVersion} `
        + `for '${groupedRelease.name}'.`,
      );
    }
  }

  const nativeVersion = release.newVersion;
  for (const groupedRelease of groupedReleases) {
    groupedRelease.newVersion = target;
  }
  return { nativeVersion };
}

export function latestPublishedVersion(refs) {
  const versions = refs
    .split(/\r?\n/)
    .map((line) => /refs\/tags\/v(\d+\.\d+\.\d+)$/.exec(line.trim())?.[1])
    .filter(Boolean);

  return versions.sort(compareSemver).at(-1);
}

export function validatePublishedReleaseState(currentVersion, plannedVersion, publishedVersion) {
  if (!publishedVersion) {
    return;
  }

  if (compareSemver(currentVersion, publishedVersion) < 0) {
    throw new Error(
      `Published v${publishedVersion} is newer than repository version ${currentVersion}. `
      + "dev is missing the previous release preparation forward-port. "
      + "Create a short-lived branch from current dev and run "
      + "`npm run release:sync-dev -- <release-preparation-sha>` before planning another release.",
    );
  }

  if (plannedVersion && compareSemver(plannedVersion, publishedVersion) <= 0) {
    throw new Error(
      `Planned v${plannedVersion} is not newer than published v${publishedVersion}. `
      + "Do not reuse or overwrite a release tag; reconcile dev with the previous release preparation first.",
    );
  }
}

export function synchronizePackageLockVersion(
  repoRoot,
  version,
  { readFile = fs.readFileSync, writeFile = fs.writeFileSync } = {},
) {
  if (!SEMVER.test(version ?? "")) {
    throw new Error(`Cannot synchronize package-lock.json to invalid semver: '${version ?? "missing"}'`);
  }

  const lockPath = path.join(repoRoot, "package-lock.json");
  const lockJson = JSON.parse(readFile(lockPath, "utf8"));
  if (!lockJson.packages?.[""]) {
    throw new Error("package-lock.json is missing the root package entry");
  }

  lockJson.version = version;
  lockJson.packages[""].version = version;
  writeFile(lockPath, `${JSON.stringify(lockJson, null, 2)}\n`);
}

export function extractChangelogSection(content, version) {
  const heading = new RegExp(`^##\\s+(?:\\[?v?${version.replace(/\./g, "\\.")}\\]?)(?:\\s|$).*?$`, "m");
  const match = heading.exec(content);
  if (!match) {
    throw new Error(`CHANGELOG.md has no section for ${version}`);
  }

  const start = match.index;
  const next = /^##\s+/gm;
  next.lastIndex = start + match[0].length;
  const following = next.exec(content);
  return content.slice(start, following?.index ?? content.length).trim();
}

export function releaseBranchVersion(branch) {
  const match = /^release\/v(\d+\.\d+\.\d+)$/.exec(branch.trim());
  return match?.[1];
}

export function parseChangesetFragment(content, { allowMajor = false } = {}) {
  const match = FRAGMENT_PATTERN.exec(content);
  if (!match) {
    throw new Error("expected Changesets frontmatter followed by user-facing prose");
  }

  const entries = [...match[1].matchAll(BUMP_PATTERN)];
  if (entries.length !== 1 || entries[0][1] !== "agentweaver") {
    throw new Error("must contain exactly one agentweaver bump");
  }

  const [, packageName, bump] = entries[0];
  if (bump === "major" && !allowMajor) {
    throw new Error("major changesets are prohibited before the intentional 1.0 release");
  }
  if (!match[2].trim()) {
    throw new Error("release-note prose is required");
  }

  return { packageName, bump, summary: match[2].trim() };
}

export function isReleaseRelevant(paths) {
  return paths.some((file) => RELEASE_RELEVANT_PATTERN.test(file) && !TEST_ONLY_PATTERN.test(file));
}

export function isReleaseMetadataOnly(paths) {
  return paths.length > 0 && paths.every((file) => RELEASE_METADATA_PATTERN.test(file));
}

export function hasChangesetExemption(labels = [], body = "") {
  return labels.includes("changeset:not-required") && /^Changeset exemption:\s*\S.+$/mi.test(body);
}

export function validateReleasePreparation(expected, branch, calculatedVersion) {
  if (!SEMVER.test(expected ?? "")) {
    throw new Error("Use --expected X.Y.Z");
  }
  if (releaseBranchVersion(branch) !== expected) {
    throw new Error(`release:prepare must run on release/v${expected}, not ${branch || "detached HEAD"}`);
  }
  if (calculatedVersion !== expected) {
    throw new Error(`Changesets calculated ${calculatedVersion}; expected ${expected}. Re-cut the release branch instead of forcing a version.`);
  }
  if (expected !== "1.0.0" && expected.split(".")[0] !== "0") {
    throw new Error("Unexpected major-version transition.");
  }
}

export function releaseMainMergeCommand(branch) {
  return `git merge -X ours origin/main --no-ff -m "merge: resolve main into ${branch}"`;
}

export function ensureReleaseBranchHasMainAncestry(
  repoRoot,
  branch,
  { allowMerge = true, log = console.log, execFile = execFileSync } = {},
) {
  const git = (...args) => execFile("git", args, { cwd: repoRoot, encoding: "utf8" }).trim();
  const command = releaseMainMergeCommand(branch);

  try {
    git("fetch", "origin", "main:refs/remotes/origin/main");
  } catch (error) {
    throw new Error(`Failed to fetch origin/main. Git error: ${(error.stderr || error.message || "").toString().trim()}`);
  }

  try {
    git("merge-base", "--is-ancestor", "origin/main", "HEAD");
    log(`origin/main is already an ancestor of ${branch}.`);
    return { merged: false, commit: git("rev-parse", "--short", "HEAD") };
  } catch (error) {
    if (error.status !== 1) {
      throw new Error(`Failed to test origin/main ancestry for ${branch}. Git error: ${(error.stderr || error.message || "").toString().trim()}`);
    }
  }

  if (!allowMerge) {
    throw new Error(`origin/main is not an ancestor of ${branch}. Run ${command} before release preparation.`);
  }

  log(`origin/main is not an ancestor of ${branch}. release:prepare will merge it before release files change.`);
  log(`Running: ${command}`);

  try {
    git("merge", "-X", "ours", "origin/main", "--no-ff", "-m", `merge: resolve main into ${branch}`);
  } catch (error) {
    throw new Error(`Failed to merge origin/main into ${branch}. Git stopped during: ${command}. Resolve the conflict, or abort the merge and run release:prepare again.`);
  }

  const commit = git("rev-parse", "--short", "HEAD");
  log(`Created ancestry merge ${commit}.`);
  return { merged: true, commit };
}

export function isReleasePlanInputPath(file) {
  const normalized = file.replaceAll("\\", "/").toLowerCase();
  return normalized.startsWith(".changeset/")
    || /(^|\/)package\.json$/.test(normalized)
    || /(^|\/)(package-lock\.json|npm-shrinkwrap\.json|pnpm-lock\.yaml|pnpm-workspace\.yaml|yarn\.lock|\.yarnrc\.yml|bun\.lockb?|lerna\.json|rush\.json)$/.test(normalized);
}

export function validateReleasePreparationFiles(sha, files) {
  for (const required of ["VERSION", "package.json", "package-lock.json", "CHANGELOG.md"]) {
    if (!files.includes(required)) {
      throw new Error(`${sha} is not a release-preparation commit (missing ${required}).`);
    }
  }
  if (!files.some((file) => file.startsWith(".changeset/"))) {
    throw new Error(`${sha} does not consume changesets.`);
  }
}

export function validateSyncBranch(branch) {
  if (!branch || branch === "dev" || branch === "main") {
    throw new Error("Run release:sync-dev on a short-lived branch from current dev.");
  }
}

// `git status --porcelain --ignored=matching` COLLAPSES a wholly-ignored directory to a
// single trailing-slash entry (e.g. `!! node_modules/`) and never enumerates the files
// inside it. So flagging that collapsed root buys almost no protection (git already refuses
// to show its contents) while making a release impossible from any real dev checkout, where
// node_modules/, dist/, bin/, obj/ and test/harness output always exist. The meaningful
// protection is catching ignored files in UNEXPECTED locations — a stray `!! malicious.js`
// at the repo root, `!! src/malicious.js` inside a tracked source tree, or an unknown
// ignored directory that isn't a recognized dependency/build/output location.
//
// Therefore the allowlist accepts the standard dependency/build/output roots (matching the
// git-collapsed `dir/` form, at the repo root or nested under any package via an optional
// leading path prefix) plus the harness run-artifact directories that keep a tracked
// `.gitignore` and so enumerate individual files. Anything else — including a named file
// that git somehow enumerates directly inside a collapsed root — is still flagged.
export function getUnexpectedIgnoredFiles(stdout) {
  const allowedPatterns = [
    // Editor / local-tooling directories and files.
    /^\.squad\//,
    /^\.idea\//,
    /^\.vscode\//,
    /^\.vs\//,
    /^\.security\//,
    /^\.worktrees\//,
    /^\.impeccable\/$/,
    /^npm-debug\.log(?:\..*)?$/,
    /\.(user|suo|userprefs)$/,
    // Local env / dev-only config, at the repo root or under a package (e.g. apps/web/.env).
    /^(?:.+\/)?\.env(\.local)?$/,
    /^(?:.+\/)?appsettings\.development\.json$/,
    // Standard wholly-ignored dependency/build/output directory roots (git collapses these
    // to a single trailing-slash entry). Optional leading path prefix covers nested
    // packages such as packages/Agentweaver.Domain/obj/ or scripts/api-harness/node_modules/.
    /^(?:.+\/)?node_modules\/$/,
    /^(?:.+\/)?dist\/$/,
    /^(?:.+\/)?bin\/(?:debug\/|release\/)?$/,
    /^(?:.+\/)?obj\/$/,
    /^(?:.+\/)?\.vite\/$/,
    /^(?:.+\/)?testresults\/$/,
    /^(?:.+\/)?playwright-report\/$/,
    /^(?:.+\/)?test-results\/$/,
    /^(?:.+\/)?public\/specs\/$/,
    // Harness run-artifact directories keep a tracked `.gitignore`, so git enumerates the
    // ignored files inside them rather than collapsing. Allow those known output locations.
    /^scripts\/(?:api|mcp|ui)-harness\/(?:findings|transcripts|transcripts-ui|verdicts|dispatch|sessions|node_modules|\.auth|test-results|playwright-report)\//,
    // Rendered/scratch outputs from the azure deployment scripts.
    /^scripts\/azure\/params\..*\.json$/,
    /^scripts\/azure\/tests\/\.scratch-/,
    /^scripts\/azure\/steps\/\.rendered\//,
    // Demo recording artifacts: playwright-cli sessions, recording outputs, auth state.
    /^\.playwright-cli\//,
    /^recordings\//,
    /^scripts\/demo-recording\/\.auth\//,
    /^scripts\/demo-recording\/\.auth$/
  ];

  return stdout
    .split("\n")
    .map(line => line.trim())
    .filter(line => line.startsWith("!! "))
    .map(line => line.slice(3))
    .filter(file => {
      const normalizedFile = file.replaceAll("\\", "/").toLowerCase();
      return !allowedPatterns.some(pattern => pattern.test(normalizedFile));
    });
}
