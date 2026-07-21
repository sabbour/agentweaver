#!/usr/bin/env node
// Renders (or checks) the AKS architecture diagrams under docs/diagrams/.
//
// Source of truth: docs/diagrams/src/*.json (plain node+edge definitions, no
// hand-placed coordinates). `npm run docs:render-diagrams` shells out to
// scripts/docs/render-diagrams/ (a standalone React Flow + dagre + Playwright
// app — see that folder's render.mjs) to lay out and render each diagram to
// docs/diagrams/<name>.svg, then records a SHA-256 hash of the source file in
// docs/diagrams/manifest.json.
//
// `npm run docs:check-diagrams` (what CI runs) does NOT re-render or compare
// any rendered geometry — that was the previous (Mermaid CLI) drift-check's
// mistake: SVG geometry is font-metric-dependent and differs across
// OS/font stacks even for byte-identical source, so a render-and-compare
// check is flaky across Windows (dev) vs Linux (CI). Instead it only checks
// "hash(current source file) == hash recorded in manifest.json", which is
// 100% deterministic, needs no browser, and has zero dependencies of its own.
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const diagramsDir = path.join(repoRoot, 'docs', 'diagrams');
const srcDir = path.join(diagramsDir, 'src');
const manifestPath = path.join(diagramsDir, 'manifest.json');
const rendererDir = path.join(__dirname, 'render-diagrams');

const DIAGRAMS = ['aks-block-diagram', 'aks-component-simplified', 'aks-component-detailed'];

const checkMode = process.argv.includes('--check');

function hashFile(contents) {
  // Normalize CRLF -> LF before hashing. Git's autocrlf normalization means a
  // source file can be CRLF on a Windows dev machine but LF once committed/
  // checked out (e.g. on Linux CI); hashing raw bytes would make the drift
  // check itself line-ending-sensitive, repeating the exact class of
  // cross-platform flakiness this hash-based check was built to avoid.
  const normalized = contents.toString('utf8').replace(/\r\n/g, '\n');
  return createHash('sha256').update(normalized).digest('hex');
}

async function loadManifest() {
  if (!existsSync(manifestPath)) return {};
  return JSON.parse(await readFile(manifestPath, 'utf8'));
}

async function runCheck() {
  const manifest = await loadManifest();
  let ok = true;
  for (const name of DIAGRAMS) {
    const srcPath = path.join(srcDir, `${name}.json`);
    const svgPath = path.join(diagramsDir, `${name}.svg`);
    const entry = manifest[name];

    if (!existsSync(srcPath)) {
      console.error(`Diagram source missing: docs/diagrams/src/${name}.json`);
      ok = false;
      continue;
    }
    if (!existsSync(svgPath)) {
      console.error(`Rendered SVG missing: docs/diagrams/${name}.svg. Run "npm run docs:render-diagrams" and commit the result.`);
      ok = false;
      continue;
    }
    if (!entry) {
      console.error(`No manifest entry for "${name}". Run "npm run docs:render-diagrams" and commit the result.`);
      ok = false;
      continue;
    }

    const currentHash = hashFile(await readFile(srcPath));
    if (currentHash !== entry.sourceHash) {
      console.error(
        `Diagram drift detected: ${name}.svg does not match ${name}.json (source hash changed). ` +
          `Run "npm run docs:render-diagrams" and commit the result.`,
      );
      ok = false;
    }
  }

  if (!ok) {
    process.exitCode = 1;
    return;
  }
  console.log(`All ${DIAGRAMS.length} architecture diagrams are in sync with their source definitions.`);
}

async function runRender() {
  if (!existsSync(path.join(rendererDir, 'node_modules'))) {
    console.log('Installing scripts/docs/render-diagrams dependencies (first run only)...');
    const install = spawnSync('npm', ['install'], { cwd: rendererDir, stdio: 'inherit', shell: true });
    if (install.status !== 0) {
      console.error('npm install failed in scripts/docs/render-diagrams');
      process.exit(install.status ?? 1);
    }
  }

  const render = spawnSync('npm', ['run', 'render'], { cwd: rendererDir, stdio: 'inherit', shell: true });
  if (render.status !== 0) {
    process.exit(render.status ?? 1);
  }

  const manifest = await loadManifest();
  for (const name of DIAGRAMS) {
    const srcPath = path.join(srcDir, `${name}.json`);
    manifest[name] = { sourceHash: hashFile(await readFile(srcPath)), generatedAt: new Date().toISOString() };
  }
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
  console.log('Updated docs/diagrams/manifest.json');
}

if (checkMode) {
  await runCheck();
} else {
  await runRender();
}
