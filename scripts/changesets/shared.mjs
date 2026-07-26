import fs from "node:fs";
import path from "node:path";

export const SEMVER = /^\d+\.\d+\.\d+$/;
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
  const lockVersion = lockJson.packages?.[""]?.version;

  for (const [name, value] of Object.entries({
    VERSION: version,
    "package.json": packageVersion,
    "package-lock.json": lockVersion,
  })) {
    if (!SEMVER.test(value ?? "")) {
      throw new Error(`${name} contains invalid semver: '${value ?? "missing"}'`);
    }
  }

  return { version, packageVersion, lockVersion };
}

export function assertVersionMirrors(repoRoot, options) {
  const mirrors = readVersionMirrors(repoRoot, options);
  if (mirrors.version !== mirrors.packageVersion || mirrors.version !== mirrors.lockVersion) {
    throw new Error(`Version mirrors disagree: VERSION=${mirrors.version}, package.json=${mirrors.packageVersion}, package-lock.json=${mirrors.lockVersion}`);
  }

  return mirrors.version;
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
    /^npm-debug\.log/,
    /\.(user|suo|userprefs)$/,
    // Local env / dev-only config, at the repo root or under a package (e.g. apps/web/.env).
    /^(?:.+\/)?\.env(\.local)?$/,
    /^(?:.+\/)?appsettings\.Development\.json$/,
    // Standard wholly-ignored dependency/build/output directory roots (git collapses these
    // to a single trailing-slash entry). Optional leading path prefix covers nested
    // packages such as packages/Agentweaver.Domain/obj/ or scripts/api-harness/node_modules/.
    /^(?:.+\/)?node_modules\/$/,
    /^(?:.+\/)?dist\/$/,
    /^(?:.+\/)?bin\/(?:Debug\/|Release\/)?$/,
    /^(?:.+\/)?obj\/$/,
    /^(?:.+\/)?\.vite\/$/,
    /^(?:.+\/)?[Tt]est[Rr]esults\/$/,
    /^(?:.+\/)?playwright-report\/$/,
    /^(?:.+\/)?test-results\/$/,
    /^(?:.+\/)?public\/specs\/$/,
    // Harness run-artifact directories keep a tracked `.gitignore`, so git enumerates the
    // ignored files inside them rather than collapsing. Allow those known output locations.
    /^scripts\/(?:api|mcp|ui)-harness\/(?:findings|transcripts|transcripts-ui|verdicts|dispatch|sessions|node_modules|\.auth|test-results|playwright-report)\//,
    // Rendered/scratch outputs from the azure deployment scripts.
    /^scripts\/azure\/params\..*\.json$/,
    /^scripts\/azure\/tests\/\.scratch-/,
    /^scripts\/azure\/steps\/\.rendered\//
  ];

  return stdout
    .split("\n")
    .map(line => line.trim())
    .filter(line => line.startsWith("!! "))
    .map(line => line.slice(3))
    .filter(file => !allowedPatterns.some(pattern => pattern.test(file)));
}
