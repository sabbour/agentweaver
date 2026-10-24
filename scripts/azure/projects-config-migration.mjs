#!/usr/bin/env node
import {
  parseProjectsConfigMigrationArgs, PROJECTS_CONFIG_MIGRATION_USAGE, runProjectsConfigMigration,
} from './lib/projects-config-migration.mjs';

let options;
try {
  options = parseProjectsConfigMigrationArgs(process.argv.slice(2));
} catch (error) {
  console.error(`${error.message}\n\n${PROJECTS_CONFIG_MIGRATION_USAGE}`);
  process.exit(2);
}
if (options.help) {
  console.log(PROJECTS_CONFIG_MIGRATION_USAGE);
  process.exit(0);
}
try {
  const receipt = await runProjectsConfigMigration(options);
  const operation = receipt.operation === 'bootstrap-first-platform-admin'
    ? 'PlatformAdmin bootstrap'
    : 'migration';
  console.log(`Projects & Config ${operation} ${receipt.result} run=${receipt.runId}; receipt=${receipt.receiptPath}`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
