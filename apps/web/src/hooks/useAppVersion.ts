import { useEffect, useState } from 'react';

interface VersionResponse {
  version?: string | null;
  gitSha?: string | null;
  isRelease?: boolean;
}

/** Build identity reported by the web host's `/api/version` route. */
export interface AppVersionInfo {
  /** Display text for the version badge, for example "1.5.0" or "1.5.0-dev+a1c11f1". */
  display: string;
  /** Base semantic version the running web image was built as. */
  version: string;
  /** Commit the running web image was built from, or null when the host does not know it. */
  gitSha: string | null;
  /** True when the image was built for a published version rather than a local build. */
  isRelease: boolean;
}

/** Builds the badge text: the plain version for releases, otherwise version plus commit. */
function displayVersion(version: string, gitSha: string | null, isRelease: boolean): string {
  if (isRelease || !gitSha) return version;
  // Keep an existing prerelease suffix instead of adding a second "-dev".
  const base = version.includes('-') ? version : `${version}-dev`;
  return `${base}+${gitSha}`;
}

/**
 * Reads the running build's version once. Returns null until the read succeeds,
 * and stays null when the host has no version route or reports no version, so
 * callers can choose their own fallback.
 */
export function useAppVersionInfo(): AppVersionInfo | null {
  const [info, setInfo] = useState<AppVersionInfo | null>(null);

  useEffect(() => {
    let active = true;
    fetch('/api/version', { cache: 'no-store' })
      .then(r => r.ok ? r.json() : null)
      .then((data: VersionResponse | null) => {
        // A missing route can fall through to the HTML app shell; that fails the JSON
        // parse above and is ignored, the same as an empty version.
        if (!active || !data?.version) return;
        const gitSha = data.gitSha || null;
        const isRelease = data.isRelease === true;
        setInfo({ display: displayVersion(data.version, gitSha, isRelease), version: data.version, gitSha, isRelease });
      })
      .catch(() => {}); // silent fallback
    return () => { active = false; };
  }, []);

  return info;
}

/**
 * Returns a display-ready version string for the Alpha badge:
 * - a real release build shows the plain semver, e.g. "0.9.70"
 * - a commit-tagged build shows the semver line it's based on plus the SHA,
 *   e.g. "0.9.71-dev+a1c11f1"
 * The string is empty until the version is known.
 */
export function useAppVersion(): string {
  return useAppVersionInfo()?.display ?? '';
}
