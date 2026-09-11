#!/usr/bin/env node
// npm-facing entry point for the architecture-diagram pipeline. The actual
// build + Playwright capture logic lives in scripts/docs/capture-diagrams.mjs
// so it can be unit-imported/tested separately from CLI arg handling.
//
// docs/diagrams/src/*.json graph and sequence specs are the content source of
// truth for reusable static diagrams embedded across the documentation. Add a
// new specification only when an existing canonical diagram cannot be reused,
// then rerun this script. No per-diagram renderer code is required.
//
// Usage:
//   node scripts/docs/render-diagrams.mjs           # render + commit PNG + hash
//   node scripts/docs/render-diagrams.mjs --check   # CI: verify no drift (no browser needed)
//   node scripts/docs/render-diagrams.mjs --spec name
//   node scripts/docs/render-diagrams.mjs --spec first --spec second
//   node scripts/docs/render-diagrams.mjs --check --spec name

import { check, render } from './capture-diagrams.mjs';

function parseArgs(args) {
  const specs = [];
  let checkMode = false;
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--check') {
      checkMode = true;
      continue;
    }
    if (arg === '--spec') {
      const name = args[index + 1];
      if (!name || name.startsWith('--')) {
        throw new Error('--spec requires a diagram spec name');
      }
      specs.push(name.replace(/\.json$/, ''));
      index += 1;
      continue;
    }
    throw new Error(`Unknown argument: ${arg}`);
  }
  return { checkMode, specs };
}

async function main() {
  const { checkMode, specs } = parseArgs(process.argv.slice(2));
  if (checkMode) {
    const ok = await check(specs);
    if (!ok) process.exitCode = 1;
    return;
  }
  await render(specs);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
