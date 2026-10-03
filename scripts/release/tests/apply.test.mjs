import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { applyPlan } from '../apply.mjs';
import { diffChanges, resolveMergeBase, validateChangesets } from '../changesets.mjs';
import { createPlan, planChecksum } from '../plan.mjs';

const csproj = (version, references = []) => `<Project><PropertyGroup><Version>${version}</Version></PropertyGroup>` +
  (references.length
    ? `<ItemGroup>${references.map((ref) => `<ProjectReference Include="../${ref}/${ref}.csproj" />`).join('')}</ItemGroup>`
    : '') + '</Project>';

const manifestBase = () => ({
  schemaVersion: 1,
  stage: 'draft',
  components: [
    { id: 'Pkg.A', kind: 'library', version: '0.1.0', project: 'packages/Pkg.A/Pkg.A.csproj' },
    { id: 'Pkg.B', kind: 'contract', version: '0.1.0', project: 'packages/Pkg.B/Pkg.B.csproj' },
  ],
  compatibility: [
    { consumer: 'Pkg.A', dependency: 'Pkg.B', versions: ['0.1.0'] },
  ],
});

function git(dir, ...args) {
  return execFileSync('git', args, { cwd: dir, encoding: 'utf8' }).trim();
}

function initRepo(t, { changeset = '---\n"Pkg.A": minor\n---\n\nAdd a feature.\n' } = {}) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const dir = mkdtempSync(path.join(fixtureRoot, 'v1-apply-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, 'packages', 'Pkg.A'), { recursive: true });
  mkdirSync(path.join(dir, 'packages', 'Pkg.B'), { recursive: true });
  mkdirSync(path.join(dir, 'releases'));
  mkdirSync(path.join(dir, '.changeset'));
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('0.1.0', ['Pkg.B']));
  writeFileSync(path.join(dir, 'packages', 'Pkg.B', 'Pkg.B.csproj'), csproj('0.1.0'));
  writeFileSync(path.join(dir, 'releases', 'foundation.json'), JSON.stringify(manifestBase(), null, 2) + '\n');
  if (changeset) writeFileSync(path.join(dir, '.changeset', 'add-feature.md'), changeset);
  git(dir, 'init', '-q');
  git(dir, 'config', 'core.autocrlf', 'false');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial');
  return dir;
}

function manifestPath(dir) {
  return path.join(dir, 'releases', 'foundation.json');
}

function readManifest(dir) {
  return JSON.parse(readFileSync(manifestPath(dir), 'utf8'));
}

test('plans and applies a single-component bump, keeping mirrors and compatibility coherent', (t) => {
  const dir = initRepo(t);
  const manifest = readManifest(dir);
  const plan = createPlan(manifest, { root: dir });
  assert.equal(plan.components.length, 1);
  assert.deepEqual(plan.components[0], {
    id: 'Pkg.A', fromVersion: '0.1.0', toVersion: '0.2.0', bump: 'minor', changesets: ['.changeset/add-feature.md'],
  });

  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result.applied, ['Pkg.A']);
  assert.deepEqual(result.skipped, []);

  const updated = readManifest(dir);
  assert.equal(updated.components.find((c) => c.id === 'Pkg.A').version, '0.2.0');
  const csprojText = readFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), 'utf8');
  assert.match(csprojText, /<Version>0\.2\.0<\/Version>/);
  assert.doesNotMatch(csprojText, /0\.1\.0/);

  assert.throws(() => readFileSync(path.join(dir, '.changeset', 'add-feature.md'), 'utf8'), /ENOENT/);
  const archived = readFileSync(path.join(dir, '.changeset', 'archive', 'add-feature.md'), 'utf8');
  assert.match(archived, /Add a feature\./);

  const changelog = readFileSync(path.join(dir, 'releases', 'CHANGELOG.md'), 'utf8');
  assert.match(changelog, /Pkg\.A.*0\.1\.0 -> 0\.2\.0 \(minor\)/);
});

