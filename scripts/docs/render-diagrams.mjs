#!/usr/bin/env node
// npm-facing entry point for the architecture-diagram pipeline. The actual
// build + Playwright capture logic lives in scripts/docs/capture-diagrams.mjs
// so it can be unit-imported/tested separately from CLI arg handling.
//
// docs/diagrams/src/*.json graph specifications are the source of truth for
// the reusable static diagrams embedded across the documentation. Add a new
// specification only when an existing canonical diagram cannot be reused, then
// rerun this script. No per-diagram renderer code is required.
//
// Usage:
//   node scripts/docs/render-diagrams.mjs           # render + commit PNG + hash
//   node scripts/docs/render-diagrams.mjs --check   # CI: verify no drift (no browser needed)

import { check, render } from './capture-diagrams.mjs';

const checkMode = process.argv.includes('--check');

async function main() {
  if (checkMode) {
    const ok = await check();
    if (!ok) process.exitCode = 1;
    return;
  }
  await render();
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
