#!/usr/bin/env node
import { assertVersionMirrors } from "./shared.mjs";

try {
  const version = assertVersionMirrors(process.cwd());
  console.log(`Version mirrors are synchronized at ${version}.`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
