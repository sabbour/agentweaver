// dev.mjs -- Node port of start-dev.ps1: local dev orchestration.
//
// Faithful behavior parity with start-dev.ps1:
//   - Kills any stale API process/port holder before starting a new one.
//   - Builds the API (unless --skip-build / opts.skipBuild) so a fresh
//     apphost binary exists.
//   - Starts the API (`dotnet run --project apps/Agentweaver.Api --no-build`)
//     as a detached child process bound to http://localhost:5000.
//   - Polls the API's /health endpoint (up to 60s), failing immediately if
//     the API process exits and streaming its output for diagnosis.
//   - Starts the Web UI (`npm run dev -- --force`) in apps/web, bound to
//     http://localhost:5173, after the API is healthy.
//   - Polls the Vite dev server (up to 20s) for readiness.
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

import { copyFileSync, constants as fsConstants } from "node:fs";
import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import { DEFAULT_REPO_ROOT } from "./variables.mjs";

export const API_URL = "http://localhost:5000";
export const WEB_URL = "http://localhost:5173";
export const API_PORT = 5000;

const isWindows = process.platform === "win32";

/** Parses `dev` subcommand argv: --setup, --skip-build, --no-browser, -h/--help. */
export function parseArgs(argv = []) {
  let skipBuild = false;
  let noBrowser = false;
  let setup = false;
  let help = false;
  for (const arg of argv) {
    if (arg === "--skip-build") skipBuild = true;
    else if (arg === "--no-browser") noBrowser = true;
    else if (arg === "--setup") setup = true;
    else if (arg === "-h" || arg === "--help") help = true;
    else throw new Error(`Unknown argument: ${arg}. Run 'dev --help' for usage.`);
  }
  return { skipBuild, noBrowser, setup, help };
}

export const HELP_TEXT = `dev -- Agentweaver local dev orchestration (port of start-dev.ps1)

Usage:
  node scripts/azure/cli.mjs dev [--skip-build] [--no-browser]
  node scripts/azure/cli.mjs dev --setup             Local dev environment setup only (no Azure)

Starts the API (http://localhost:5000) and the Web UI (http://localhost:5173).

--setup checks prerequisites (git, .NET 10 SDK, Node 20+), installs apps/web's
npm dependencies, and restores .NET packages -- then exits without starting
any servers. Does NOT touch Azure. This replaces install.sh/install.ps1's
install_local() and is what 'npm run setup' runs.
`;

/** Polls a URL until it responds 200, its process exits, or the timeout elapses. */
export async function waitForHttpOk(
  url,
  {
    timeoutMs = 60_000,
    intervalMs = 2_000,
    fetchImpl = fetch,
    sleep = defaultSleep,
    log = logDefault,
    childProcess,
  } = {},
) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() <= deadline) {
    if (childProcess?.exitCode !== null && childProcess?.exitCode !== undefined) {
      return false;
    }
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
    return spawn("wsl", ["--exec", "bash", "-c", command], { stdio: ["ignore", "inherit", "inherit"] });
  }
  return spawn("dotnet", ["run", "--project", "apps/Agentweaver.Api", "--configuration", "Release", "--urls", API_URL, "--no-build"], {
    cwd: repoRoot,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" },
    stdio: ["ignore", "inherit", "inherit"],
  });
}

/** Starts the Web UI (Vite) dev server. Returns the child process handle. */
export function startWeb({ repoRoot = DEFAULT_REPO_ROOT, log = logDefault, spawn = defaultSpawn } = {}) {
  log.info("Starting Web UI (Vite)...");
  return spawn("npm", ["run", "dev", "--", "--force"], {
    cwd: path.join(repoRoot, "apps", "web"),
    stdio: ["ignore", "inherit", "inherit"],
  });
}

// `npm` (and any other Node-ecosystem launcher) is a `.cmd` shim on Windows;
// child_process.spawn cannot execute it directly without going through
// cmd.exe (see lib/exec.mjs's module banner). Route through the same
// buildSpawnPlan() the awaited run()/capture() helpers use so long-running
// detached spawns (like the Web UI dev server) get correct `.cmd`/`.bat`
// resolution instead of an unhandled `spawn npm ENOENT`.
async function defaultSpawn(cmd, args, opts) {
  const { spawn } = await import("node:child_process");
  const { buildSpawnPlan } = await import("./lib/exec.mjs");
  const plan = buildSpawnPlan(cmd, args);
  return spawn(plan.file, plan.spawnArgs, { ...plan.spawnOpts, ...opts });
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
 * Returns a copy-pasteable install command for `tool`, matching the current
 * platform's package manager (winget on Windows, Homebrew on macOS, apt-get
 * on Linux). Falls back to a generic doc-link message on unrecognized
 * platforms (e.g. FreeBSD) rather than guessing a wrong command.
 */
