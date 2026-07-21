#!/usr/bin/env node
// Renders (or checks) every diagram spec under docs/diagrams/specs/.
//
// Source of truth: docs/diagrams/specs/*.json (plain node+edge+group graph
// definitions, no hand-placed coordinates). Diagrams are entirely data-driven:
// adding a new one is "add a spec file", no code changes anywhere in this
// pipeline. `npm run docs:render-diagrams` discovers every spec file, shells
// out to scripts/docs/render-diagrams/ (a standalone, generic React Flow +
// dagre + Playwright renderer — see that folder's render.mjs/src/) to lay out
// and render each diagram to docs/diagrams/<name>.svg, then records a SHA-256
// hash of each source file in docs/diagrams/manifest.json.
//
// `npm run docs:check-diagrams` (what CI runs) does NOT re-render or compare
// any rendered geometry — that was the previous (Mermaid CLI) drift-check's
// mistake: SVG geometry is font-metric-dependent and differs across
// OS/font stacks even for byte-identical source, so a render-and-compare
// check is flaky across Windows (dev) vs Linux (CI). Instead it only checks
// "hash(current source file) == hash recorded in manifest.json", which is
// 100% deterministic, needs no browser, and has zero dependencies of its own.
import { createHash } from 'node:crypto';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const diagramsDir = path.join(repoRoot, 'docs', 'diagrams');
const specsDir = path.join(diagramsDir, 'specs');
const manifestPath = path.join(diagramsDir, 'manifest.json');
const rendererDir = path.join(__dirname, 'render-diagrams');

const checkMode = process.argv.includes('--check');

/** Every diagram spec under docs/diagrams/specs/*.json -- adding a new diagram is just
 *  "add a spec file, rerun this script", no code changes required anywhere. */
async function listDiagramNames() {
  if (!existsSync(specsDir)) return [];
  const entries = await readdir(specsDir, { withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith('.json'))
    .map((entry) => entry.name.replace(/\.json$/, ''))
    .sort();
}

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
  const diagrams = await listDiagramNames();
  let ok = true;
  for (const name of diagrams) {
    const srcPath = path.join(specsDir, `${name}.json`);
    const svgPath = path.join(diagramsDir, `${name}.svg`);
    const entry = manifest[name];

    if (!existsSync(srcPath)) {
      console.error(`Diagram spec missing: docs/diagrams/specs/${name}.json`);
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

  // Also catch stale manifest entries / stale SVGs left behind by a removed or renamed spec.
  for (const name of Object.keys(manifest)) {
    if (!diagrams.includes(name)) {
      console.error(`Stale manifest entry for "${name}" has no matching docs/diagrams/specs/${name}.json. Remove it (and the orphaned SVG, if any) or re-run "npm run docs:render-diagrams".`);
      ok = false;
    }
  }

  if (!ok) {
    process.exitCode = 1;
    return;
  }
  console.log(`All ${diagrams.length} architecture diagrams are in sync with their source definitions.`);
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

  const diagrams = await listDiagramNames();
  const manifest = {};
  for (const name of diagrams) {
    const srcPath = path.join(specsDir, `${name}.json`);
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
