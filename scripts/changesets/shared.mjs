import fs from "node:fs";
import path from "node:path";

export const SEMVER = /^\d+\.\d+\.\d+$/;

export function readVersionMirrors(repoRoot, { readFile = fs.readFileSync } = {}) {
  const version = readFile(path.join(repoRoot, "VERSION"), "utf8").trim();
  const packageJson = JSON.parse(readFile(path.join(repoRoot, "package.json"), "utf8"));
  const lockJson = JSON.parse(readFile(path.join(repoRoot, "package-lock.json"), "utf8"));
  const packageVersion = packageJson.version;
  const lockVersion = lockJson.packages?.[""]?.version;
  for (const [name, value] of Object.entries({ VERSION: version, "package.json": packageVersion, "package-lock.json": lockVersion })) {
    if (!SEMVER.test(value ?? "")) throw new Error(`${name} contains invalid semver: '${value ?? "missing"}'`);
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
  if (!match) throw new Error(`CHANGELOG.md has no section for ${version}`);
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