export function installHint(tool, platform = process.platform) {
  const commands = {
    git: {
      win32: "winget install --id Git.Git -e",
      darwin: "brew install git",
      linux: "sudo apt-get update && sudo apt-get install -y git",
    },
    dotnet: {
      win32: "winget install Microsoft.DotNet.SDK.10",
      darwin: "brew install --cask dotnet-sdk",
      linux: "curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 && export PATH=\"$HOME/.dotnet:$PATH\"",
    },
    node: {
      win32: "winget install OpenJS.NodeJS.LTS",
      darwin: "brew install node@20",
      linux: "curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash - && sudo apt-get install -y nodejs",
    },
    wsl2: {
      // Windows-only: `wsl --install` needs an elevated PowerShell + a reboot;
      // bubblewrap must then be installed *inside* the distro for real
      // isolation (the WSL sandbox executor probes for it, never installs it).
      win32: "wsl --install (elevated PowerShell, then reboot), then inside the distro: sudo apt-get install -y bubblewrap",
    },
  };
  const byPlatform = commands[tool];
  const cmd = byPlatform?.[platform];
  if (cmd) return `Install with: ${cmd}`;
  return "See https://git-scm.com/, https://dot.net/download, or https://nodejs.org/ for platform-specific install instructions.";
}

/**
 * Windows-only, **advisory** WSL2 presence check. On Windows, `dev.mjs`
 * launches the API through `wsl --exec` because the API's sandbox executor
 * needs the Linux bwrap backend for genuine filesystem/PID/network isolation
 * (the native-Windows processcontainer backend cannot enforce network
 * restrictions — see MxcSandboxExecutor's network warning — and on a stock
 * Windows 11 host the mxc probe refuses the base-container tier and falls
 * through to WSL2 anyway). Without WSL2, `npm run dev` fails at `wsl.exe`.
 *
 * This probe is deliberately kept to a cheap, reliable `wsl --status`
 * exit-code check. It does NOT try to detect whether bubblewrap is installed
 * *inside* the distro: that requires booting the default distro
 * (`wsl --exec bash -lc 'command -v bwrap'`), which is slow and order-
 * dependent (which distro is default), so it would be a flaky gate. That
 * requirement is documented in getting-started.md instead, and the runtime
 * degrades gracefully (bwrap -> unshare -> passthrough) with its own logged
 * warnings. Because it is advisory, a false negative only prints an extra
 * hint — it never fails setup.
 */
export async function checkWsl2({ exec = execDefault } = {}) {
  const hint = installHint("wsl2");
  const result = await exec.capture("wsl", ["--status"], { allowFailure: true });
  if (result.code !== 0) {
    return {
      name: "wsl2",
      ok: false,
      advisory: true,
      message: `WSL2 not detected — it is required for Windows local dev (the API runs its sandbox executor inside WSL2). ${hint}`,
    };
  }
  return { name: "wsl2", ok: true, advisory: true, version: "detected" };
}

/**
 * Checks all prerequisites (git, .NET 10 SDK, Node 20+) concurrently and
 * reports every failure at once, rather than stopping at the first one --
 * so a user missing both dotnet and node learns about both in a single run
 * instead of fixing one, re-running, then discovering the next.
 *
 * On Windows, an extra **advisory** WSL2 check is appended (see
 * {@link checkWsl2}). Advisory results never flip the overall `ok`: WSL2 is
 * not needed for `npm run setup` itself (which only restores/installs), only
 * for the later `npm run dev`, so a missing WSL2 warns rather than fails.
 *
 * Returns `{ ok, results }` where `results` is one entry per tool:
 * `{ name, ok, version }` on success or `{ name, ok: false, message }` on
 * failure (message already includes the platform-specific install hint);
 * advisory entries additionally carry `advisory: true`.
 */
export async function checkPrerequisites({ exec = execDefault, platform = process.platform } = {}) {
  const specs = [
    { name: "git", cmd: "git", args: ["--version"] },
    { name: "dotnet", cmd: "dotnet", args: ["--version"], minMajor: 10, versionLabel: ".NET 10 SDK" },
    { name: "node", cmd: "node", args: ["--version"], minMajor: 20, versionLabel: "Node.js 20.19+ or 22.12+" },
  ];

  const results = await Promise.all(
    specs.map(async (spec) => {
      const hint = installHint(spec.name);
      const result = await exec.capture(spec.cmd, spec.args, { allowFailure: true });
      if (result.code !== 0) {
        return { name: spec.name, ok: false, message: `'${spec.cmd}' not found or not working. ${hint}` };
      }
      const version = result.stdout.trim();
      if (spec.minMajor) {
        const major = Number.parseInt(version.replace(/^v/, "").split(".")[0], 10);
        if (!(major >= spec.minMajor)) {
          return { name: spec.name, ok: false, message: `${spec.versionLabel} is required (found ${version}). ${hint}` };
        }
      }
      return { name: spec.name, ok: true, version };
    }),
  );

  if (platform === "win32") {
    results.push(await checkWsl2({ exec }));
  }

  return { ok: results.filter((r) => !r.advisory).every((r) => r.ok), results };
}

