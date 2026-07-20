// exec.mjs -- Cross-platform external CLI execution (az, kubectl, docker,
// openssl, git) with no shell dependency.
//
// WINDOWS LAUNCHER RESOLUTION AND ESCAPING (read this before touching spawn logic)
// ---------------------------------------------------------------------------
// `az`, `pnpm`, and `npm` are installed on Windows as `.cmd` shims (batch
// files) that in turn re-exec a real interpreter. Windows' CreateProcess API
// cannot execute a `.cmd`/`.bat` file directly -- it must be run through
// `cmd.exe`. Node's `child_process.spawn(file, args, { shell: false })`
// throws a *synchronous* `EINVAL` when `file` resolves to a `.cmd`/`.bat`
// (verified empirically on this machine, Node v24: `spawn('az.cmd', [...],
// { shell: false })` throws EINVAL before any 'error' event fires).
//
// The naive fix -- `spawn(file, args, { shell: true })` -- silently
// reintroduces shell injection: Node's own docs mark this combination
// deprecated (DEP0190) because with `shell: true` the args array is
// *concatenated*, not escaped, into the command string. Verified: an
// argument containing `&`/`|` gets interpreted as a shell operator instead of
// being passed through literally.
//
// The approach used here (same technique as the widely-used `cross-spawn`
// package, reimplemented locally to avoid a new dependency): resolve the
// executable's real path + extension ourselves via a PATH/PATHEXT-style
// scan, and when it resolves to `.cmd`/`.bat`, invoke it as:
//
//   spawn('cmd.exe', ['/d', '/s', '/c', '"<escaped command line>"'],
//         { shell: false, windowsVerbatimArguments: true })
//
// where the command line is built by quoting each argument in double quotes
// and then escaping cmd.exe's own metacharacters (`()%!^"<>&|;, `) with `^`
// -- this is required because cmd.exe interprets those characters even
// *inside* double quotes when tokenizing the line before handing the
// (unescaped) argument through to the target program. `windowsVerbatimArguments:
// true` tells Node not to apply its own quoting on top of ours (we already
// did it). This was verified with a probe script: an argument `a&b|c`
// round-trips byte-for-byte through `cmd.exe /c` to a Node child's
// `process.argv`, whereas `shell: true` mis-parses it as a pipe.
//
// Plain executables (`.exe`, or extension-less on POSIX) are spawned
// directly with `shell: false` and an argv array -- no shell involved, no
// escaping needed; Node/CreateProcess handles argv quoting correctly for
// normal executables.

import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { redact, redactArgs } from "./secret.mjs";
import * as log from "./log.mjs";

const isWindows = process.platform === "win32";