test('mixed bumps on one component use the highest severity, and independent components stay separate', (t) => {
  const dir = initRepo(t, { changeset: false });
  writeFileSync(path.join(dir, '.changeset', 'patch-a.md'), '---\n"Pkg.A": patch\n---\n\nFix a bug.\n');
  writeFileSync(path.join(dir, '.changeset', 'major-a.md'), '---\n"Pkg.A": major\n---\n\nBreaking change.\n');
  writeFileSync(path.join(dir, '.changeset', 'minor-b.md'), '---\n"Pkg.B": minor\n---\n\nNew contract field.\n');
  const manifest = readManifest(dir);
  manifest.compatibility[0].versions.push('0.2.0');
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'changesets');

  const plan = createPlan(readManifest(dir), { root: dir });
  const byId = Object.fromEntries(plan.components.map((c) => [c.id, c]));
  assert.equal(byId['Pkg.A'].bump, 'major');
  assert.equal(byId['Pkg.A'].toVersion, '1.0.0');
  assert.deepEqual(byId['Pkg.A'].changesets.sort(), ['.changeset/major-a.md', '.changeset/patch-a.md']);
  assert.equal(byId['Pkg.B'].bump, 'minor');
  assert.equal(byId['Pkg.B'].toVersion, '0.2.0');

  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result.applied.sort(), ['Pkg.A', 'Pkg.B']);
  const updated = readManifest(dir);
  assert.equal(updated.components.find((c) => c.id === 'Pkg.A').version, '1.0.0');
  assert.equal(updated.components.find((c) => c.id === 'Pkg.B').version, '0.2.0');
  assert.deepEqual(updated.compatibility[0].versions.sort(), ['0.1.0', '0.2.0']);
});

test('reports nothing to apply for a plan with no pending changesets', (t) => {
  const dir = initRepo(t, { changeset: false });
  const plan = createPlan(readManifest(dir), { root: dir });
  assert.deepEqual(plan.components, []);
  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result, { applied: [], skipped: [], message: 'nothing to apply' });
});

test('rejects an unknown component ID in a changeset during planning', (t) => {
  const dir = initRepo(t, { changeset: false });
  writeFileSync(path.join(dir, '.changeset', 'bad.md'), '---\n"Pkg.Ghost": minor\n---\n\nTypo.\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'bad changeset');
  assert.throws(() => createPlan(readManifest(dir), { root: dir }), /unknown component "Pkg\.Ghost"/);
});

test('reruns are idempotent: a fully applied plan is a safe no-op even on an uncommitted tree', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  applyPlan(plan, manifestPath(dir), { root: dir });
  // The tree is now legitimately dirty with the result of the first apply, but not committed.
  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result, { applied: [], skipped: ['Pkg.A'], message: 'already applied; nothing to do' });
});

test('bumping a draft service clears its previous image digest and verifies the rerun', (t) => {
  const dir = initRepo(t);
  const manifest = readManifest(dir);
  const service = manifest.components[0];
  service.kind = 'service';
  service.project = 'services/Pkg.A/Pkg.A.csproj';
  service.imageDigest = `sha256:${'a'.repeat(64)}`;
  manifest.compatibility = [];
  mkdirSync(path.dirname(path.join(dir, service.project)), { recursive: true });
  writeFileSync(path.join(dir, service.project), csproj('0.1.0'));
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'draft service');
  const plan = createPlan(readManifest(dir), { root: dir });

  applyPlan(plan, manifestPath(dir), { root: dir });
  const updated = readManifest(dir).components[0];
  assert.equal(updated.version, '0.2.0');
  assert.equal(Object.hasOwn(updated, 'imageDigest'), false);
  assert.deepEqual(applyPlan(plan, manifestPath(dir), { root: dir }),
    { applied: [], skipped: ['Pkg.A'], message: 'already applied; nothing to do' });

  updated.imageDigest = service.imageDigest;
  const tampered = readManifest(dir);
  tampered.components[0] = updated;
  writeFileSync(manifestPath(dir), JSON.stringify(tampered, null, 2) + '\n');
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /preparation|differ|match/);
});

test('rejects a stale plan whose sourceSha no longer matches HEAD', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  writeFileSync(path.join(dir, 'unrelated.txt'), 'x');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'unrelated');
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /stale plan/);
});

test('rejects applying onto a dirty working tree with unrelated uncommitted changes', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  writeFileSync(path.join(dir, 'unrelated.txt'), 'uncommitted');
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /dirty working tree/);
});

test('rejects a version collision when the component drifted from the plan precondition', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const manifest = readManifest(dir);
  manifest.components.find((c) => c.id === 'Pkg.A').version = '9.9.9';
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  // Keep the csproj mirror consistent with the manifest so validateManifest's own drift check
  // doesn't mask the collision check apply.mjs is responsible for.
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('9.9.9', ['Pkg.B']));
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /collision: expected version 0\.1\.0, found 9\.9\.9/);
});

