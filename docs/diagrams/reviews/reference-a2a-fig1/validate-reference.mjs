// Bounded validation runner; does not change shared pipeline/configuration.
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  createDiagramStamp, drawioExportArgs, fileHash, verifyDrawioVersion,
} from '../../../../scripts/docs/diagram-sources.mjs';

const review = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(review, '..', '..', '..', '..');
const docs = path.join(root, 'docs');
const plan = JSON.parse(await readFile(path.join(root, '.github', 'skills', 'docs-diagram-audit', 'reports', 'plan-reference.json'), 'utf8'));
const names = ['reference-a2a-fig1', 'reference-scaling-data-layer-fig1'];
const save = async (name, value) => writeFile(path.join(review, name), JSON.stringify(value, null, 2) + '\n');
const command = process.argv[2];

if (command === 'render') {
  const renderer = path.join(docs, 'diagrams', 'reviews', 'canonical-api-host', 'renderer', 'desktop', 'draw.io.exe');
  const prefixArgs = [`--user-data-dir=${path.join(review, 'renderer-cache')}`];
  const rendererVersion = verifyDrawioVersion({ command: renderer, prefixArgs });
  const results = [];
  for (const name of names) {
    const source = { name, kind: 'drawio', path: path.join(docs, 'diagrams', 'src', `${name}.drawio`) };
    const png = path.join(docs, 'diagrams', `${name}.png`);
    const checkPng = path.join(docs, 'diagrams', 'reviews', name, 'render-verification.png');
    execFileSync(renderer, [...prefixArgs, ...drawioExportArgs(source.path, checkPng, 'png')], { stdio: 'pipe' });
    const expected = await fileHash(png);
    const actual = await fileHash(checkPng);
    if (expected !== actual) throw new Error(`${name}: independently rendered PNG does not match the inspected final PNG`);
    const stamp = await createDiagramStamp(source, source.path, png, { rendererVersion });
    await writeFile(path.join(docs, 'diagrams', `${name}.hash.txt`), JSON.stringify(stamp, null, 2) + '\n');
    results.push({ name, rendererVersion, png_sha256: expected, independent_export_identical: true });
  }
  await save('render-validation.json', results);
  console.log(JSON.stringify(results, null, 2));
} else if (command === 'links') {
  const { createMarkdownRenderer } = await import('../../../node_modules/vitepress/dist/node/index.js');
  const md = await createMarkdownRenderer(docs, { attrs: { disable: true }, highlight: () => '' });
  const cache = new Map();
  const parse = async (file) => {
    if (cache.has(file)) return cache.get(file);
    const text = await readFile(file, 'utf8');
    const tokens = md.parse(text, { path: file, relativePath: path.relative(docs, file) });
    const ids = new Set();
    const links = [];
    const walk = (items) => {
      for (const token of items) {
        if (token.type === 'heading_open' && token.attrGet('id')) ids.add(token.attrGet('id'));
        if (token.type === 'link_open' && token.attrGet('class') !== 'header-anchor') links.push(token.attrGet('href'));
        if (token.type === 'image') links.push(token.attrGet('src'));
        if (token.children) walk(token.children);
      }
    };
    walk(tokens);
    for (const match of text.matchAll(/<(?:a|h[1-6])\b[^>]*\bid=["']([^"']+)["']/g)) ids.add(match[1]);
    const parsed = { ids, links };
    cache.set(file, parsed);
    return parsed;
  };
  const failures = [];
  let local = 0;
  let fragments = 0;
  let external = 0;
  for (const relative of plan.document_paths) {
    const file = path.join(root, relative);
    const parsed = await parse(file);
    for (const href of parsed.links) {
      if (!href) continue;
      if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(href)) { external++; continue; }
      const [rawPath, rawFragment] = href.split('#', 2);
      const decoded = decodeURIComponent(rawPath.split('?')[0]);
      let target = decoded.startsWith('/') ? path.join(docs, decoded) : path.resolve(path.dirname(file), decoded || path.basename(file));
      if (!existsSync(target) && target.endsWith('.html')) target = target.slice(0, -5) + '.md';
      if (!existsSync(target) && !path.extname(target)) {
        if (existsSync(target + '.md')) target += '.md';
        else target = path.join(target, 'index.md');
      }
      local++;
      if (!existsSync(target)) { failures.push({ document: relative, href, error: 'missing target' }); continue; }
      if (rawFragment && target.endsWith('.md')) {
        fragments++;
        const ids = (await parse(target)).ids;
        if (!ids.has(decodeURIComponent(rawFragment))) {
          failures.push({ document: relative, href, error: 'missing rendered heading ID', available: [...ids] });
        }
      }
    }
  }
  const result = { documents: plan.document_paths.length, local_links: local, fragments, external_urls_not_network_probed: external, failures };
  await save('link-validation.json', result);
  console.log(JSON.stringify(result, null, 2));
  if (failures.length) process.exitCode = 1;
} else if (command === 'build') {
  const { build } = await import('../../../node_modules/vitepress/dist/node/index.js');
  const output = path.join(review, 'docs-build');
  let pageCount;
  await mkdir(output, { recursive: true });
  await build(docs, {
    onAfterConfigResolve(config) {
      config.outDir = output;
      config.tempDir = path.join(review, 'docs-build-temp');
      config.cacheDir = path.join(review, 'docs-build-cache');
      config.vite ??= {};
      config.vite.cacheDir = path.join(review, 'docs-vite-cache');
      pageCount = config.pages.length;
    },
  });
  await save('build-validation.json', { passed: true, pages: pageCount, all_configured_pages_included: true,
    command: 'VitePress build(docs) with unchanged site config/theme; output/temp/cache redirected into owned review directory',
    output: path.relative(root, output).replaceAll('\\', '/') });
  console.log(`Built all ${pageCount} configured pages.`);
} else {
  throw new Error('Expected render, links, or build');
}
