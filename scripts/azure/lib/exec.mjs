// Dependency-free wrapper around spawnSync for invoking `az` and `git`.
// Always receives a literal argument array (never a shell string), so no
// caller input can be interpreted as shell syntax. Captures stdout/stderr
// and redacts well-known secret-shaped substrings from any surfaced error
// before it is thrown, logged, or returned to a caller.
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { delimiter, isAbsolute, join } from 'node:path';

const isWindows = process.platform === 'win32';
// Characters cmd.exe treats specially; escape them so a shim invocation
// cannot be reinterpreted as additional shell syntax.
const CMD_META_CHARS = /([()%!^"<>&|;,\s])/g;

const SECRET_PATTERNS = [
  // Authorization headers / bearer tokens.
  /Bearer\s+[A-Za-z0-9._-]+/gi,
  // Azure Storage / Service Bus / generic account keys and SAS tokens.
  /(AccountKey|SharedAccessKey|sig)=[^&\s"']+/gi,
  // Connection strings containing a key= component.
  /(key|password|secret|pwd)=[^;\s"']+/gi,
  // JWT-shaped tokens (three base64url segments).
  /[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g,
];

export function redact(text) {
  if (typeof text !== 'string' || text.length === 0) return text;
  return SECRET_PATTERNS.reduce((value, pattern) => value.replace(pattern, '[redacted]'), text);
}

// Escapes a single argument for safe inclusion in a cmd.exe command line
// when we must invoke a .cmd/.bat shim (e.g. `az` on Windows) through
// cmd.exe directly, since spawnSync cannot exec a shim with shell:false.
function escapeCmdArgument(arg) {
  let value = String(arg);
  value = value.replace(/(\\*)"/g, '$1$1\\"');
  value = value.replace(/(\\*)$/, '$1$1');
  value = `"${value}"`;
  return value.replace(CMD_META_CHARS, '^$1');
}

function escapeCmdCommand(cmd) {
  return String(cmd).replace(CMD_META_CHARS, '^$1');
}

// Resolves `cmd` against PATH/PATHEXT on Windows to determine whether it is
// a .cmd/.bat shim (as `az` is when installed via the standard MSI/npm
// installers) rather than a true executable. spawnSync with shell:false can
// only exec real executables directly; shims require cmd.exe as the host.
// `env` is the environment the spawned process will actually see (falls
// back to process.env), so a caller-supplied PATH override is honored
// exactly as spawnSync would honor it, rather than resolving against this
// process's own PATH.
function resolveExecutable(cmd, env = process.env) {
  if (!isWindows) return { file: cmd, isShim: false };
  // An absolute path still needs its extension checked: it may itself be a
  // .cmd/.bat shim (e.g. a caller passing a full path to az.cmd directly),
  // which still cannot be exec'd without cmd.exe as the host. Only the
  // PATH-search below is skipped for an absolute path, not shim detection.
  if (isAbsolute(cmd)) return { file: cmd, isShim: /\.(cmd|bat)$/i.test(cmd) };
  const pathExt = (env.PATHEXT || '.COM;.EXE;.BAT;.CMD').split(';');
  const dirs = (env.PATH || '').split(delimiter);
  // Windows' own PATH resolution tries PATHEXT-suffixed names before an
  // extension-less file, so an extension-less same-named file (as `az`
  // ships alongside `az.cmd`, for POSIX/WSL use) must not shadow the real
  // Windows entry point. Only fall back to the bare name if nothing with a
  // recognized extension is found.
  for (const dir of dirs) {
    for (const ext of pathExt) {
      const candidate = join(dir, cmd + ext.toLowerCase());
      if (existsSync(candidate)) {
        return { file: candidate, isShim: /\.(cmd|bat)$/i.test(candidate) };
      }
    }
  }
  for (const dir of dirs) {
    const candidate = join(dir, cmd);
    if (existsSync(candidate)) {
      return { file: candidate, isShim: false };
    }
  }
  return { file: cmd, isShim: false };
}

// Wraps spawnSync so Windows .cmd/.bat shims (which cannot be exec'd
// directly with shell:false) are routed through cmd.exe with escaped
// arguments, while every other case keeps the original no-shell behavior.
function spawnPlatformSafe(command, args, spawnOptions) {
  if (isWindows) {
    const { file, isShim } = resolveExecutable(command, spawnOptions.env ?? process.env);
    if (isShim) {
      const commandLine = [escapeCmdCommand(file), ...args.map(escapeCmdArgument)].join(' ');
      return spawnSync('cmd.exe', ['/d', '/s', '/c', `"${commandLine}"`], {
        ...spawnOptions,
        shell: false,
        windowsVerbatimArguments: true,
      });
    }
    return spawnSync(file, args, { ...spawnOptions, shell: false });
  }
  return spawnSync(command, args, { ...spawnOptions, shell: false });
}

/**
 * Runs a command with a literal argument array. Never executes through a
 * shell (on Windows, .cmd/.bat shims are routed through cmd.exe with fully
 * escaped arguments, which is required because such shims cannot be exec'd
 * directly, but caller-supplied argument values are never parsed as shell
 * syntax). Returns { status, stdout, stderr } with stdout and stderr
 * redacted. Throws a redacted error when `check` is true (default) and the
 * process exits non-zero or fails to spawn.
 */
export function run(command, args, options = {}) {
  const { check = true, cwd, env, input, timeout } = options;
  const result = spawnPlatformSafe(command, args, {
    cwd,
    env,
    input,
    timeout,
    encoding: 'utf8',
    windowsHide: true,
  });

  const stdout = redact(result.stdout ?? '');
  const stderr = redact(result.stderr ?? '');

  if (result.error) {
    throw new Error(`Failed to start ${command}: ${redact(result.error.message)}`);
  }

  if (check && result.status !== 0) {
    throw new Error(
      redact(`${command} ${args.join(' ')} exited with status ${result.status}.\n${stderr || stdout}`.trim()),
    );
  }

  return { status: result.status, stdout, stderr };
}

export function runAz(args, options = {}) {
  return run('az', args, options);
}

export function runGit(args, options = {}) {
  return run('git', args, options);
}

function normalizeLineEndings(content) {
  const normalized = Buffer.allocUnsafe(content.length);
  let length = 0;
  for (let index = 0; index < content.length; index += 1) {
    if (content[index] === 0x0d && content[index + 1] === 0x0a) {
      normalized[length++] = 0x0a;
      index += 1;
    } else {
      normalized[length++] = content[index];
    }
  }
  return normalized.subarray(0, length);
}

export function sourceBytesMatch(committed, expected) {
  return normalizeLineEndings(committed).equals(normalizeLineEndings(expected));
}

export function gitShowMatches(revision, file, expected, { cwd, env, timeout } = {}) {
  const args = ['show', `${revision}:${file}`];
  const result = spawnPlatformSafe('git', args, { cwd, env, timeout, windowsHide: true });
  const stdout = Buffer.isBuffer(result.stdout) ? result.stdout : Buffer.from(result.stdout ?? '');
  const stderr = Buffer.isBuffer(result.stderr) ? result.stderr.toString('utf8') : result.stderr ?? '';

  if (result.error) throw new Error(`Failed to start git: ${redact(result.error.message)}`);
  if (result.status !== 0) {
    throw new Error(redact(`git ${args.join(' ')} exited with status ${result.status}.\n${redact(stderr || stdout.toString('utf8'))}`.trim()));
  }

  return sourceBytesMatch(stdout, expected);
}
