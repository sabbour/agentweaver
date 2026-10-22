import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import {
  assertImageReusePlan, createGitReader, findBaselinePublication, planImageReuse, reusedImageIds, verifyReusedImages,
} from '../image-reuse.mjs';
import { componentImageTag } from '../pack.mjs';

const registry = 'registry.example.invalid/owner';
const digestA = `sha256:${'a'.repeat(64)}`;
const digestB = `sha256:${'b'.repeat(64)}`;

function fixture(t) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'image-reuse-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const write = (file, text) => {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  };
  const components = [
    { id: 'Pkg', kind: 'library', version: '0.1.0', project: 'packages/Pkg/Pkg.csproj' },
    { id: 'SvcA', kind: 'service', version: '0.1.0', project: 'services/A/A.csproj' },
    { id: 'SvcB', kind: 'service', version: '0.1.0', project: 'services/B/B.csproj' },
    { id: 'Agentweaver.FoundationProbe', kind: 'service', version: '0.0.1', project: 'tools/Probe/Probe.csproj' },
  ];
  const manifest = { schemaVersion: 1, stage: 'draft', components, compatibility: [] };
  write('releases/foundation.json', JSON.stringify(manifest));
  write('Directory.Build.props', '<Project />\n');
  write('README.md', 'readme\n');
  write('scripts/release/pack.mjs', '// build tooling\n');
  write('packages/Pkg/Pkg.csproj', '<Project />\n');
  write('packages/Shared/Shared.csproj', '<Project />\n');
  write('packages/Shared/Shared.cs', 'class Shared {}\n');
  write('packages/Deep/Deep.csproj', '<Project />\n');
  write('packages/Deep/Deep.cs', 'class Deep {}\n');
  write('packages/Mid/Mid.csproj', '<Project><ItemGroup><ProjectReference Include="..\\Deep\\Deep.csproj" /></ItemGroup></Project>\n');
  write('services/A/A.csproj', '<Project><ItemGroup><ProjectReference Include="../../packages/Shared/Shared.csproj" />' +
    '<ProjectReference Include="../../packages/Mid/Mid.csproj" /></ItemGroup></Project>\n');
  write('services/A/A.cs', 'class A {}\n');
  write('services/B/B.csproj', '<Project><PropertyGroup><ContainerBaseImage>base@' + digestA + '</ContainerBaseImage></PropertyGroup>' +
    '<ItemGroup><EmbeddedResource Include="../../infra/b.sql" /></ItemGroup></Project>\n');
  write('services/B/B.cs', 'class B {}\n');
  write('services/B/Dockerfile', 'FROM base\n');
  write('infra/b.sql', 'select 1;\n');
  write('tools/Probe/Probe.csproj', '<Project />\n');
  git('init', '-q');
  git('config', 'core.autocrlf', 'false');
  git('config', 'user.name', 'Test');
  git('config', 'user.email', 'test@example.invalid');
  const commit = message => {
    git('add', '-A');
    git('commit', '-qm', message, '--allow-empty');
    return git('rev-parse', 'HEAD');
  };
  const first = commit('source');
  const manifestAt = () => JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
  const setVersion = (id, version) => {
    const next = manifestAt();
    next.components.find(component => component.id === id).version = version;
    write('releases/foundation.json', JSON.stringify(next));
  };
  const imageEntries = (sourceSha, id, version, digest) => ({
    planned: { id, version, kind: 'image', sha256: 'c'.repeat(64),
      destination: `${registry}/${id.toLowerCase()}:${componentImageTag(version, sourceSha)}` },
    published: { id, version, kind: 'image', image: `${registry}/${id.toLowerCase()}@${digest}`, archiveSha256: 'c'.repeat(64) },
  });
  const receiptFor = (sourceSha, { status = 'published', ids = ['SvcA', 'SvcB'], extra = {}, entries } = {}) => {
    const images = entries ?? ids.map(id => imageEntries(sourceSha, id, '0.1.0', id === 'SvcA' ? digestA : digestB));
    return {
      schemaVersion: 1, sourceSha, requiredPackageIds: [], provenanceSha256: 'd'.repeat(64),
      planned: images.map(entry => entry.planned), status,
      published: status === 'published' ? images.map(entry => entry.published) : [], ...extra,
    };
  };
  const tagResult = (sourceSha, receipt) => git('tag', '-a', `agentweaver-publication/${sourceSha}/result`,
    '-m', typeof receipt === 'string' ? receipt : JSON.stringify(receipt), sourceSha);
  const plan = (options = {}) => planImageReuse({
    manifest: manifestAt(), sourceSha: git('rev-parse', 'HEAD'), registry, root, ...options,
  });
  const byId = result => Object.fromEntries(result.components.map(entry => [entry.id, entry]));
  return { root, git, write, commit, first, manifestAt, setVersion, imageEntries, receiptFor, tagResult, plan, byId };
}

