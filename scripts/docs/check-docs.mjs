import { execFileSync } from 'node:child_process';
import { access, readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const docsRoot = path.join(repoRoot, 'docs');
const distRoot = path.join(docsRoot, '.vitepress', 'dist');
const mapPath = path.join(docsRoot, 'docs-source-map.json');
const siteBase = '/agentweaver/v1/';

function htmlDecode(value) {
  return value
    .replaceAll('&amp;', '&')
    .replaceAll('&quot;', '"')
    .replaceAll('&#39;', "'")
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>');
}

function linkTargets(markdown) {
  const targets = [];
  for (const match of markdown.matchAll(/!?\[[^\]]*\]\(([^)\s]+)(?:\s+["'][^)]*["'])?\)/g)) {
    targets.push(match[1]);
  }
  for (const match of markdown.matchAll(/\b(?:href|src)\s*=\s*["']([^"']+)["']/gi)) {
    targets.push(htmlDecode(match[1]));
  }
  for (const match of markdown.matchAll(/:(?:href|src)\s*=\s*["'](["'])([^"']+)\1["']/gi)) {
    targets.push(match[2]);
  }
  return targets;
}

function slugHeading(value) {
  return value
    .replace(/`([^`]*)`/g, '$1')
    .replace(/<[^>]*>/g, '')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}_ -]/gu, '')
    .trim()
    .replace(/\s+/g, '-');
}

function markdownAnchors(markdown) {
  const anchors = new Set();
  const counts = new Map();
  for (const line of markdown.split(/\r?\n/)) {
    const heading = line.match(/^#{1,6}\s+(.+?)\s*#*\s*$/);
    if (!heading) continue;
    const base = slugHeading(heading[1]);
    const count = counts.get(base) ?? 0;
    counts.set(base, count + 1);
    anchors.add(count === 0 ? base : `${base}-${count}`);
  }
  for (const match of markdown.matchAll(/\bid=["']([^"']+)["']/g)) {
    anchors.add(match[1]);
  }
  return anchors;
}

function localSourcePath(pagePath, targetPath, siteRoot) {
  let resolved;
  if (targetPath.startsWith(siteBase)) {
    resolved = path.resolve(siteRoot, 'public', decodeURIComponent(targetPath.slice(siteBase.length)));
  } else if (targetPath.startsWith('/')) {
    resolved = path.resolve(siteRoot, decodeURIComponent(targetPath.slice(1)));
  } else {
    resolved = path.resolve(path.dirname(pagePath), decodeURIComponent(targetPath));
  }
  return resolved;
}

async function resolvePageFile(candidate) {
  const possibilities = path.extname(candidate)
    ? [candidate]
    : [candidate, `${candidate}.md`, path.join(candidate, 'index.md'), path.join(candidate, 'README.md')];
  for (const file of possibilities) {
    try {
      if ((await stat(file)).isFile()) return file;
    } catch {
      continue;
    }
  }
  return null;
}

export async function inspectMarkdownPage(repoRelativePage, contents, root = repoRoot) {
  const pagePath = path.resolve(root, repoRelativePage);
  const siteRoot = path.resolve(root, 'docs');
  const errors = [];
  for (const target of linkTargets(contents)) {
    if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(target)) continue;
    const separator = target.indexOf('#');
    const rawPath = separator < 0 ? target : target.slice(0, separator);
    const anchor = separator < 0 ? '' : decodeURIComponent(target.slice(separator + 1));
    const candidate = rawPath ? localSourcePath(pagePath, rawPath, siteRoot) : pagePath;
    const targetFile = await resolvePageFile(candidate);
    if (!targetFile) {
      errors.push(`${repoRelativePage}: missing local target ${target}`);
      continue;
    }
    if (anchor && targetFile.endsWith('.md')) {
      const anchors = markdownAnchors(await readFile(targetFile, 'utf8'));
      if (!anchors.has(anchor)) errors.push(`${repoRelativePage}: missing anchor ${target}`);
    }
  }
  return errors;
}

export function findMissingDocumentation(changedPaths, rules) {
  const changed = new Set(changedPaths);
  const failures = [];
  for (const rule of rules) {
    const affected = changedPaths.filter((file) =>
      rule.sourcePrefixes.some((prefix) => file.startsWith(prefix)));
    if (!affected.length) continue;
    const missing = rule.required.filter((file) => !changed.has(file));
    if (missing.length) failures.push({ rule: rule.name, affected, missing });
  }
  return failures;
}

async function walk(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await walk(fullPath));
    else files.push(fullPath);
  }
  return files;
}

function builtTargetPath(url, pageUrl, dist) {
  const parsed = new URL(htmlDecode(url), `https://docs.invalid${pageUrl}`);
  if (parsed.origin !== 'https://docs.invalid') return null;
  if (!parsed.pathname.startsWith(siteBase)) {
    return { error: `local URL is outside ${siteBase}: ${url}` };
  }
  let relative = decodeURIComponent(parsed.pathname.slice(siteBase.length));
  if (!relative || relative.endsWith('/')) relative = `${relative}index.html`;
  let target = path.resolve(dist, relative);
  if (!path.extname(target)) target = `${target}.html`;
  return { target, anchor: decodeURIComponent(parsed.hash.slice(1)) };
}

async function inspectBuiltSite() {
  const pages = (await walk(distRoot)).filter((file) => file.endsWith('.html'));
  const errors = [];
  for (const page of pages) {
    const html = await readFile(page, 'utf8');
    const relative = path.relative(distRoot, page).split(path.sep).join('/');
    const route = relative === 'index.html'
      ? siteBase
      : relative.endsWith('/index.html')
        ? `${siteBase}${relative.slice(0, -'index.html'.length)}`
        : `${siteBase}${relative}`;
    const ids = new Set([...html.matchAll(/\bid=["']([^"']+)["']/g)].map((match) => match[1]));
    for (const match of html.matchAll(/\b(?:href|src)\s*=\s*["']([^"']+)["']/gi)) {
      const target = builtTargetPath(match[1], route, distRoot);
      if (!target) continue;
      if (target.error) {
        errors.push(`${relative}: ${target.error}`);
        continue;
      }
      try {
        await access(target.target);
      } catch {
        errors.push(`${relative}: missing built target ${match[1]}`);
        continue;
      }
      if (target.anchor && target.target.endsWith('.html')) {
        const targetHtml = await readFile(target.target, 'utf8');
        const targetIds = new Set([...targetHtml.matchAll(/\bid=["']([^"']+)["']/g)].map((item) => item[1]));
        if (!targetIds.has(target.anchor)) {
          errors.push(`${relative}: missing built anchor ${match[1]}`);
        }
      }
    }
    for (const match of html.matchAll(/<img\b[^>]*\balt=["']\s*["'][^>]*>/gi)) {
      errors.push(`${relative}: image has empty alt text`);
    }
  }
  return errors;
}

async function checkLinks() {
  const sourceMap = JSON.parse(await readFile(mapPath, 'utf8'));
  const errors = [];
  for (const page of sourceMap.pages) {
    const content = await readFile(path.join(repoRoot, page), 'utf8');
    errors.push(...await inspectMarkdownPage(page, content));
  }
  for (const rule of sourceMap.rules) {
    for (const required of rule.required) {
      try {
        await access(path.join(repoRoot, required));
      } catch {
        errors.push(`${rule.name}: missing mapped page or diagram source ${required}`);
      }
    }
  }
  try {
    await access(distRoot);
    errors.push(...await inspectBuiltSite());
  } catch {
    errors.push('Build the VitePress site before checking generated links and assets.');
  }
  if (errors.length) throw new Error(errors.join('\n'));
  console.log(`Checked ${sourceMap.pages.length} documentation pages and generated links.`);
}

function changedFiles(base) {
  const tracked = execFileSync('git', ['diff', '--name-only', base, '--'], {
    cwd: repoRoot,
    encoding: 'utf8',
  }).split(/\r?\n/).filter(Boolean);
  const untracked = execFileSync('git', ['ls-files', '--others', '--exclude-standard'], {
    cwd: repoRoot,
    encoding: 'utf8',
  }).split(/\r?\n/).filter(Boolean);
  return [...new Set([...tracked, ...untracked])];
}

async function checkDrift(base) {
  const sourceMap = JSON.parse(await readFile(mapPath, 'utf8'));
  const failures = findMissingDocumentation(changedFiles(base), sourceMap.rules);
  if (!failures.length) {
    console.log('No mapped implementation changes are missing documentation updates.');
    return;
  }
  for (const failure of failures) {
    console.warn(`WARNING ${failure.rule}: changed ${failure.affected.join(', ')} without ${failure.missing.join(', ')}`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--links')) await checkLinks();
  if (args.includes('--drift')) {
    const baseIndex = args.indexOf('--base');
    const base = baseIndex < 0 ? '' : args[baseIndex + 1];
    if (!base || base.startsWith('--')) throw new Error('--drift requires --base <commit-or-ref>');
    await checkDrift(base);
  }
  if (!args.includes('--links') && !args.includes('--drift')) {
    throw new Error('Use --links or --drift --base <commit-or-ref>.');
  }
}
