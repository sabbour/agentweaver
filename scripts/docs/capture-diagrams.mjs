#!/usr/bin/env node
// Builds docs/diagram-renderer (a small Vite + React Flow app, see that
// folder's README) and uses Playwright to screenshot each
// docs/diagrams/src/*.json graph or sequence spec as a static PNG, replacing the old
// mermaid-cli pipeline. See scripts/docs/render-diagrams.mjs for the
// npm-facing entry point (render vs. --check) that calls into this module.

import { cp, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync, createReadStream, statSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const specsDir = path.join(repoRoot, 'docs', 'diagrams', 'src');
const outDir = path.join(repoRoot, 'docs', 'diagrams');
const rendererDir = path.join(repoRoot, 'docs', 'diagram-renderer');
const rendererPublicSpecsDir = path.join(rendererDir, 'public', 'specs');
const rendererDistDir = path.join(rendererDir, 'dist');

const DPR = 2; // export at 2x for crisp embeds on high-DPI displays

async function listSpecNames() {
  const entries = await readdir(specsDir);
  return entries
    .filter((f) => f.endsWith('.json') && !f.endsWith('-spec.schema.json'))
    .map((f) => f.replace(/\.json$/, ''))
    .sort();
}

function npmBin() {
  return process.platform === 'win32' ? 'npm.cmd' : 'npm';
}

async function buildRendererApp(specNames) {
  await rm(rendererPublicSpecsDir, { recursive: true, force: true });
  await mkdir(rendererPublicSpecsDir, { recursive: true });
  for (const name of specNames) {
    await cp(path.join(specsDir, `${name}.json`), path.join(rendererPublicSpecsDir, `${name}.json`));
  }

  if (!existsSync(path.join(rendererDir, 'node_modules'))) {
    execFileSync(npmBin(), ['install'], { cwd: rendererDir, stdio: 'inherit', shell: process.platform === 'win32' });
  }
  execFileSync(npmBin(), ['run', 'build'], { cwd: rendererDir, stdio: 'inherit', shell: process.platform === 'win32' });
}

const MIME = {
  '.html': 'text/html',
  '.js': 'text/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
};

// A static file server, not file://, because Chromium blocks `fetch()`
// against file: URLs (the built app fetches its own /specs/*.json at
// runtime) -- an http origin sidesteps that CORS restriction entirely.
function serveDist(dir) {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const urlPath = decodeURIComponent(req.url.split('?')[0]);
      const filePath = path.join(dir, urlPath === '/' ? 'index.html' : urlPath);
      if (!existsSync(filePath) || !statSync(filePath).isFile()) {
        res.writeHead(404);
        res.end();
        return;
      }
      const ext = path.extname(filePath);
      res.writeHead(200, { 'Content-Type': MIME[ext] ?? 'application/octet-stream' });
      createReadStream(filePath).pipe(res);
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function captureAll(specNames) {
  await buildRendererApp(specNames);

  const server = await serveDist(rendererDistDir);
  const { port } = server.address();
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ deviceScaleFactor: DPR });

    for (const name of specNames) {
      await page.goto(`http://127.0.0.1:${port}/?spec=${encodeURIComponent(name)}`, {
        waitUntil: 'domcontentloaded',
        timeout: 60000,
      });
      await page.waitForSelector('#diagram-root[data-diagram-ready="true"]', { timeout: 60000 });
      const el = await page.$('#diagram-root');
      const outPath = path.join(outDir, `${name}.png`);
      await el.screenshot({ path: outPath });
      console.log(`Rendered ${name}.png`);
    }
  } finally {
    await browser.close();
    server.close();
  }
}

function canonicalize(value) {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === 'object') {
    return Object.keys(value)
      .sort()
      .reduce((acc, k) => {
        acc[k] = canonicalize(value[k]);
        return acc;
      }, {});
  }
  return value;
}

async function specHash(name) {
  const raw = await readFile(path.join(specsDir, `${name}.json`), 'utf8');
  const canonical = JSON.stringify(canonicalize(JSON.parse(raw)));
  const { createHash } = await import('node:crypto');
  return createHash('sha256').update(canonical).digest('hex');
}

export async function render() {
  const specNames = await listSpecNames();
  await captureAll(specNames);
  for (const name of specNames) {
    const hash = await specHash(name);
    await writeFile(path.join(outDir, `${name}.hash.txt`), `${hash}\n`);
    console.log(`Wrote ${name}.hash.txt`);
  }
}

// Fast, browser-free drift check: a graph-spec's committed PNG is trusted to
// still be accurate as long as the spec's content hash matches the hash
// recorded the last time someone ran `npm run docs:render-diagrams`. This
// deliberately does NOT re-render and diff pixels/geometry in CI -- Trinity's
// earlier mermaid-cli drift check re-rendered on every CI run and compared
// SVG geometry, which broke because mmdc's layout geometry (viewBox, path
// coordinates) depends on the host's installed font metrics and differs
// between Windows and Linux runners even for byte-identical input. Comparing
// a content hash sidesteps that class of bug entirely: it only fails when the
// *spec* (nodes/edges/labels) actually changed since the PNG was last built.
export async function check() {
  const specNames = await listSpecNames();
  let drift = false;
  for (const name of specNames) {
    const hash = await specHash(name);
    const hashFile = path.join(outDir, `${name}.hash.txt`);
    const pngFile = path.join(outDir, `${name}.png`);
    if (!existsSync(hashFile) || !existsSync(pngFile)) {
      console.error(`Missing rendered output for ${name}: run "npm run docs:render-diagrams" and commit the result.`);
      drift = true;
      continue;
    }
    const committed = (await readFile(hashFile, 'utf8')).trim();
    if (committed !== hash) {
      console.error(`Diagram drift detected: ${name}.json changed since ${name}.png was last rendered. Run "npm run docs:render-diagrams" and commit the result.`);
      drift = true;
    } else {
      console.log(`OK: ${name}.png is in sync with ${name}.json`);
    }
  }
  return !drift;
}
