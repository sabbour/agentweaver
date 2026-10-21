#!/usr/bin/env node
/**
 * Command-line entry for the v1 P1 database maintenance runner. See scripts/azure/README.md.
 * Exit code 0 means the run (or dry run) passed; 1 means it failed and the receipt says why.
 */
import { parseDbMaintenanceArgs, runDbMaintenance, USAGE } from './lib/db-maintenance.mjs';

let options;
try {
  options = parseDbMaintenanceArgs(process.argv.slice(2));
} catch (error) {
  console.error(`${error.message}\n\n${USAGE}`);
  process.exit(2);
}
if (options.help) {
  console.log(USAGE);
  process.exit(0);
}
try {
  const receipt = await runDbMaintenance(options);
  console.log(`db-maintenance ${receipt.result} run=${receipt.runId}`);
} catch (error) {
  console.error(`db-maintenance failed: ${error.message}`);
  process.exit(1);
}