test('rejects a plan referencing a component deleted from the manifest since planning', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const manifest = readManifest(dir);
  manifest.components = manifest.components.filter((c) => c.id !== 'Pkg.A');
  manifest.compatibility = [];
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /deleted since the plan was created/);
});

test('rejects a partially applied (mixed) plan instead of silently resuming it', (t) => {
  const dir = initRepo(t, { changeset: false });
  writeFileSync(path.join(dir, '.changeset', 'minor-a.md'), '---\n"Pkg.A": minor\n---\n\nFeature A.\n');
  writeFileSync(path.join(dir, '.changeset', 'minor-b.md'), '---\n"Pkg.B": minor\n---\n\nFeature B.\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'changesets');
  const plan = createPlan(readManifest(dir), { root: dir });
  // Simulate a hand-edited manifest+mirror where only one of the two components was already bumped.
  const manifest = readManifest(dir);
  manifest.components.find((c) => c.id === 'Pkg.A').version = '0.2.0';
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('0.2.0', ['Pkg.B']));
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /partially applied plan detected/);
});

test('rejects a tampered plan whose checksum no longer matches its contents', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const tampered = { ...plan, components: [{ ...plan.components[0], toVersion: '9.0.0' }] };
  assert.throws(() => applyPlan(tampered, manifestPath(dir), { root: dir }), /does not match its recorded checksum/);
});

test('rejects a missing pending changeset before any mutation (fails closed, no partial writes)', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const originalManifest = readFileSync(manifestPath(dir), 'utf8');
  const originalCsproj = readFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), 'utf8');
  const fakeRead = (file, encoding) => {
    if (path.resolve(file) === path.resolve(dir, '.changeset', 'add-feature.md')) {
      throw new Error('ENOENT: no such file or directory');
    }
    return readFileSync(file, encoding);
  };
  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir,
    git: () => plan.sourceSha,
    status: () => '',
    readFileSync: fakeRead,
  }), /cannot read changeset/);
  // Nothing should have been written: manifest and project file are untouched.
  assert.equal(readFileSync(manifestPath(dir), 'utf8'), originalManifest);
  assert.equal(readFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), 'utf8'), originalCsproj);
});

test('rejects an existing archive destination instead of silently overwriting it', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  // Create the stale archive file after planning without advancing HEAD or committing, then bypass
  // the stale/dirty preconditions (already covered by dedicated tests) to isolate this check.
  mkdirSync(path.join(dir, '.changeset', 'archive'), { recursive: true });
  writeFileSync(path.join(dir, '.changeset', 'archive', 'add-feature.md'), 'stale archive content');
  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir, git: () => plan.sourceSha, status: () => '',
  }), /archive destination already exists/);
});

test('rolls back every write when an I/O failure occurs mid-sequence, leaving the tree untouched', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const originalManifest = readFileSync(manifestPath(dir), 'utf8');
  const originalCsproj = readFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), 'utf8');

  let writeCount = 0;
  const failingWrite = (file, content) => {
    writeCount += 1;
    // Let the project-file write and manifest write succeed, then fail on the changelog write.
    if (writeCount === 3) throw new Error('simulated disk full');
    writeFileSync(file, content);
  };

  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir, writeFileSync: failingWrite,
  }), /I\/O failure while writing release artifacts, rolled back: simulated disk full/);

  assert.equal(readFileSync(manifestPath(dir), 'utf8'), originalManifest);
  assert.equal(readFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), 'utf8'), originalCsproj);
  assert.equal(readFileSync(path.join(dir, '.changeset', 'add-feature.md'), 'utf8'),
    '---\n"Pkg.A": minor\n---\n\nAdd a feature.\n');
});

test('release-stage services keep requiring exact-source evidence after a draft-only apply', (t) => {
  const dir = initRepo(t, { changeset: false });
  const manifest = readManifest(dir);
  manifest.stage = 'release';
  manifest.components.push({
    id: 'Svc.Example', kind: 'service', version: '1.0.0', project: 'services/Svc.Example/Svc.Example.csproj',
  });
  // Deliberately omit imageDigest and evidence: this manifest must fail validation (used indirectly
  // via applyPlan's internal validateManifest call) rather than apply silently treating it as fine.
  mkdirSync(path.join(dir, 'services', 'Svc.Example'), { recursive: true });
  writeFileSync(path.join(dir, 'services', 'Svc.Example', 'Svc.Example.csproj'), csproj('1.0.0'));
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  writeFileSync(path.join(dir, '.changeset', 'minor-a.md'), '---\n"Pkg.A": minor\n---\n\nFeature.\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'release stage, no evidence');
  const plan = createPlan(readManifest(dir), { root: dir });
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }),
    /requires a published sha256 image digest|manifest\.evidence/);
});

