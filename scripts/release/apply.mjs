// Dependency-free release application: consumes a plan produced by `plan.mjs` and deterministically
// applies it to the current source tree. Every precondition is checked before any file is touched;
// writes are performed as an ordered list of {do, undo} operations so a mid-sequence I/O failure
// rolls the tree back to exactly its prior state. See releases/README.md for the full contract.
//
// Atomicity scope: this guards against *caught* I/O exceptions (permission errors, disk full,
// injected failures in tests), not process crashes or power loss mid-rename. Each write goes
// through a temp-file-then-rename so a failure while writing content never touches the real
// destination; only the rename step is a single filesystem syscall.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateManifest } from './validate.mjs';
import { canonicalizeComponents, createPlan, planChecksum } from './plan.mjs';
import { preparationFiles, verifyPreparation } from './preparation.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const sha = /^[a-f0-9]{40}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function defaultGit(root) {
  return (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function defaultStatus(root) {
  return execFileSync('git', ['status', '--porcelain'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

const changesetPathPattern = /^\.changeset\/[a-z0-9][a-z0-9-]*\.md$/;

function validatePlanShape(plan) {
  if (plan === null || typeof plan !== 'object' || Array.isArray(plan)) fail('plan', 'expected an object');
  if (plan.schemaVersion !== 1) fail('plan.schemaVersion', 'expected schema version 1');
  if (typeof plan.sourceSha !== 'string' || !sha.test(plan.sourceSha)) fail('plan.sourceSha', 'expected a 40-character commit SHA');
  if (!Array.isArray(plan.components)) fail('plan.components', 'expected an array');
  for (const [index, component] of plan.components.entries()) {
    const location = `plan.components[${index}]`;
    if (typeof component.id !== 'string') fail(`${location}.id`, 'expected a component ID string');
    if (typeof component.fromVersion !== 'string' || typeof component.toVersion !== 'string') {
      fail(location, 'expected fromVersion and toVersion strings');
    }
    if (component.fromVersion === component.toVersion) fail(location, 'fromVersion and toVersion must differ');
    if (!Array.isArray(component.changesets) || component.changesets.length === 0) {
      fail(`${location}.changesets`, 'expected at least one contributing changeset path');
    }
    for (const file of component.changesets) {
      if (typeof file !== 'string' || !changesetPathPattern.test(file)) {
        fail(`${location}.changesets`, `unsafe or unexpected changeset path "${file}"`);
      }
    }
  }
  if (typeof plan.checksum !== 'string' || plan.checksum !== planChecksum(plan)) {
    fail('plan.checksum', 'plan does not match its recorded checksum; it may be corrupted or hand-edited');
  }
}

function atomicWrite(write, rename, unlink, exists, file, content) {
  const tmp = `${file}.tmp-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  try {
    write(tmp, content);
  } catch (error) {
    try {
      if (exists(tmp)) unlink(tmp);
    } catch (cleanupError) {
      throw new AggregateError([error, cleanupError], `${error.message}; additionally failed to remove leftover temp file ${tmp}: ${cleanupError.message}`);
    }
    throw error;
  }
  try {
    rename(tmp, file);
  } catch (error) {
    let cleanupError;
    try {
      if (exists(tmp)) unlink(tmp);
    } catch (failure) {
      cleanupError = failure;
    }
    if (cleanupError) {
      throw new AggregateError([error, cleanupError], `${error.message} (additionally failed to remove leftover temp file ${tmp}: ${cleanupError.message})`);
    }
    throw error;
  }
}

function runWithRollback(operations) {
  const completed = [];
  try {
    for (const operation of operations) {
      operation.do();
      completed.push(operation);
    }
  } catch (error) {
    const failures = [];
    for (const operation of completed.reverse()) {
      try {
        operation.undo();
      } catch (undoError) {
        failures.push(new Error(`${operation.label}: ${undoError.message}`, { cause: undoError }));
      }
    }
    if (failures.length) {
      throw new AggregateError([error, ...failures], `apply: I/O failure (${error.message}); rollback also failed for: ${failures.map(({ message }) => message).join('; ')} — manual recovery required`);
    }
    throw new Error(`apply: I/O failure while writing release artifacts, rolled back: ${error.message}`, { cause: error });
  }
}

/**
 * Applies a release plan to the manifest at `manifestPath`. Returns `{ applied, skipped }` component
 * ID lists. Fails closed (no writes) on: a malformed/tampered plan, a stale plan (HEAD moved since
 * planning), a dirty working tree (when there is real pending work), a version collision (a
 * component's current version matches neither the plan's `fromVersion` nor `toVersion`), a mixed
 * partially-applied plan (some components already bumped, others not — never produced by a true
 * atomic run; treated as tampering), or a missing changeset/archive-destination conflict. A plan
 * whose every component is already at `toVersion` is a no-op only after its receipt and all dirty
 * paths match the exact source-derived preparation bytes.
 */
export function applyPlan(plan, manifestPath, {
  root = repositoryRoot,
  git = defaultGit(root),
  status = () => defaultStatus(root),
  readFileSync: readFile = readFileSync,
  readdirSync: readdir = readdirSync,
  writeFileSync: writeFileImpl = writeFileSync,
  renameSync: renameImpl = renameSync,
  unlinkSync: unlinkImpl = unlinkSync,
  mkdirSync: mkdirImpl = mkdirSync,
  rmdirSync: rmdirImpl = rmdirSync,
  existsSync: existsImpl = existsSync,
  now = () => new Date(),
} = {}) {
  validatePlanShape(plan);

  const head = git('rev-parse', 'HEAD');
  if (!sha.test(head) || head !== plan.sourceSha) {
    fail('plan', `stale plan: computed at ${plan.sourceSha}, HEAD is ${head}; re-run release:plan`);
  }

  if (plan.components.length === 0) {
    const sourcePaths = git('ls-tree', '-r', '--name-only', plan.sourceSha).split(/\r?\n/);
    const readSource = (file) => execFileSync('git', ['show', `${plan.sourceSha}:${file}`],
      { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    const sourceManifest = JSON.parse(readSource(path.relative(root, manifestPath).replaceAll('\\', '/')));
    const recomputed = createPlan(sourceManifest, {
      root, git: () => plan.sourceSha,
      readdirSync: () => sourcePaths.filter((file) => /^\.changeset\/[^/]+$/.test(file)).map((file) => path.posix.basename(file)),
      readFileSync: (file) => readSource(path.relative(root, file).replaceAll('\\', '/')),
    });
    if (recomputed.components.length !== 0) {
      fail('plan', 'empty plan does not match genuine committed source intent; re-run release:plan');
    }
    return { applied: [], skipped: [], message: 'nothing to apply' };
  }

  let manifestText;
  try {
    manifestText = readFile(manifestPath, 'utf8');
  } catch (error) {
    fail(manifestPath, `cannot read manifest: ${error.message}`);
  }
  let manifest;
  try {
    manifest = JSON.parse(manifestText);
  } catch (error) {
    fail(manifestPath, `invalid JSON: ${error.message}`);
  }
  validateManifest(manifest, { root, readProject: (file) => readFile(file, 'utf8') });

  const archiveDir = path.join(root, '.changeset', 'archive');
  const receiptPath = path.join(root, 'releases', 'receipts', `${plan.sourceSha}.json`);

  const componentsById = new Map(manifest.components.map((c) => [c.id, c]));
  const applied = [];
  const pending = [];
  for (const entry of plan.components) {
    const component = componentsById.get(entry.id);
    if (!component) fail(`plan component ${entry.id}`, 'component no longer exists in the manifest (deleted since the plan was created)');
    if (component.version === entry.toVersion) {
      applied.push(entry.id);
    } else if (component.version === entry.fromVersion) {
      pending.push(entry);
    } else {
      fail(`plan component ${entry.id}`, `collision: expected version ${entry.fromVersion}, found ${component.version}; re-run release:plan`);
    }
  }

  if (pending.length === 0) {
    // A safe no-op is only ever "nothing pending, every component already at toVersion" AND
    // independently verified real evidence of a genuine prior application: the original
    // top-level changeset is gone, its archive exists, and a matching receipt was committed.
    // Version-number equality alone (e.g. someone hand-editing versions to match) is not enough.
    for (const entry of plan.components) {
      for (const file of entry.changesets) {
        const original = path.resolve(root, file);
        const archived = path.join(archiveDir, path.basename(file));
        if (existsImpl(original)) fail(file, 'plan reports this component as already applied, but the original changeset still exists; investigate before retrying');
        if (!existsImpl(archived)) fail(file, `plan reports this component as already applied, but no archived changeset was found at ${path.relative(root, archived)}; investigate before retrying`);
      }
    }
    if (!existsImpl(receiptPath)) {
      fail('plan', `plan reports every component as already applied, but no release receipt was found at ${path.relative(root, receiptPath)}; investigate before retrying`);
    }
    const receipt = JSON.parse(readFile(receiptPath, 'utf8'));
    const verified = verifyPreparation(root, receipt, { readFile });
    if (receipt.sourceSha !== plan.sourceSha || verified.plan.checksum !== plan.checksum ||
        receipt.manifestPath !== path.relative(root, manifestPath).replaceAll('\\', '/')) {
      fail('plan', 'prior application receipt does not match this plan and manifest');
    }
    for (const [file, expected] of verified.files) {
      if (!Buffer.from(readFile(path.join(root, file))).equals(Buffer.from(expected))) {
        fail(file, 'prior preparation bytes do not match source-bound receipt');
      }
    }
    const allowed = new Set([...verified.files.keys(), ...verified.deleted, path.relative(root, receiptPath).replaceAll('\\', '/')]);
    const dirtyPaths = execFileSync('git', ['ls-files', '-z', '--modified', '--deleted', '--others', '--exclude-standard'],
      { cwd: root, encoding: 'utf8' }).split('\0').filter(Boolean);
    const stagedPaths = execFileSync('git', ['diff', '--cached', '--name-only', '-z'], { cwd: root, encoding: 'utf8' })
      .split('\0').filter(Boolean);
    if ([...dirtyPaths, ...stagedPaths].some((file) => !allowed.has(file))) fail('plan', 'unrelated dirty edits alongside prior preparation');
    const indexed = new Set(execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0'));
    for (const file of stagedPaths) {
      if (verified.deleted.includes(file)) {
        if (indexed.has(file)) fail(file, 'staged source note must remain deleted after preparation');
        continue;
      }
      const expected = file === path.relative(root, receiptPath).replaceAll('\\', '/')
        ? readFile(receiptPath) : verified.files.get(file);
      const staged = execFileSync('git', ['show', `:${file}`], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
      if (!staged.equals(Buffer.from(expected))) fail(file, 'staged preparation differs from verified current bytes');
    }
    return { applied: [], skipped: applied, message: 'already applied; nothing to do' };
  }
  if (applied.length > 0) {
    fail('plan', `partially applied plan detected (already bumped: ${applied.join(', ')}; still pending: ${pending.map((e) => e.id).join(', ')}) — this never happens from a single atomic apply; investigate before retrying`);
  }

  // Strict dirty-tree check only once we know there is real work to do.
  const porcelain = status();
  if (porcelain.trim() !== '') {
    fail('plan', 'dirty working tree: commit or stash pending changes before applying the release plan');
  }

  // Bind the plan to the actual current changesets: recompute a fresh plan from disk and compare
  // its canonicalized decisions against the ones this plan claims. A checksum alone only proves
  // internal self-consistency; this proves the plan's claims match reality at this source commit.
  const recomputed = createPlan(manifest, { root, readdirSync: readdir, readFileSync: readFile, git });
  if (JSON.stringify(canonicalizeComponents(recomputed.components)) !== JSON.stringify(canonicalizeComponents(pending))) {
    fail('plan', 'plan does not match the actual pending changesets at this source commit (forged, stale, or tampered plan); re-run release:plan');
  }

  // Preflight: every consumed changeset must exist and every archive destination must be free,
  // and no receipt must already exist at this source commit — all checked before any mutation so
  // a late discovery can never happen mid-sequence.
  if (existsImpl(receiptPath)) {
    fail('plan', `a release receipt already exists at ${path.relative(root, receiptPath)}; investigate before retrying`);
  }
  const changesetFiles = [...new Set(pending.flatMap((entry) => entry.changesets))].sort();
  const changesetContents = new Map();
  for (const file of changesetFiles) {
    const source = path.resolve(root, file);
    let content;
    try {
      content = readFile(source, 'utf8');
    } catch (error) {
      fail(file, `cannot read pending changeset: ${error.message}`);
    }
    const dest = path.join(archiveDir, path.basename(file));
    if (existsImpl(dest)) fail(file, `archive destination already exists: ${path.relative(root, dest)}`);
    changesetContents.set(file, { source, dest, content });
  }

  const appliedAt = now().toISOString();
  const prepared = preparationFiles(manifestText, plan, appliedAt, (file) => {
    try {
      return readFile(path.join(root, file), 'utf8');
    } catch (error) {
      if (file === 'releases/CHANGELOG.md' && error.code !== 'ENOENT') fail(file, `cannot read existing changelog: ${error.message}`);
      throw error;
    }
  });
  // Buffer exact original bytes separately from existence, including empty files.
  const projectWrites = new Map();
  for (const entry of pending) {
    const component = componentsById.get(entry.id);
    const file = path.resolve(root, component.project);
    let original;
    try {
      original = readFile(file, 'utf8');
    } catch (error) {
      fail(component.project, `cannot read checked-in project: ${error.message}`);
    }
    const next = prepared.files.get(component.project);
    projectWrites.set(file, { original, next });
  }
  const manifestNext = prepared.manifestText;
  const changelogPath = path.join(root, 'releases', 'CHANGELOG.md');
  const changelogExisted = existsImpl(changelogPath);
  const changelogOriginal = changelogExisted ? readFile(changelogPath) : undefined;
  const changelogNext = prepared.files.get('releases/CHANGELOG.md');

  const receipt = {
    schemaVersion: 1,
    sourceSha: plan.sourceSha,
    manifestPath: path.relative(root, manifestPath).replaceAll('\\', '/'),
    planChecksum: plan.checksum,
    appliedAt,
    components: pending.map(({ id, fromVersion, toVersion, bump }) => ({ id, fromVersion, toVersion, bump })),
    changesets: [...changesetContents].map(([file, { dest, content }]) => ({
      sourcePath: file,
      archivedPath: path.relative(root, dest).replaceAll('\\', '/'),
      sha256: createHash('sha256').update(content).digest('hex'),
    })),
  };
  const receiptNext = JSON.stringify(receipt, null, 2) + '\n';
  const verified = verifyPreparation(root, receipt, { readFile, verifyArchives: false });
  for (const [file, expected] of verified.files) {
    const actual = file === receipt.manifestPath ? manifestNext
      : file.startsWith('.changeset/archive/') ? changesetContents.get(`.changeset/${path.basename(file)}`).content
      : prepared.files.get(file);
    if (!Buffer.from(actual).equals(Buffer.from(expected))) fail(file, 'preparation differs from source Git bytes; use a byte-preserving checkout');
  }

  const operations = [];
  for (const [file, { original, next }] of projectWrites) {
    operations.push({
      label: `project ${path.relative(root, file)}`,
      do: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, file, next),
      undo: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, file, original),
    });
  }
  operations.push({
    label: 'manifest',
    do: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, manifestPath, manifestNext),
    undo: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, manifestPath, manifestText),
  });
  operations.push({
    label: 'changelog',
    do: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, changelogPath, changelogNext),
    undo: () => {
      if (changelogExisted) atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, changelogPath, changelogOriginal);
      else unlinkImpl(changelogPath);
    },
  });
  function addDirectory(directory) {
    if (existsImpl(directory)) return;
    if (!existsImpl(path.dirname(directory))) fail(directory, 'expected existing owned parent directory');
    operations.push({
      label: `directory ${path.relative(root, directory)}`,
      do: () => mkdirImpl(directory),
      undo: () => rmdirImpl(directory),
    });
  }
  addDirectory(path.dirname(receiptPath));
  operations.push({
    label: 'release receipt',
    do: () => atomicWrite(writeFileImpl, renameImpl, unlinkImpl, existsImpl, receiptPath, receiptNext),
    undo: () => {
      if (existsImpl(receiptPath)) unlinkImpl(receiptPath);
    },
  });
  if (changesetContents.size > 0) {
    addDirectory(archiveDir);
  }
  for (const [file, { source, dest }] of changesetContents) {
    operations.push({
      label: `archive ${file}`,
      do: () => renameImpl(source, dest),
      undo: () => {
        renameImpl(dest, source);
      },
    });
  }

  runWithRollback(operations);

  return {
    applied: pending.map((entry) => entry.id),
    skipped: applied,
    changesets: changesetFiles,
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [planFileArg, manifestArg = 'releases/foundation.json'] = process.argv.slice(2);
    if (!planFileArg) fail('usage', 'node scripts/release/apply.mjs <plan.json> [manifest.json]');
    const plan = JSON.parse(readFileSync(path.resolve(repositoryRoot, planFileArg), 'utf8'));
    const manifestPath = path.resolve(repositoryRoot, manifestArg);
    const result = applyPlan(plan, manifestPath, { root: repositoryRoot });
    if (result.applied.length === 0) {
      console.log(result.message ?? 'nothing to apply');
    } else {
      console.log(`Applied ${result.applied.length} component bump(s): ${result.applied.join(', ')}`);
    }
  } catch (error) {
    console.error(`Release apply failed: ${error.message}`);
    process.exitCode = 1;
  }
}
