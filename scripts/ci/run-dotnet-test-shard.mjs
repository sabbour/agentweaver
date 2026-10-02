// CLI used by `.github/workflows/ci.yml`'s `dotnet-test-shards` job to run
// exactly one .NET test shard, with coverage collection as an opt-in flag
// rather than a separate test invocation. This keeps the ordinary PR path
// (no flag) and the coverage path (schedule/manual `collect_coverage=true`)
// as the SAME single `dotnet test` process per shard — never two — so
// turning on coverage never doubles the required test execution.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { TEST_SHARDS } from './dotnet-test-shards.mjs';
import { runDotnetShard } from './coverage.mjs';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

function parseArgs(argv) {
  const options = { shardId: null, collectCoverage: false };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--shard') {
      options.shardId = argv[++index];
    } else if (argument === '--collect-coverage') {
      options.collectCoverage = true;
    } else {
      throw new Error(`unknown argument: ${argument}`);
    }
  }
  if (!options.shardId) {
    throw new Error('usage: run-dotnet-test-shard.mjs --shard <id> [--collect-coverage]');
  }
  return options;
}

function main(argv) {
  const { shardId, collectCoverage } = parseArgs(argv);
  const shard = TEST_SHARDS.find(({ id }) => id === shardId);
  if (!shard) {
    throw new Error(`unknown .NET test shard: ${shardId}`);
  }
  const startedAt = Date.now();
  const coverageRoot = path.resolve(REPO_ROOT, 'TestResults');
  const tests = runDotnetShard(shard, coverageRoot, startedAt, { collectCoverage });
  console.log(
    `[run-dotnet-test-shard] shard=${shardId} tests=${tests} collectCoverage=${collectCoverage}`,
  );
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(`[run-dotnet-test-shard] ${error.stack ?? error}`);
    process.exitCode = 1;
  }
}
