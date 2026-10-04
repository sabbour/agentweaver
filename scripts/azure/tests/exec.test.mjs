import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, delimiter, join } from 'node:path';
import { gitShowMatches, redact, run, runAz, runGit, sourceBytesMatch } from '../lib/exec.mjs';

const isWindows = process.platform === 'win32';
const FIXTURES_DIR = join(dirname(fileURLToPath(import.meta.url)), 'fixtures');
const ECHO_ARGS_CMD = join(FIXTURES_DIR, 'echo-args.cmd');
const SOURCE_REDACTION_FIXTURE = 'scripts/azure/tests/fixtures/source-redaction.txt';
const SENSITIVE_FIXTURE_VALUE = 'synthetic-sensitive-fixture-value';

// Resolves whether `command` is runnable on PATH at all, without caring how
// (real executable, Windows .cmd/.bat shim, etc). Used to skip the real-CLI
// regression tests gracefully on a machine that lacks `az`.
function isCommandAvailable(command) {
  try {
    const result = run(command, ['--version'], { check: false });
    return !result.error;
  } catch {
    return false;
  }
}

test('redact masks bearer tokens', () => {
  const input = 'Authorization: Bearer abc123.def456-ghi789';
  assert.ok(!redact(input).includes('abc123'));
  assert.match(redact(input), /\[redacted\]/);
});

test('redact masks key= style secrets in connection strings', () => {
  const input = 'Server=x;AccountKey=supersecretvalue==;Database=y';
  const redacted = redact(input);
  assert.ok(!redacted.includes('supersecretvalue'));
});

test('redact masks JWT-shaped tokens', () => {
  const jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
  assert.ok(!redact(jwt).includes(jwt));
});

test('redact is a no-op on plain text', () => {
  assert.equal(redact('plain status message'), 'plain status message');
});

test('git source comparison preserves redacted-looking bytes without exposing them', () => {
  const fixture = readFileSync(join(FIXTURES_DIR, 'source-redaction.txt'));
  const text = fixture.toString('utf8');
  assert.ok(text.includes('aks.properties.identityProfile.kubeletidentity.objectId'));
  assert.ok(text.includes(`AccountKey=${SENSITIVE_FIXTURE_VALUE}`));

  assert.notEqual(redact(text), text);
  assert.ok(!redact(text).includes(SENSITIVE_FIXTURE_VALUE));
  const ordinaryOutput = runGit(['show', `HEAD:${SOURCE_REDACTION_FIXTURE}`], { cwd: process.cwd() }).stdout;
  assert.ok(!ordinaryOutput.includes(SENSITIVE_FIXTURE_VALUE));
  assert.ok(!ordinaryOutput.includes('aks.properties.identityProfile.kubeletidentity.objectId'));

  assert.equal(gitShowMatches('HEAD', SOURCE_REDACTION_FIXTURE, fixture, { cwd: process.cwd() }), true);
  const changed = Buffer.from(text.replace('objectId', 'otherId'));
  assert.equal(gitShowMatches('HEAD', SOURCE_REDACTION_FIXTURE, changed, { cwd: process.cwd() }), false);

  const missingFile = `scripts/azure/tests/fixtures/missing-AccountKey=${SENSITIVE_FIXTURE_VALUE}.txt`;
  assert.throws(
    () => gitShowMatches('HEAD', missingFile, fixture, { cwd: process.cwd() }),
    error => !error.message.includes(SENSITIVE_FIXTURE_VALUE) && error.message.includes('[redacted]'),
  );
});

test('source byte comparison preserves distinct invalid bytes and normalizes only CRLF', () => {
  const invalidUtf8A = Buffer.from([0xc3, 0x28]);
  const invalidUtf8B = Buffer.from([0xff, 0x28]);
  assert.equal(invalidUtf8A.toString('utf8'), invalidUtf8B.toString('utf8'));
  assert.equal(sourceBytesMatch(invalidUtf8A, invalidUtf8B), false);
  assert.equal(sourceBytesMatch(Buffer.from('a\r\nb'), Buffer.from('a\nb')), true);
});

