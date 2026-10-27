import { createHash } from 'node:crypto';
import { appendFileSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { suites, reportsIn } from '../coverage/dotnet.mjs';

export const brokerShardCount = 4;
export const testMatrix = suites.flatMap(([name, project]) =>
  name === 'identity-broker'
    ? Array.from({ length: brokerShardCount }, (_, index) => ({ name: `${name}-${index + 1}`, project, shard: index + 1 }))
    : [{ name, project, shard: 0 }]);

export const brokerWeightsPath = fileURLToPath(new URL('./broker-test-weights.json', import.meta.url));

export const environmentP1RequiredMethods = [
  'P1EnvironmentRegistrationLinksTheCommittedOperationAndRejectsRetrofitting',
  'P1ConcurrentRegistrationRetriesRetainOneExactBinding',
  'P1WorkspaceVolumeCompletionLinksTheRealProviderIdentityAndRetainsUncertainResults',
  'P1SandboxCompletionLinksTheActualProviderResourceAndLeaseFence',
].map((name) => `Agentweaver.Environment.Tests.EnvironmentP1CreationLinkPostgresTests.${name}`)
  .concat('Agentweaver.Environment.Tests.EnvironmentP1CreationReadPostgresTests.ReadsActualOwnerCommittedRecordsWithoutWritesAndRejectsForeignOrStaleJoins');

export function loadBrokerWeights(path = brokerWeightsPath) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

// Weights are measured seconds per test method (all theory cases summed). They only steer
// placement: every discovered method is still assigned to exactly one shard, and a method
// without a recorded weight uses defaultSeconds.
export function partitionMethods(discovery, weights = { defaultSeconds: 1, methods: {} }) {
  const methods = [...new Set([...discovery.matchAll(/^\s+(Agentweaver\.[\w.+`]+)(?:\(|\s*$)/gm)]
    .map((match) => match[1]))].sort();
  if (!methods.length) throw new Error('Test discovery produced no Agentweaver test methods');
  const weightOf = (method) => weights.methods?.[method] ?? weights.defaultSeconds ?? 1;
  const tieBreak = (method) => createHash('sha256').update(method).digest().readUInt32BE();
  // Longest-processing-time-first: place the heaviest remaining method on the least-loaded shard.
  // Ties use a stable hash, then the lowest shard index, so the result is deterministic.
  const ordered = methods.toSorted((a, b) => weightOf(b) - weightOf(a) || tieBreak(a) - tieBreak(b) || a.localeCompare(b));
  const partitions = Array.from({ length: brokerShardCount }, () => []);
  const loads = Array(brokerShardCount).fill(0);
  for (const method of ordered) {
    const index = loads.indexOf(Math.min(...loads));
    partitions[index].push(method);
    loads[index] += weightOf(method);
  }
  if (partitions.some((partition) => !partition.length)) throw new Error('Test discovery produced an empty Broker shard');
  return partitions.map((partition) => partition.sort());
}

export function measureWeights(trxDocuments, { thresholdSeconds = 1, source = '' } = {}) {
  const seconds = new Map();
  for (const xml of trxDocuments) {
    const methodById = new Map([...xml.matchAll(/<UnitTest\b[^>]*\bid="([^"]+)"[\s\S]*?<TestMethod\b[^>]*className="([^"]+)"[^>]*name="([^"]+)"/g)]
      .map((match) => [match[1], `${match[2]}.${match[3]}`]));
    for (const match of xml.matchAll(/<UnitTestResult\b[^>]*testId="([^"]+)"[^>]*duration="(\d+):(\d+):([\d.]+)"/g)) {
      const method = methodById.get(match[1]);
      if (!method) throw new Error(`TRX result ${match[1]} has no test definition`);
      seconds.set(method, (seconds.get(method) ?? 0) + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]));
    }
  }
  if (!seconds.size) throw new Error('TRX receipts contained no test results');
  const light = [...seconds.values()].filter((value) => value < thresholdSeconds);
  const round = (value) => Math.round(value * 10) / 10;
  return {
    source,
    defaultSeconds: round(light.length ? light.reduce((sum, value) => sum + value, 0) / light.length : thresholdSeconds) || 0.1,
    methods: Object.fromEntries([...seconds].filter(([, value]) => value >= thresholdSeconds)
      .sort(([a], [b]) => a.localeCompare(b)).map(([method, value]) => [method, round(value)])),
  };
}

export function verifyTrx(xml) {
  const counters = xml.match(/<Counters\b[^>]*\/>/)?.[0];
  const count = (name) => Number(counters?.match(new RegExp(`\\b${name}="(\\d+)"`))?.[1]);
  const skipped = [...xml.matchAll(/<UnitTestResult\b[^>]*outcome="NotExecuted"/g)].length;
  if (!counters || !(count('executed') > 0) || count('executed') > count('total') ||
      count('passed') !== count('executed') || count('failed') !== 0 ||
      count('executed') + skipped !== count('total')) {
    throw new Error('Expected a nonempty, passing TRX receipt with accounted-for skips');
  }
}

/** Require actual passing results, not discovery or compilation, for each named owner-transaction test. */
export function verifyRequiredMethods(xml, methods) {
  const methodsById = new Map([...xml.matchAll(/<UnitTest\b[^>]*\bid="([^"]+)"[\s\S]*?<TestMethod\b[^>]*className="([^"]+)"[^>]*name="([^"]+)"/g)]
    .map((match) => [match[1], `${match[2]}.${match[3]}`]));
  const results = [...xml.matchAll(/<UnitTestResult\b([^>]*)>/g)].map(([, attributes]) => ({
    method: methodsById.get(attributes.match(/\btestId="([^"]+)"/)?.[1]),
    outcome: attributes.match(/\boutcome="([^"]+)"/)?.[1],
  }));
  for (const method of methods) {
    const matches = results.filter((result) => result.method === method);
    if (!matches.length || matches.some((result) => result.outcome !== 'Passed')) {
      throw new Error(`Required test did not execute successfully: ${method}`);
    }
  }
}

function run(args, capture = false) {
  const result = spawnSync(process.env.DOTNET_HOST_PATH || 'dotnet', args, {
    encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit', maxBuffer: 32 * 1024 * 1024,
  });
  if (result.error || result.status !== 0) {
    throw new Error(`dotnet ${args.join(' ')} failed: ${result.error?.message ?? result.stdout ?? result.status}\n${result.stderr ?? ''}`);
  }
  return result.stdout;
}

function main([command, name]) {
  if (command === 'matrix') {
    console.log(JSON.stringify({ include: testMatrix }));
    return;
  }
  if (command === 'weights') {
    const [, source, ...files] = process.argv.slice(2);
    if (!source || !files.length) throw new Error('Usage: v1-test-plan.mjs weights <source description> <shard.trx>...');
    const weights = measureWeights(files.map((file) => readFileSync(file, 'utf8')), { source });
    writeFileSync(brokerWeightsPath, `${JSON.stringify(weights, null, 2)}\n`);
    console.log(`Wrote ${Object.keys(weights.methods).length} weighted methods to ${brokerWeightsPath}`);
    return;
  }
  const entry = testMatrix.find((item) => item.name === name);
  if (command !== 'run' || !entry) throw new Error('Usage: v1-test-plan.mjs matrix | run <matrix name>');
  const started = Date.now();
  const project = `tests/${entry.project}/${entry.project}.csproj`;
  const args = ['test', project, '--no-build', '--no-restore', '--configuration', 'Release'];
  const directory = `artifacts/coverage/dotnet/${entry.name}`;
  rmSync(directory, { recursive: true, force: true });
  mkdirSync(directory, { recursive: true });
  let methods;
  if (entry.shard) {
    const partitions = partitionMethods(run([...args, '--list-tests'], true), loadBrokerWeights());
    methods = partitions[entry.shard - 1];
    writeFileSync(`${directory}/partition.json`, JSON.stringify(partitions, null, 2));
    args.push('--filter', methods.map((method) => `FullyQualifiedName=${method}`).join('|'));
  }
  if (process.env.COLLECT_COVERAGE === 'true') {
    args.push('--settings', 'coverage.runsettings', '--collect', 'XPlat Code Coverage');
  }
  args.push('--logger', `trx;LogFileName=${entry.name}.trx`, '--results-directory', directory);
  run(args);
  const trx = readFileSync(`${directory}/${entry.name}.trx`, 'utf8');
  verifyTrx(trx);
  if (entry.name === 'environment') verifyRequiredMethods(trx, environmentP1RequiredMethods);
  if (methods) {
    const executed = new Set([...trx.matchAll(/<TestMethod\b[^>]*className="([^"]+)"[^>]*name="([^"]+)"/g)]
      .map((match) => `${match[1]}.${match[2]}`));
    if (executed.size !== methods.length || methods.some((method) => !executed.has(method))) {
      throw new Error('Broker TRX methods differ from the discovered shard; refusing incomplete or overlapping results');
    }
  }
  if (process.env.COLLECT_COVERAGE === 'true' && reportsIn(directory).length !== 1) {
    throw new Error(`Expected one distinct Cobertura report for ${entry.name}`);
  }
  const summary = `| ${entry.name} | ${((Date.now() - started) / 1000).toFixed(1)} seconds | ${process.env.COLLECT_COVERAGE === 'true'} |\n`;
  writeFileSync(`${directory}/timing.md`, summary);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
