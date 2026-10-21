#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { constants } from 'node:fs';
import { chmod, link, open, readFile, rename, unlink } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  canonicalPrNumber,
  canonicalRepository,
  canonicalSha,
  ledgerEvidence,
  parseLedgerBytes,
  readAdmissionLedger,
  resolveDeclaredExternalStateDirectory,
  resolveLedgerPath,
  validateAdmissionLedger,
} from './squad-admission-common.mjs';

async function remove(path) {
  try {
    await unlink(path);
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  }
}

async function writeTemporary(destination, bytes, operations) {
  const temporary = join(dirname(destination), `.${randomUUID()}.tmp`);
  let handle;
  try {
    handle = await operations.open(temporary, constants.O_CREAT | constants.O_EXCL | constants.O_WRONLY, 0o600);
    await handle.writeFile(bytes);
    await handle.sync();
    await handle.close();
    handle = undefined;
    await operations.chmod(temporary, 0o600).catch((error) => {
      if (error?.code !== 'ENOSYS' && error?.code !== 'EPERM') throw error;
    });
    return temporary;
  } catch (error) {
    await handle?.close().catch(() => {});
    await operations.remove(temporary).catch(() => {});
    throw error;
  }
}

async function syncDirectory(path, operations) {
  let handle;
  try {
    handle = await operations.open(path, constants.O_RDONLY);
    await handle.sync();
  } catch (error) {
    if (!['EACCES', 'EINVAL', 'EISDIR', 'ENOSYS', 'EPERM'].includes(error?.code)) throw error;
  } finally {
    await handle?.close().catch(() => {});
  }
}

const defaultOperations = {
  chmod,
  link,
  open,
  readLedger: readAdmissionLedger,
  remove,
  rename,
};

export async function materializeAdmissionLedger(repositoryValue, prValue, options = {}) {
  const repository = canonicalRepository(repositoryValue);
  const prNumber = canonicalPrNumber(prValue);
  const headSha = canonicalSha(options.headSha);
  const replaceExistingHead = options.replaceExistingHead === undefined
    ? undefined
    : canonicalSha(options.replaceExistingHead, 'replacement head SHA');
  const operations = { ...defaultOperations, ...options.operations };
  const inputBytes = await (options.readInput ?? readFile)(options.inputFile);
  const inputLedger = parseLedgerBytes(inputBytes, 'input ledger');
  validateAdmissionLedger(inputLedger, { repository, prNumber, headSha });
  const stateDirectory = options.stateDirectory ?? await resolveDeclaredExternalStateDirectory(options.resolution);
  const { key, destination } = await resolveLedgerPath(stateDirectory, repository, prNumber, { createParent: true });
  const evidence = ledgerEvidence(inputBytes);

  let existing;
  try {
    existing = await operations.readLedger(stateDirectory, repository, prNumber);
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  }
  if (existing) {
    const existingEvidence = ledgerEvidence(existing.bytes);
    if (existingEvidence.sha256 === evidence.sha256 && Buffer.compare(existing.bytes, inputBytes) === 0) {
      return { operation: 'materialize', disposition: 'unchanged', repository, prNumber, headSha, ledgerKey: key, ...evidence };
    }
    if (!replaceExistingHead) throw new Error('admission ledger already exists with different bytes; --replace-existing-head is required');
    validateAdmissionLedger(existing.ledger, { repository, prNumber, headSha: replaceExistingHead });
  } else if (replaceExistingHead) {
    throw new Error('cannot replace a missing admission ledger');
  }

  const temporary = await writeTemporary(destination, inputBytes, operations);
  let backup;
  let installed = false;
  let preserveBackup = false;
  try {
    if (existing) {
      const current = await operations.readLedger(stateDirectory, repository, prNumber);
      if (Buffer.compare(current.bytes, existing.bytes) !== 0) {
        throw new Error('existing admission ledger changed during replacement');
      }
      backup = join(dirname(destination), `.${randomUUID()}.bak`);
      await operations.link(destination, backup);
      await operations.rename(temporary, destination);
      installed = true;
    } else {
      await operations.link(temporary, destination);
      installed = true;
      await operations.remove(temporary);
    }
    await syncDirectory(dirname(destination), operations);
    const persisted = await operations.readLedger(stateDirectory, repository, prNumber);
    validateAdmissionLedger(persisted.ledger, { repository, prNumber, headSha });
    const persistedEvidence = ledgerEvidence(persisted.bytes);
    if (persistedEvidence.sha256 !== evidence.sha256 || Buffer.compare(persisted.bytes, inputBytes) !== 0) {
      throw new Error('persisted admission ledger failed read-back verification');
    }
    if (backup) {
      await operations.remove(backup);
      backup = undefined;
    }
    return {
      operation: 'materialize',
      disposition: existing ? 'replaced' : 'created',
      repository,
      prNumber,
      headSha,
      ledgerKey: key,
      ...persistedEvidence,
    };
  } catch (error) {
    if (installed) {
      try {
        if (backup) {
          await operations.rename(backup, destination);
          backup = undefined;
        } else {
          await operations.remove(destination);
        }
      } catch (restoreError) {
        preserveBackup = true;
        throw new AggregateError([error, restoreError], 'admission ledger write failed and the prior destination could not be restored');
      }
    }
    throw error;
  } finally {
    await operations.remove(temporary).catch(() => {});
    if (backup && !preserveBackup) await operations.remove(backup).catch(() => {});
  }
}

export function parseMaterializeArguments(argv) {
  if (argv[0] !== 'materialize' || argv.length < 3) {
    throw new Error('usage: squad-admission-ledger.mjs materialize <owner/repo> <pr> --head-sha <lowercase-40-sha> --input-file <ledger.json> [--replace-existing-head <lowercase-40-sha>]');
  }
  const result = { repository: argv[1], prNumber: argv[2] };
  const allowed = new Set(['--head-sha', '--input-file', '--replace-existing-head']);
  for (let index = 3; index < argv.length; index += 2) {
    const option = argv[index];
    const value = argv[index + 1];
    if (!allowed.has(option)) throw new Error(`unknown option: ${option ?? ''}`);
    const property = option === '--head-sha' ? 'headSha' : option === '--input-file' ? 'inputFile' : 'replaceExistingHead';
    if (result[property] !== undefined) throw new Error(`repeated option: ${option}`);
    if (!value || value.startsWith('--')) throw new Error(`${option} requires a value`);
    result[property] = value;
  }
  if (!result.headSha || !result.inputFile) throw new Error('--head-sha and --input-file are required');
  return result;
}

async function main() {
  const args = parseMaterializeArguments(process.argv.slice(2));
  console.log(JSON.stringify(await materializeAdmissionLedger(args.repository, args.prNumber, {
    ...args,
  })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(JSON.stringify({
      operation: 'materialize',
      error: error?.code ? 'filesystem operation failed' : error.message,
    }));
    process.exitCode = 1;
  });
}
