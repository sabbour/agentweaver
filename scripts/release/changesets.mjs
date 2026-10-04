import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { verifyPreparation } from './preparation.mjs';

const recordName = /^[a-z0-9][a-z0-9-]*\.md$/;
const entry = /^"([A-Za-z][A-Za-z0-9.-]*)": (patch|minor|major|baseline)$/;
const sha = /^[a-f0-9]{40}$/;
const receiptName = /^releases\/receipts\/([a-f0-9]{40})\.json$/;
const sourcePathPattern = /^\.changeset\/[a-z0-9][a-z0-9-]*\.md$/;
const archivedPathPattern = /^\.changeset\/archive\/[a-z0-9][a-z0-9-]*\.md$/;
const initialBaselinePath = 'releases/initial-baseline.json';

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

export function parseChangesetEntries(text, filename, componentIds) {
  const lines = text.replaceAll('\r\n', '\n').split('\n');
  if (lines[0] !== '---') fail(filename, 'expected opening --- frontmatter');
  const end = lines.indexOf('---', 1);
  if (end < 0) fail(filename, 'expected closing --- frontmatter');
  const entries = [];
  const seen = new Set();
  for (const line of lines.slice(1, end)) {
    const match = entry.exec(line);
    if (!match) fail(filename, `invalid component/bump entry "${line}"`);
    if (!componentIds.has(match[1])) fail(filename, `unknown component "${match[1]}"`);
    if (seen.has(match[1])) fail(filename, `duplicate component "${match[1]}"`);
    seen.add(match[1]);
    entries.push({ id: match[1], bump: match[2] });
  }
  const summary = lines.slice(end + 1).join('\n').trim();
  if (!summary || !/\S/.test(summary)) fail(filename, 'expected a nonempty human summary');
  if (!lines[end + 1]?.match(/^\s*$/)) fail(filename, 'expected a blank line before the summary');
  return { entries, summary };
}

export function parseChangeset(text, filename, componentIds) {
  const { entries } = parseChangesetEntries(text, filename, componentIds);
  return new Set(entries.map(({ id }) => id));
}

