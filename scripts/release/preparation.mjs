import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createPlan } from './plan.mjs';
import { validateManifest, WEB_LOCK_PATH, WEB_PROJECT_PATH } from './validate.mjs';

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fail = (message) => { throw new Error(`release preparation: ${message}`); };

export function preparationFiles(manifestText, plan, appliedAt, readSource) {
  const manifest = JSON.parse(manifestText);
  if (manifest.stage !== 'draft') fail('apply only prepares draft compositions; publication and deployment evidence must not carry across version changes');
  const files = new Map();
  for (const entry of plan.components) {
    const component = manifest.components.find(({ id }) => id === entry.id);
    if (!component || component.version !== entry.fromVersion) fail(`source version mismatch for ${entry.id}`);
    if (component.project === WEB_PROJECT_PATH) {
      if (component.id !== 'Agentweaver.Web' || component.kind !== 'service') fail(`invalid web component ${entry.id}`);
      let packageJson;
      let packageLock;
      try {
        packageJson = JSON.parse(readSource(WEB_PROJECT_PATH));
        packageLock = JSON.parse(readSource(WEB_LOCK_PATH));
      } catch (error) {
        fail(WEB_PROJECT_PATH, `cannot read npm version mirrors: ${error.message}`);
      }
      if (packageJson.version !== entry.fromVersion) fail(`${WEB_PROJECT_PATH}: source version mismatch for ${entry.id}`);
      if (packageLock?.packages?.['']?.version !== entry.fromVersion) {
        fail(`${WEB_LOCK_PATH}: source root package version mismatch for ${entry.id}`);
      }
      packageJson.version = entry.toVersion;
      packageLock.packages[''].version = entry.toVersion;
      files.set(WEB_PROJECT_PATH, JSON.stringify(packageJson, null, 2) + '\n');
      files.set(WEB_LOCK_PATH, JSON.stringify(packageLock, null, 2) + '\n');
      component.version = entry.toVersion;
      if (component.kind === 'service') delete component.imageDigest;
      continue;
    }
    const original = readSource(component.project);
    const marker = `<Version>${entry.fromVersion}</Version>`;
    if (original.split(marker).length !== 2) fail(`expected a single ${marker} in ${component.project}`);
    files.set(component.project, original.replace(marker, `<Version>${entry.toVersion}</Version>`));
    component.version = entry.toVersion;
    if (component.kind === 'service') delete component.imageDigest;
  }
  for (const rule of manifest.compatibility) {
    const bumped = plan.components.find(({ id }) => id === rule.dependency);
    if (bumped && !rule.versions.includes(bumped.toVersion)) {
      fail(`${bumped.bump} bump of ${bumped.id} to ${bumped.toVersion} is not automatically assumed compatible for consumer ${rule.consumer}; verify compatibility and explicitly declare the version in the source manifest before planning`);
    }
  }
  validateManifest(manifest, {
    root: '.',
    readProject: (file) => {
      const relative = path.relative('.', file).replaceAll('\\', '/');
      return files.get(relative) ?? readSource(relative);
    },
  });
  let original;
  try {
    original = readSource('releases/CHANGELOG.md');
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    original = '';
  }
  const base = original.trim() ? original : '# Release changelog\n\nApplied component version changes, newest first.\n';
  const lines = [`## ${appliedAt} — source \`${plan.sourceSha}\``, ''];
  for (const entry of plan.components) {
    lines.push(`- \`${entry.id}\`: ${entry.fromVersion} -> ${entry.toVersion} (${entry.bump}) — ${entry.changesets.join(', ')}`);
  }
  const section = lines.join('\n') + '\n';
  const index = base.indexOf('\n## ');
  files.set('releases/CHANGELOG.md', index < 0
    ? `${base.trimEnd()}\n\n${section}`
    : `${base.slice(0, index + 1)}${section}\n${base.slice(index + 1)}`);
  return { files, manifestText: JSON.stringify(manifest, null, 2) + '\n' };
}

// Verify from Git source bytes, not from hashes or component claims supplied by a receipt.
export function verifyPreparation(root, receipt, { readFile = readFileSync, verifyArchives = true } = {}) {
  if (!receipt || receipt.schemaVersion !== 1 ||
      !/^[a-f0-9]{40}$/.test(receipt.sourceSha ?? '') ||
      !/^releases\/[a-z0-9][a-z0-9-]*\.json$/.test(receipt.manifestPath ?? '') ||
      typeof receipt.appliedAt !== 'string' || !Number.isFinite(Date.parse(receipt.appliedAt))) {
    fail('malformed source-bound receipt');
  }
  const git = (...args) => execFileSync('git', args, { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  git('cat-file', '-e', `${receipt.sourceSha}^{commit}`);
  const sourcePaths = new Set(git('ls-tree', '-r', '--name-only', receipt.sourceSha).toString('utf8').trim().split('\n'));
  const sourceBytes = (file) => {
    if (!sourcePaths.has(file)) throw Object.assign(new Error(`missing source file ${file}`), { code: 'ENOENT' });
    return git('show', `${receipt.sourceSha}:${file}`);
  };
  const readSource = (file) => sourceBytes(file).toString('utf8');
  const sourceManifest = readSource(receipt.manifestPath);
  const plan = createPlan(JSON.parse(sourceManifest), {
    root,
    git: () => receipt.sourceSha,
    readdirSync: () => [...sourcePaths].filter((file) => /^\.changeset\/[^/]+$/.test(file)).map((file) => path.posix.basename(file)),
    readFileSync: (file) => readSource(path.relative(root, file).replaceAll('\\', '/')),
  });
  if (plan.components.length === 0 || receipt.planChecksum !== plan.checksum ||
      JSON.stringify(receipt.components) !== JSON.stringify(plan.components.map(({ id, fromVersion, toVersion, bump }) => ({ id, fromVersion, toVersion, bump })))) {
    fail('receipt decisions do not match genuine source changesets');
  }
  const notes = [...new Set(plan.components.flatMap(({ changesets }) => changesets))].sort();
  const expectedArchives = notes.map((sourcePath) => ({
    sourcePath,
    archivedPath: `.changeset/archive/${path.posix.basename(sourcePath)}`,
    sha256: hash(sourceBytes(sourcePath)),
  }));
  if (JSON.stringify(receipt.changesets) !== JSON.stringify(expectedArchives)) fail('receipt archives do not match genuine source changeset bytes');
  const prepared = preparationFiles(sourceManifest, plan, receipt.appliedAt, readSource);
  prepared.files.set(receipt.manifestPath, prepared.manifestText);
  for (const { sourcePath, archivedPath } of expectedArchives) {
    if (verifyArchives) {
      const archived = readFile(path.join(root, archivedPath));
      if (!Buffer.from(archived).equals(sourceBytes(sourcePath))) fail(`archive ${archivedPath} differs from source Git note bytes`);
    }
    prepared.files.set(archivedPath, sourceBytes(sourcePath));
  }
  return { plan, files: prepared.files, deleted: notes };
}
