import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

const recordName = /^[a-z0-9][a-z0-9-]*\.md$/;
const entry = /^"([A-Za-z][A-Za-z0-9.-]*)": (patch|minor|major)$/;
const sha = /^[a-f0-9]{40}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

export function parseChangeset(text, filename, componentIds) {
  const lines = text.replaceAll('\r\n', '\n').split('\n');
  if (lines[0] !== '---') fail(filename, 'expected opening --- frontmatter');
  const end = lines.indexOf('---', 1);
  if (end < 0) fail(filename, 'expected closing --- frontmatter');
  const components = new Set();
  for (const line of lines.slice(1, end)) {
    const match = entry.exec(line);
    if (!match) fail(filename, `invalid component/bump entry "${line}"`);
    if (!componentIds.has(match[1])) fail(filename, `unknown component "${match[1]}"`);
    if (components.has(match[1])) fail(filename, `duplicate component "${match[1]}"`);
    components.add(match[1]);
  }
  const summary = lines.slice(end + 1).join('\n').trim();
  if (!summary || !/\S/.test(summary)) fail(filename, 'expected a nonempty human summary');
  if (!lines[end + 1]?.match(/^\s*$/)) fail(filename, 'expected a blank line before the summary');
  return components;
}

export function diffChanges(root, base) {
  if (!sha.test(base)) fail('base', 'expected a full lowercase 40-character commit SHA');
  const git = (...args) => execFileSync('git', args, {
    cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  try {
    git('cat-file', '-e', `${base}^{commit}`);
    const ancestor = git('merge-base', base, 'HEAD');
    const output = execFileSync('git', ['diff', '--name-status', '-z', '--no-renames', ancestor, 'HEAD'],
      { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    const parts = output.split('\0');
    const changes = [];
    for (let i = 0; i < parts.length - 1; i += 2) {
      changes.push({ status: parts[i], file: parts[i + 1] });
    }
    return changes;
  } catch (error) {
    fail('base', `cannot compare ${base} to HEAD: ${error.message}`);
  }
}

export function validateChangesets(manifest, { root, changes } = {}) {
  const ids = new Set(manifest.components.map(({ id }) => id));
  const records = new Map();
  const directory = path.join(root, '.changeset');
  let filenames;
  try {
    filenames = readdirSync(directory);
  } catch (error) {
    fail(directory, `cannot read changesets: ${error.message}`);
  }
  for (const filename of filenames) {
    if (filename === 'README.md') continue;
    if (!recordName.test(filename)) fail(filename, 'expected a safe .changeset/*.md record name');
    const file = `.changeset/${filename}`;
    let text;
    try {
      text = readFileSync(path.join(directory, filename), 'utf8');
    } catch (error) {
      fail(file, `cannot read changeset: ${error.message}`);
    }
    records.set(file, parseChangeset(text, file, ids));
  }
  if (changes !== undefined) {
    const fresh = new Set();
    for (const { status, file } of changes) {
      if (['A', 'M'].includes(status) && records.has(file)) {
        for (const id of records.get(file)) fresh.add(id);
      }
    }
    const missing = new Set();
    for (const { file } of changes) {
      for (const component of manifest.components) {
        const prefix = component.project.slice(0, component.project.lastIndexOf('/') + 1);
        if (!file.startsWith(prefix)) continue;
        const relative = file.slice(prefix.length);
        if (!relative || /^(?:bin|obj|artifacts|tests?)\//i.test(relative) ||
            /(?:^|\/)(?:README\.md|[^/]+\.md)$/i.test(relative) ||
            /(?:^|\/)(?:Generated|generated)\//.test(relative) ||
            /\.(?:g|generated)\.cs$/i.test(relative)) continue;
        if (!fresh.has(component.id)) missing.add(component.id);
      }
    }
    if (missing.size) fail('changeset coverage', `missing new or modified changeset entries for ${[...missing].join(', ')}`);
  }
  return records;
}