test('rejects a major bump of a compatibility dependency without an explicit pre-declared version', (t) => {
  const dir = initRepo(t, { changeset: false });
  writeFileSync(path.join(dir, '.changeset', 'major-b.md'), '---\n"Pkg.B": major\n---\n\nBreaking contract change.\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'major changeset');
  const plan = createPlan(readManifest(dir), { root: dir });
  assert.equal(plan.components[0].bump, 'major');
  assert.equal(plan.components[0].toVersion, '1.0.0');
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }),
    /major bump of Pkg\.B to 1\.0\.0 is not automatically assumed compatible/);
  // Nothing should have been written: a failed preflight check must not mutate the manifest.
  assert.equal(readManifest(dir).components.find((c) => c.id === 'Pkg.B').version, '0.1.0');
});

test('allows a major bump of a compatibility dependency once the version is explicitly pre-declared', (t) => {
  const dir = initRepo(t, { changeset: false });
  writeFileSync(path.join(dir, '.changeset', 'major-b.md'), '---\n"Pkg.B": major\n---\n\nBreaking contract change.\n');
  const manifest = readManifest(dir);
  manifest.compatibility[0].versions.push('1.0.0');
  writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'pre-declared compatibility');
  const plan = createPlan(readManifest(dir), { root: dir });
  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result.applied, ['Pkg.B']);
  const updated = readManifest(dir);
  assert.deepEqual(updated.compatibility[0].versions.sort(), ['0.1.0', '1.0.0']);
});

test('rejects a forged plan whose checksum was recomputed to match tampered, non-actual decisions', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  // An attacker who controls the plan file can recompute planChecksum over tampered contents, so the
  // self-consistency check alone passes; only the recompute-against-reality check in apply.mjs can
  // catch this.
  const forged = { ...plan, components: [{ ...plan.components[0], bump: 'major', toVersion: '1.0.0' }] };
  forged.checksum = planChecksum(forged);
  assert.throws(() => applyPlan(forged, manifestPath(dir), { root: dir }),
    /plan does not match the actual pending changesets at this source commit/);
});

test('rejects a changelog read failure that is not a missing-file error', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const changelogPath = path.join(dir, 'releases', 'CHANGELOG.md');
  const fakeRead = (file, encoding) => {
    if (path.resolve(file) === path.resolve(changelogPath)) {
      const error = new Error('EACCES: permission denied');
      error.code = 'EACCES';
      throw error;
    }
    return readFileSync(file, encoding);
  };
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir, readFileSync: fakeRead }),
    /cannot read existing changelog/);
  // Nothing should have been written.
  assert.equal(readManifest(dir).components.find((c) => c.id === 'Pkg.A').version, '0.1.0');
});

test('rolls back and leaves no leftover temp files when a write fails mid-sequence', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const before = {
    releases: readdirSync(path.join(dir, 'releases')).sort(),
    pkgA: readdirSync(path.join(dir, 'packages', 'Pkg.A')).sort(),
  };

  let writeCount = 0;
  const failingWrite = (file, content) => {
    writeCount += 1;
    if (writeCount === 3) throw new Error('simulated disk full');
    writeFileSync(file, content);
  };

  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir, writeFileSync: failingWrite,
  }), /I\/O failure while writing release artifacts, rolled back: simulated disk full/);

  assert.deepEqual(readdirSync(path.join(dir, 'releases')).sort(), before.releases);
  assert.deepEqual(readdirSync(path.join(dir, 'packages', 'Pkg.A')).sort(), before.pkgA);
});

test('cleans up a leftover temp file when the rename step itself fails', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const before = {
    releases: readdirSync(path.join(dir, 'releases')).sort(),
    pkgA: readdirSync(path.join(dir, 'packages', 'Pkg.A')).sort(),
  };

  let renameCount = 0;
  const failingRename = (from, to) => {
    renameCount += 1;
    if (renameCount === 3) throw new Error('simulated rename failure');
    renameSync(from, to);
  };

  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir, renameSync: failingRename,
  }), /I\/O failure while writing release artifacts, rolled back: simulated rename failure/);

  assert.deepEqual(readdirSync(path.join(dir, 'releases')).sort(), before.releases);
  assert.deepEqual(readdirSync(path.join(dir, 'packages', 'Pkg.A')).sort(), before.pkgA);
});

