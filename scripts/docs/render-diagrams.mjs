#!/usr/bin/env node
// Renders every Mermaid source file under docs/diagrams/*.mmd to a matching
// checked-in SVG using @mermaid-js/mermaid-cli (mmdc).
//
// Why this exists: GitHub's built-in Mermaid renderer clips long subgraph
// cluster labels and node labels (e.g. "AKS Cluster" -> "AKS Cluste",
// "PostgreSQL" -> "PostgreSQ") on the larger architecture diagrams in
// README.md and docs/guide/architecture-aks.md. Pre-rendering to static SVG
// at authoring time removes the dependency on any viewer's live Mermaid
// renderer (GitHub web UI, VitePress, etc.) and keeps the diagrams'
// appearance 100% consistent everywhere.
//
// The .mmd files remain the single source of truth -- edit those, then rerun
// this script (`npm run docs:render-diagrams`) and commit the regenerated
// SVGs alongside the source change. CI verifies the SVGs are not stale.
//
// Usage:
//   node scripts/docs/render-diagrams.mjs [--check]
//
// --check: render into memory/temp and diff against the committed SVGs
//          instead of overwriting them; exits non-zero on drift. Used by CI.

import { readdir, readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const diagramsDir = path.join(repoRoot, 'docs', 'diagrams');
const mmdcBin = path.join(
  repoRoot,
  'node_modules',
  '.bin',
  process.platform === 'win32' ? 'mmdc.cmd' : 'mmdc',
);

const checkMode = process.argv.includes('--check');

function renderOne(mmdFile, outSvg) {
  const args = [
    '-i', mmdFile,
    '-o', outSvg,
    '-b', 'white',
  ];
  // On locked-down/CI Linux runners Chromium needs --no-sandbox. This config
  // file is harmless to pass on Windows/macOS as well.
  const puppeteerConfig = path.join(__dirname, 'puppeteer-config.json');
  if (existsSync(puppeteerConfig)) {
    args.push('--puppeteerConfigFile', puppeteerConfig);
  }
  // Windows' .cmd shims must be spawned through a shell (execFileSync with
  // shell:true quotes args safely via the built-in escaping in Node >=18).
  execFileSync(mmdcBin, args, { stdio: 'inherit', shell: process.platform === 'win32' });
}

async function main() {
  const entries = await readdir(diagramsDir);
  const mmdFiles = entries.filter((f) => f.endsWith('.mmd')).sort();

  if (mmdFiles.length === 0) {
    console.log(`No .mmd files found under ${diagramsDir}`);
    return;
  }

  let tmpDir;
  if (checkMode) {
    tmpDir = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-diagrams-'));
  }

  let drift = false;

  for (const mmdName of mmdFiles) {
    const mmdPath = path.join(diagramsDir, mmdName);
    const svgName = mmdName.replace(/\.mmd$/, '.svg');
    const committedSvgPath = path.join(diagramsDir, svgName);

    if (checkMode) {
      const renderedSvgPath = path.join(tmpDir, svgName);
      renderOne(mmdPath, renderedSvgPath);

      const rendered = fingerprintSvg(await readFile(renderedSvgPath, 'utf8'));
      const committed = existsSync(committedSvgPath)
        ? fingerprintSvg(await readFile(committedSvgPath, 'utf8'))
        : null;

      if (committed === null) {
        console.error(`Missing committed SVG for ${mmdName}: ${committedSvgPath}`);
        drift = true;
      } else if (rendered !== committed) {
        console.error(`Diagram drift detected: ${svgName} does not match ${mmdName}. Run "npm run docs:render-diagrams" and commit the result.`);
        drift = true;
      } else {
        console.log(`OK: ${svgName} is in sync with ${mmdName}`);
      }
    } else {
      renderOne(mmdPath, committedSvgPath);
      console.log(`Rendered ${svgName} from ${mmdName}`);
    }
  }

  if (tmpDir) {
    await rm(tmpDir, { recursive: true, force: true });
  }

  if (drift) {
    process.exitCode = 1;
  }
}

// mmdc's rounded-rect path curves have tiny run-to-run floating point jitter
// (headless Chromium text-metrics variance), so a byte-for-byte SVG diff is
// not reliable. Compare a content fingerprint instead: every rendered label
// (node/edge/cluster text) in document order, plus the overall viewBox. This
// still catches real drift (label text changes, nodes added/removed, layout
// size changes) without false positives from sub-pixel curve noise.
function fingerprintSvg(svg) {
  const labels = [...svg.matchAll(/<p>(.*?)<\/p>/g)].map((m) => m[1]);
  const viewBox = svg.match(/viewBox="([^"]*)"/)?.[1] ?? '';
  return `${labels.join('|')}::${viewBox}`;
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
