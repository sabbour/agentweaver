#!/usr/bin/env node
import { assertVersionMirrors } from "./shared.mjs";
try { console.log(`Version mirrors are synchronized at ${assertVersionMirrors(process.cwd())}.`); }
catch (error) { console.error(error.message); process.exitCode = 1; }
