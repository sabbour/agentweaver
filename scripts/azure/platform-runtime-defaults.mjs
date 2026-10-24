#!/usr/bin/env node
import {
  parsePlatformRuntimeDefaultsArgs,
  PLATFORM_RUNTIME_DEFAULTS_USAGE,
  runPlatformRuntimeDefaults,
} from './lib/platform-runtime-defaults.mjs';

let options;
try {
  options = parsePlatformRuntimeDefaultsArgs(process.argv.slice(2));
} catch (error) {
  console.error(`${error.message}\n\n${PLATFORM_RUNTIME_DEFAULTS_USAGE}`);
  process.exit(2);
}
if (options.help) {
  console.log(PLATFORM_RUNTIME_DEFAULTS_USAGE);
  process.exit(0);
}
try {
  const result = await runPlatformRuntimeDefaults(options);
  process.exitCode = result.exitCode;
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
