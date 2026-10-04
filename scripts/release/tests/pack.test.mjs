import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { packComponents } from '../pack.mjs';

const csproj = (version) => `<Project><PropertyGroup><Version>${version}</Version></PropertyGroup></Project>`;

const manifestBase = () => ({
  schemaVersion: 1,
  stage: 'draft',
  components: [
    { id: 'Pkg.A', kind: 'library', version: '0.1.0', project: 'packages/Pkg.A/Pkg.A.csproj' },
    { id: 'Pkg.B', kind: 'contract', version: '0.1.0', project: 'packages/Pkg.B/Pkg.B.csproj' },
  ],
  compatibility: [],
});

function git(dir, ...args) {
  return execFileSync('git', args, { cwd: dir, encoding: 'utf8' }).trim();
}

function initRepo(t) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const dir = mkdtempSync(path.join(fixtureRoot, 'v1-pack-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, 'packages', 'Pkg.A'), { recursive: true });
  mkdirSync(path.join(dir, 'packages', 'Pkg.B'), { recursive: true });
  // Mirrors the real repository: the pack output directory is gitignored, so producing (or finding
  // leftover) artifacts under it never counts as "dirty" on its own.
  writeFileSync(path.join(dir, '.gitignore'), 'artifacts/\n');
  writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('0.1.0'));
  writeFileSync(path.join(dir, 'packages', 'Pkg.B', 'Pkg.B.csproj'), csproj('0.1.0'));
  for (const id of ['Pkg.A', 'Pkg.B']) writeFileSync(path.join(dir, 'packages', id, 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  git(dir, 'init', '-q');
  git(dir, 'config', 'core.autocrlf', 'false');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial');
  return dir;
}

// A fake `dotnet` that never shells out: it only reacts to the `pack --output <dir>` step by
// dropping a deterministic fake .nupkg next to where the real CLI would, so tests stay fast and
// dependency-free (no network restore, no real MSBuild).
function fakeDotnet(calls, { onBuild } = {}) {
  return (args) => {
    calls.push(args);
    if (args[0] === 'build' && onBuild) onBuild();
    if (args[0] === 'pack') {
      const outIndex = args.indexOf('--output');
      const outDir = args[outIndex + 1];
      const project = args[1];
      const name = path.basename(project, '.csproj');
      writeFileSync(path.join(outDir, `${name}.0.1.0.nupkg`), `fake package for ${name}`);
    }
  };
}

test('packs every non-service component, hashing only the artifacts it produced', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  const calls = [];
  const provenance = packComponents(manifestBase(), { root: dir, dotnet: fakeDotnet(calls), outDir });

  assert.equal(provenance.schemaVersion, 1);
  assert.equal(provenance.stage, 'draft');
  assert.match(provenance.sourceSha, /^[a-f0-9]{40}$/);
  assert.deepEqual(provenance.components.map((c) => c.id).sort(), ['Pkg.A', 'Pkg.B']);
  assert.equal(provenance.artifacts.length, 2);
  for (const artifact of provenance.artifacts) {
    assert.match(artifact.sha256, /^[a-f0-9]{64}$/);
    assert.ok(artifact.size > 0);
  }
  // restore, build, and pack each ran once per component, in order, before any pack.
  assert.deepEqual(calls.map((c) => c[0]), ['restore', 'build', 'pack', 'restore', 'build', 'pack']);

  const onDisk = JSON.parse(readFileSync(path.join(outDir, 'provenance.json'), 'utf8'));
  assert.deepEqual(onDisk, provenance);
});

test('packages-only preparation leaves service images out while retaining the full manifest hash', (t) => {
  const dir = initRepo(t);
  const manifest = manifestBase();
  manifest.components.push({
    id: 'Svc', kind: 'service', version: '0.1.0', project: 'services/Svc/Svc.csproj',
  });
  const outDir = path.join(dir, 'artifacts', 'release', 'packages');
  const calls = [];
  const provenance = packComponents(manifest, {
    root: dir, dotnet: fakeDotnet(calls), outDir, packagesOnly: true,
  });

  assert.deepEqual(provenance.components.map(({ id }) => id), ['Pkg.A', 'Pkg.B']);
  assert.deepEqual(provenance.artifacts.map(({ kind }) => kind), ['package', 'package']);
  assert.deepEqual(calls.map(([verb]) => verb), ['restore', 'build', 'pack', 'restore', 'build', 'pack']);
  assert.ok(!existsSync(path.join(outDir, 'Svc.0.1.0.tar.gz')));
  assert.equal(provenance.manifestSha256, createHash('sha256').update(JSON.stringify(manifest)).digest('hex'));
});

test('never writes provenance.json when the working tree is dirty before packing', (t) => {
  const dir = initRepo(t);
  writeFileSync(path.join(dir, 'uncommitted.txt'), 'x');
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet: fakeDotnet([]), outDir }),
    /working tree must be clean before packing/,
  );
  assert.ok(!existsSync(outDir));
});

test('rejects drift introduced by the build step instead of trusting it silently', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  const calls = [];
  const dotnet = fakeDotnet(calls, {
    onBuild: () => writeFileSync(path.join(dir, 'packages', 'Pkg.A', 'Pkg.A.csproj'), csproj('9.9.9')),
  });
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet, outDir }),
    /working tree is no longer clean after the build/,
  );
  // No provenance should have been written even though some pack artifacts may already exist.
  assert.ok(!existsSync(path.join(outDir, 'provenance.json')));
});