test('run throws a redacted error on non-zero exit by default', () => {
  assert.throws(
    () => run(process.execPath, ['-e', `console.error("AccountKey=${SENSITIVE_FIXTURE_VALUE}"); process.exit(2)`]),
    (error) => {
      assert.ok(!error.message.includes(SENSITIVE_FIXTURE_VALUE));
      assert.ok(error.message.includes('[redacted]'));
      return true;
    },
  );
});

test('run does not throw on non-zero exit when check is false', () => {
  const result = run(process.execPath, ['-e', 'process.exit(3)'], { check: false });
  assert.equal(result.status, 3);
});

test('run never shells out through a string (no injection surface)', () => {
  // If this were shell-interpreted, `; echo pwned` would execute a second
  // command. With shell:false and a literal argument array, it is passed
  // verbatim as a single argv entry.
  const result = run(process.execPath, ['-e', 'console.log(process.argv[1])', 'hello; echo pwned'], { check: false });
  assert.match(result.stdout, /hello; echo pwned/);
});

// Regression coverage for the Windows .cmd/.bat shim bug: spawnSync cannot
// exec a shim directly with shell:false, so `az` (a .cmd shim on the
// standard Windows installer) previously failed with ENOENT. These tests
// exercise the real CLI resolution path (not a fake execAz), skipping
// gracefully when the tool isn't installed on the current machine.
test('runGit resolves and runs the real git binary', { skip: !isCommandAvailable('git') }, () => {
  const result = runGit(['--version'], { check: false });
  assert.equal(result.status, 0);
  assert.match(result.stdout, /^git version/);
});

test('runAz resolves and runs the real az CLI (including Windows .cmd shim)', { skip: !isCommandAvailable('az') }, () => {
  const result = runAz(['version'], { check: false });
  assert.equal(result.status, 0);
  assert.match(result.stdout, /azure-cli/);
});

// Literal-argv-preservation round trip through the Windows cmd.exe shim
// path: a .cmd file cannot be exec'd directly, so spawnPlatformSafe routes
// it through `cmd.exe /d /s /c` with every argument escaped. These tests
// prove that routing does not corrupt or reinterpret cmd.exe-meaningful
// characters, by having the fixture echo back exactly what it received.
const SPECIAL_ARGS = [
  'plain',
  'has spaces',
  '100%cpu',
  'bang!here',
  'a&b',
  'a|b',
  'a^b',
  'quote"inside',
  'paren(one)',
  'semicolon;here',
  'trailing\\',
  'c:\\path\\with\\backslashes\\',
  '%AW_V1_ARG_SENTINEL%',
  '!name!',
  'quoted"trailing\\',
  '',
];

test(
  'echo-args.cmd round-trips special characters via PATH resolution (Windows shim routing)',
  { skip: !isWindows },
  () => {
    const env = { ...process.env, AW_V1_ARG_SENTINEL: 'must-not-expand',
      PATH: `${FIXTURES_DIR}${delimiter}${process.env.PATH ?? ''}` };
    const result = run('echo-args', SPECIAL_ARGS, { check: false, env });
    assert.equal(result.status, 0);
    const lines = result.stdout.replace(/\r?\n$/, '').split(/\r?\n/);
    assert.deepEqual(lines, SPECIAL_ARGS);
  },
);

test(
  'echo-args.cmd round-trips special characters via an absolute .cmd path (not misclassified as a plain executable)',
  { skip: !isWindows },
  () => {
    const result = run(ECHO_ARGS_CMD, SPECIAL_ARGS, { check: false,
      env: { ...process.env, AW_V1_ARG_SENTINEL: 'must-not-expand' } });
    assert.equal(result.status, 0);
    const lines = result.stdout.replace(/\r?\n$/, '').split(/\r?\n/);
    assert.deepEqual(lines, SPECIAL_ARGS);
  },
);
