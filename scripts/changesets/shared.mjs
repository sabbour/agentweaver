import fs from "node:fs";
import path from "node:path";

export const SEMVER = /^\d+\.\d+\.\d+$/;
const FRAGMENT_PATTERN = /^---\s*\n([\s\S]*?)\n---\s*\n([\s\S]+)$/;
const BUMP_PATTERN = /^\s*["']?([^"':\s]+)["']?\s*:\s*(major|minor|patch)\s*$/gm;
const RELEASE_METADATA_PATTERN = /^(VERSION|package\.json|package-lock\.json|CHANGELOG\.md|\.changeset\/)/;
const RELEASE_RELEVANT_PATTERN = /^(apps\/|packages\/|scripts\/azure\/|k8s\/)/;

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
  const heading = new RegExp(`^##\\s+(?:\\[?${version.replace(/\./g, "\\.")}\\]?)(?:\\s|$).*?$`, "m");
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
  return paths.some((file) => RELEASE_RELEVANT_PATTERN.test(file));
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
