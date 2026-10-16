import { spawnSync } from 'node:child_process';

function operationName(bin, args) {
  if (bin === 'docker' && args[0] === 'buildx' && args[1] === 'imagetools' && args[2] === 'inspect')
    return 'docker.manifest-read';
  if (bin === 'docker' && ['login', 'load', 'inspect', 'tag', 'push'].includes(args[0]))
    return `docker.${args[0]}`;
  if (bin === 'docker' && args[0] === 'image' && args[1] === 'inspect')
    return 'docker.image-inspect';
  if (bin === 'docker' && args[0] === 'run' && args.at(-1) === '--verify-native-runtime')
    return 'docker.runtime-verify';
  if (bin === 'gh' && args[0] === 'api') return 'github.api';
  if (bin === 'dotnet' && args[0] === 'nuget' && args[1] === 'push') return 'nuget.push';
  if (bin === 'git' && ['rev-parse', 'status'].includes(args[0])) return 'git.source-read';
  throw new Error('manual publication: unsupported external operation');
}

function redactDiagnosticUrl(value) {
  const suffix = value.match(/[),.;]+$/)?.[0] ?? '';
  const candidate = suffix ? value.slice(0, -suffix.length) : value;
  try {
    const url = new URL(candidate);
    return `${url.protocol}//${url.host}${url.pathname}${suffix}`;
  } catch {
    return `[URL redacted]${suffix}`;
  }
}

function truncateDiagnostic(diagnostic) {
  const limit = 8192;
  const suffix = ' [truncated]';
  const bytes = Buffer.from(diagnostic, 'utf8');
  if (bytes.length <= limit) return diagnostic;

  const prefixLimit = limit - Buffer.byteLength(suffix, 'utf8');
  let prefix = bytes.subarray(0, prefixLimit).toString('utf8');
  while (Buffer.byteLength(prefix, 'utf8') > prefixLimit)
    prefix = Array.from(prefix).slice(0, -1).join('');
  return `${prefix}${suffix}`;
}

function sanitizePublicationDiagnostic(stdout, stderr, args) {
  const secrets = [
    process.env.RELEASE_NUGET_API_KEY,
    process.env.GH_TOKEN,
    process.env.GITHUB_TOKEN,
    process.env.NUGET_API_KEY,
    process.env.NUGET_AUTH_TOKEN,
  ];
  for (let index = 0; index < args.length; index++) {
    const argument = String(args[index] ?? '');
    if (/^--api-key=/i.test(argument)) secrets.push(argument.slice(argument.indexOf('=') + 1));
    if (/^--api-key$/i.test(argument)) secrets.push(String(args[index + 1] ?? ''));
  }

  const output = [];
  if (stdout) output.push(`stdout: ${stdout}`);
  if (stderr) output.push(`stderr: ${stderr}`);
  let diagnostic = output.join('\n')
    .replace(/\u001B\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, ' ');
  for (const secret of new Set(secrets.filter(value => typeof value === 'string' && value.length > 0)))
    diagnostic = diagnostic.split(secret).join('[REDACTED]');

  diagnostic = diagnostic
    .replace(/\b(authorization|proxy-authorization)\s*:\s*[^\r\n]*/gi, '$1: [REDACTED]')
    .replace(/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi, '$1 [REDACTED]')
    .replace(/(<add\s+key="(?:clear[_-]?text[_-]?password|password|api[_-]?key|token)"\s+value=")[^"]*(")/gi,
      '$1[REDACTED]$2')
    .replace(/"(access[_-]?token|api[_-]?key|gh[_-]?token|github[_-]?token|proxy-authorization|authorization|token|password|secret)"\s*:\s*"(?:\\.|[^"\\])*"/gi,
      '"$1":"[REDACTED]"')
    .replace(/\b(clear[_-]?text[_-]?password|access[_-]?token|api[_-]?key|gh[_-]?token|github[_-]?token|token|password|secret)\b\s*[:=]\s*[^,\s;]+/gi,
      '$1=[REDACTED]')
    .replace(/(--api-key(?:=|\s+))[^\s|,;]+/gi, '$1[REDACTED]')
    .replace(/\bhttps?:\/\/[^\s<>"']+/gi, redactDiagnosticUrl)
    .replace(/([?&](?:access[_-]?token|api[_-]?key|apikey|token|password|secret)=)[^&#\s]*/gi, '$1[REDACTED]')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean)
    .join(' | ')
    .replace(/\s+/g, ' ');

  return truncateDiagnostic(diagnostic);
}

export function runPublicationCommand(bin, args, input, spawn = spawnSync) {
  const operation = operationName(bin, args);
  const result = spawn(bin, args, {
    input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'],
    ...(operation === 'docker.runtime-verify' ? { timeout: 60_000 } : {}),
  });
  if (result.error || result.status !== 0) {
    const rawStderr = String(result.stderr ?? '');
    const stderr = rawStderr.slice(0, 8192);
    const code = result.error?.code === 'ETIMEDOUT' ? 'PROCESS_TIMEOUT'
      : result.error?.code === 'ENOENT' ? 'COMMAND_NOT_FOUND'
      : operation === 'docker.push' && /\bpermission_denied\b.*\bwrite_package\b/i.test(stderr) ? 'REGISTRY_WRITE_DENIED'
      : operation === 'docker.manifest-read' && /\bmanifest unknown\b|\bname unknown\b|\b404\b/i.test(stderr) ? 'REGISTRY_NOT_FOUND'
      : /\bunauthorized\b|\bauthentication required\b|\baccess denied\b|\bforbidden\b|\bpermission_denied\b/i.test(stderr) ? 'AUTHORIZATION_DENIED'
      : /\bTLS handshake timeout\b|\bconnection refused\b|\bnetwork is unreachable\b/i.test(stderr) ? 'TRANSPORT_FAILED'
      : 'OPERATION_FAILED';
    const exitCode = Number.isInteger(result.status) ? result.status : null;
    const failure = Object.assign(new Error(`manual publication: ${operation} failed (exit=${exitCode ?? 'unknown'}, code=${code}); inspect the target independently before retrying`),
      { operation, exitCode, code });
    if (operation === 'nuget.push' || operation === 'docker.manifest-read') {
      const nativeDiagnostic = sanitizePublicationDiagnostic(result.stdout, rawStderr, args);
      if (nativeDiagnostic)
        Object.defineProperty(failure, 'nativeDiagnostic', { value: nativeDiagnostic });
    }
    throw failure;
  }
  // Successful native output can contain credentials. Callers inspect it in memory only.
  return args.includes('--raw') ? result.stdout : result.stdout.trim();
}