test('first release without any published receipt builds every product image and excludes the Probe', t => {
  const f = fixture(t);
  const result = f.plan();
  assert.equal(result.baseline, null);
  assert.match(result.baselineReason, /no earlier published product receipt/);
  assert.deepEqual(result.components.map(({ id, action }) => [id, action]), [['SvcA', 'build'], ['SvcB', 'build']]);
  assert.equal(reusedImageIds(result).size, 0);
  assertImageReusePlan(result, { sourceSha: f.git('rev-parse', 'HEAD'), manifest: f.manifestAt() });
});

test('a legacy baseline receipt from before incremental publication builds every image', t => {
  const f = fixture(t);
  const receipt = f.receiptFor(f.first);
  delete receipt.requiredPackageIds;
  f.tagResult(f.first, receipt);
  f.write('README.md', 'changed\n');
  f.commit('docs');
  const result = f.plan();
  assert.equal(result.baseline, null);
  assert.match(result.baselineReason, /predates incremental publication/);
  assert.ok(result.components.every(entry => entry.action === 'build'));
});

test('an unchanged release reuses every image digest with its original source-bound tag and provenance', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.write('README.md', 'unrelated change\n');
  const source = f.commit('docs');
  const result = f.plan();
  assert.equal(result.baseline.sourceSha, f.first);
  const { SvcA, SvcB } = f.byId(result);
  for (const [entry, digest] of [[SvcA, digestA], [SvcB, digestB]]) {
    assert.equal(entry.action, 'reuse');
    assert.equal(entry.reused.sourceSha, f.first);
    assert.equal(entry.reused.sourceTree, f.git('rev-parse', `${f.first}^{tree}`));
    assert.equal(entry.reused.receiptSourceSha, f.first);
    assert.equal(entry.reused.inputFingerprint, entry.inputFingerprint);
    assert.ok(entry.published.image.endsWith(`@${digest}`));
    assert.ok(entry.planned.destination.endsWith(`:${componentImageTag('0.1.0', f.first)}`));
    assert.ok(!entry.planned.destination.includes(source));
    assert.deepEqual(entry.planned.reused, entry.reused);
    assert.deepEqual(entry.published.reused, entry.reused);
  }
  assertImageReusePlan(result, { sourceSha: source, manifest: f.manifestAt() });
});