// cmd.exe metacharacters that must be caret-escaped even inside a quoted
// argument. Mirrors cross-spawn's windows/escapeArgument regex.
const CMD_META_CHARS = /([()%!^"<>&|;,\s])/g;

function escapeCmdArgument(arg) {
  let value = String(arg);
  // A run of backslashes immediately before a double quote (or at the end of
  // the string) must be doubled so the eventual C-runtime argv parser in the
  // target process doesn't misinterpret the escaping.
  value = value.replace(/(\\*)"/g, '$1$1\\"');
  value = value.replace(/(\\*)$/, "$1$1");
  value = `"${value}"`;
  value = value.replace(CMD_META_CHARS, "^$1");
  return value;
}

function escapeCmdCommand(cmd) {
  return String(cmd).replace(CMD_META_CHARS, "^$1");
}

/**
 * Resolve `cmd` to a concrete file on PATH, preferring `<cmd>.cmd` then
 * `<cmd>.exe` then the bare name on Windows (per the launcher-resolution
 * contract this module documents), or the bare name on POSIX. Falls back to
 * returning `cmd` unchanged if nothing is found on PATH, so the eventual
 * spawn() ENOENT error still surfaces a sensible message.
 */
export function resolveExecutable(cmd) {
  if (path.isAbsolute(cmd) && fs.existsSync(cmd)) return cmd;

  const pathEnvKey = Object.keys(process.env).find((k) => k.toLowerCase() === "path");
  const pathDirs = (pathEnvKey ? process.env[pathEnvKey] : "")
    .split(path.delimiter)
    .filter(Boolean);

  const alreadyHasExt = /\.(cmd|bat|exe|ps1)$/i.test(cmd);
  const candidateNames = isWindows
    ? (alreadyHasExt ? [cmd] : [`${cmd}.cmd`, `${cmd}.exe`, `${cmd}.bat`, cmd])
    : [cmd];

  for (const dir of pathDirs) {
    for (const name of candidateNames) {
      const full = path.join(dir, name);
      if (fs.existsSync(full) && fs.statSync(full).isFile()) return full;
    }
  }
  return cmd;
}

function needsCmdWrapper(resolvedPath) {
  if (!isWindows) return false;
  return /\.(cmd|bat)$/i.test(resolvedPath);
}

function buildSpawnPlan(cmd, args) {
  const resolved = resolveExecutable(cmd);
  if (needsCmdWrapper(resolved)) {
    const commandLine = [escapeCmdCommand(resolved), ...args.map(escapeCmdArgument)].join(" ");
    return {
      file: "cmd.exe",
      spawnArgs: ["/d", "/s", "/c", `"${commandLine}"`],
      spawnOpts: { shell: false, windowsVerbatimArguments: true },
      displayCmd: cmd,
    };
  }
  return {
    file: resolved,
    spawnArgs: args,
    spawnOpts: { shell: false },
    displayCmd: cmd,
  };
}

function formatCommandLine(cmd, args) {
  return [cmd, ...redactArgs(args)].join(" ");
}

// Azure CLI is a Python app; when stdout/stderr are piped (not a TTY) on
// Windows it can hit console-codepage/Unicode issues (mojibake, or
// UnicodeEncodeError crashes on non-ASCII output) and may still try to
// color/format output for a "detected" terminal width. Forcing UTF-8 I/O and
// disabling color output makes captured output deterministic across
// platforms. These only affect the child process's environment.
const AZ_SAFE_ENV = {
  PYTHONIOENCODING: "utf-8",
  PYTHONUTF8: "1",
  AZURE_CORE_NO_COLOR: "1",
  NO_COLOR: "1",
};

let dryRunEnabled = false;

/** Enable/disable global dry-run mode: run()/capture() log instead of executing. */
export function setDryRun(enabled) {
  dryRunEnabled = Boolean(enabled);
}

export function isDryRun() {
  return dryRunEnabled;
}

class ExecError extends Error {
  constructor(message, { command: cmdLine, exitCode, stderr } = {}) {
    super(message);
    this.name = "ExecError";
    this.command = cmdLine;
    this.exitCode = exitCode;
    this.stderr = stderr;
  }
}

function mergeEnv(extraEnv) {
  return { ...process.env, ...(extraEnv ?? {}) };
}

/**
 * Run a command with inherited stdio, streaming output directly to the
 * console. Use for long-running/interactive operations (builds, deploys).
 * Fails fast: rejects with an ExecError on non-zero exit, replicating the
 * bash scripts' `set -euo pipefail` intent.
 *
 * @param {string} cmd
 * @param {string[]} args
 * @param {{ cwd?: string, env?: Record<string,string>, dryRun?: boolean, azSafeEnv?: boolean }} [opts]
 */
export function run(cmd, args = [], opts = {}) {
  const dryRun = opts.dryRun ?? dryRunEnabled;
  const displayLine = formatCommandLine(cmd, args);
  if (dryRun) {
    log.command(`(dry-run) ${displayLine}`);
    return Promise.resolve({ code: 0, dryRun: true });
  }
  log.command(displayLine);

  const plan = buildSpawnPlan(cmd, args);
  const env = mergeEnv({ ...(opts.azSafeEnv === false ? {} : AZ_SAFE_ENV), ...(opts.env ?? {}) });

  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn(plan.file, plan.spawnArgs, {
        ...plan.spawnOpts,
        cwd: opts.cwd,
        env,
        stdio: "inherit",
      });
    } catch (err) {
      reject(new ExecError(`Failed to spawn '${redact(cmd)}': ${redact(err.message)}`, { command: displayLine }));
      return;
    }
    child.on("error", (err) => {
      reject(new ExecError(`Failed to spawn '${redact(cmd)}': ${redact(err.message)}`, { command: displayLine }));
    });
    child.on("close", (code, signal) => {
      if (code === 0) {
        resolve({ code: 0 });
        return;
      }
      reject(
        new ExecError(
          `Command failed (exit ${code}${signal ? `, signal ${signal}` : ""}): ${redact(displayLine)}`,
          { command: displayLine, exitCode: code },
        ),
      );
    });
  });
}

