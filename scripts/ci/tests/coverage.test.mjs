import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { TEST_SHARDS } from '../dotnet-test-shards.mjs';
import {
  assertCoberturaReport,
  assertTrxContainsTests,
  dotnetShardArguments,
  expectedDotnetShardIds,
  findSingleCoberturaReport,
  run,
} from '../coverage.mjs';

function fixtureDirectory(t) {
  const directory = mkdtempSync(path.join(tmpdir(), 'agentweaver-coverage-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

function cobertura() {
  return '<?xml version="1.0"?><coverage line-rate="1"><packages></packages></coverage>';
}

test('coverage keeps the authoritative .NET shard matrix intact', () => {
  assert.deepEqual(expectedDotnetShardIds(), TEST_SHARDS.map(({ id }) => id));
  for (const shard of TEST_SHARDS) {
    const args = dotnetShardArguments(shard, path.join('TestResults', 'coverage', shard.id));
    assert.equal(args[args.indexOf('--filter') + 1], shard.filter);
    assert.ok(args.includes('--no-build'));
    assert.ok(args.includes('--no-restore'));
    assert.ok(args.includes('XPlat Code Coverage;Format=cobertura'));
    assert.ok(args.some((arg) => arg.includes('Configuration.Include=[Agentweaver.Api]*')));
    assert.ok(args.some((arg) => arg.includes('[Agentweaver.Api.Data]*')));
    assert.ok(args.some((arg) => arg.includes('Configuration.ExcludeByFile=**/obj/**')));
    if (shard.settings) {
      assert.equal(args[args.indexOf('--settings') + 1], shard.settings);
      // The `dotnet test` CLI is order-insensitive, but the ordinary CI
      // shard command must stay byte-identical to its pre-redesign bash
      // form, which put `--settings` after `--filter <value>`, not before.
      assert.ok(
        args.indexOf('--settings') > args.indexOf('--filter'),
        '--settings must come after --filter, matching the original shard command order',
      );
    }
  }
});

test('coverage omits coverage flags entirely when collectCoverage is false, leaving an otherwise identical command', () => {
  for (const shard of TEST_SHARDS) {
    const shardDirectory = path.join('TestResults', shard.id);
    const withCoverage = dotnetShardArguments(shard, shardDirectory);
    const withoutCoverage = dotnetShardArguments(shard, shardDirectory, { collectCoverage: false });
    assert.ok(!withoutCoverage.includes('--collect'));
    assert.ok(!withoutCoverage.some((arg) => arg.includes('XPlat Code Coverage')));
    assert.ok(!withoutCoverage.some((arg) => arg.includes('DataCollectionRunSettings')));
    // Every other argument (project, filter, logger, settings, results
    // directory) must be byte-identical, proving an ordinary PR run's test
    // command line is unchanged by the coverage feature existing at all.
    const withoutCoverageFlags = new Set(['--collect', 'XPlat Code Coverage;Format=cobertura', '--']);
    const dataCollectionArgs = withCoverage.filter((arg) => (
      typeof arg === 'string' && arg.startsWith('DataCollectionRunSettings')
    ));
    const strippedWithCoverage = withCoverage.filter((arg) => (
      !withoutCoverageFlags.has(arg) && !dataCollectionArgs.includes(arg)
    ));
    assert.deepEqual(strippedWithCoverage, withoutCoverage);
  }
});

test('findSingleCoberturaReport resolves the Coverlet report under its GUID attachment directory, ignoring an MSTest deployment copy of the same file', (t) => {
  const root = fixtureDirectory(t);
  const guid = '11111111-2222-3333-4444-555555555555';
  mkdirSync(path.join(root, guid), { recursive: true });
  writeFileSync(path.join(root, guid, 'coverage.cobertura.xml'), cobertura());
  // MSTest's deployment mechanism copies the entire results tree (including
  // the report Coverlet just wrote) into a deployment-item directory nested
  // two levels deeper than the GUID attachment directory.
  mkdirSync(path.join(root, '_runner_20240101', 'In', 'runner'), { recursive: true });
  writeFileSync(path.join(root, '_runner_20240101', 'In', 'runner', 'coverage.cobertura.xml'), cobertura());

  const resolved = findSingleCoberturaReport(root);
  assert.equal(resolved, path.join(root, guid, 'coverage.cobertura.xml'));
});

test('findSingleCoberturaReport rejects a true duplicate across two distinct GUID attachment directories', (t) => {
  const root = fixtureDirectory(t);
  const guidA = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  const guidB = 'ffffffff-0000-1111-2222-333333333333';
  for (const guid of [guidA, guidB]) {
    mkdirSync(path.join(root, guid), { recursive: true });
    writeFileSync(path.join(root, guid, 'coverage.cobertura.xml'), cobertura());
  }
  assert.throws(() => findSingleCoberturaReport(root), /Expected exactly one/);
});

test('coverage rejects missing, stale, malformed, and out-of-root Cobertura reports', (t) => {
  const root = fixtureDirectory(t);
  const report = path.join(root, 'coverage.cobertura.xml');
  assert.throws(() => assertCoberturaReport(root, report, Date.now()), /missing/);

  writeFileSync(report, cobertura());
  assert.throws(() => assertCoberturaReport(root, report, Date.now() + 1000), /stale/);

  writeFileSync(report, '<coverage line-rate="1"><packages></packages></coverage>');
  assert.throws(() => assertCoberturaReport(root, report, 0), /malformed/);

  const outside = path.join(fixtureDirectory(t), 'outside.xml');
  writeFileSync(outside, cobertura());
  assert.throws(() => assertCoberturaReport(root, outside, 0), /outside/);
});

test('coverage rejects symlinked reports when supported by the host', (t) => {
  const root = fixtureDirectory(t);
  const source = path.join(root, 'source.xml');
  const report = path.join(root, 'coverage.cobertura.xml');
  writeFileSync(source, cobertura());
  try {
    symlinkSync(source, report, 'file');
  } catch (error) {
    if (error.code === 'EPERM') {
      t.skip('creating file symlinks requires Windows developer mode or elevation');
      return;
    }
    throw error;
  }
  assert.throws(() => assertCoberturaReport(root, report, 0), /symlink/);
});

test('coverage rejects zero-test and missing TRX results', (t) => {
  const root = fixtureDirectory(t);
  const results = path.join(root, 'results');
  mkdirSync(results);
  assert.throws(() => assertTrxContainsTests(root, results, 0), /No TRX/);
  writeFileSync(path.join(results, 'empty.trx'), '<TestRun></TestRun>');
  assert.throws(() => assertTrxContainsTests(root, results, 0), /Zero tests/);
  writeFileSync(path.join(results, 'skipped.trx'), '<TestRun><UnitTestResult outcome="NotExecuted" /></TestRun>');
  assert.throws(() => assertTrxContainsTests(root, results, 0), /Zero tests/);
  writeFileSync(path.join(results, 'passed.trx'), '<TestRun><UnitTestResult outcome="Passed" /></TestRun>');
  assert.doesNotThrow(() => assertTrxContainsTests(root, results, 0));
  writeFileSync(path.join(results, 'failed.trx'), '<TestRun><UnitTestResult outcome="Failed" /></TestRun>');
  assert.throws(() => assertTrxContainsTests(root, results, 0), /Failed tests/);
});

test('coverage propagates a failing test process and missing executable', () => {
  assert.throws(() => run(process.execPath, ['-e', 'process.exit(7)']), /exit code 7/);
  assert.throws(() => run('agentweaver-missing-coverage-tool', []), /ENOENT/);
  assert.throws(
    () => run(process.execPath, ['-e', 'setTimeout(() => {}, 10000)'], process.cwd(), process.env, 100),
    /exceeded/,
  );
});

test('c8 --all includes untouched matching product source', {
  skip: !existsSync(path.resolve('node_modules/c8/bin/c8.js')),
}, (t) => {
  const root = fixtureDirectory(t);
  mkdirSync(path.join(root, 'src'));
  mkdirSync(path.join(root, 'tests'));
  writeFileSync(path.join(root, 'src', 'untouched.mjs'), 'export const untouched = true;\n');
  writeFileSync(path.join(root, 'src', 'used.mjs'), 'export const used = true;\n');
  writeFileSync(
    path.join(root, 'tests', 'used.test.mjs'),
    "import { used } from '../src/used.mjs';\nimport assert from 'node:assert/strict';\nimport test from 'node:test';\ntest('used product source executes', () => assert.equal(used, true));\n",
  );
  const environment = { ...process.env };
  delete environment.NODE_TEST_CONTEXT;
  run(process.execPath, [
    path.resolve('node_modules/c8/bin/c8.js'), '--all', '--extension=.mjs',
    '--include=src/**/*.mjs', '--exclude=**/tests/**',
    '--reports-dir=coverage', '--reporter=json',
    process.execPath, '--test', 'tests/used.test.mjs',
  ], root, environment);
  const report = JSON.parse(
    readFileSync(path.join(root, 'coverage', 'coverage-final.json'), 'utf8'),
  );
  assert.ok(Object.keys(report).some((name) => name.endsWith('untouched.mjs')));
  assert.ok(Object.keys(report).some((name) => name.endsWith('used.mjs')));
  assert.ok(Object.keys(report).every((name) => !name.includes('.test.mjs')));
});