for (const [name, change, expected] of [
  ['a direct service source change rebuilds only that image', f => f.write('services/A/A.cs', 'class A { int x; }\n'),
    { SvcA: 'build', SvcB: 'reuse' }],
  ['a shared ProjectReference change rebuilds only its dependents', f => f.write('packages/Shared/Shared.cs', 'class Shared { }\n'),
    { SvcA: 'build', SvcB: 'reuse' }],
  ['a transitive ProjectReference change rebuilds dependents through the closure', f => f.write('packages/Deep/Deep.cs', 'class Deep { }\n'),
    { SvcA: 'build', SvcB: 'reuse' }],
  ['an out-of-directory MSBuild item change rebuilds the including image', f => f.write('infra/b.sql', 'select 2;\n'),
    { SvcA: 'reuse', SvcB: 'build' }],
  ['a Dockerfile change rebuilds that image', f => f.write('services/B/Dockerfile', 'FROM other\n'),
    { SvcA: 'reuse', SvcB: 'build' }],
  ['a pinned base image change rebuilds that image', f => f.write('services/B/B.csproj',
    readFileSync(path.join(f.root, 'services/B/B.csproj'), 'utf8').replace(digestA, digestB)), { SvcA: 'reuse', SvcB: 'build' }],
  ['a repository-wide Directory.Build.props change rebuilds every image',
    f => f.write('Directory.Build.props', '<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>\n'),
    { SvcA: 'build', SvcB: 'build' }],
  ['a new nested Directory.Build.props rebuilds images beneath it', f => f.write('services/A/Directory.Build.props', '<Project />\n'),
    { SvcA: 'build', SvcB: 'reuse' }],
  ['an image build tooling change rebuilds every image', f => f.write('scripts/release/pack.mjs', '// changed tooling\n'),
    { SvcA: 'build', SvcB: 'build' }],
  ['a component version change rebuilds that image', f => f.setVersion('SvcB', '0.2.0'), { SvcA: 'reuse', SvcB: 'build' }],
  ['an unresolvable MSBuild expression rebuilds rather than guessing', f => f.write('services/A/A.csproj',
    '<Project><ItemGroup><Compile Include="$(SharedRoot)/x.cs" /></ItemGroup></Project>\n'), { SvcA: 'build', SvcB: 'reuse' }],
]) {
  test(name, t => {
    const f = fixture(t);
    f.tagResult(f.first, f.receiptFor(f.first));
    change(f);
    f.commit('change');
    const result = f.plan();
    assert.deepEqual(Object.fromEntries(result.components.map(({ id, action }) => [id, action])), expected);
    for (const entry of result.components.filter(item => item.action === 'build')) {
      assert.equal(entry.reused, undefined);
      assert.equal(entry.planned, undefined);
    }
  });
}

test('the reason names the version change and build entries carry no reuse evidence', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.setVersion('SvcA', '0.2.0');
  f.commit('bump');
  const { SvcA } = f.byId(f.plan());
  assert.match(SvcA.reason, /version changed from 0\.1\.0/);
});

test('the explicit rebuild escape hatch builds every image even when nothing changed', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.commit('empty');
  const result = f.plan({ rebuildAll: true });
  assert.equal(result.rebuildAll, true);
  assert.equal(result.baseline, null);
  assert.match(result.baselineReason, /explicitly requested/);
  assert.ok(result.components.every(entry => entry.action === 'build'));
});

test('an image absent from the baseline receipt is built', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first, { ids: ['SvcA'] }));
  f.commit('empty');
  const { SvcA, SvcB } = f.byId(f.plan());
  assert.equal(SvcA.action, 'reuse');
  assert.equal(SvcB.action, 'build');
  assert.match(SvcB.reason, /no image in baseline receipt/);
});

test('an image published to a different registry is rebuilt rather than reused', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.commit('empty');
  const result = f.plan({ registry: 'registry.example.invalid/elsewhere' });
  assert.ok(result.components.every(entry => entry.action === 'build' && /was published to/.test(entry.reason)));
});

test('partial publication results are skipped in favour of the nearest complete one', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.write('README.md', 'second\n');
  const second = f.commit('second');
  f.tagResult(second, f.receiptFor(second, { status: 'partial' }));
  f.write('README.md', 'third\n');
  f.commit('third');
  const result = f.plan();
  assert.equal(result.baseline.sourceSha, f.first);
  assert.ok(result.components.every(entry => entry.action === 'reuse' && entry.reused.sourceSha === f.first));
});