test('a real plan -> apply -> commit round trip produces a receipt that satisfies the CI changeset guard', (t) => {
  const dir = initRepo(t);
  const baseSha = git(dir, 'rev-parse', 'HEAD');
  const plan = createPlan(readManifest(dir), { root: dir });
  const result = applyPlan(plan, manifestPath(dir), { root: dir });
  assert.deepEqual(result.applied, ['Pkg.A']);

  git(dir, 'add', '-A');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'apply release plan');

  const changes = diffChanges(dir, baseSha);
  const ancestorSha = resolveMergeBase(dir, baseSha);
  assert.equal(ancestorSha, baseSha);
  assert.doesNotThrow(() => validateChangesets(readManifest(dir), { root: dir, changes, ancestorSha }));
});

test('an unrelated product-code edit with no changeset or receipt still fails the coverage guard', (t) => {
  const dir = initRepo(t, { changeset: false });
  const baseSha = git(dir, 'rev-parse', 'HEAD');
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Extra.cs'), 'class Extra {}\n');
  git(dir, 'add', '-A');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'unrelated edit, no changeset');

  const changes = diffChanges(dir, baseSha);
  const ancestorSha = resolveMergeBase(dir, baseSha);
  assert.throws(() => validateChangesets(readManifest(dir), { root: dir, changes, ancestorSha }),
    /missing new or modified changeset entries for Pkg\.A/);
});

test('a receipt with a spoofed sourceSha is rejected by the coverage guard, not silently credited', (t) => {
  const dir = initRepo(t);
  const baseSha = git(dir, 'rev-parse', 'HEAD');
  const plan = createPlan(readManifest(dir), { root: dir });
  applyPlan(plan, manifestPath(dir), { root: dir });

  // Tamper the committed receipt's sourceSha after the real apply, before committing: this must
  // still fail even though the filename and archive are otherwise genuine.
  const receiptFile = path.join(dir, 'releases', 'receipts', `${plan.sourceSha}.json`);
  const receipt = JSON.parse(readFileSync(receiptFile, 'utf8'));
  receipt.sourceSha = '0'.repeat(40);
  writeFileSync(receiptFile, JSON.stringify(receipt, null, 2) + '\n');

  git(dir, 'add', '-A');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'apply with spoofed receipt');

  const changes = diffChanges(dir, baseSha);
  const ancestorSha = resolveMergeBase(dir, baseSha);
  assert.throws(() => validateChangesets(readManifest(dir), { root: dir, changes, ancestorSha }),
    /receipt sourceSha does not match its own filename/);
});

test('a deleted-only top-level changeset with no receipt at all still fails the coverage guard', (t) => {
  const dir = initRepo(t, { changeset: false });
  // Keep an unrelated changeset present so `.changeset/` itself survives the deletion below; the
  // point under test is that deleting Pkg.A's note (without a genuine receipt+archive) must not be
  // mistaken for legitimate coverage of a real Pkg.A product-code change.
  writeFileSync(path.join(dir, '.changeset', 'unrelated-b.md'), '---\n"Pkg.B": patch\n---\n\nUnrelated.\n');
  writeFileSync(path.join(dir, '.changeset', 'to-delete-a.md'), '---\n"Pkg.A": minor\n---\n\nWill be deleted.\n');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'changesets present');
  const baseSha = git(dir, 'rev-parse', 'HEAD');

  // Simulate a real Pkg.A product-code bump without going through applyPlan at all (no archive, no
  // receipt) — just deleting the top-level note, which must not be credited as coverage.
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('0.2.0', ['Pkg.B']));
  execFileSync('git', ['rm', '-q', '.changeset/to-delete-a.md'], { cwd: dir });
  git(dir, 'add', '-A');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'bump without receipt');

  const changes = diffChanges(dir, baseSha);
  const ancestorSha = resolveMergeBase(dir, baseSha);
  assert.throws(() => validateChangesets(readManifest(dir), { root: dir, changes, ancestorSha }),
    /missing new or modified changeset entries for Pkg\.A/);
});

function commit(dir, message = 'fixture') {
  git(dir, 'add', '-A');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', message);
}