/**
 * Run a command and capture its stdout, for query commands (e.g. `az ... -o
 * json`). Does not stream to the console. Rejects on non-zero exit. When
 * `json: true`, parses stdout as JSON (throwing a clear error on parse
 * failure, including a truncated raw-output excerpt for diagnostics).
 *
 * @param {string} cmd
 * @param {string[]} args
 * @param {{ cwd?: string, env?: Record<string,string>, json?: boolean, dryRun?: boolean, trim?: boolean, allowFailure?: boolean, azSafeEnv?: boolean }} [opts]
 * @returns {Promise<{ stdout: string, stderr: string, code: number, json?: unknown }>}
 */
export function capture(cmd, args = [], opts = {}) {
  const dryRun = opts.dryRun ?? dryRunEnabled;
  const displayLine = formatCommandLine(cmd, args);
  if (dryRun) {
    log.command(`(dry-run, capture) ${displayLine}`);
    return Promise.resolve({ stdout: "", stderr: "", code: 0, json: opts.json ? null : undefined, dryRun: true });
  }

  const plan = buildSpawnPlan(cmd, args);
  const env = mergeEnv({ ...(opts.azSafeEnv === false ? {} : AZ_SAFE_ENV), ...(opts.env ?? {}) });

  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn(plan.file, plan.spawnArgs, {
        ...plan.spawnOpts,
        cwd: opts.cwd,
        env,
        stdio: ["ignore", "pipe", "pipe"],
      });
    } catch (err) {
      reject(new ExecError(`Failed to spawn '${redact(cmd)}': ${redact(err.message)}`, { command: displayLine }));
      return;
    }

    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.on("error", (err) => {
      if (opts.allowFailure) {
        // Matches the non-zero-exit-code allowFailure path below: a missing
        // binary (ENOENT) is a form of command failure, not a fatal defect
        // in the caller. Without this, `requireCmd`-style prerequisite
        // checks (deploy.mjs's --local setup) would crash with a raw
        // ExecError instead of reporting a friendly "not found" message --
        // found live when `dotnet` wasn't on PATH in a fresh environment.
        resolve({ stdout: "", stderr: redact(err.message), code: 127 });
        return;
      }
      reject(new ExecError(`Failed to spawn '${redact(cmd)}': ${redact(err.message)}`, { command: displayLine }));
    });
    child.on("close", (code, signal) => {
      const trimmedStdout = opts.trim === false ? stdout : stdout.trim();
      if (code !== 0 && !opts.allowFailure) {
        reject(
          new ExecError(
            `Command failed (exit ${code}${signal ? `, signal ${signal}` : ""}): ${redact(displayLine)}\n${redact(stderr.trim())}`,
            { command: displayLine, exitCode: code, stderr: redact(stderr.trim()) },
          ),
        );
        return;
      }
      let parsed;
      if (opts.json && trimmedStdout) {
        try {
          parsed = JSON.parse(trimmedStdout);
        } catch (err) {
          reject(
            new ExecError(
              `Failed to parse JSON output of: ${redact(displayLine)}\n${redact(err.message)}\nRaw output (first 500 chars): ${redact(trimmedStdout.slice(0, 500))}`,
              { command: displayLine, exitCode: code },
            ),
          );
          return;
        }
      } else if (opts.json) {
        parsed = null;
      }
      resolve({ stdout: trimmedStdout, stderr: stderr.trim(), code, json: parsed });
    });
  });
}

export { ExecError };