test('chained reuse keeps the commit that actually built the image', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.write('services/A/A.cs', 'class A { int changed; }\n');
  const second = f.commit('second');
  const secondPlan = f.plan();
  const entries = secondPlan.components.map(entry => entry.action === 'reuse'
    ? { planned: entry.planned, published: entry.published }
    : f.imageEntries(second, entry.id, entry.version, `sha256:${'e'.repeat(64)}`));
  f.tagResult(second, f.receiptFor(second, { entries }));
  f.write('README.md', 'third\n');
  f.commit('third');
  const { SvcA, SvcB } = f.byId(f.plan());
  assert.equal(SvcA.reused.sourceSha, second);
  assert.equal(SvcA.published.image, `${registry}/svca@sha256:${'e'.repeat(64)}`);
  assert.equal(SvcB.reused.sourceSha, f.first);
  assert.equal(SvcB.reused.receiptSourceSha, second);
  assert.ok(SvcB.planned.destination.endsWith(componentImageTag('0.1.0', f.first)));
  assert.equal(SvcB.published.image, `${registry}/svcb@${digestB}`);
});

test('a chained reuse compares inputs at the original build commit, not the intermediate receipt', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.write('README.md', 'second\n');
  const second = f.commit('second');
  const entries = f.plan().components.map(entry => ({ planned: entry.planned, published: entry.published }));
  f.tagResult(second, f.receiptFor(second, { entries }));
  f.write('services/B/B.cs', 'class B { int later; }\n');
  f.commit('third');
  const { SvcA, SvcB } = f.byId(f.plan());
  assert.equal(SvcA.action, 'reuse');
  assert.equal(SvcB.action, 'build');
  assert.match(SvcB.reason, new RegExp(`changed since ${f.first}`));
});

test('malformed or unbound baseline receipts fail explicitly instead of rebuilding silently', t => {
  for (const [label, setup, pattern] of [
    ['invalid JSON', f => f.tagResult(f.first, '{not json'), /not valid JSON.*rebuild_all_images/],
    ['another source', f => f.tagResult(f.first, f.receiptFor('f'.repeat(40))), /bound to another source/],
    ['lightweight tag', f => f.git('tag', `agentweaver-publication/${f.first}/result`, f.first), /not an annotated tag/],
    ['mutable digest', f => {
      const receipt = f.receiptFor(f.first);
      receipt.published[0].image = `${registry}/svca:latest`;
      f.tagResult(f.first, receipt);
    }, /invalid SvcA image entry \(destination tag or immutable digest\)/],
    ['tag bound to another source', f => {
      const receipt = f.receiptFor(f.first);
      receipt.planned[1].destination = `${registry}/svcb:${componentImageTag('0.1.0', 'f'.repeat(40))}`;
      f.tagResult(f.first, receipt);
    }, /invalid SvcB image entry/],
    ['archive mismatch', f => {
      const receipt = f.receiptFor(f.first);
      receipt.published[0].archiveSha256 = 'f'.repeat(64);
      f.tagResult(f.first, receipt);
    }, /kind, version, or archive hash/],
  ]) {
    const f = fixture(t);
    setup(f);
    f.commit('empty');
    assert.throws(() => f.plan(), pattern, label);
  }
});

test('baseline lookup ignores results that are not on the first-parent history', t => {
  const f = fixture(t);
  f.git('checkout', '-q', '-b', 'side');
  f.write('README.md', 'side\n');
  const side = f.commit('side');
  f.tagResult(side, f.receiptFor(side));
  f.git('checkout', '-q', '-');
  f.write('README.md', 'main\n');
  const head = f.commit('main');
  assert.equal(findBaselinePublication(createGitReader(f.root), head), undefined);
});

