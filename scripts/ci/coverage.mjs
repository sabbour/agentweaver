import { execFileSync, spawnSync } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { globSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { TEST_SHARDS } from './dotnet-test-shards.mjs';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const DOTNET_PROJECT = 'tests/Agentweaver.Tests/Agentweaver.Tests.csproj';
const PROPERTY = '-p:CopilotSkipCliDownload=true';
const DOTNET_ASSEMBLIES = [
  'Agentweaver.Api', 'Agentweaver.Api.Data', 'Agentweaver.AgentHost', 'Agentweaver.Mcp',
  'Agentweaver.Web', 'Agentweaver.Api.Migrations.Postgres',
  'Agentweaver.AgentRuntime', 'Agentweaver.AgentTools', 'Agentweaver.AspNetCore',
  'Agentweaver.Domain', 'Agentweaver.SandboxExec', 'Agentweaver.SandboxFs',
  'Agentweaver.Squad',
];
const DOTNET_INCLUDE = DOTNET_ASSEMBLIES.map((name) => `[${name}]*`).join(',');
const NODE_TEST_GLOBS = [
  'scripts/azure/tests/*.test.mjs',
  'scripts/changesets/tests/*.test.mjs',
  'scripts/ci/tests/*.test.mjs',
  'scripts/demo-recording/test/*.test.mjs',
];
const NODE_SOURCE_GLOBS = [
  'scripts/azure/**/*.mjs',
  'scripts/changesets/**/*.mjs',
  'scripts/ci/**/*.mjs',
  'scripts/demo-recording/**/*.mjs',
];
const NODE_EXCLUDES = [
  '**/tests/**', '**/test/**', '**/fixtures/**', '**/__fixtures__/**',
  '**/generated/**', '**/*.generated.mjs', '**/vendor/**',
  '**/node_modules/**', '**/dist/**', '**/.scratch/**',
];

export function expectedDotnetShardIds() {
  return TEST_SHARDS.map((shard) => shard.id);
}

export function isWithin(parent, candidate) {
  const relative = path.relative(path.resolve(parent), path.resolve(candidate));
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function assertRegularFile(repoRoot, reportPath, startedAt, label) {
  const resolved = path.resolve(reportPath);
  if (!isWithin(repoRoot, resolved)) {
    throw new Error(`${label} is outside the repository: ${reportPath}`);
  }
  const details = lstatSync(resolved, { throwIfNoEntry: false });
  if (!details) {
    throw new Error(`${label} is missing: ${reportPath}`);
  }
  if (details.isSymbolicLink() || !details.isFile()) {
    throw new Error(`${label} must be a regular file, not a symlink or directory: ${reportPath}`);
  }
  for (
    let directory = path.dirname(resolved);
    isWithin(repoRoot, directory) && directory !== path.resolve(repoRoot);
    directory = path.dirname(directory)
  ) {
    if (lstatSync(directory).isSymbolicLink()) {
      throw new Error(`${label} uses a symlinked output directory: ${reportPath}`);
    }
  }
  const canonical = realpathSync(resolved);
  if (!isWithin(repoRoot, canonical) || !isWithin(realpathSync(repoRoot), canonical)) {
    throw new Error(`${label} resolves outside the expected output root: ${reportPath}`);
  }
  if (statSync(resolved).mtimeMs < startedAt) {
    throw new Error(`${label} is stale: ${reportPath}`);
  }
  if (details.size === 0) {
    throw new Error(`${label} is empty: ${reportPath}`);
  }
  return resolved;
}

export function assertCoberturaReport(repoRoot, reportPath, startedAt) {
  const resolved = assertRegularFile(repoRoot, reportPath, startedAt, 'Cobertura report');
  const xml = readFileSync(resolved, 'utf8').trim();
  if (
    !/^<\?xml\b/u.test(xml)
    || !/<coverage\b[^>]*\bline-rate="[^"]+"/u.test(xml)
    || !/<packages>/u.test(xml)
    || !/<\/coverage>\s*$/u.test(xml)
  ) {
    throw new Error(`Cobertura report is malformed: ${reportPath}`);
  }
  return resolved;
}

export function assertTrxContainsTests(repoRoot, resultsDirectory, startedAt) {
  const reports = globSync('**/*.trx', { cwd: resultsDirectory, nodir: true });
  if (reports.length === 0) {
    throw new Error(`No TRX result was produced in ${resultsDirectory}`);
  }
  let testCount = 0;
  for (const report of reports) {
    const reportPath = path.join(resultsDirectory, report);
    const resolved = assertRegularFile(repoRoot, reportPath, startedAt, 'TRX result');
    const xml = readFileSync(resolved, 'utf8');
    if (!/<TestRun\b/u.test(xml) || !/<\/TestRun>\s*$/u.test(xml)) {
      throw new Error(`TRX result is malformed: ${reportPath}`);
    }
    const outcomes = [...xml.matchAll(/<UnitTestResult\b[^>]*\boutcome="([^"]+)"/gu)]
      .map((match) => match[1]);
    if (outcomes.some((outcome) => !['Passed', 'NotExecuted', 'Skipped'].includes(outcome))) {
      throw new Error(`Failed tests were reported in ${reportPath}`);
    }
    testCount += outcomes.filter((outcome) => outcome === 'Passed').length;
  }
  if (testCount === 0) {
    throw new Error(`Zero tests ran in ${resultsDirectory}`);
  }
  return testCount;
}

export function run(command, args, cwd = REPO_ROOT, env = process.env, timeoutMs) {
  console.log(`[coverage] starting ${command} ${args.join(' ')}`);
  const result = spawnSync(command, args, {
    cwd,
    env,
    shell: process.platform === 'win32' && command.endsWith('.cmd'),
    stdio: 'inherit',
    timeout: timeoutMs,
  });
  if (result.error) {
    if (result.error.code === 'ETIMEDOUT') {
      throw new Error(`${command} ${args.join(' ')} exceeded ${timeoutMs / 60_000} minutes`);
    }
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} failed with exit code ${result.status}`);
  }
}

function removeOutputDirectory(outputDirectory) {
  const resolved = path.resolve(REPO_ROOT, outputDirectory);
  if (!isWithin(REPO_ROOT, resolved)) {
    throw new Error(`Refusing to remove an out-of-root coverage directory: ${outputDirectory}`);
  }
  const details = lstatSync(resolved, { throwIfNoEntry: false });
  if (details?.isSymbolicLink()) {
    throw new Error(`Refusing to remove symlinked coverage directory: ${outputDirectory}`);
  }
  let ancestor = path.dirname(resolved);
  while (!existsSync(ancestor)) {
    ancestor = path.dirname(ancestor);
  }
  if (!isWithin(realpathSync(REPO_ROOT), realpathSync(ancestor))) {
    throw new Error(`Refusing to remove coverage through an out-of-root directory: ${outputDirectory}`);
  }
  rmSync(resolved, { recursive: true, force: true });
  mkdirSync(resolved, { recursive: true });
  return resolved;
}

function supportsBubblewrap() {
  return process.platform === 'linux'
    && spawnSync('bwrap', ['--version'], { stdio: 'ignore' }).status === 0;
}

function status(scope, details) {
  const revision = execFileSync('git', ['rev-parse', '--short=12', 'HEAD'], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
  }).trim();
  console.log(`[coverage] revision=${revision} scope=${scope} ${details}`);
}

function findSingleCoberturaReport(shardDirectory) {
  const reports = globSync('**/coverage.cobertura.xml', { cwd: shardDirectory, nodir: true });
  if (reports.length !== 1) {
    throw new Error(
      `Expected exactly one Coverlet Cobertura report in ${shardDirectory}; found ${reports.length}`,
    );
  }
  return path.join(shardDirectory, reports[0]);
}

export function dotnetShardArguments(shard, shardDirectory) {
  const args = [
    'test', DOTNET_PROJECT, '--no-build', '--no-restore', PROPERTY,
    '--filter', shard.filter,
    '--logger', `trx;LogFileName=${shard.id}.trx`,
    '--results-directory', shardDirectory,
    '--collect', 'XPlat Code Coverage;Format=cobertura',
    '--',
    `DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=${DOTNET_INCLUDE}`,
    'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile=**/obj/**,**/*Designer.cs',
  ];
  if (shard.settings) {
    args.splice(5, 0, '--settings', shard.settings);
  }
  return args;
}

function runDotnetShard(shard, coverageRoot, startedAt) {
  const shardDirectory = path.join(coverageRoot, shard.id);
  mkdirSync(shardDirectory, { recursive: true });
  const args = dotnetShardArguments(shard, shardDirectory);
  run('dotnet', args, REPO_ROOT, process.env, (shard.timeoutMinutes ?? 15) * 60_000);
  const tests = assertTrxContainsTests(shardDirectory, shardDirectory, startedAt);
  const sourceReport = assertCoberturaReport(
    shardDirectory,
    findSingleCoberturaReport(shardDirectory),
    startedAt,
  );
  const reportPath = path.join(shardDirectory, 'coverage.cobertura.xml');
  copyFileSync(sourceReport, reportPath);
  assertCoberturaReport(shardDirectory, reportPath, startedAt);
  return tests;
}

function combineDotnetReports(coverageRoot, shardIds, startedAt) {
  const reportPaths = shardIds.map((id) => (
    path.join(coverageRoot, id, 'coverage.cobertura.xml')
  ));
  for (const reportPath of reportPaths) {
    assertCoberturaReport(path.dirname(reportPath), reportPath, startedAt);
  }
  const combined = path.join(coverageRoot, 'combined');
  mkdirSync(combined, { recursive: true });
  run('dotnet', ['tool', 'restore']);
  run('dotnet', [
    'tool', 'run', 'reportgenerator',
    `-reports:${reportPaths.join(';')}`,
    `-targetdir:${combined}`,
    '-reporttypes:Cobertura;JsonSummary;TextSummary;Html',
  ]);
  for (const report of ['Cobertura.xml', 'Summary.json', 'Summary.txt', 'index.html']) {
    assertRegularFile(REPO_ROOT, path.join(combined, report), startedAt, `Combined ${report}`);
  }
  const summary = JSON.parse(readFileSync(path.join(combined, 'Summary.json'), 'utf8'));
  const totals = summary.summary;
  const metrics = [
    ['lines', 'coveredlines', 'coverablelines'],
    ['branches', 'coveredbranches', 'totalbranches'],
    ['methods', 'coveredmethods', 'totalmethods'],
  ].map(([label, coveredKey, totalKey]) => {
    const covered = totals?.[coveredKey];
    const total = totals?.[totalKey];
    if (!Number.isFinite(covered) || !Number.isFinite(total) || covered > total) {
      throw new Error(`Combined .NET ${label} coverage summary is malformed`);
    }
    return `${label}=${covered}/${total} uncovered=${total - covered}`;
  });
  const instrumented = new Set(summary.coverage?.assemblies?.map(({ name }) => name));
  if (!instrumented.size || [...instrumented].some((name) => !DOTNET_ASSEMBLIES.includes(name))) {
    throw new Error('Combined .NET report contains absent or unexpected instrumented assemblies');
  }
  const absent = DOTNET_ASSEMBLIES.filter((name) => !instrumented.has(name));
  console.log(`[coverage] dotnet ${metrics.join(' ')} assemblies=${instrumented.size}/${DOTNET_ASSEMBLIES.length} absent=${absent.join(',') || 'none'} summary=${path.relative(REPO_ROOT, combined)}/Summary.json`);
  return absent;
}

export function runDotnetCoverage() {
  const startedAt = Date.now();
  const coverageRoot = removeOutputDirectory('TestResults/coverage');
  status(
    'dotnet',
    `source=${DOTNET_ASSEMBLIES.join(',')} (only loaded/instrumented assemblies; absent assemblies are not counted) exclude=tests,obj,*Designer.cs reports=${path.relative(REPO_ROOT, coverageRoot)} required=${expectedDotnetShardIds().join(',')}`,
  );
  run('dotnet', ['restore', DOTNET_PROJECT, '--locked-mode', PROPERTY]);
  run('dotnet', ['build', DOTNET_PROJECT, '--no-restore', PROPERTY]);
  const failures = [];
  const completed = [];
  for (const shard of TEST_SHARDS) {
    if (shard.requiresBubblewrap && !supportsBubblewrap()) {
      failures.push(`${shard.id}: unsupported (bubblewrap user namespace is required)`);
      continue;
    }
    try {
      const tests = runDotnetShard(shard, coverageRoot, startedAt);
      completed.push({ id: shard.id, tests });
    } catch (error) {
      failures.push(`${shard.id}: ${error.message}`);
    }
  }
  let absentAssemblies = DOTNET_ASSEMBLIES;
  let combinedReport = null;
  if (completed.length > 0) {
    try {
      absentAssemblies = combineDotnetReports(coverageRoot, completed.map(({ id }) => id), startedAt);
      combinedReport = 'combined/Cobertura.xml';
      if (absentAssemblies.length > 0) {
        failures.push(`assemblies absent from instrumentation: ${absentAssemblies.join(',')}`);
      }
    } catch (error) {
      failures.push(`report merge: ${error.message}`);
    }
  }
  writeFileSync(path.join(coverageRoot, 'status.json'), `${JSON.stringify({
    revision: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: REPO_ROOT, encoding: 'utf8' }).trim(),
    scope: DOTNET_ASSEMBLIES,
    complete: failures.length === 0,
    completed,
    missing: failures,
    absentAssemblies,
    combined: combinedReport,
  }, null, 2)}\n`);
  if (failures.length > 0) {
    console.error(`[coverage] dotnet partial; ${completed.length} verified shard(s), ${failures.join('; ')}`);
    throw new Error('Required .NET coverage families are partial');
  }
  console.log(`[coverage] dotnet complete; reports=${path.relative(REPO_ROOT, coverageRoot)}/combined/{Cobertura.xml,Summary.json,Summary.txt,index.html} status=${path.relative(REPO_ROOT, coverageRoot)}/status.json`);
}

function assertReports(reportDirectory, reports, startedAt, scope) {
  for (const report of reports) {
    assertRegularFile(REPO_ROOT, path.join(reportDirectory, report), startedAt, `${scope} report`);
  }
}

function printJavascriptSummary(reportDirectory, scope) {
  const report = JSON.parse(readFileSync(path.join(reportDirectory, 'coverage-summary.json'), 'utf8'));
  const totals = ['lines', 'branches', 'functions'].map((metric) => {
    const { total, covered } = report.total?.[metric] ?? {};
    if (!Number.isFinite(total) || !Number.isFinite(covered) || covered > total) {
      throw new Error(`${scope} ${metric} coverage summary is malformed`);
    }
    return `${metric}=${covered}/${total} uncovered=${total - covered}`;
  });
  const uncovered = Object.entries(report).filter(([file, metrics]) => (
    file !== 'total' && ['lines', 'branches', 'functions'].some((metric) => (
      metrics[metric]?.covered < metrics[metric]?.total
    ))
  ));
  console.log(`[coverage] ${scope} ${totals.join(' ')} uncoveredSources=${uncovered.length} summary=${path.relative(REPO_ROOT, reportDirectory)}/coverage-summary.json`);
}

export function runWebCoverage() {
  const startedAt = Date.now();
  const reportDirectory = removeOutputDirectory('apps/web/coverage');
  status(
    'web',
    'source=apps/web/src/**/*.{ts,tsx} excluding tests,fixtures,setup,declarations,generated reports=apps/web/coverage/{coverage-final.json,coverage-summary.json,lcov.info,index.html}',
  );
  run(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['--prefix', 'apps/web', 'run', 'coverage']);
  assertReports(reportDirectory, [
    'coverage-final.json', 'coverage-summary.json', 'lcov.info', 'index.html',
  ], startedAt, 'Web coverage');
  printJavascriptSummary(reportDirectory, 'web');
}

export function runNodeCoverage() {
  const startedAt = Date.now();
  const reportDirectory = removeOutputDirectory('coverage/node');
  const testFiles = NODE_TEST_GLOBS.flatMap((pattern) => {
    const files = globSync(pattern, { cwd: REPO_ROOT, nodir: true });
    if (files.length === 0) {
      throw new Error(`No Node coverage tests were found for ${pattern}`);
    }
    return files;
  }).sort();
  const c8 = path.join(REPO_ROOT, 'node_modules', 'c8', 'bin', 'c8.js');
  if (!existsSync(c8)) {
    throw new Error('Pinned c8 is unavailable; run npm run deps:ensure before coverage:node');
  }
  status(
    'node',
    `source=${NODE_SOURCE_GLOBS.join(',')} reports=coverage/node/{coverage-final.json,coverage-summary.json,lcov.info,index.html} tests=${testFiles.length}`,
  );
  run(process.execPath, [
    c8, '--all', '--extension=.mjs',
    ...NODE_SOURCE_GLOBS.map((pattern) => `--include=${pattern}`),
    ...NODE_EXCLUDES.map((pattern) => `--exclude=${pattern}`),
    `--temp-directory=${path.join(reportDirectory, '.tmp')}`,
    `--reports-dir=${reportDirectory}`,
    '--reporter=json', '--reporter=json-summary', '--reporter=lcov', '--reporter=text', '--reporter=html',
    process.execPath, '--test', ...testFiles,
  ]);
  assertReports(reportDirectory, [
    'coverage-final.json', 'coverage-summary.json', 'lcov.info', 'index.html',
  ], startedAt, 'Node coverage');
  const coverage = JSON.parse(readFileSync(path.join(reportDirectory, 'coverage-final.json'), 'utf8'));
  const untouched = path.join(REPO_ROOT, 'scripts', 'ci', 'dotnet-test-shards.mjs');
  if (!Object.keys(coverage).some((file) => path.resolve(file) === untouched)) {
    throw new Error(`Node all-source report omitted untouched product source: ${untouched}`);
  }
  printJavascriptSummary(reportDirectory, 'node');
}

function main(command) {
  const runners = { dotnet: runDotnetCoverage, web: runWebCoverage, node: runNodeCoverage };
  if (command === 'all') {
    const failures = [];
    for (const [name, runner] of Object.entries(runners)) {
      try {
        runner();
      } catch (error) {
        failures.push(`${name}: ${error.message}`);
      }
    }
    if (failures.length > 0) {
      throw new Error(`Coverage is partial: ${failures.join('; ')}`);
    }
    return;
  }
  if (!runners[command]) {
    throw new Error('usage: coverage.mjs <dotnet|web|node|all>');
  }
  runners[command]();
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv[2]);
  } catch (error) {
    console.error(`[coverage] ${error.stack ?? error}`);
    process.exitCode = 1;
  }
}
