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

// mmdc's rendering geometry (node/cluster bounding boxes, the overall SVG
// viewBox) depends on the host's font metrics, which differ across
// operating systems and font stacks -- confirmed empirically: the exact same
// .mmd rendered on Windows (Segoe UI) vs. Linux (DejaVu/Liberation
// fallback, since "Segoe UI" isn't installed) produces different viewBox
// dimensions (e.g. "0 0 1132.16 744.78" vs "0 0 1152.64 745.96") even
// though every rendered label's text is byte-identical. A committed SVG
// rendered on one OS will therefore always look like "drift" to a
// freshly-rendered comparison done on a different OS -- this bit both the
// GitHub-web-vs-VitePress live-Mermaid clipping bug this whole pre-render
// approach was built to avoid, and (ironically) an early version of this
// very drift check, which was authored/verified on Windows and then failed
// on CI's Linux runners for exactly this reason.
//
// The fingerprint therefore intentionally excludes all rendering-derived
// geometry (viewBox, path coordinates, node positions) and compares only
// the semantic content that mermaid's layout pass produces deterministically
// regardless of font metrics: every rendered label (node/edge/cluster text)
// in document order. This still catches real drift (label text changes,
// nodes/edges added or removed, wording changes) while being robust to
// which OS/font-stack rendered the currently-committed SVG.
function fingerprintSvg(svg) {
  const labels = [...svg.matchAll(/<p>(.*?)<\/p>/g)].map((m) => m[1]);
  return labels.join('|');
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