function snapshot(dir) {
  const entries = [];
  function visit(relative) {
    for (const entry of readdirSync(path.join(dir, relative), { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (entry.name === '.git') continue;
      const file = path.join(relative, entry.name);
      entries.push([file, entry.isDirectory() ? null : readFileSync(path.join(dir, file)).toString('hex')]);
      if (entry.isDirectory()) visit(file);
    }
  }
  visit('');
  return entries;
}

for (const [bump, version] of [['patch', '0.1.1'], ['minor', '0.2.0']]) {
  test(`${bump} dependency bump rejects missing compatibility before writes, then accepts explicit declaration`, (t) => {
    const dir = initRepo(t, { changeset: `---\n"Pkg.B": ${bump}\n---\n\nDependency change.\n` });
    let plan = createPlan(readManifest(dir), { root: dir });
    const before = snapshot(dir);
    assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /not automatically assumed compatible/);
    assert.deepEqual(snapshot(dir), before);
    const manifest = readManifest(dir);
    manifest.compatibility[0].versions.push(version);
    writeFileSync(manifestPath(dir), JSON.stringify(manifest, null, 2) + '\n');
    commit(dir, 'declare verified compatibility');
    plan = createPlan(readManifest(dir), { root: dir });
    assert.deepEqual(applyPlan(plan, manifestPath(dir), { root: dir }).applied, ['Pkg.B']);
  });
}

test('planning rejects uncommitted notes and edited committed note bytes', (t) => {
  const dir = initRepo(t);
  const note = path.join(dir, '.changeset', 'new-note.md');
  writeFileSync(note, '---\n"Pkg.B": patch\n---\n\nUncommitted note.\n');
  assert.throws(() => createPlan(readManifest(dir), { root: dir }), /missing committed source changeset/);
  unlinkSync(note);
  writeFileSync(path.join(dir, '.changeset', 'add-feature.md'), '---\n"Pkg.A": major\n---\n\nChanged intent.\n');
  assert.throws(() => createPlan(readManifest(dir), { root: dir }), /changeset bytes differ from source HEAD/);
});

test('rerun accepts fully staged preparation but rejects hidden staged project edits', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  applyPlan(plan, manifestPath(dir), { root: dir });
  git(dir, 'add', '-A');
  assert.deepEqual(applyPlan(plan, manifestPath(dir), { root: dir }).skipped, ['Pkg.A']);
  const file = path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj');
  const expected = readFileSync(file);
  writeFileSync(file, expected.toString().replace('</Project>', '<!-- hidden index change --></Project>'));
  git(dir, 'add', 'packages/Pkg.A/Pkg.A.csproj');
  writeFileSync(file, expected);
  assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /staged preparation differs/);
});

for (const file of ['packages/Pkg.A/Extra.cs', 'packages/Pkg.B/Extra.cs', 'unrelated.txt']) {
  test(`rerun rejects unrelated dirty ${file}`, (t) => {
    const dir = initRepo(t);
    const plan = createPlan(readManifest(dir), { root: dir });
    applyPlan(plan, manifestPath(dir), { root: dir });
    writeFileSync(path.join(dir, file), 'unrelated edit');
    assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }), /unrelated dirty edits/);
  });
}

for (const corruption of ['empty', 'components', 'checksum', 'archive', 'changelog', 'project']) {
  test(`rerun rejects forged prior-application ${corruption}`, (t) => {
    const dir = initRepo(t);
    const plan = createPlan(readManifest(dir), { root: dir });
    applyPlan(plan, manifestPath(dir), { root: dir });
    const receiptFile = path.join(dir, 'releases', 'receipts', `${plan.sourceSha}.json`);
    const receipt = JSON.parse(readFileSync(receiptFile, 'utf8'));
    if (corruption === 'empty') writeFileSync(receiptFile, '');
    if (corruption === 'components') {
      receipt.components = [];
      writeFileSync(receiptFile, JSON.stringify(receipt));
    }
    if (corruption === 'checksum') {
      receipt.planChecksum = '0'.repeat(64);
      writeFileSync(receiptFile, JSON.stringify(receipt));
    }
    if (corruption === 'archive') {
      const text = '---\n"Pkg.B": minor\n---\n\nSpoofed archive.\n';
      writeFileSync(path.join(dir, '.changeset', 'archive', 'add-feature.md'), text);
      receipt.changesets[0].sha256 = createHash('sha256').update(text).digest('hex');
      writeFileSync(receiptFile, JSON.stringify(receipt));
    }
    if (corruption === 'changelog') writeFileSync(path.join(dir, 'releases', 'CHANGELOG.md'), 'fabricated');
    if (corruption === 'project') {
      const file = path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj');
      writeFileSync(file, readFileSync(file, 'utf8').replace('</Project>', '<!-- unrelated --></Project>'));
    }
    assert.throws(() => applyPlan(plan, manifestPath(dir), { root: dir }));
  });
}

