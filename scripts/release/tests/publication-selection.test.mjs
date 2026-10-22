import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import {
  readPublicationPackageComponents, resolveVersionApplicationSourceSha, selectPublicationPackageComponents,
} from '../publication-selection.mjs';

const sourceSha = 'a'.repeat(40);
const baseSha = 'b'.repeat(40);
const manifest = {
  components: [
    { id: 'Agentweaver.Zeta', kind: 'library', version: '0.2.0' },
    { id: 'Agentweaver.Alpha', kind: 'library', version: '1.1.0' },
    { id: 'Agentweaver.Service', kind: 'service', version: '0.3.0' },
    { id: 'Agentweaver.Unchanged', kind: 'library', version: '0.1.0' },
  ],
};
const plan = {
  components: [
    { id: 'Agentweaver.Zeta', toVersion: '0.2.0' },
    { id: 'Agentweaver.Service', toVersion: '0.3.0' },
    { id: 'Agentweaver.Alpha', toVersion: '1.1.0' },
  ],
};

/** Creates an empty temporary repository root for receipt path checks. */
function fixtureRoot(t) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'v1-publication-selection-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(path.join(root, 'releases', 'receipts'), { recursive: true });
  return root;
}

test('selects only changed non-service packages in stable order', () => {
  assert.deepEqual(selectPublicationPackageComponents(manifest, plan), ['Agentweaver.Alpha', 'Agentweaver.Zeta']);
  assert.deepEqual(selectPublicationPackageComponents(manifest, { components: [] }), []);
});

test('rejects selections that are not bound to the current manifest versions', () => {
  assert.throws(() => selectPublicationPackageComponents(undefined, plan), /expected manifest components and a verified version plan/);
  assert.throws(() => selectPublicationPackageComponents(manifest, {}), /expected manifest components and a verified version plan/);
  assert.throws(() => selectPublicationPackageComponents(
    { components: [...manifest.components, manifest.components[0]] }, plan,
  ), /manifest component IDs must be unique/);

  const cases = [
    [{ id: 'Agentweaver.Missing', toVersion: '1.0.0' }, /for Agentweaver\.Missing/],
    [{ id: 'Agentweaver.Alpha', toVersion: '9.9.9' }, /for Agentweaver\.Alpha/],
    [{ id: 'Agentweaver.Alpha' }, /for Agentweaver\.Alpha/],
    // A numeric version must not be accepted as a manifest version string.
    [{ id: 'Agentweaver.Alpha', toVersion: 1 }, /for Agentweaver\.Alpha/],
    [null, /for an unknown component/],
  ];
  for (const [entry, message] of cases) {
    assert.throws(() => selectPublicationPackageComponents(manifest, { components: [entry] }),
      new RegExp(`version plan does not match the current manifest ${message.source}`));
  }
  assert.throws(() => selectPublicationPackageComponents(manifest, {
    components: [plan.components[0], plan.components[0]],
  }), /version plan does not match the current manifest for Agentweaver\.Zeta/);
});

test('requires full commit SHAs and a v1 push or manual dispatch before resolving the comparison base', () => {
  const root = path.resolve('.');
  for (const shas of [{ testedSourceSha: 'abc', baseSha }, { testedSourceSha: sourceSha, baseSha: undefined }]) {
    assert.throws(() => resolveVersionApplicationSourceSha(root, { eventName: 'push', ...shas }),
      /release source: expected full tested-source and comparison-base commit SHAs/);
  }
  for (const eventName of ['pull_request', 'schedule', undefined]) {
    assert.throws(() => resolveVersionApplicationSourceSha(root, { eventName, testedSourceSha: sourceSha, baseSha }),
      /release source: publication requires a v1 push or workflow_dispatch event/);
  }
});

/** Creates a two-commit linear git repository and returns its root and commit SHAs. */
function linearRepository(t) {
  const root = fixtureRoot(t);
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  git('init', '-q');
  git('config', 'user.email', 'release-tests@example.invalid');
  git('config', 'user.name', 'Release tests');
  git('config', 'commit.gpgsign', 'false');
  writeFileSync(path.join(root, 'base.txt'), 'base\n');
  git('add', '.');
  git('commit', '-qm', 'base');
  const base = git('rev-parse', 'HEAD');
  writeFileSync(path.join(root, 'head.txt'), 'head\n');
  git('add', '.');
  git('commit', '-qm', 'head');
  return { root, base, head: git('rev-parse', 'HEAD') };
}

test('resolves the comparison base for v1 push and manual dispatch from the tested checkout', (t) => {
  const { root, base, head } = linearRepository(t);
  for (const eventName of ['push', 'workflow_dispatch']) {
    assert.equal(resolveVersionApplicationSourceSha(root, { eventName, testedSourceSha: head, baseSha: base }), base);
  }
  assert.throws(() => resolveVersionApplicationSourceSha(root, { eventName: 'push', testedSourceSha: base, baseSha: base }),
    /checkout HEAD does not match the integrated tested source SHA/);
});

test('rejects unsafe receipt identities, manifest paths, and unreadable or mismatched receipts', (t) => {
  const root = fixtureRoot(t);
  assert.throws(() => readPublicationPackageComponents(root, 'HEAD'), /release receipt: expected a full source commit SHA/);
  for (const manifestPath of ['../foundation.json', 'releases/../foundation.json', 'other/foundation.json', 'releases/Foundation.json']) {
    assert.throws(() => readPublicationPackageComponents(root, sourceSha, manifestPath),
      /manifest: expected a repository-relative release composition path/);
  }

  assert.throws(() => readPublicationPackageComponents(root, sourceSha),
    /cannot read a valid version-application receipt/);
  const receiptPath = path.join(root, 'releases', 'receipts', `${sourceSha}.json`);
  writeFileSync(receiptPath, '{ not json');
  assert.throws(() => readPublicationPackageComponents(root, sourceSha),
    /cannot read a valid version-application receipt/);

  writeFileSync(receiptPath, JSON.stringify({ sourceSha: baseSha, manifestPath: 'releases/foundation.json' }));
  assert.throws(() => readPublicationPackageComponents(root, sourceSha),
    /receipt source and manifest path must match the exact release comparison/);
  writeFileSync(receiptPath, JSON.stringify({ sourceSha, manifestPath: 'releases/other.json' }));
  assert.throws(() => readPublicationPackageComponents(root, sourceSha),
    /receipt source and manifest path must match the exact release comparison/);
});
