#!/usr/bin/env node
/**
 * Command-line entry for the one-time, repeatable database maintenance setup. See
 * scripts/azure/README.md. Exit code 0 means the setup is complete and verified; 1 means something
 * is missing or failed, and the receipt says what.
 */
import { parseSetupArgs, runDbMaintenanceSetup, SETUP_USAGE } from './lib/db-maintenance-setup.mjs';

let options;
try {
  options = parseSetupArgs(process.argv.slice(2));
} catch (error) {
  console.error(`${error.message}\n\n${SETUP_USAGE}`);
  process.exit(2);
}
if (options.help) {
  console.log(SETUP_USAGE);
  process.exit(0);
}
try {
  const receipt = await runDbMaintenanceSetup(options);
  console.log(`db-maintenance-setup ${receipt.result} run=${receipt.runId}`);
} catch (error) {
  console.error(`db-maintenance-setup failed: ${error.message}`);
  process.exit(1);
}