for (const component of ['Pkg.A', 'Pkg.B']) {
  test(`receipt cannot cover extra ${component} product edits, but a fresh note can`, (t) => {
    const dir = initRepo(t);
    const base = git(dir, 'rev-parse', 'HEAD');
    applyPlan(createPlan(readManifest(dir), { root: dir }), manifestPath(dir), { root: dir });
    writeFileSync(path.join(dir, 'packages', component, 'Extra.cs'), 'class Extra {}');
    commit(dir);
    assert.throws(() => validateChangesets(readManifest(dir), {
      root: dir, changes: diffChanges(dir, base), ancestorSha: base,
    }), /missing new or modified changeset entries/);
    writeFileSync(path.join(dir, '.changeset', 'fresh.md'), `---\n"${component}": patch\n---\n\nExtra change.\n`);
    commit(dir);
    assert.doesNotThrow(() => validateChangesets(readManifest(dir), {
      root: dir, changes: diffChanges(dir, base), ancestorSha: base,
    }));
  });
}

test('receipt cannot credit extra edits in a bumped project mirror', (t) => {
  const dir = initRepo(t);
  const base = git(dir, 'rev-parse', 'HEAD');
  applyPlan(createPlan(readManifest(dir), { root: dir }), manifestPath(dir), { root: dir });
  const project = path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj');
  writeFileSync(project, readFileSync(project, 'utf8').replace('</Project>', '<!-- new product configuration --></Project>'));
  commit(dir);
  assert.throws(() => validateChangesets(readManifest(dir), {
    root: dir, changes: diffChanges(dir, base), ancestorSha: base,
  }), /missing new or modified changeset entries/);
});

test('spoofed archive with a new self-hash and empty components cannot credit ComponentB', (t) => {
  const dir = initRepo(t);
  const base = git(dir, 'rev-parse', 'HEAD');
  applyPlan(createPlan(readManifest(dir), { root: dir }), manifestPath(dir), { root: dir });
  const archive = '---\n"Pkg.B": patch\n---\n\nSpoofed note.\n';
  writeFileSync(path.join(dir, '.changeset', 'archive', 'add-feature.md'), archive);
  const receiptFile = path.join(dir, 'releases', 'receipts', `${base}.json`);
  const receipt = JSON.parse(readFileSync(receiptFile, 'utf8'));
  receipt.components = [];
  receipt.changesets[0].sha256 = createHash('sha256').update(archive).digest('hex');
  writeFileSync(receiptFile, JSON.stringify(receipt));
  writeFileSync(path.join(dir, 'packages', 'Pkg.B', 'Feature.cs'), 'class Feature {}');
  commit(dir);
  assert.throws(() => validateChangesets(readManifest(dir), {
    root: dir, changes: diffChanges(dir, base), ancestorSha: base,
  }), /genuine source changesets/);
});

test('a changed source archive cannot pass even with genuine component decisions and a fresh self-hash', (t) => {
  const dir = initRepo(t);
  const base = git(dir, 'rev-parse', 'HEAD');
  applyPlan(createPlan(readManifest(dir), { root: dir }), manifestPath(dir), { root: dir });
  const archive = '---\n"Pkg.A": minor\n---\n\nFabricated source summary.\n';
  writeFileSync(path.join(dir, '.changeset', 'archive', 'add-feature.md'), archive);
  const receiptFile = path.join(dir, 'releases', 'receipts', `${base}.json`);
  const receipt = JSON.parse(readFileSync(receiptFile, 'utf8'));
  receipt.changesets[0].sha256 = createHash('sha256').update(archive).digest('hex');
  writeFileSync(receiptFile, JSON.stringify(receipt));
  commit(dir);
  assert.throws(() => validateChangesets(readManifest(dir), {
    root: dir, changes: diffChanges(dir, base), ancestorSha: base,
  }), /genuine source changeset bytes/);
});

