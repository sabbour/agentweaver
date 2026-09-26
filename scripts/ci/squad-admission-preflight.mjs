#!/usr/bin/env node
import { pathToFileURL } from 'node:url';
import {
  KIND,
  canonicalPrNumber,
  canonicalRepository,
  canonicalSha,
  ledgerEvidence,
  readAdmissionLedger,
  resolveDeclaredExternalStateDirectory,
  validateAdmissionLedger,
} from './squad-admission-common.mjs';

export { KIND, resolveDeclaredExternalStateDirectory, validateAdmissionLedger as validateAdmissionPreflight };

export async function runAdmissionPreflight(repositoryValue, prValue, dependencies = {}) {
  const repository = canonicalRepository(repositoryValue);
  const prNumber = canonicalPrNumber(prValue);
  const headSha = canonicalSha(dependencies.headSha, 'launcher-attested live PR head');
  const stateDirectory = dependencies.stateDirectory ?? await resolveDeclaredExternalStateDirectory(dependencies.resolution);
  const persisted = dependencies.readLedger
    ? await dependencies.readLedger(stateDirectory, repository, prNumber)
    : await readAdmissionLedger(stateDirectory, repository, prNumber);
  const ledger = persisted?.ledger ?? persisted;
  const bytes = persisted?.bytes ?? Buffer.from(JSON.stringify(ledger));
  const result = validateAdmissionLedger(ledger, { repository, prNumber, headSha });
  return {
    operation: 'preflight',
    admitted: true,
    repository,
    prNumber,
    headSha,
    ledgerKey: persisted?.key ?? `admission/findings/${repository}/${prNumber}.json`,
    ...ledgerEvidence(bytes),
    findings: result.findings,
  };
}

function parseArguments(argv) {
  if (argv.length !== 4 || argv[2] !== '--head-sha') {
    throw new Error('usage: squad-admission-preflight.mjs <owner/repo> <pr-number> --head-sha <lowercase-40-sha>');
  }
  return { repository: argv[0], prNumber: argv[1], headSha: argv[3] };
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  console.log(JSON.stringify(await runAdmissionPreflight(args.repository, args.prNumber, { headSha: args.headSha })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(JSON.stringify({
      operation: 'preflight',
      error: error?.code ? 'filesystem operation failed' : error.message,
    }));
    process.exitCode = 1;
  });
}
