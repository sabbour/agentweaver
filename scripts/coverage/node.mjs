import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const output = path.join(root, 'artifacts', 'coverage', 'node');
const lcov = path.join(output, 'lcov.info');
const spec = path.join(output, 'summary.txt');

export function hasValidatorCoverage(report) {
  const section = report.split(/^end_of_record\s*$/m).find((record) =>
    record.split(/\r?\n/).some((line) => {
      if (!line.startsWith('SF:')) return false;
      const source = line.slice(3).replaceAll('\\', '/');
      return source === 'scripts/release/validate.mjs' || source.endsWith('/scripts/release/validate.mjs');
    }));
  return section !== undefined && /^DA:\d+,[1-9]\d*(?:,|$)/m.test(section);
}

async function main() {
  rmSync(output, { recursive: true, force: true });
  mkdirSync(output, { recursive: true });

  const args = [
    '--test',
    '--experimental-test-coverage',
    '--test-coverage-include=scripts/release/*.mjs',
    '--test-reporter=spec',
    '--test-reporter-destination=stdout',
    '--test-reporter=lcov',
    `--test-reporter-destination=${lcov}`,
    'scripts/release/tests/*.test.mjs',
    'scripts/azure/tests/*.test.mjs',
  ];
  const chunks = [];
  const child = spawn(process.execPath, args, { cwd: root, stdio: ['inherit', 'pipe', 'inherit'] });
  child.stdout.on('data', (chunk) => {
    chunks.push(chunk);
    process.stdout.write(chunk);
  });
  const status = await new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', (code, signal) => resolve({ code, signal }));
  });
  writeFileSync(spec, Buffer.concat(chunks));
  if (status.code !== 0) {
    throw new Error(`Node tests failed (${status.signal ?? `exit ${status.code}`})`);
  }
  let report;
  try {
    report = readFileSync(lcov, 'utf8');
  } catch {
    throw new Error(`Node coverage report missing: ${lcov}`);
  }
  if (!hasValidatorCoverage(report)) {
    throw new Error(`Node coverage report has no executed validator target: ${lcov}`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
}
