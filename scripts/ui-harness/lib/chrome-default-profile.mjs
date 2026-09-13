import { execFileSync } from 'node:child_process';
import { existsSync, openSync, closeSync, statSync } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';

export const CHROME_DEFAULT_PROFILE_DIRECTORY = 'Default';

const EXCLUDED_DIRECTORIES = new Set([
  'BrowserMetrics', 'Cache', 'Code Cache', 'Crashpad', 'DawnCache', 'GPUCache',
  'GrShaderCache', 'ShaderCache', 'component_crx_cache', 'extensions_crx_cache',
]);
const EXCLUDED_FILES = new Set(['LOCK', 'SingletonCookie', 'SingletonLock', 'SingletonSocket']);

export function resolveChromeDefaultProfile(localAppData = process.env.LOCALAPPDATA) {
  if (!localAppData) throw new Error('LOCALAPPDATA is not set. Google Chrome Default profile cannot be located.');
  const userDataDir = path.resolve(localAppData, 'Google', 'Chrome', 'User Data');
  return {
    userDataDir,
    profileDirectory: CHROME_DEFAULT_PROFILE_DIRECTORY,
    profilePath: path.join(userDataDir, CHROME_DEFAULT_PROFILE_DIRECTORY),
    localStatePath: path.join(userDataDir, 'Local State'),
  };
}

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
      + 'the Chrome Default-profile login flow does not fall back to Playwright Chromium.',
    );
  }
  return path.resolve(executablePath);
}

export function buildChromeLaunchOptions(
  automationUserDataDir,
  executablePath = resolveGoogleChromeExecutable(),
) {
  return {
    channel: 'chrome',
    executablePath,
    headless: false,
    viewport: null,
    args: [
      `--profile-directory=${CHROME_DEFAULT_PROFILE_DIRECTORY}`,
      '--no-first-run',
      '--no-default-browser-check',
    ],
    userDataDir: path.resolve(automationUserDataDir),
  };
}

export function listChromeProcessIds() {
  if (process.platform !== 'win32') {
    throw new Error('The Google Chrome Default profile login flow is supported only on Windows.');
  }
  const output = execFileSync('powershell.exe', [
    '-NoProfile', '-NonInteractive', '-Command',
    "$p = Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" -ErrorAction SilentlyContinue; if ($p) { $p.ProcessId -join ',' }",
  ], { encoding: 'utf8', windowsHide: true });
  return String(output).trim().split(',').filter(Boolean);
}

export function assertChromeProfileIsUnlocked(processIds = listChromeProcessIds()) {
  if (processIds.length > 0) {
    throw new Error(
      'Google Chrome is running and may hold the Default profile lock. Close every Chrome window, '
      + 'including background Chrome processes, then rerun this command. The harness will not open Chrome with the live Default profile.',
    );
  }
}

export function shouldCopyChromeProfileEntry(sourcePath, profileRoot) {
  const relative = path.relative(profileRoot, sourcePath);
  if (!relative || relative.startsWith('..')) return true;
  const segments = relative.split(path.sep);
  if (segments.some((segment) => EXCLUDED_DIRECTORIES.has(segment))) return false;
  return !EXCLUDED_FILES.has(path.basename(sourcePath));
}

function assertIgnored(authRoot) {
  const probe = path.join(path.resolve(authRoot), '.chrome-profile-probe');
  try {
    execFileSync('git', ['check-ignore', '--no-index', '-q', '--', probe], { stdio: 'ignore', windowsHide: true });
  } catch {
    throw new Error(`Refusing to use ${authRoot}: Chrome authentication data must be Git-ignored.`);
  }
}

export async function refreshDisposableChromeProfile({
  authRoot,
  automationUserDataDir,
  chromeProfile = resolveChromeDefaultProfile(),
  processIds = listChromeProcessIds(),
} = {}) {
  assertIgnored(authRoot);
  assertChromeProfileIsUnlocked(processIds ?? listChromeProcessIds());
  if (!existsSync(chromeProfile.profilePath) || !existsSync(chromeProfile.localStatePath)) {
    throw new Error('The managed Google Chrome Default profile is unavailable. Restore it, then rerun the login command.');
  }

  const refreshRoot = `${automationUserDataDir}.refresh-${process.pid}-${Date.now()}`;
  const refreshDefault = path.join(refreshRoot, CHROME_DEFAULT_PROFILE_DIRECTORY);
  try {
    await fs.rm(automationUserDataDir, { recursive: true, force: true });
    await fs.mkdir(refreshDefault, { recursive: true, mode: 0o700 });
    await fs.copyFile(chromeProfile.localStatePath, path.join(refreshRoot, 'Local State'));
    await fs.cp(chromeProfile.profilePath, refreshDefault, {
      recursive: true,
      force: true,
      filter: (sourcePath) => {
        if (!shouldCopyChromeProfileEntry(sourcePath, chromeProfile.profilePath)) return false;
        try {
          if (!statSync(sourcePath).isDirectory()) {
            const descriptor = openSync(sourcePath, 'r');
            closeSync(descriptor);
          }
          return true;
        } catch {
          return false;
        }
      },
    });
    await fs.rename(refreshRoot, automationUserDataDir);
  } catch (error) {
    await fs.rm(refreshRoot, { recursive: true, force: true }).catch(() => {});
    throw new Error(
      `Could not create a disposable Chrome Default-profile clone. Close Chrome completely and rerun. (${error?.code ?? 'copy failed'})`,
    );
  }
}

export async function navigateAndStartAgentweaverSignIn(page, baseUrl, {
  signInTimeoutMs = 120_000,
  write = (message) => process.stdout.write(message),
} = {}) {
  await page.goto(baseUrl, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  const hasSession = await page.evaluate(
    () => window.sessionStorage.getItem('agentweaver.sessionToken') !== null,
  ).catch(() => false);
  if (hasSession) return { hasSession: true, signInStarted: false };

  const signInButton = page.getByRole('button', {
    name: 'Sign in with Microsoft Entra ID',
    exact: true,
  });
  await signInButton.waitFor({ state: 'visible', timeout: signInTimeoutMs });
  await signInButton.click();
  write('Agentweaver sign-in was opened. Complete any Microsoft Entra prompts privately; the harness will not interact with them.\n');
  return { hasSession: false, signInStarted: true };
}

export function assertAuthenticatedAgentweaverSession(origin, entries, baseUrl) {
  if (origin !== new URL(baseUrl).origin) {
    throw new Error('Sign-in did not return to the configured Agentweaver origin.');
  }
  if (typeof entries?.['agentweaver.sessionToken'] !== 'string'
    || entries['agentweaver.sessionToken'].length === 0) {
    throw new Error(
      'Agentweaver authentication was not completed. Complete the sign-in in Chrome, '
      + 'wait for the Agentweaver app, then press Resume.',
    );
  }
}
