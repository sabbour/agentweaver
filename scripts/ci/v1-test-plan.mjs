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

export function partitionMethods(discovery) {
  const methods = [...new Set([...discovery.matchAll(/^\s+(Agentweaver\.[\w.+`]+)(?:\(|\s*$)/gm)]
    .map((match) => match[1]))].sort();
  if (!methods.length) throw new Error('Test discovery produced no Agentweaver test methods');
  const partitions = Array.from({ length: brokerShardCount }, () => []);
  for (const method of methods) {
    const index = createHash('sha256').update(method).digest().readUInt32BE() % brokerShardCount;
    partitions[index].push(method);
  }
  if (partitions.some((partition) => !partition.length)) throw new Error('Test discovery produced an empty Broker shard');
  return partitions;
}

export function verifyTrx(xml) {
  const counters = xml.match(/<Counters\b[^>]*\/>/)?.[0];
  const count = (name) => Number(counters?.match(new RegExp(`\\b${name}="(\\d+)"`))?.[1]);
  if (!counters || !(count('executed') > 0) || count('executed') > count('total') ||
      count('passed') !== count('executed') || count('failed') !== 0 ||
      count('executed') + (count('notExecuted') || 0) !== count('total')) {
    throw new Error('Expected a nonempty, passing TRX receipt with accounted-for skips');
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
    const partitions = partitionMethods(run([...args, '--list-tests'], true));
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
