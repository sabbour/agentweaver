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
    }
  }
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
