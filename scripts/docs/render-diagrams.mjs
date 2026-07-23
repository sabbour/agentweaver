#!/usr/bin/env node
// npm-facing entry point for the architecture-diagram pipeline. The actual
// build + Playwright capture logic lives in scripts/docs/capture-diagrams.mjs
// so it can be unit-imported/tested separately from CLI arg handling.
//
// docs/diagrams/src/*.json (graph-specs) are the content source of truth for
// the 3 Fluent-styled diagrams embedded in README.md and
// docs/guide/architecture-aks.md (the block diagram, and the simplified +
// detailed component diagrams). Adding a 4th diagram = add one more spec
// file under docs/diagrams/src/ + rerun this script; no bespoke per-diagram
// code needed anywhere in the pipeline.
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