test('reuse plan validation rejects tampered provenance and selection drift', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.write('services/A/A.cs', 'class A { int y; }\n');
  const source = f.commit('change');
  const manifest = f.manifestAt();
  const good = f.plan();
  const clone = () => JSON.parse(JSON.stringify(good));
  const reuse = plan => plan.components.find(entry => entry.action === 'reuse');
  const build = plan => plan.components.find(entry => entry.action === 'build');
  for (const [label, mutate, pattern] of [
    ['other source', plan => { plan.sourceSha = f.first; }, /exact source/],
    ['missing image', plan => { plan.components.pop(); }, /exact source/],
    ['reuse from current source', plan => {
      const entry = reuse(plan);
      entry.reused.sourceSha = source;
      entry.planned.reused.sourceSha = source;
      entry.published.reused.sourceSha = source;
      entry.planned.destination = entry.planned.destination.replace(f.first, source);
    }, /inconsistent original provenance/],
    ['moved destination tag', plan => { reuse(plan).planned.destination += 'x'; }, /inconsistent original provenance/],
    ['foreign repository digest', plan => { reuse(plan).published.image = `other/svcb@${digestB}`; }, /inconsistent original provenance/],
    ['reuse under rebuild-all', plan => { plan.rebuildAll = true; }, /inconsistent original provenance/],
    ['build carrying evidence', plan => { build(plan).reused = reuse(plan).reused; }, /carries reuse evidence/],
    ['unknown action', plan => { build(plan).action = 'retag'; }, /malformed/],
  ]) {
    const plan = clone();
    mutate(plan);
    assert.throws(() => assertImageReusePlan(plan, { sourceSha: source, manifest }), pattern, label);
  }
  assert.doesNotThrow(() => assertImageReusePlan(clone(), { sourceSha: source, manifest }));
});

test('registry verification reads digest and original tag and rejects missing or moved images', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.commit('empty');
  const plan = f.plan();
  const registryState = new Map([
    [`${registry}/svca@${digestA}`, digestA],
    [`${registry}/svca:${componentImageTag('0.1.0', f.first)}`, digestA],
    [`${registry}/svcb@${digestB}`, digestB],
    [`${registry}/svcb:${componentImageTag('0.1.0', f.first)}`, digestB],
  ]);
  const reads = [];
  const reader = state => reference => {
    reads.push(reference);
    if (!state.has(reference)) throw new Error('manifest unknown');
    return { digest: state.get(reference) };
  };
  verifyReusedImages(plan, reader(registryState));
  assert.equal(reads.length, 4);

  const moved = new Map(registryState);
  moved.set(`${registry}/svcb:${componentImageTag('0.1.0', f.first)}`, digestA);
  assert.throws(() => verifyReusedImages(plan, reader(moved)), /no longer resolves to.*rerun with rebuild_all_images/);

  const missing = new Map(registryState);
  missing.delete(`${registry}/svca@${digestA}`);
  assert.throws(() => verifyReusedImages(plan, reader(missing)), /cannot be read from the registry/);

  assert.throws(() => verifyReusedImages(plan, () => ({ digest: digestB })), /SvcA digest .* is missing/);
});

test('the planning CLI rejects an invalid rebuild flag and missing arguments', t => {
  const f = fixture(t);
  f.tagResult(f.first, f.receiptFor(f.first));
  f.commit('empty');
  const script = path.resolve('scripts', 'release', 'image-reuse.mjs');
  const out = path.join(f.root, 'artifacts', 'plans', 'plan.json');
  const runCli = (env, target = out) => spawnSync(process.execPath, [script, 'releases/foundation.json', '--out', target, '--registry', registry], {
    cwd: f.root, encoding: 'utf8', env: { ...process.env, ...env },
  });
  // The CLI reads the repository that contains it, so exercise argument validation here and planning above.
  const invalid = runCli({ RELEASE_REBUILD_ALL_IMAGES: 'yes' });
  assert.notEqual(invalid.status, 0);
  assert.match(invalid.stderr, /RELEASE_REBUILD_ALL_IMAGES must be true or false/);
  const usage = spawnSync(process.execPath, [script, '--out'], { cwd: f.root, encoding: 'utf8' });
  assert.notEqual(usage.status, 0);
  assert.match(usage.stderr, /--out requires a value|usage/);
});
