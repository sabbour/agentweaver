#!/usr/bin/env node
// Renders every diagram under docs/diagrams/src/*.json to a static SVG under
// docs/diagrams/. Invoked via `npm run render` from this package (so it can
// resolve its own local vite/@playwright/test install) — the top-level
// `npm run docs:render-diagrams` (scripts/docs/render-diagrams.mjs) shells
// out to this script and then records source-file hashes in the manifest.
//
// Pipeline: a tiny standalone Vite + React app (index.html/src/) mounts a
// real <ReactFlow> graph laid out with `dagre` (see src/layout.ts) — the same
// stack (@xyflow/react + dagre) the product uses for its live workflow
// graphs — and Playwright drives a headless Chromium page to load it. Once
// layout settles the page hands back a hand-built, self-contained SVG string
// (real <rect>/<text>/<path>, no foreignObject/embedded HTML) computed
// directly from the dagre layout, which we write to disk. Using ReactFlow's
// own DOM/foreignObject export was deliberately avoided: GitHub's markdown
// image pipeline does not reliably render foreignObject-based SVGs, and a
// plain-SVG artifact is the same robust format Mermaid itself produces (just
// laid out correctly, this time).
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import { chromium } from '@playwright/test';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const diagramsDir = path.resolve(__dirname, '..', '..', '..', 'docs', 'diagrams');
const srcDir = path.join(diagramsDir, 'src');
const publicDir = path.join(__dirname, 'public');

const DIAGRAMS = ['aks-block-diagram', 'aks-component-simplified', 'aks-component-detailed'];

async function waitForServerReady(port, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/`);
      if (res.ok) return;
    } catch {
      // not ready yet
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Dev server on port ${port} did not become ready within ${timeoutMs}ms`);
}

async function renderOne(name, page, port) {
  const srcPath = path.join(srcDir, `${name}.json`);
  const source = await readFile(srcPath, 'utf8');
  // Served as a static file (rather than a huge base64 query string) so we
  // never risk hitting a URL-length limit on the larger detailed diagram.
  await writeFile(path.join(publicDir, 'diagram-data.json'), source, 'utf8');

  await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: 'networkidle' });
  await page.waitForFunction('window.__DIAGRAM_READY__ === true', { timeout: 20000 });
  const svg = await page.evaluate('window.__DIAGRAM_SVG__');

  const svgPath = path.join(diagramsDir, `${name}.svg`);
  await writeFile(svgPath, svg, 'utf8');
  console.log(`Rendered docs/diagrams/${name}.svg`);
}

async function main() {
  await mkdir(diagramsDir, { recursive: true });
  await mkdir(publicDir, { recursive: true });

  // A fixed, unlikely-to-collide port with strictPort so we fail loudly
  // instead of silently talking to some other already-running dev server
  // (this repo's apps/web dev server also defaults to Vite's usual 5173).
  const port = 47821;
  const server = await createServer({ root: __dirname, server: { port, strictPort: true, host: '127.0.0.1' } });
  await server.listen();

  // Vite's listen() can resolve slightly before the underlying Node HTTP
  // server is actually ready to accept external (non-Vite-internal)
  // connections — poll until a real HTTP request succeeds before launching
  // the browser, to avoid a flaky ERR_CONNECTION_REFUSED race.
  await waitForServerReady(port);

  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1200 } });
    for (const name of DIAGRAMS) {
      await renderOne(name, page, port);
    }
  } finally {
    await browser.close();
    await server.close();
  }
}

await main();
