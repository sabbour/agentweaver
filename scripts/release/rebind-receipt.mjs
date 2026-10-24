/**
 * Rebinds a verified preparation receipt to the v1 commit that integrated a squash-merged release.
 * The original plan source and generated release artifacts remain independently verifiable.
 */
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyPreparation } from './preparation.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SHA = /^[a-f0-9]{40}$/;
const RECEIPT_PATH = /^releases\/receipts\/([a-f0-9]{40})\.json$/;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

function fail(message) {
  throw new Error(`release receipt rebind: ${message}`);
}

export function createReboundReceipt(root, originalReceiptPath, integratedSourceSha, {
  readFile = readFileSync,
} = {}) {
  const git = (...args) => execFileSync('git', args, {
    cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  const match = RECEIPT_PATH.exec(originalReceiptPath);
  if (!match) fail('original receipt must be under releases/receipts/<source-sha>.json');
  if (!SHA.test(integratedSourceSha ?? '')) fail('integrated source must be a full lowercase commit SHA');
  const originalBytes = readFile(path.join(root, originalReceiptPath));
  let original;
  try {
    original = JSON.parse(originalBytes.toString('utf8'));
  } catch (error) {
    fail(`original receipt is not valid JSON: ${error.message}`);
  }
  if (original.schemaVersion !== 1 || original.sourceSha !== match[1]) {
    fail('original receipt must be a source-bound version-application receipt');
  }
  verifyPreparation(root, original, { readFile });

  let preparationBaseSha;
  try {
    const bases = git('merge-base', '--all', integratedSourceSha, original.sourceSha).split(/\r?\n/);
    if (bases.length !== 1 || !SHA.test(bases[0])) fail('could not establish one common ancestor for the squash merge');
    preparationBaseSha = bases[0];
    git('merge-base', '--is-ancestor', integratedSourceSha, 'HEAD');
  } catch (error) {
    if (error.message.startsWith('release receipt rebind:')) throw error;
    fail(`integrated source is not in the current history: ${error.message}`);
  }

  const rebound = {
    schemaVersion: 2,
    sourceSha: integratedSourceSha,
    planSourceSha: original.sourceSha,
    preparationBaseSha,
    originalReceiptSha256: hash(originalBytes),
    manifestPath: original.manifestPath,
    planChecksum: original.planChecksum,
    appliedAt: original.appliedAt,
    components: original.components,
    changesets: original.changesets,
  };
  verifyPreparation(root, rebound, { readFile });
  return rebound;
}

function main(argv) {
  if (argv.length !== 2) {
    fail('usage: npm run release:rebind-receipt -- <original-receipt> <integrated-source-sha>');
  }
  const [originalReceiptPath, integratedSourceSha] = argv;
  const git = (...args) => execFileSync('git', args, {
    cwd: repositoryRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  if (git('status', '--porcelain', '--untracked-files=all')) {
    fail('working tree must be clean before rebinding a receipt');
  }
  const head = git('rev-parse', 'HEAD');
  try {
    git('fetch', '--quiet', 'origin', 'v1');
    if (git('rev-parse', 'origin/v1') !== integratedSourceSha) {
      fail('integrated source SHA must match the current origin/v1 tip');
    }
    git('merge-base', '--is-ancestor', integratedSourceSha, head);
  } catch (error) {
    fail(`integrated source SHA must already be on origin/v1: ${error.message}`);
  }
  const receipt = createReboundReceipt(repositoryRoot, originalReceiptPath, integratedSourceSha);
  const output = `releases/receipts/${integratedSourceSha}.json`;
  if (existsSync(path.join(repositoryRoot, output))) fail(`refusing to overwrite ${output}`);
  writeFileSync(path.join(repositoryRoot, output), `${JSON.stringify(receipt, null, 2)}\n`, { flag: 'wx' });
  console.log(`Rebound source-verified release receipt: ${output}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(`Release receipt rebind failed: ${error.message}`);
    process.exitCode = 1;
  }
}
