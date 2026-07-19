// dev.mjs -- Node port of start-dev.ps1: local dev orchestration.
//
// Faithful behavior parity with start-dev.ps1:
//   - Kills any stale API process/port holder before starting a new one.
//   - Builds the API (unless --skip-build / opts.skipBuild) so a fresh
//     apphost binary exists.
//   - Starts the API (`dotnet run --project apps/Agentweaver.Api --no-build`)
//     as a detached child process bound to http://localhost:5000.
//   - Starts the Web UI (`npm run dev -- --force`) in apps/web, bound to
//     http://localhost:5173.
//   - Polls the API's /health endpoint (up to 60s) and the Vite dev server's
//     stdout (up to 20s) for readiness.
//   - Opens the browser at the Web UI URL unless --no-browser / opts.noBrowser.
//
// PLATFORM NOTE: start-dev.ps1 always launches the API through WSL2 (`wsl
// --exec bash -c ...`) because the API depends on the Linux bwrap sandbox
// executor being present, and it opens a separate Windows Terminal/wsl
// window for it. This port keeps that same WSL2-via-bash launch strategy on
// win32 (matching the legacy script's behavior exactly) and runs both
// processes directly (no WSL indirection) on POSIX platforms, since the
// sandbox executor there is already native.
//
// This module intentionally does NOT try to replicate start-dev.ps1's
// AppInsights user-secrets/.env bridging (Enable-AppInsightsWslBridge) --
// that is local-machine convenience wiring, not part of the deploy/CLI
// toolchain's scope for this port; local App Insights config should be set
// directly via apps/Agentweaver.Api's own configuration providers.

import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import { DEFAULT_REPO_ROOT } from "./variables.mjs";

export const API_URL = "http://localhost:5000";
export const WEB_URL = "http://localhost:5173";
export const API_PORT = 5000;

const isWindows = process.platform === "win32";

/** Parses `dev` subcommand argv: --skip-build, --no-browser, -h/--help. */
export function parseArgs(argv = []) {
  let skipBuild = false;
  let noBrowser = false;
  let help = false;
  for (const arg of argv) {
    if (arg === "--skip-build") skipBuild = true;
    else if (arg === "--no-browser") noBrowser = true;
    else if (arg === "-h" || arg === "--help") help = true;
    else throw new Error(`Unknown argument: ${arg}. Run 'dev --help' for usage.`);
  }
  return { skipBuild, noBrowser, help };
}

export const HELP_TEXT = `dev -- Agentweaver local dev orchestration (port of start-dev.ps1)

Usage:
  node scripts/azure/cli.mjs dev [--skip-build] [--no-browser]

Starts the API (http://localhost:5000) and the Web UI (http://localhost:5173).
`;

/** Polls a URL's status via fetch until it responds 200 or the timeout elapses. Mirrors Invoke-WebRequest polling. */
export async function waitForHttpOk(url, { timeoutMs = 60_000, intervalMs = 2_000, fetchImpl = fetch, sleep = defaultSleep, log = logDefault } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() <= deadline) {
    try {
      const resp = await fetchImpl(url, { method: "GET" });
      if (resp.status === 200) return true;
    } catch {
      // not ready yet
    }
    log.info(`  ... waiting for ${url}`);
    await sleep(intervalMs);
  }
  return false;
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Best-effort: frees the API port and kills any stale process, mirroring start-dev.ps1's `pkill`/`fuser` step. Never throws. */
export async function stopStaleApiProcess({ exec = execDefault, port = API_PORT } = {}) {
  if (isWindows) {
    await exec
      .run("wsl", ["--exec", "bash", "-c", `pkill -f '[A]gentweaver.Api' 2>/dev/null; fuser -k ${port}/tcp 2>/dev/null; true`])
      .catch(() => {});
  } else {
    await exec.run("bash", ["-c", `pkill -f '[A]gentweaver.Api' 2>/dev/null; fuser -k ${port}/tcp 2>/dev/null; true`]).catch(() => {});
  }
}

/** Builds the API (Release config). On win32, delegates through WSL2 like start-dev.ps1. */
export async function buildApi({ exec = execDefault, repoRoot = DEFAULT_REPO_ROOT, log = logDefault } = {}) {
  log.info("Building API...");
  if (isWindows) {
    const wslRoot = toWslPath(repoRoot);
    await exec.run("wsl", ["--exec", "bash", "-c", `cd '${wslRoot}' && dotnet build apps/Agentweaver.Api -c Release -v q --nologo`]);
  } else {
    await exec.run("dotnet", ["build", "apps/Agentweaver.Api", "-c", "Release", "-v", "q", "--nologo"], { cwd: repoRoot });
  }
  log.ok("Build OK");
}

