import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { mergePacks } from '../merge-packs.mjs';

function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function writePack(root, directory, components) {
  mkdirSync(directory, { recursive: true });
  const artifacts = [];
  for (const component of components) {
    const name = component.kind === 'service' ? `${component.id}.${component.version}.tar.gz` :
      `${path.basename(component.project, '.csproj')}.${component.version}.nupkg`;
    const bytes = Buffer.from(`${component.id} ${component.kind}`);
    writeFileSync(path.join(directory, name), bytes);
    artifacts.push({
      path: name,
      sha256: createHash('sha256').update(bytes).digest('hex'),
      size: bytes.length,
      componentId: component.id,
      kind: component.kind === 'service' ? 'image' : 'package',
    });
  }
  const manifest = JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
  const provenance = {
    schemaVersion: 1,
    sourceSha: git(root, 'rev-parse', 'HEAD'),
    manifestSha256: createHash('sha256').update(JSON.stringify(manifest)).digest('hex'),
    stage: manifest.stage,
    createdAt: '2026-01-01T00:00:00.000Z',
    components: components.map(({ id, kind, version, project }) => ({
      id, kind, version, project, lock: { path: `${path.posix.dirname(project)}/packages.lock.json`, sha256: 'a'.repeat(64) },
    })),
    artifacts,
  };
  writeFileSync(path.join(directory, 'provenance.json'), `${JSON.stringify(provenance, null, 2)}\n`);
  return provenance;
}

function fixture(t, { includeProbe = false } = {}) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const root = mkdtempSync(path.join(fixtureRoot, 'v1-merge-packs-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const components = [
    { id: 'Pkg', kind: 'library', version: '0.1.0', project: 'packages/Pkg/Pkg.csproj' },
    { id: 'Svc', kind: 'service', version: '0.1.0', project: 'services/Svc/Svc.csproj' },
  ];
  if (includeProbe) {
    components.push({
      id: 'Agentweaver.FoundationProbe', kind: 'service', version: '0.0.1',
      project: 'tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj',
    });
  }
  writeFileSync(path.join(root, '.gitignore'), 'artifacts/\n');
  mkdirSync(path.join(root, 'releases'), { recursive: true });
  mkdirSync(path.join(root, 'packages', 'Pkg'), { recursive: true });
  mkdirSync(path.join(root, 'services', 'Svc'), { recursive: true });
  writeFileSync(path.join(root, 'packages', 'Pkg', 'Pkg.csproj'), '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>');
  writeFileSync(path.join(root, 'packages', 'Pkg', 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  writeFileSync(path.join(root, 'services', 'Svc', 'Svc.csproj'), '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>');
  writeFileSync(path.join(root, 'services', 'Svc', 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  if (includeProbe) {
    const probeDir = path.join(root, 'tools', 'Agentweaver.FoundationProbe');
    mkdirSync(probeDir, { recursive: true });
    writeFileSync(path.join(probeDir, 'Agentweaver.FoundationProbe.csproj'),
      '<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>');
    writeFileSync(path.join(probeDir, 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  }
  const manifest = { schemaVersion: 1, stage: 'draft', components, compatibility: [] };
  writeFileSync(path.join(root, 'releases', 'foundation.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  git(root, 'init', '-q');
  git(root, 'config', 'core.autocrlf', 'false');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'release source');
  const productComponents = components.filter(component => component.id !== 'Agentweaver.FoundationProbe');
  const packages = path.join(root, 'artifacts', 'release', 'packages');
  const images = path.join(root, 'artifacts', 'release', 'images');
  const combined = path.join(root, 'artifacts', 'release', 'combined');
  const packageProvenance = writePack(root, packages, productComponents.filter(component => component.kind !== 'service'));
  const imageProvenance = writePack(root, images, productComponents.filter(component => component.kind === 'service'));
  return { root, packages, images, combined, packageProvenance, imageProvenance };
}

test('merges parallel package and product-image packs into one source-bound publisher input', t => {
  const f = fixture(t, { includeProbe: true });
  const provenance = mergePacks(
    'releases/foundation.json', f.packages, f.images, f.combined, { root: f.root, platformOnly: true },
  );

  assert.deepEqual(provenance.components.map(component => component.id), ['Pkg', 'Svc']);
  assert.deepEqual(provenance.artifacts.map(artifact => artifact.componentId), ['Pkg', 'Svc']);
  assert.equal(provenance.sourceSha, git(f.root, 'rev-parse', 'HEAD'));
  assert.equal(readFileSync(path.join(f.combined, 'Pkg.0.1.0.nupkg'), 'utf8'), 'Pkg library');
  assert.equal(readFileSync(path.join(f.combined, 'Svc.0.1.0.tar.gz'), 'utf8'), 'Svc service');
  assert.deepEqual(JSON.parse(readFileSync(path.join(f.combined, 'provenance.json'), 'utf8')), provenance);
});

test('rejects packs from different source revisions before creating publisher input', t => {
  const f = fixture(t);
  const imageProvenance = { ...f.imageProvenance, sourceSha: 'b'.repeat(40) };
  writeFileSync(path.join(f.images, 'provenance.json'), `${JSON.stringify(imageProvenance, null, 2)}\n`);

  assert.throws(() => mergePacks(
    'releases/foundation.json', f.packages, f.images, f.combined, { root: f.root },
  ), /provenance must match the release source/);
});

test('rejects unlisted image output rather than copying it into the publisher input', t => {
  const f = fixture(t);
  writeFileSync(path.join(f.images, 'unexpected.txt'), 'not part of provenance');

  assert.throws(() => mergePacks(
    'releases/foundation.json', f.packages, f.images, f.combined, { root: f.root },
  ), /artifact directory contains missing or unlisted images files/);
});

test('rejects source changes before reading release packs', t => {
  const f = fixture(t);
  // The fixture is committed and clean, so this edit isolates the dirty-source check.
  writeFileSync(path.join(f.root, 'releases', 'foundation.json'), 'changed');

  assert.throws(() => mergePacks(
    'releases/foundation.json', f.packages, f.images, f.combined, { root: f.root },
  ), /working tree must be clean before merging release packs/);
});

test('rejects overlapping artifact directories without copying files', t => {
  const f = fixture(t);
  // Put the output under the package input to verify overlap is rejected before copying.
  const combined = path.join(f.packages, 'combined');

  assert.throws(() => mergePacks(
    'releases/foundation.json', f.packages, f.images, combined, { root: f.root },
  ), /package, image, and combined artifact directories must not overlap/);
  assert.equal(readFileSync(path.join(f.packages, 'Pkg.0.1.0.nupkg'), 'utf8'), 'Pkg library');
});

test('rejects an existing nonempty combined directory without replacing its contents', t => {
  const f = fixture(t);
  // Preserve a sentinel file to prove a rejected merge does not overwrite existing output.
  mkdirSync(f.combined, { recursive: true });
  writeFileSync(path.join(f.combined, 'keep.txt'), 'existing output');

  assert.throws(() => mergePacks(
    'releases/foundation.json', f.packages, f.images, f.combined, { root: f.root },
  ), /expected a fresh, empty combined artifact directory/);
  assert.equal(readFileSync(path.join(f.combined, 'keep.txt'), 'utf8'), 'existing output');
});