export function readInitialBaseline(manifest, {
  root, readFileSync: readFile = readFileSync,
} = {}) {
  const baselinePath = path.join(root, initialBaselinePath);
  let text;
  try {
    text = readFile(baselinePath, 'utf8');
  } catch (error) {
    const stderr = Buffer.isBuffer(error.stderr) ? error.stderr.toString('utf8') : error.stderr ?? '';
    if (error.code === 'ENOENT' ||
        (error.status === 128 && stderr.includes(`fatal: path '${initialBaselinePath}' does not exist in `))) {
      return undefined;
    }
    fail(initialBaselinePath, `cannot read initial baseline record: ${error.message}`);
  }

  let baseline;
  try {
    baseline = JSON.parse(text);
  } catch (error) {
    fail(initialBaselinePath, `invalid JSON: ${error.message}`);
  }
  if (baseline === null || typeof baseline !== 'object' || Array.isArray(baseline)) {
    fail(initialBaselinePath, 'expected an object');
  }
  const required = ['schemaVersion', 'issue', 'baselineVersion', 'components', 'changeset', 'initialNotesReceipt'];
  for (const key of required) {
    if (!Object.hasOwn(baseline, key)) fail(initialBaselinePath, `missing required field "${key}"`);
  }
  for (const key of Object.keys(baseline)) {
    if (!required.includes(key)) fail(initialBaselinePath, `unknown field "${key}"`);
  }
  if (baseline.schemaVersion !== 1) fail(initialBaselinePath, 'expected schema version 1');
  if (!Number.isSafeInteger(baseline.issue) || baseline.issue < 1) fail(initialBaselinePath, 'expected a positive issue number');
  if (baseline.baselineVersion !== '0.0.0') fail(initialBaselinePath, 'the initial foundation package baseline must be 0.0.0');
  if (!Array.isArray(baseline.components) || baseline.components.length === 0 ||
      baseline.components.some((component) => typeof component !== 'string') ||
      new Set(baseline.components).size !== baseline.components.length) {
    fail(initialBaselinePath, 'expected a nonempty list of unique component IDs');
  }
  const components = new Map(manifest.components.map((component) => [component.id, component]));
  for (const id of baseline.components) {
    const component = components.get(id);
    if (!component || component.kind === 'service') fail(initialBaselinePath, `unknown NuGet component "${id}"`);
  }
  if (typeof baseline.changeset !== 'string' || !sourcePathPattern.test(baseline.changeset) ||
      path.posix.basename(baseline.changeset) === 'README.md') {
    fail(initialBaselinePath, 'expected a safe top-level changeset path');
  }

  const receiptMatch = typeof baseline.initialNotesReceipt === 'string' && receiptName.exec(baseline.initialNotesReceipt);
  if (!receiptMatch) fail(initialBaselinePath, 'expected a source-derived release receipt path for the initial notes');
  let notesReceipt;
  try {
    notesReceipt = JSON.parse(readFile(path.join(root, baseline.initialNotesReceipt), 'utf8'));
  } catch (error) {
    fail(initialBaselinePath, `cannot read initial notes receipt: ${error.message}`);
  }
  if (notesReceipt === null || typeof notesReceipt !== 'object' || Array.isArray(notesReceipt) ||
      notesReceipt.schemaVersion !== 1 || notesReceipt.sourceSha !== receiptMatch[1] ||
      !Array.isArray(notesReceipt.components) || !Array.isArray(notesReceipt.changesets)) {
    fail(initialBaselinePath, 'initial notes receipt does not match its source-derived filename and shape');
  }
  const preparedIds = new Set(notesReceipt.components.map((component) => component?.id));
  for (const id of baseline.components) {
    if (!preparedIds.has(id)) fail(initialBaselinePath, `initial notes receipt does not include ${id}`);
  }

  let changesetText;
  try {
    changesetText = readFile(path.join(root, baseline.changeset), 'utf8');
  } catch (error) {
    fail(baseline.changeset, `cannot read initial baseline changeset: ${error.message}`);
  }
  const { entries } = parseChangesetEntries(changesetText, baseline.changeset, new Set(components.keys()));
  if (entries.some(({ bump }) => bump !== 'baseline') ||
      JSON.stringify(entries.map(({ id }) => id).sort()) !== JSON.stringify([...baseline.components].sort())) {
    fail(baseline.changeset, 'must contain baseline intent for exactly the recorded NuGet components');
  }

  return {
    path: initialBaselinePath,
    text,
    baselineVersion: baseline.baselineVersion,
    components: [...baseline.components].sort(),
    changeset: baseline.changeset,
    initialNotesReceipt: baseline.initialNotesReceipt,
  };
}

/**
 * Scans `.changeset/*.md` (skipping `README.md`, same rules as `validateChangesets`) and returns
 * a `Map<file, {entries, summary}>` preserving per-component bump severity, for release planning.
 * This does not consume/archive anything; it is a read-only view used by `plan.mjs`.
 */
export function collectChangesetEntries(root, componentIds, { readdirSync: readdir = readdirSync, readFileSync: readFile = readFileSync } = {}) {
  const directory = path.join(root, '.changeset');
  let filenames;
  try {
    filenames = readdir(directory);
  } catch (error) {
    fail(directory, `cannot read changesets: ${error.message}`);
  }
  const records = new Map();
  for (const filename of filenames) {
    if (filename === 'README.md' || filename === 'archive') continue;
    if (!recordName.test(filename)) fail(filename, 'expected a safe .changeset/*.md record name');
    const file = `.changeset/${filename}`;
    let text;
    try {
      text = readFile(path.join(directory, filename), 'utf8');
    } catch (error) {
      fail(file, `cannot read changeset: ${error.message}`);
    }
    records.set(file, parseChangesetEntries(text, file, componentIds));
  }
  return records;
}

