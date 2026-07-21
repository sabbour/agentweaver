import { useEffect, useState } from 'react';

interface VersionResponse {
  version?: string;
  gitSha?: string | null;
  isRelease?: boolean;
}

/**
 * Returns a display-ready version string for the Alpha badge:
 * - a real release build (e.g. `npm run azure:release`) shows the plain semver, e.g. "0.9.70"
 * - a git-SHA-tagged build (`azure:upgrade`/`azure:deploy-from-local`) shows the semver line
 *   it's based on plus the SHA, e.g. "0.9.71-dev+a1c11f1"
 */
export function useAppVersion(): string {
  const [display, setDisplay] = useState<string>('');

  useEffect(() => {
    fetch('/api/version')
      .then(r => r.ok ? r.json() : null)
      .then((data: VersionResponse | null) => {
        if (!data?.version) return;
        if (data.isRelease || !data.gitSha) {
          setDisplay(data.version);
          return;
        }
        const base = data.version.includes('-') ? data.version : `${data.version}-dev`;
        setDisplay(`${base}+${data.gitSha}`);
      })
      .catch(() => {}); // silent fallback
  }, []);

  return display;
}