test('rejects a non-empty existing output directory instead of mixing artifacts across runs', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  mkdirSync(outDir, { recursive: true });
  writeFileSync(path.join(outDir, 'stale.nupkg'), 'leftover from a previous run');
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet: fakeDotnet([]), outDir }),
    /expected a fresh, empty output directory/,
  );
});

test('rejects a service without a checked-in locked project', (t) => {
  const dir = initRepo(t);
  const serviceOnly = { ...manifestBase(), components: [
    { id: 'Svc', kind: 'service', version: '0.1.0', project: 'services/Svc/Svc.csproj' },
  ] };
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  assert.throws(
    () => packComponents(serviceOnly, { root: dir, dotnet: fakeDotnet([]), outDir }),
    /checked-in packages.lock.json is required/,
  );
});

test('surfaces a failing dotnet invocation without writing provenance', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  const dotnet = (args) => {
    if (args[0] === 'build') throw new Error('dotnet: build: MSB1234 simulated failure');
  };
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet, outDir }),
    /simulated failure/,
  );
  assert.ok(!existsSync(path.join(outDir, 'provenance.json')));
});

test('rejects a pack step that silently produces no artifact for a component', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  // A pack invocation that "succeeds" (exit 0) without actually dropping the expected .nupkg must
  // still be caught: this is the exact per-component artifact check, not just "some output exists".
  const dotnet = (args) => {
    if (args[0] !== 'pack') return;
    const outIndex = args.indexOf('--output');
    const project = args[1];
    const name = path.basename(project, '.csproj');
    if (name === 'Pkg.B') return; // Pkg.A packs fine; Pkg.B silently produces nothing.
    writeFileSync(path.join(args[outIndex + 1], `${name}.0.1.0.nupkg`), `fake package for ${name}`);
  };
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet, outDir }),
    /expected package artifact Pkg\.B\.0\.1\.0\.nupkg was not produced for Pkg\.B/,
  );
  assert.ok(!existsSync(path.join(outDir, 'provenance.json')));
});

test('rejects packing when HEAD moves mid-run instead of trusting a stale sourceSha', (t) => {
  const dir = initRepo(t);
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  let committed = false;
  const dotnet = fakeDotnet([], {
    onBuild: () => {
      // Simulate HEAD moving mid-build (e.g. a concurrent process) exactly once; restoring the tree
      // to clean first so the existing post-build dirty-tree check doesn't mask this different
      // failure mode, without attempting a second no-op commit on the second component's build.
      if (committed) return;
      committed = true;
      writeFileSync(path.join(dir, 'extra.txt'), 'x');
      git(dir, 'add', '.');
      git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'concurrent commit');
    },
  });
  assert.throws(
    () => packComponents(manifestBase(), { root: dir, dotnet, outDir }),
    /HEAD moved during packing/,
  );
  assert.ok(!existsSync(path.join(outDir, 'provenance.json')));
});

test('a real (non-mocked) dotnet restore --locked-mode, build, and pack produce real artifacts with HEAD unchanged', { timeout: 60_000 }, (t) => {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const dir = mkdtempSync(path.join(fixtureRoot, 'v1-pack-real-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const projectDir = path.join(dir, 'packages', 'Real.Pkg');
  mkdirSync(projectDir, { recursive: true });
  // A trivial, dependency-free net10.0 classlib: no PackageReference, no NuGet feed required, so
  // restore/build/pack all run fully offline. Confirmed working in this sandbox (dotnet 10.0.401).
  writeFileSync(path.join(projectDir, 'Real.Pkg.csproj'),
    '<Project Sdk="Microsoft.NET.Sdk">\n<PropertyGroup>\n<TargetFramework>net10.0</TargetFramework>\n' +
    '<Version>0.1.0</Version>\n</PropertyGroup>\n</Project>\n');
  writeFileSync(path.join(projectDir, 'Class1.cs'), 'namespace Real.Pkg;\npublic class Class1 {}\n');
  writeFileSync(path.join(projectDir, 'packages.lock.json'), '{"version":1,"dependencies":{"net10.0":{}}}\n');
  // bin/ and obj/ are build output, not source; they must be ignored so a real build never counts
  // as repository drift (mirrors how the actual repository's package projects are configured).
  writeFileSync(path.join(dir, '.gitignore'), 'bin/\nobj/\nartifacts/\n');
  git(dir, 'init', '-q');
  git(dir, 'config', 'core.autocrlf', 'false');
  git(dir, 'add', '.');
  git(dir, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial');

  const manifest = {
    schemaVersion: 1,
    stage: 'draft',
    components: [{ id: 'Real.Pkg', kind: 'library', version: '0.1.0', project: 'packages/Real.Pkg/Real.Pkg.csproj' }],
    compatibility: [],
  };
  const outDir = path.join(dir, 'artifacts', 'release', 'pack');
  const headBefore = git(dir, 'rev-parse', 'HEAD');

  const provenance = packComponents(manifest, { root: dir, outDir }); // real default `dotnet` runner

  assert.equal(provenance.sourceSha, headBefore);
  assert.equal(git(dir, 'rev-parse', 'HEAD'), headBefore);
  assert.equal(provenance.artifacts.length, 1);
  assert.equal(provenance.artifacts[0].path, 'Real.Pkg.0.1.0.nupkg');
  assert.ok(existsSync(path.join(outDir, 'Real.Pkg.0.1.0.nupkg')));
  const onDiskHash = createHash('sha256').update(readFileSync(path.join(outDir, 'Real.Pkg.0.1.0.nupkg'))).digest('hex');
  assert.equal(onDiskHash, provenance.artifacts[0].sha256);
});
