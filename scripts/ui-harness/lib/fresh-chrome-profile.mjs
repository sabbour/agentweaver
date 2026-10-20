import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';

/** Resolve the installed Google Chrome executable without reading a browser profile. */
export function resolveGoogleChromeExecutable({
  localAppData = process.env.LOCALAPPDATA,
  programFiles = process.env.ProgramFiles,
  programFilesX86 = process.env['ProgramFiles(x86)'],
  exists = existsSync,
} = {}) {
  const candidates = [
    localAppData && path.join(localAppData, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    programFiles && path.join(programFiles, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    programFilesX86 && path.join(programFilesX86, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  ].filter(Boolean);
  const executablePath = candidates.find((candidate) => exists(candidate));
  if (!executablePath) {
    throw new Error(
      'Installed Google Chrome (chrome.exe) was not found. Install or restore Google Chrome; '
      + 'the P1 sign-in flow does not fall back to Playwright Chromium.',
    );
  }
  return path.resolve(executablePath);
}

/** Configure headed Google Chrome to use the caller's fresh, isolated user-data directory. */
export function buildFreshChromeLaunchOptions(
  userDataDir,
  executablePath = resolveGoogleChromeExecutable(),
) {
  return {
    channel: 'chrome',
    executablePath,
    headless: false,
    viewport: null,
    args: ['--no-first-run', '--no-default-browser-check'],
    userDataDir: path.resolve(userDataDir),
  };
}

function assertIgnored(authRoot) {
  const probe = path.join(path.resolve(authRoot), '.p1-profile-probe');
  try {
    execFileSync('git', ['check-ignore', '--no-index', '-q', '--', probe], {
      stdio: 'ignore',
      windowsHide: true,
    });
  } catch {
    throw new Error('Refusing to create a disposable Chrome profile outside an ignored directory.');
  }
}

/**
 * Make a new empty browser profile under an ignored directory. No pre-existing
 * Chrome profile, cookie store, session, or credential cache is read or copied.
 */
export async function createFreshChromeUserDataDir({
  authRoot,
  prefix = 'p1-signin-',
  makeTempDir = fs.mkdtemp,
} = {}) {
  if (typeof authRoot !== 'string' || !authRoot.trim()) {
    throw new Error('An ignored directory for the disposable Chrome profile is required.');
  }
  assertIgnored(authRoot);
  await fs.mkdir(authRoot, { recursive: true, mode: 0o700 });
  return makeTempDir(path.join(path.resolve(authRoot), prefix));
}