/**
 * Resolves the merge-base between `base` and HEAD: the actual ancestor commit a diff is computed
 * against. Exposed separately from `diffChanges` so callers (the CI changeset guard) can bind a
 * release receipt's claimed `sourceSha` to this same ancestor, rather than trusting the receipt's
 * own say-so.
 */
export function resolveMergeBase(root, base) {
  if (!sha.test(base)) fail('base', 'expected a full lowercase 40-character commit SHA');
  const git = (...args) => execFileSync('git', args, {
    cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  try {
    git('cat-file', '-e', `${base}^{commit}`);
    return git('merge-base', base, 'HEAD');
  } catch (error) {
    fail('base', `cannot compare ${base} to HEAD: ${error.message}`);
  }
}

export function diffChanges(root, base) {
  const ancestor = resolveMergeBase(root, base);
  try {
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

/**
 * Credits only exact version-mirror paths when a diff contains a genuine,
 * receipt-proven `apply.mjs` archive rather than a brand-new top-level `.changeset/*.md` record.
 * This is deliberately NOT a generic exemption: every claim is independently re-verified against
 * the actual diff and actual archived file bytes, never trusted from the receipt's own say-so.
 *
 * A receipt only counts when ALL of the following hold:
 *  - its path is `releases/receipts/<sha>.json` and it is newly *added* (status `A`) in this diff;
 *  - the `<sha>` in its filename matches both its own `sourceSha` field and the diff's actual
 *    merge-base ancestor (so a receipt computed for a different commit, or copied/renamed from
 *    elsewhere, cannot be reused to claim coverage here);
 *  - for each archived changeset it claims, the archive destination is itself newly added in this
 *    diff and the original top-level note is actually deleted in this diff (so merely deleting a
 *    note, without a real corresponding archive, is never credited);
 *  - the archived file's actual on-disk bytes hash to exactly what the receipt recorded (so a
 *    forged or edited archive cannot be credited using a stale hash).
 * Source Git notes, decisions, hashes, and prepared mirror bytes are independently checked.
 * Other product edits still require genuinely fresh top-level notes.
 */
export function creditReceiptedComponents(root, changes, componentIds, ancestorSha, {
  readFileSync: readFile = readFileSync,
} = {}) {
  const credited = new Set();
  if (!changes || !ancestorSha) return credited;
  const statusByFile = new Map(changes.map(({ status, file }) => [file, status]));
  for (const { status, file } of changes) {
    const match = receiptName.exec(file);
    if (!match) continue;
    if (status !== 'A') fail(file, 'a release receipt must be freshly added in this diff, not modified or renamed');
    const [, shaInName] = match;
    let receipt;
    try {
      receipt = JSON.parse(readFile(path.join(root, file), 'utf8'));
    } catch (error) {
      fail(file, `cannot read release receipt: ${error.message}`);
    }
    if (receipt === null || typeof receipt !== 'object' || Array.isArray(receipt)) fail(file, 'expected a release receipt object');
    if (receipt.schemaVersion !== 1) fail(file, 'unsupported release receipt schemaVersion');
    if (typeof receipt.sourceSha !== 'string' || receipt.sourceSha !== shaInName) {
      fail(file, 'receipt sourceSha does not match its own filename');
    }
    if (receipt.sourceSha !== ancestorSha) {
      fail(file, `receipt sourceSha ${receipt.sourceSha} does not match the diff's actual base ancestor ${ancestorSha}; stale or unrelated receipt`);
    }
    if (!Array.isArray(receipt.changesets) || receipt.changesets.length === 0) {
      fail(file, 'expected at least one archived changeset entry');
    }
    for (const entryRecord of receipt.changesets) {
      if (entryRecord === null || typeof entryRecord !== 'object' ||
          typeof entryRecord.sourcePath !== 'string' || typeof entryRecord.archivedPath !== 'string' ||
          typeof entryRecord.sha256 !== 'string') {
        fail(file, 'malformed changeset entry in release receipt');
      }
      if (!sourcePathPattern.test(entryRecord.sourcePath)) fail(file, `unsafe or unexpected original changeset path "${entryRecord.sourcePath}"`);
      if (!archivedPathPattern.test(entryRecord.archivedPath)) fail(file, `unsafe or unexpected archived changeset path "${entryRecord.archivedPath}"`);
      if (statusByFile.get(entryRecord.archivedPath) !== 'A') {
        fail(file, `archived changeset ${entryRecord.archivedPath} is not newly added in this diff; cannot credit`);
      }
      if (statusByFile.get(entryRecord.sourcePath) !== 'D') {
        fail(file, `original changeset ${entryRecord.sourcePath} was not actually removed in this diff; cannot credit`);
      }
      let archivedText;
      try {
        archivedText = readFile(path.join(root, entryRecord.archivedPath), 'utf8');
      } catch (error) {
        fail(entryRecord.archivedPath, `cannot read archived changeset: ${error.message}`);
      }
      const actualHash = createHash('sha256').update(archivedText).digest('hex');
      if (actualHash !== entryRecord.sha256) {
        fail(entryRecord.archivedPath, 'archived changeset content does not match the receipt hash; forged or corrupted');
      }
      parseChangesetEntries(archivedText, entryRecord.archivedPath, componentIds);
    }
    const verified = verifyPreparation(root, receipt, { readFile });
    const currentManifest = JSON.parse(readFile(path.join(root, receipt.manifestPath), 'utf8'));
    for (const entry of verified.plan.components) {
      if (currentManifest.components.find(({ id }) => id === entry.id)?.version !== entry.toVersion) {
        fail(file, `actual component version does not match preparation decision for ${entry.id}`);
      }
    }
    if (!Buffer.from(readFile(path.join(root, 'releases/CHANGELOG.md'))).equals(Buffer.from(verified.files.get('releases/CHANGELOG.md')))) {
      fail(file, 'actual changelog does not match source-derived preparation');
    }
    for (const sourcePath of verified.deleted) {
      if (statusByFile.get(sourcePath) !== 'D') fail(file, `source note ${sourcePath} was not deleted`);
    }
    for (const [preparedPath, expected] of verified.files) {
      if (statusByFile.has(preparedPath) &&
          Buffer.from(readFile(path.join(root, preparedPath))).equals(Buffer.from(expected))) credited.add(preparedPath);
    }
  }
  return credited;
}

export function validateChangesets(manifest, { root, changes, ancestorSha } = {}) {
  const ids = new Set(manifest.components.map(({ id }) => id));
  const baseline = readInitialBaseline(manifest, { root });
  const records = new Map();
  const directory = path.join(root, '.changeset');
  let filenames;
  try {
    filenames = readdirSync(directory);
  } catch (error) {
    fail(directory, `cannot read changesets: ${error.message}`);
  }
  for (const filename of filenames) {
    if (filename === 'README.md' || filename === 'archive') continue;
    if (!recordName.test(filename)) fail(filename, 'expected a safe .changeset/*.md record name');
    const file = `.changeset/${filename}`;
    let text;
    try {
      text = readFileSync(path.join(directory, filename), 'utf8');
    } catch (error) {
      fail(file, `cannot read changeset: ${error.message}`);
    }
    const { entries } = parseChangesetEntries(text, file, ids);
    const hasBaselineIntent = entries.some(({ bump }) => bump === 'baseline');
    if (hasBaselineIntent !== (file === baseline?.changeset)) {
      fail(file, 'baseline intent is allowed only in the recorded initial baseline changeset');
    }
    records.set(file, new Set(entries.map(({ id }) => id)));
  }
  if (changes !== undefined) {
    const fresh = new Set();
    for (const { status, file } of changes) {
      if (['A', 'M'].includes(status) && records.has(file)) {
        for (const id of records.get(file)) fresh.add(id);
      }
    }
    const preparedPaths = creditReceiptedComponents(root, changes, ids, ancestorSha);
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
        if (!fresh.has(component.id) && !preparedPaths.has(file)) missing.add(component.id);
      }
    }
    if (missing.size) fail('changeset coverage', `missing new or modified changeset entries for ${[...missing].join(', ')}`);
  }
  return records;
}