test('receipt with genuine archived notes cannot fabricate an application without actual prepared versions', (t) => {
  const dir = initRepo(t);
  const base = git(dir, 'rev-parse', 'HEAD');
  applyPlan(createPlan(readManifest(dir), { root: dir }), manifestPath(dir), { root: dir });
  writeFileSync(manifestPath(dir), execFileSync('git', ['show', `${base}:releases/foundation.json`], { cwd: dir }));
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'),
    execFileSync('git', ['show', `${base}:packages/Pkg.A/Pkg.A.csproj`], { cwd: dir }));
  commit(dir);
  assert.throws(() => validateChangesets(readManifest(dir), {
    root: dir, changes: diffChanges(dir, base), ancestorSha: base,
  }), /actual component version does not match preparation decision/);
});

for (const failure of ['write', 'rename', 'archive']) {
  for (const emptyChangelog of [false, true]) {
    test(`full-tree rollback after ${failure} failure preserves ${emptyChangelog ? 'empty existing' : 'absent'} changelog and directories`, (t) => {
      const dir = initRepo(t);
      if (emptyChangelog) {
        writeFileSync(path.join(dir, 'releases', 'CHANGELOG.md'), '');
        commit(dir);
      }
      const before = snapshot(dir);
      const plan = createPlan(readManifest(dir), { root: dir });
      const options = { root: dir };
      if (failure === 'write') options.writeFileSync = (file, content) => {
        writeFileSync(file, content);
        if (file.includes(`${plan.sourceSha}.json.tmp-`)) throw new Error('write failed after creating bytes');
      };
      if (failure === 'rename') options.renameSync = (from, to) => {
        if (to.endsWith(`${plan.sourceSha}.json`)) throw new Error('receipt rename failed');
        renameSync(from, to);
      };
      if (failure === 'archive') options.renameSync = (from, to) => {
        if (to.includes(`${path.sep}archive${path.sep}`)) throw new Error('archive rename failed');
        renameSync(from, to);
      };
      assert.throws(() => applyPlan(plan, manifestPath(dir), options), /rolled back/);
      assert.deepEqual(snapshot(dir), before);
      assert.deepEqual(applyPlan(plan, manifestPath(dir), { root: dir }).applied, ['Pkg.A']);
    });
  }
}

test('rollback keeps pre-existing empty owned directories', (t) => {
  const dir = initRepo(t);
  mkdirSync(path.join(dir, '.changeset', 'archive'));
  mkdirSync(path.join(dir, 'releases', 'receipts'));
  const before = snapshot(dir);
  const plan = createPlan(readManifest(dir), { root: dir });
  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir,
    renameSync: (from, to) => {
      if (to.includes(`${path.sep}archive${path.sep}`)) throw new Error('archive failed');
      renameSync(from, to);
    },
  }), /rolled back/);
  assert.deepEqual(snapshot(dir), before);
});

test('changelog ENOENT text does not hide a different error code', (t) => {
  const dir = initRepo(t);
  const plan = createPlan(readManifest(dir), { root: dir });
  const before = snapshot(dir);
  assert.throws(() => applyPlan(plan, manifestPath(dir), {
    root: dir,
    readFileSync: (file, encoding) => {
      if (file.endsWith('CHANGELOG.md')) throw Object.assign(new Error('ENOENT mentioned but access denied'), { code: 'EACCES' });
      return readFileSync(file, encoding);
    },
  }), /cannot read existing changelog/);
  assert.deepEqual(snapshot(dir), before);
});

for (const failure of ['write', 'rename', 'receipt-undo', 'directory-undo']) {
  test(`cleanup failures remain explicit alongside original ${failure} failure`, (t) => {
    const dir = initRepo(t);
    const plan = createPlan(readManifest(dir), { root: dir });
    const options = { root: dir };
    if (failure === 'write') options.writeFileSync = (file, content) => {
      writeFileSync(file, content);
      throw new Error('original write failure');
    };
    options.renameSync = (from, to) => {
      if (failure === 'rename' || to.includes(`${path.sep}archive${path.sep}`)) throw new Error('original rename failure');
      renameSync(from, to);
    };
    if (failure === 'directory-undo') options.rmdirSync = () => { throw new Error('directory cleanup denied'); };
    else options.unlinkSync = (file) => {
      if (failure !== 'receipt-undo' || file.endsWith(`${plan.sourceSha}.json`)) throw new Error('cleanup denied');
      unlinkSync(file);
    };
    assert.throws(() => applyPlan(plan, manifestPath(dir), options), /original.*failure.*cleanup|rollback also failed/);
    assert.ok(existsSync(path.join(dir, '.changeset', 'add-feature.md')));
  });
}
