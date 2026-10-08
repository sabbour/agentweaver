import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { validateManifest } from '../validate.mjs';

const fixtureRoot = path.resolve('artifacts', 'release-tests');
const component = {
  id: 'Agentweaver.Web',
  kind: 'service',
  version: '0.1.0',
  project: 'apps/web/package.json',
};
const manifest = () => ({
  schemaVersion: 1,
  stage: 'draft',
  components: [structuredClone(component)],
  compatibility: [],
});
const packageJson = (version = '0.1.0') => JSON.stringify({ name: 'web', version, dependencies: { react: '^19.0.0' } }, null, 2);
const packageLock = (version = '0.1.0', dependency = '19.0.0') => JSON.stringify({
  name: 'web',
  version,
  lockfileVersion: 3,
  packages: {
    '': { name: 'web', version, dependencies: { react: dependency } },
    'node_modules/react': { version: dependency },
  },
}, null, 2);

function withFiles(t, files, check = manifest()) {
  mkdirSync(fixtureRoot, { recursive: true });
  const root = mkdtempSync(path.join(fixtureRoot, 'v1-web-validator-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return validateManifest(check, {
    root,
    readProject(file) {
      const relative = path.relative(root, file).replaceAll('\\', '/');
      if (!Object.hasOwn(files, relative)) throw Object.assign(new Error('missing fixture file'), { code: 'ENOENT' });
      return files[relative];
    },
  });
}

test('accepts the exact web service package and lockfile version mirrors', (t) => {
  const result = withFiles(t, {
    'apps/web/package.json': packageJson(),
    'apps/web/package-lock.json': packageLock(),
  });
  assert.equal(result.components[0].project, 'apps/web/package.json');
});

test('rejects an npm package version or root lockfile version drift', (t) => {
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson('0.2.0'),
    'apps/web/package-lock.json': packageLock(),
  }), /package\.json must declare version 0\.1\.0/);
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson(),
    'apps/web/package-lock.json': packageLock('0.2.0'),
  }), /packages\[""\]\.version must match 0\.1\.0/);
});

test('requires a committed-shape npm lockfile with a root package record', (t) => {
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson(),
  }), /cannot read checked-in project: missing fixture file/);
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson(),
    'apps/web/package-lock.json': JSON.stringify({ lockfileVersion: 3, packages: {} }),
  }), /must contain a packages\[""\] object/);
});

test('only Agentweaver.Web may use the package mirror and it must remain a service', (t) => {
  const wrongId = manifest();
  wrongId.components[0].id = 'Agentweaver.Other';
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson(),
    'apps/web/package-lock.json': packageLock(),
  }, wrongId), /only Agentweaver.Web may use apps\/web\/package\.json/);
  const wrongProject = manifest();
  wrongProject.components[0].project = 'services/web/Web.csproj';
  assert.throws(() => withFiles(t, {
    'apps/web/package.json': packageJson(),
    'apps/web/package-lock.json': packageLock(),
  }, wrongProject), /Agentweaver\.Web must be a service mirrored by apps\/web\/package\.json/);
});