function scaffoldDevelopmentAppSettings(repoRoot) {
  const apiRoot = path.join(repoRoot, "apps", "Agentweaver.Api");
  const examplePath = path.join(apiRoot, "appsettings.Development.json.example");
  const developmentPath = path.join(apiRoot, "appsettings.Development.json");
  // Copy only if the destination does not already exist, atomically:
  // COPYFILE_EXCL makes copyFileSync fail with EEXIST rather than overwrite,
  // which closes the check-then-copy (TOCTOU) race where a file appearing
  // between an existsSync() check and the copy would be clobbered. An
  // existing file is left untouched (same user-visible behavior as before).
  try {
    copyFileSync(examplePath, developmentPath, fsConstants.COPYFILE_EXCL);
    return true;
  } catch (err) {
    if (err.code === "EEXIST") return false;
    throw err;
  }
}

/**
 * Local dev environment setup only: prereq checks (git, .NET 10 SDK, Node
 * 20+) + `apps/web` npm install + `dotnet restore`. No Azure calls at all.
 * Mirrors install.sh/install.ps1's install_local(). Invoked via `dev --setup`
 * (moved here from deploy.mjs's old `--local` flag -- dev.mjs is the
 * canonical "local dev" entry point, so local-only setup belongs here rather
 * than nested under the Azure-focused `deploy` command).
 */
export async function runLocalSetup({ exec = execDefault, log = logDefault, repoRoot = DEFAULT_REPO_ROOT } = {}) {
  log.section("Agentweaver local dev setup");

  const { ok, results } = await checkPrerequisites({ exec });
  for (const r of results) {
    if (r.ok) log.ok(r.name === "git" ? r.version : `${r.name} ${r.version}`);
    else if (r.advisory) log.warn(r.message);
    else log.error(r.message);
  }
  if (!ok) {
    const failures = results.filter((r) => !r.ok && !r.advisory);
    // Message intentionally short -- the per-tool details were already
    // printed above via log.error(); this is just the thrown signal that
    // stops setup and sets a non-zero exit code.
    throw new Error(
      `${failures.length} prerequisite check${failures.length === 1 ? "" : "s"} failed (${failures
        .map((f) => f.name)
        .join(", ")}). See errors above.`,
    );
  }

  log.info("");
  log.info("Installing web dependencies...");
  await exec.run("npm", ["--prefix", path.join(repoRoot, "apps", "web"), "install"]);
  log.ok("Web dependencies installed.");

  log.info("");
  log.info("Restoring .NET packages...");
  await exec.run("dotnet", ["restore", path.join(repoRoot, "agentweaver.sln"), "-v", "q", "--nologo"]);
  log.ok(".NET packages restored.");

  const scaffoldedDevelopmentAppSettings = scaffoldDevelopmentAppSettings(repoRoot);

  log.info("");
  log.section("LOCAL DEV READY");
  log.info("  Start the API:   npm run dev:api");
  log.info("  Start the Web:   npm run dev:web");
  log.info("  Or both at once: npm run dev");
  if (scaffoldedDevelopmentAppSettings) {
    log.info(
      "  Scaffolded apps/Agentweaver.Api/appsettings.Development.json from .example; configure Auth:Entra:ClientId and Auth:Entra:TenantId before first sign-in.",
    );
  }
  log.info("  For local sign-in, configure a Microsoft Entra app registration.");
  log.info("  Callback URL:      http://localhost:5000/auth/entra/callback");
  log.info("  Set Auth:Entra:ClientId and Auth:Entra:TenantId in apps/Agentweaver.Api/appsettings.Development.json.");
  log.info("  Full walkthrough:  docs/guide/getting-started.md#1-configure-the-api");

  return { ok: true };
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

  const { skipBuild, noBrowser, setup, help } = parseArgs(argv);

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  if (setup) {
    return runLocalSetup({ exec, log, repoRoot });
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

  log.info("");
  log.info(`Waiting for API health endpoint on ${API_URL} ...`);
  const apiReady = await waitForHttpOk(`${API_URL}/health`, { fetchImpl, sleep, log, childProcess: apiProcess });
  if (!apiReady) {
    const exitDetail =
      apiProcess?.exitCode !== null && apiProcess?.exitCode !== undefined
        ? `exited with code ${apiProcess.exitCode}`
        : "did not become healthy within 60 seconds";
    throw new Error(`API ${exitDetail}. Review the API output above for the startup failure.`);
  }
  log.info("  API is ready");

  const webProcess = await startWeb({ repoRoot, log, spawn });

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
