/**
 * Web app version for the v1 shell and admin pages. The web host reports the
 * version and commit baked into the running image. When it cannot (for example
 * the Vite dev server, which has no `/api/version` route), the version from the
 * web package is shown instead, with no commit.
 */
import { Text } from '@fluentui/react-components';
import { useAppVersionInfo } from '../hooks/useAppVersion';
import packageInfo from '../../package.json';

export interface WebAppVersion {
  /** Badge text, for example "1.5.0" or "1.5.0-dev+a1c11f1". */
  display: string;
  /** Full commit of the running build, or null when it is not known. */
  gitSha: string | null;
}

/** Reads the running web build's version, falling back to the bundled package version. */
// eslint-disable-next-line react-refresh/only-export-components
export function useWebAppVersion(): WebAppVersion {
  const info = useAppVersionInfo();
  // Until the host answers, and when it never does, the bundled package version is
  // the best available fact: it is the version this browser code was built as.
  if (!info) return { display: packageInfo.version, gitSha: null };
  return { display: info.display, gitSha: info.gitSha };
}

/**
 * One line naming the running web app version and, when known, its build commit.
 * Admin pages show it next to the per-component versions so the whole build is
 * visible in one place.
 */
export function WebAppVersionLine({ version }: { version: WebAppVersion }) {
  return (
    <Text className="v1-muted" data-testid="web-app-version">
      Web app version v{version.display}
      {/* Release badges hide the commit, so show the short commit here for support.
          Local builds already carry it after "+", so it is not repeated. */}
      {version.gitSha && !version.display.includes('+') && <> · build <code title={version.gitSha}>{version.gitSha.slice(0, 7)}</code></>}
    </Text>
  );
}
