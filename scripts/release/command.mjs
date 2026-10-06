import { spawnSync } from 'node:child_process';

function operationName(bin, args) {
  if (bin === 'docker' && args[0] === 'buildx' && args[1] === 'imagetools' && args[2] === 'inspect')
    return 'docker.manifest-read';
  if (bin === 'docker' && ['login', 'load', 'inspect', 'tag', 'push'].includes(args[0]))
    return `docker.${args[0]}`;
  if (bin === 'gh' && args[0] === 'api') return 'github.api';
  if (bin === 'dotnet' && args[0] === 'nuget' && args[1] === 'push') return 'nuget.push';
  if (bin === 'git' && ['rev-parse', 'status'].includes(args[0])) return 'git.source-read';
  throw new Error('manual publication: unsupported external operation');
}

export function runPublicationCommand(bin, args, input, spawn = spawnSync) {
  const operation = operationName(bin, args);
  const result = spawn(bin, args, {
    input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'],
  });
  if (result.error || result.status !== 0) {
    const stderr = String(result.stderr ?? '').slice(0, 8192);
    const code = result.error?.code === 'ETIMEDOUT' ? 'PROCESS_TIMEOUT'
      : result.error?.code === 'ENOENT' ? 'COMMAND_NOT_FOUND'
      : operation === 'docker.push' && /\bpermission_denied\b.*\bwrite_package\b/i.test(stderr) ? 'REGISTRY_WRITE_DENIED'
      : /\bunauthorized\b|\bauthentication required\b|\baccess denied\b|\bforbidden\b|\bpermission_denied\b/i.test(stderr) ? 'AUTHORIZATION_DENIED'
      : /\bTLS handshake timeout\b|\bconnection refused\b|\bnetwork is unreachable\b/i.test(stderr) ? 'TRANSPORT_FAILED'
      : 'OPERATION_FAILED';
    const exitCode = Number.isInteger(result.status) ? result.status : null;
    throw Object.assign(new Error(`manual publication: ${operation} failed (exit=${exitCode ?? 'unknown'}, code=${code}); inspect the target independently before retrying`),
      { operation, exitCode, code });
  }
  // Native output can contain credentials. Callers inspect it in memory only.
  return args.includes('--raw') ? result.stdout : result.stdout.trim();
}
