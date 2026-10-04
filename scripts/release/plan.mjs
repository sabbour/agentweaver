// Dependency-free release planning: turns pending `.changeset/*.md` records into a deterministic,
// source-bound plan describing the semver bump each touched component will receive. Planning never
// mutates the repository; `apply.mjs` consumes the plan it produces. See releases/README.md.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { writeFileSync, mkdirSync, readFileSync as nativeReadFile } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile } from './validate.mjs';
import { collectChangesetEntries, readInitialBaseline } from './changesets.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const BUMP_RANK = { patch: 0, minor: 1, major: 2 };
const sha = /^[a-f0-9]{40}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function defaultGit(root) {
  return (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

/**
 * Bumps the semver *core* (major.minor.patch), discarding any prerelease/build metadata: changeset
 * intent targets the next stable core version, not a continuation of an existing prerelease line.
 */
export function bumpVersion(version, bump) {
  const core = version.split(/[-+]/, 1)[0];
  const parts = core.split('.');
  if (parts.length !== 3 || parts.some((part) => !/^\d+$/.test(part))) {
    fail('version', `cannot bump malformed core version "${version}"`);
  }
  const [major, minor, patch] = parts.map(Number);
  for (const value of [major, minor, patch]) {
    if (!Number.isSafeInteger(value)) fail('version', `version component in "${version}" exceeds safe integer range`);
  }
  if (bump === 'major') return `${major + 1}.0.0`;
  if (bump === 'minor') return `${major}.${minor + 1}.0`;
  if (bump === 'patch') return `${major}.${minor}.${patch + 1}`;
  fail('bump', `unknown bump "${bump}"`);
}

/**
 * Canonicalizes a plan's component decisions into a stable, comparable shape: sorted by ID, with
 * each entry's `changesets` list sorted too. Used both by `planChecksum` (self-consistency) and by
 * `apply.mjs` (source-binding: comparing a submitted plan against one freshly recomputed from the
 * actual current changesets).
 */
export function canonicalizeComponents(components) {
  return [...components]
    .map((c) => ({ id: c.id, fromVersion: c.fromVersion, toVersion: c.toVersion, bump: c.bump, changesets: [...c.changesets].sort() }))
    .sort((a, b) => a.id.localeCompare(b.id));
}

/**
 * Canonical checksum over the decisions a plan makes (not `createdAt`, which is informational only):
 * detects accidental or manual corruption of a plan file before `apply.mjs` trusts it. This is an
 * integrity guard, not a cryptographic signature.
 */
export function planChecksum({ schemaVersion, sourceSha, components, initialBaseline }) {
  const decisions = { schemaVersion, sourceSha, components: canonicalizeComponents(components) };
  if (initialBaseline !== undefined) decisions.initialBaseline = initialBaseline;
  const canonical = JSON.stringify(decisions);
  return createHash('sha256').update(canonical).digest('hex');
}

/**
 * Computes a deterministic release plan from the current manifest and pending changesets. When a
 * component is touched by more than one changeset, the highest-severity bump wins (major > minor >
 * patch) — this is "highest intent per component", not a sum of bumps. Returns `{ components: [] }`
 * (no `sourceSha`/`checksum` work skipped) when there is nothing pending.
 */
export function createPlan(manifest, {
  root = repositoryRoot, readdirSync, readFileSync: readFile, git = defaultGit(root),
  readSource = (sourceSha, file) => execFileSync('git', ['show', `${sourceSha}:${file}`],
    { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }),
} = {}) {
  const ids = new Set(manifest.components.map(({ id }) => id));
  const records = collectChangesetEntries(root, ids, { readdirSync, readFileSync: readFile });

  const perComponent = new Map();
  const initialBaseline = readInitialBaseline(manifest, { root, readFileSync: readFile });
  for (const [file, { entries }] of records) {
    if (file === initialBaseline?.changeset) continue;
    for (const { id, bump } of entries) {
      if (bump === 'baseline') fail(file, 'baseline intent is allowed only in the recorded initial baseline changeset');
      const existing = perComponent.get(id);
      if (!existing) {
        perComponent.set(id, { bump, files: [file] });
      } else {
        if (BUMP_RANK[bump] > BUMP_RANK[existing.bump]) existing.bump = bump;
        existing.files.push(file);
      }
    }
  }

  const sourceSha = git('rev-parse', 'HEAD');
  if (!sha.test(sourceSha)) fail('plan', 'cannot resolve a full 40-character commit SHA for HEAD');
  if (initialBaseline) {
    let source;
    try {
      source = readSource(sourceSha, initialBaseline.path);
    } catch (error) {
      fail(initialBaseline.path, `missing committed source baseline record: ${error.message}`);
    }
    if (source !== initialBaseline.text) {
      fail(initialBaseline.path, 'baseline record bytes differ from source HEAD; commit edits or use a byte-preserving checkout');
    }
  }
  for (const file of records.keys()) {
    let source;
    try {
      source = readSource(sourceSha, file);
    } catch (error) {
      fail(file, `missing committed source changeset: ${error.message}`);
    }
    if (source !== (readFile ?? nativeReadFile)(path.join(root, file), 'utf8')) {
      fail(file, 'changeset bytes differ from source HEAD; commit edits or use a byte-preserving checkout');
    }
  }

  if (perComponent.size === 0) {
    const empty = { schemaVersion: 1, sourceSha, createdAt: new Date().toISOString(), components: [] };
    if (initialBaseline) {
      empty.initialBaseline = {
        baselineVersion: initialBaseline.baselineVersion,
        components: initialBaseline.components,
        changeset: initialBaseline.changeset,
        initialNotesReceipt: initialBaseline.initialNotesReceipt,
      };
    }
    empty.checksum = planChecksum(empty);
    return empty;
  }

  const components = [];
  for (const [id, { bump, files }] of perComponent) {
    const component = manifest.components.find((c) => c.id === id);
    components.push({
      id,
      fromVersion: component.version,
      toVersion: bumpVersion(component.version, bump),
      bump,
      changesets: [...files].sort(),
    });
  }
  components.sort((a, b) => a.id.localeCompare(b.id));

  const plan = { schemaVersion: 1, sourceSha, createdAt: new Date().toISOString(), components };
  if (initialBaseline) {
    plan.initialBaseline = {
      baselineVersion: initialBaseline.baselineVersion,
      components: initialBaseline.components,
      changeset: initialBaseline.changeset,
      initialNotesReceipt: initialBaseline.initialNotesReceipt,
    };
  }
  plan.checksum = planChecksum(plan);
  return plan;
}

export function createPlanFromFile(manifestPath, options = {}) {
  const manifest = validateFile(manifestPath, options);
  return createPlan(manifest, options);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const manifestArg = args.find((arg) => !arg.startsWith('--'));
    const outIndex = args.indexOf('--out');
    const out = outIndex >= 0 ? args[outIndex + 1] : 'artifacts/release/plan.json';
    if (!manifestArg || (outIndex >= 0 && !args[outIndex + 1])) {
      fail('usage', 'node scripts/release/plan.mjs <manifest.json> [--out <plan.json>]');
    }
    const plan = createPlanFromFile(manifestArg, { root: repositoryRoot });
    if (plan.components.length === 0) {
      if (plan.initialBaseline) {
        console.log(`No version bumps pending; the initial ${plan.initialBaseline.baselineVersion} package baseline notes remain source-bound.`);
      } else {
        console.log('No pending changesets; nothing to plan.');
      }
    } else {
      const outFile = path.resolve(repositoryRoot, out);
      mkdirSync(path.dirname(outFile), { recursive: true });
      writeFileSync(outFile, JSON.stringify(plan, null, 2) + '\n');
      console.log(`Wrote release plan for ${plan.components.length} component(s) to ${path.relative(repositoryRoot, outFile)} (source ${plan.sourceSha})`);
      for (const component of plan.components) {
        console.log(`  ${component.id}: ${component.fromVersion} -> ${component.toVersion} (${component.bump})`);
      }
    }
  } catch (error) {
    console.error(`Release plan failed: ${error.message}`);
    process.exitCode = 1;
  }
}
