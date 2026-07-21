#!/usr/bin/env node
// Rewrites Markdown docs by replacing ```mermaid *flowchart* fences with a
// pre-rendered graph-spec diagram embed (matching the convention used by
// docs/guide/architecture-aks.md), and writing the generated graph-spec JSON
// under docs/diagrams/src/. Non-flowchart Mermaid blocks (sequence/state/
// class/er) are left untouched and reported as skipped follow-ups.
//
// Usage:
//   node scripts/docs/migrate-mermaid.mjs <file.md> [<file2.md> ...]
//   node scripts/docs/migrate-mermaid.mjs --dir docs/deep-dive
//   node scripts/docs/migrate-mermaid.mjs --dir docs/deep-dive --dry
//
// Idempotent: files with no convertible fences are left unchanged.

import { readFile, writeFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { convertFlowchart, mermaidType } from './mermaid-to-graphspec.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const specsDir = path.join(repoRoot, 'docs', 'diagrams', 'src');

const args = process.argv.slice(2);
const dry = args.includes('--dry');

async function collectFiles() {
  const files = [];
  for (let i = 0; i < args.length; i += 1) {
    const a = args[i];
    if (a === '--dry') continue;
    if (a === '--dir') {
      const dir = path.resolve(repoRoot, args[++i]);
      const entries = await readdir(dir);
      for (const e of entries) if (e.endsWith('.md')) files.push(path.join(dir, e));
    } else {
      files.push(path.resolve(repoRoot, a));
    }
  }
  return files;
}

function slugBase(file) {
  // Namespace specs by the doc's parent directory so same-named docs in
  // different folders (e.g. deep-dive/agent-communication.md and
  // experience/agent-communication.md) don't collide on a shared basename.
  // NOTE: the first migrated batch (docs/deep-dive) shipped with bare
  // `<doc>-figN` names in PR #389; every later batch is directory-scoped.
  const dir = path.basename(path.dirname(file));
  const doc = path.basename(file, '.md');
  return dir === 'deep-dive' ? doc : `${dir}-${doc}`;
}

function embedBlock(name, alt) {
  return [
    `![${alt}](../diagrams/${name}.png)`,
    '',
    `<!-- Rendered from ../diagrams/src/${name}.json by docs/diagram-renderer +`,
    '     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.',
    '     Edit the JSON, then run `npm run docs:render-diagrams` and commit the',
    '     regenerated PNG + .hash.txt. -->',
  ].join('\n');
}

async function processFile(file) {
  const raw = await readFile(file, 'utf8');
  const lines = raw.split('\n');
  const out = [];
  const base = slugBase(file);
  let heading = base;
  let fig = 0;
  const result = { file, converted: [], skipped: [], warnings: [] };

  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    const h = line.match(/^#{1,6}\s+(.+?)\s*#*\s*$/);
    if (h) heading = h[1].trim();

    const fence = line.match(/^(\s*)```mermaid\s*$/);
    if (!fence) {
      out.push(line);
      continue;
    }
    // gather the fence body
    const indent = fence[1];
    const body = [];
    let j = i + 1;
    for (; j < lines.length; j += 1) {
      if (/^\s*```\s*$/.test(lines[j])) break;
      body.push(lines[j]);
    }
    const bodyStr = body.join('\n');
    const type = mermaidType(bodyStr);

    if (type === 'flowchart' || type === 'graph') {
      const nextFig = fig + 1;
      const name = `${base}-fig${nextFig}`;
      const title = heading === base ? name : `${heading}`;
      const converted = convertFlowchart(bodyStr, { title, name });
      if (converted && converted.spec.nodes.length > 0) {
        fig = nextFig;
        result.converted.push({ name, spec: converted.spec });
        result.warnings.push(...converted.warnings.map((w) => `${name}: ${w}`));
        out.push(indent + embedBlock(name, converted.spec.alt));
        i = j; // skip past closing fence
        continue;
      }
      result.skipped.push(`${type} (conversion produced no nodes)`);
    } else {
      result.skipped.push(type);
    }
    // leave block untouched
    for (let k = i; k <= j && k < lines.length; k += 1) out.push(lines[k]);
    i = j;
  }

  const newContent = out.join('\n');
  if (!dry && newContent !== raw) {
    await writeFile(file, newContent);
    for (const c of result.converted) {
      await writeFile(path.join(specsDir, `${c.name}.json`), `${JSON.stringify(c.spec, null, 2)}\n`);
    }
  }
  return result;
}

async function main() {
  const files = await collectFiles();
  let totalConverted = 0;
  const totalSkipped = {};
  const allWarnings = [];
  for (const file of files) {
    const r = await processFile(file);
    if (r.converted.length || r.skipped.length) {
      console.log(`\n${path.relative(repoRoot, file)}`);
      if (r.converted.length) console.log(`  converted: ${r.converted.map((c) => c.name).join(', ')}`);
      if (r.skipped.length) console.log(`  skipped:   ${r.skipped.join(', ')}`);
    }
    totalConverted += r.converted.length;
    for (const s of r.skipped) totalSkipped[s] = (totalSkipped[s] ?? 0) + 1;
    allWarnings.push(...r.warnings);
  }
  console.log(`\n=== Summary ===`);
  console.log(`Converted ${totalConverted} flowcharts across ${files.length} files.`);
  console.log(`Skipped (left as Mermaid): ${JSON.stringify(totalSkipped)}`);
  if (allWarnings.length) {
    console.log(`\nWarnings (${allWarnings.length}):`);
    for (const w of allWarnings) console.log(`  - ${w}`);
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