/** Converts a Windows drive path (C:\foo\bar) to its WSL mount equivalent (/mnt/c/foo/bar). */
export function toWslPath(windowsPath) {
  const match = /^([A-Za-z]):\\(.*)$/.exec(windowsPath);
  if (!match) {
    throw new Error(`Cannot convert path '${windowsPath}' to a WSL path. Use a drive-letter path such as C:\\path\\agentweaver.`);
  }
  const drive = match[1].toLowerCase();
  const rest = match[2].replace(/\\/g, "/");
  return `/mnt/${drive}/${rest}`;
}

/** Starts the API as a detached, long-running process. Returns the child process handle. */
export function startApi({ exec = execDefault, repoRoot = DEFAULT_REPO_ROOT, log = logDefault, spawn = defaultSpawn } = {}) {
  log.info("Starting API...");
  if (isWindows) {
    const wslRoot = toWslPath(repoRoot);
    const command = `cd '${wslRoot}' && ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Agentweaver.Api --configuration Release --urls ${API_URL} --no-build`;
    return spawn("wsl", ["--exec", "bash", "-c", command], { stdio: "ignore" });
  }
  return spawn("dotnet", ["run", "--project", "apps/Agentweaver.Api", "--configuration", "Release", "--urls", API_URL, "--no-build"], {
    cwd: repoRoot,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" },
    stdio: "ignore",
  });
}

/** Starts the Web UI (Vite) dev server. Returns the child process handle. */
export function startWeb({ repoRoot = DEFAULT_REPO_ROOT, log = logDefault, spawn = defaultSpawn } = {}) {
  log.info("Starting Web UI (Vite)...");
  return spawn("npm", ["run", "dev", "--", "--force"], { cwd: path.join(repoRoot, "apps", "web"), stdio: "ignore" });
}

async function defaultSpawn(cmd, args, opts) {
  const { spawn } = await import("node:child_process");
  return spawn(cmd, args, opts);
}

/** Opens the default browser at `url`, best-effort (never throws). */
export async function openBrowser(url, { exec = execDefault } = {}) {
  try {
    if (isWindows) await exec.run("cmd", ["/c", "start", "", url]);
    else if (process.platform === "darwin") await exec.run("open", [url]);
    else await exec.run("xdg-open", [url]);
  } catch {
    // best-effort only
  }
}

/**
 * Main entry point for the `dev` subcommand: stops any stale API process,
 * optionally builds, starts API + Web UI, waits for both to become ready,
 * then opens the browser (unless skipped).
 *
 * @param {object} [opts]
 * @param {string[]} [opts.argv]
 * @param {string} [opts.repoRoot]
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {typeof fetch} [opts.fetchImpl]
 * @param {(cmd:string, args:string[], opts:object) => Promise<unknown>|unknown} [opts.spawn] Injectable process spawner for testing.
 * @param {(ms:number) => Promise<void>} [opts.sleep] Injectable for tests.
 */
export async function run(opts = {}) {
  const {
    argv = [],
    repoRoot = DEFAULT_REPO_ROOT,
    exec = execDefault,
    log = logDefault,
    fetchImpl = fetch,
    spawn = defaultSpawn,
    sleep = defaultSleep,
  } = opts;

  const { skipBuild, noBrowser, help } = parseArgs(argv);

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  log.section("Agentweaver dev");
  log.field("API", API_URL);
  log.field("Web", WEB_URL);

  log.info("Stopping any existing API processes...");
  await stopStaleApiProcess({ exec });

  if (!skipBuild) {
    await buildApi({ exec, repoRoot, log });
  }

  const apiProcess = await startApi({ exec, repoRoot, log, spawn });
  const webProcess = await startWeb({ repoRoot, log, spawn });

  log.info("");
  log.info(`Waiting for API health endpoint on ${API_URL} ...`);
  const apiReady = await waitForHttpOk(`${API_URL}/health`, { fetchImpl, sleep, log });
  log.info(apiReady ? "  API is ready" : `  API did not respond within the timeout window -- check the API process output`);

  log.info("Waiting for Vite...");
  const webReady = await waitForHttpOk(WEB_URL, { timeoutMs: 20_000, intervalMs: 1_000, fetchImpl, sleep, log }).catch(() => false);
  log.info(webReady ? "  Web UI is ready" : "  Vite starting (may still be installing dependencies)");

  if (!noBrowser && webReady) {
    await openBrowser(WEB_URL, { exec });
  }

  log.info("");
  log.info(`  API   ${API_URL}`);
  log.info(`  Web   ${WEB_URL}`);

  return { ok: true, apiReady, webReady, apiProcess, webProcess };
}
