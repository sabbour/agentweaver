import assert from 'node:assert/strict';
import test from 'node:test';
import { runPublicationCommand } from '../command.mjs';

function captureFailure(bin, args, stdout, stderr) {
  let failure;
  try {
    runPublicationCommand(bin, args, undefined, () => ({
      error: null, status: 1, stdout, stderr,
    }));
  } catch (error) {
    failure = error;
  }
  assert.ok(failure instanceof Error, 'expected the command to fail');
  return failure;
}

test('captures a NuGet push error written only to stdout', () => {
  const failure = captureFailure(
    'dotnet',
    ['nuget', 'push', 'fixture.nupkg', '--source', 'https://nuget.pkg.github.com/sabbour/index.json'],
    'error: Response status code does not indicate success: 403 (Forbidden).',
    '',
  );

  assert.equal(failure.operation, 'nuget.push');
  assert.equal(failure.code, 'OPERATION_FAILED');
  assert.ok(failure.nativeDiagnostic.includes('403 (Forbidden)'));
  assert.ok(failure.nativeDiagnostic.includes('stdout:'));
  assert.ok(!Object.keys(failure).includes('nativeDiagnostic'));
});

test('sanitizes both NuGet output streams and caps the diagnostic at 8 KiB', () => {
  const secrets = {
    RELEASE_NUGET_API_KEY: 'ENV_NUGET_SECRET_123',
    GH_TOKEN: 'GH_TOKEN_SECRET_123',
  };
  const previous = Object.fromEntries(Object.keys(secrets).map(name => [name, process.env[name]]));
  Object.assign(process.env, secrets);

  try {
    const stdout = [
      'Authorization: Bearer STDOUT_BEARER_SECRET',
      'Source: https://nuget-user:URL_PASSWORD@nuget.pkg.github.com/sabbour/index.json?access_token=URL_TOKEN',
      'api_key=API_KEY_SECRET_123',
      'token=STDOUT_TOKEN_SECRET_123',
      '{"password":"JSON SECRET WITH SPACES","token":"JSON_TOKEN_123"}',
      `--api-key ARG_NUGET_SECRET_123 ${secrets.RELEASE_NUGET_API_KEY} ${secrets.GH_TOKEN}`,
    ].join('\n');
    const stderr = [
      'token=STDERR_TOKEN_SECRET_123',
      'password=STDERR_PASSWORD_SECRET_123',
      '{"api_key":"STDERR JSON SECRET WITH SPACES"}',
      'Bearer STDERR_BEARER_SECRET_123',
      '😀'.repeat(5000),
    ].join('\n');
    const failure = captureFailure(
      'dotnet',
      ['nuget', 'push', 'fixture.nupkg', '--source', 'https://nuget.pkg.github.com/sabbour/index.json',
        '--api-key', 'ARG_NUGET_SECRET_123'],
      stdout,
      stderr,
    );

    assert.ok(failure.nativeDiagnostic.includes('https://nuget.pkg.github.com/sabbour/index.json'));
    for (const secret of [
      ...Object.values(secrets),
      'URL_PASSWORD',
      'URL_TOKEN',
      'API_KEY_SECRET_123',
      'STDOUT_BEARER_SECRET',
      'STDOUT_TOKEN_SECRET_123',
      'JSON SECRET WITH SPACES',
      'JSON_TOKEN_123',
      'ARG_NUGET_SECRET_123',
      'STDERR_TOKEN_SECRET_123',
      'STDERR_PASSWORD_SECRET_123',
      'STDERR JSON SECRET WITH SPACES',
      'STDERR_BEARER_SECRET_123',
    ])
      assert.ok(!failure.nativeDiagnostic.includes(secret), `diagnostic leaked ${secret}`);
    assert.ok(failure.nativeDiagnostic.includes('"password":"[REDACTED]"'));
    assert.ok(failure.nativeDiagnostic.includes('"api_key":"[REDACTED]"'));
    assert.ok(Buffer.byteLength(failure.nativeDiagnostic, 'utf8') <= 8192);
    assert.ok(failure.nativeDiagnostic.endsWith('[truncated]'));
  } finally {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});

test('does not attach native diagnostics to failures from other operations', () => {
  const failure = captureFailure('gh', ['api', 'repos/sabbour/agentweaver'], 'token=OUTPUT_TOKEN', '');

  assert.equal(failure.operation, 'github.api');
  assert.equal('nativeDiagnostic' in failure, false);
});
