// CLI invoked by `.github/workflows/ci.yml`'s `dotnet-coverage-combine` job
// to merge the per-shard Cobertura reports that `dotnet-test-shards`
// already produced (as a side effect of its single coverage-instrumented
// test invocation) into one combined report, without re-running any tests.
import { copyFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DOTNET_ASSEMBLIES, assertCoberturaReport, combineDotnetReports, expectedDotnetShardIds } from './coverage.mjs';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--downloaded-dir') {
      options.downloadedDir = argv[++index];
    } else if (argument === '--out') {
      options.out = argv[++index];
    } else {
      throw new Error(`unknown argument: ${argument}`);
    }
  }
  if (!options.downloadedDir || !options.out) {
    throw new Error('usage: coverage-combine.mjs --downloaded-dir <dir> --out <dir>');
  }
  return options;
}

export function main(argv) {
  const { downloadedDir, out } = parseArgs(argv);
  const downloadedRoot = path.resolve(REPO_ROOT, downloadedDir);
  const outRoot = path.resolve(REPO_ROOT, out);
  mkdirSync(outRoot, { recursive: true });

  // Freshly downloaded moments ago in this same job; the staleness check
  // `assertCoberturaReport` applies to a long-lived local working tree does
  // not apply here, so we pass startedAt=0 to disable it while keeping every
  // other integrity check (missing, malformed, symlinked, out-of-root, empty).
  const startedAt = 0;
  const completed = [];
  const missing = [];
  for (const id of expectedDotnetShardIds()) {
    const downloadedReport = path.join(downloadedRoot, `dotnet-coverage-${id}`, 'coverage.cobertura.xml');
    if (!existsSync(downloadedReport)) {
      missing.push(`${id}: coverage artifact was not produced (shard job may have failed, skipped, or timed out)`);
      continue;
    }
    const shardDirectory = path.join(outRoot, id);
    mkdirSync(shardDirectory, { recursive: true });
    const reportPath = path.join(shardDirectory, 'coverage.cobertura.xml');
    copyFileSync(downloadedReport, reportPath);
    try {
      assertCoberturaReport(shardDirectory, reportPath, startedAt);
      completed.push(id);
    } catch (error) {
      missing.push(`${id}: ${error.message}`);
    }
  }

  let absentAssemblies = DOTNET_ASSEMBLIES;
  let combinedReport = null;
  if (completed.length > 0) {
    try {
      absentAssemblies = combineDotnetReports(outRoot, completed, startedAt);
      combinedReport = 'combined/Cobertura.xml';
      if (absentAssemblies.length > 0) {
        missing.push(`assemblies absent from instrumentation: ${absentAssemblies.join(',')}`);
      }
    } catch (error) {
      missing.push(`report merge: ${error.message}`);
    }
  }

  writeFileSync(path.join(outRoot, 'status.json'), `${JSON.stringify({
    revision: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: REPO_ROOT, encoding: 'utf8' }).trim(),
    scope: DOTNET_ASSEMBLIES,
    complete: missing.length === 0,
    completed: completed.map((id) => ({ id })),
    missing,
    absentAssemblies,
    combined: combinedReport,
  }, null, 2)}\n`);

  if (missing.length > 0) {
    console.error(`[coverage-combine] partial; ${completed.length} verified shard(s), ${missing.join('; ')}`);
    throw new Error('Required .NET coverage families are partial');
  }
  console.log(`[coverage-combine] complete; reports=${path.relative(REPO_ROOT, outRoot)}/combined/{Cobertura.xml,Summary.json,Summary.txt,index.html}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(`[coverage-combine] ${error.stack ?? error}`);
    process.exitCode = 1;
  }
}
