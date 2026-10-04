import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { diffChanges, parseChangeset, validateChangesets } from '../changesets.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const manifest = JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
const ids = new Set(manifest.components.map(({ id }) => id));
const record = (id = 'Agentweaver.Providers', bump = 'minor') =>
  `---\n"${id}": ${bump}\n---\n\nAdd provider functionality.\n`;

function fixture(t, records = { 'provider.md': record() }) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const directory = mkdtempSync(path.join(fixtureRoot, 'v1-changesets-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  mkdirSync(path.join(directory, '.changeset'));
  for (const [name, content] of Object.entries(records)) {
    writeFileSync(path.join(directory, '.changeset', name), content);
  }
  return directory;
}

test('checked-in backfill validates against all manifest component IDs', () => {
  const records = validateChangesets(manifest, { root });
  const archiveDirectory = path.join(root, '.changeset', 'archive');
  const archivedRecords = existsSync(archiveDirectory)
    ? readdirSync(archiveDirectory).filter((file) => file.endsWith('.md')).length : 0;
  assert.ok(records.size + archivedRecords >= 15);
  const providerChangeset = records.get('.changeset/provider-foundation-1735.md') ??
    parseChangeset(readFileSync(path.join(archiveDirectory, 'provider-foundation-1735.md'), 'utf8'),
      '.changeset/provider-foundation-1735.md', ids);
  assert.deepEqual([...providerChangeset].sort(),
    ['Agentweaver.Abstractions', 'Agentweaver.Providers']);
  assert.equal(records.get('.changeset/project-workflow-1743.md').size, 0);
});

test('rejects malformed records, unknown IDs, invalid bumps and empty summaries', () => {
  for (const [content, error] of [
    ['"Agentweaver.Providers": minor\n---\n\nSummary', /opening/],
    ['---\n"Agentweaver.Providers": minor\n\nSummary', /closing/],
    ['---\nAgentweaver.Providers: minor\n---\n\nSummary', /invalid component/],
    [record('Missing.Component'), /unknown component/],
    [record('Agentweaver.Providers', 'feature'), /invalid component/],
    ['---\n"Agentweaver.Providers": minor\n"Agentweaver.Providers": patch\n---\n\nSummary', /duplicate component/],
    ['---\n"Agentweaver.Providers": minor\n---\n\n   ', /nonempty human summary/],
    ['---\n---\n\n', /nonempty human summary/],
    ['---\n---\nSummary', /blank line/],
  ]) {
    assert.throws(() => parseChangeset(content, 'bad.md', ids), error);
  }
  assert.equal(parseChangeset('---\n---\n\nTooling only.\n', 'tool.md', ids).size, 0);
  assert.deepEqual([...parseChangeset(record().replaceAll('\n', '\r\n'), 'windows.md', ids)],
    ['Agentweaver.Providers']);
});

test('rejects unsafe or non-record files in the changeset directory', (t) => {
  const directory = fixture(t, { 'wrong.txt': record() });
  assert.throws(() => validateChangesets(manifest, { root: directory }), /safe .changeset\/\*\.md record name/);
  rmSync(path.join(directory, '.changeset', 'wrong.txt'));
  writeFileSync(path.join(directory, '.changeset', 'provider.md'), record('Unknown'));
  assert.throws(() => validateChangesets(manifest, { root: directory }), /unknown component/);
});

test('a product diff needs a current record for each changed component, not a historical one', (t) => {
  const directory = fixture(t);
  const product = (component, file = 'Library.cs') => ({
    status: 'M', file: `packages/${component}/${file}`,
  });
  assert.throws(() => validateChangesets(manifest, {
    root: directory, changes: [product('Agentweaver.Providers')],
  }), /missing new or modified changeset entries for Agentweaver.Providers/);
  assert.equal(validateChangesets(manifest, {
    root: directory, changes: [product('Agentweaver.Providers'), {
      status: 'A', file: '.changeset/provider.md',
    }],
  }).size, 1);
  assert.throws(() => validateChangesets(manifest, {
    root: directory, changes: [product('Agentweaver.Providers'), product('Agentweaver.Abstractions', 'Contract.cs'),
      { status: 'M', file: '.changeset/provider.md' }],
  }), /missing new or modified changeset entries for Agentweaver.Abstractions/);
  assert.throws(() => validateChangesets(manifest, {
    root: directory, changes: [product('Agentweaver.Providers'),
      { status: 'D', file: '.changeset/provider.md' }],
  }), /missing new or modified changeset entries/);
  assert.throws(() => validateChangesets(manifest, {
    root: directory, changes: [{ status: 'D', file: 'packages/Agentweaver.Providers/Library.cs' }],
  }), /missing new or modified changeset entries/);
  assert.equal(validateChangesets(manifest, {
    root: directory, changes: [{ status: 'D', file: 'packages/Agentweaver.Providers/Library.cs' },
      { status: 'A', file: '.changeset/provider.md' }],
  }).size, 1);
});

test('project, schema and functional configuration count; docs, tests, generated files and tooling do not', (t) => {
  const directory = fixture(t, { 'tooling.md': '---\n---\n\nImprove workflow UI.\n' });
  for (const filename of ['Agentweaver.Providers.csproj', 'schema/registry.sql', 'config/settings.json']) {
    assert.throws(() => validateChangesets(manifest, { root: directory, changes: [{
      status: 'A', file: `packages/Agentweaver.Providers/${filename}`,
    }] }), /missing new or modified changeset entries/);
  }
  const exempt = ['packages/Agentweaver.Providers/README.md',
    'packages/Agentweaver.Providers/docs/guide.md',
    'packages/Agentweaver.Providers/obj/Generated.cs',
    'packages/Agentweaver.Providers/Generated/Generated.cs',
    'packages/Agentweaver.Providers/tests/ResolverTests.cs',
    'packages/Agentweaver.Providers/Build.g.cs',
    'packages/Agentweaver.Providers/docs/retired.md',
    'tests/Agentweaver.Providers.Tests/Test.cs',
    'scripts/release/validate.mjs', '.github/workflows/v1-ci.yml'];
  const changes = exempt.map((file) => ({ status: file.endsWith('retired.md') ? 'D' : 'M', file }));
  assert.equal(validateChangesets(manifest, { root: directory, changes }).size, 1);
});

test('diff base must exist, be a full SHA, and include only committed file changes', (t) => {
  const directory = fixture(t);
  const git = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8' }).trim();
  git('init', '-q');
  writeFileSync(path.join(directory, 'initial.txt'), 'before');
  git('-c', 'core.autocrlf=false', 'add', '.');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial');
  const base = git('rev-parse', 'HEAD');
  writeFileSync(path.join(directory, 'next.txt'), 'after');
  git('-c', 'core.autocrlf=false', 'add', 'next.txt');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'next');
  assert.deepEqual(diffChanges(directory, base), [{ status: 'A', file: 'next.txt' }]);
  assert.throws(() => diffChanges(directory, 'v1'), /full lowercase 40-character commit SHA/);
  assert.throws(() => diffChanges(directory, 'a'.repeat(40)), /cannot compare/);
});

test('CLI rejects a missing base instead of treating it as a docs-only diff', () => {
  const result = spawnSync(process.execPath, [
    path.join(root, 'scripts', 'release', 'validate.mjs'), 'releases/foundation.json', '--base', '',
  ], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /full lowercase 40-character commit SHA/);
});
