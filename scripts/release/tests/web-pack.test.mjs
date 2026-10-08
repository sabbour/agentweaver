import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { cleanBuildEnvironment, packComponents } from '../pack.mjs';

const baseImage = `registry.example/web-static@sha256:${'a'.repeat(64)}`;
const imageId = `sha256:${'b'.repeat(64)}`;
const manifest = () => ({
  schemaVersion: 1,
  stage: 'draft',
  components: [{
    id: 'Agentweaver.Web',
    kind: 'service',
    version: '0.1.0',
    project: 'apps/web/package.json',
  }],
  compatibility: [],
});

function git(dir, ...args) {
  return execFileSync('git', args, { cwd: dir, encoding: 'utf8' }).trim();
}

function initRepo(t, { dockerfile = null, ignoreDist = true } = {}) {
  const fixtureRoot = path.resolve('artifacts', 'release-tests');
  mkdirSync(fixtureRoot, { recursive: true });
  const root = mkdtempSync(path.join(fixtureRoot, 'v1-web-pack-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const web = path.join(root, 'apps', 'web');
  mkdirSync(path.join(web, 'src'), { recursive: true });
  writeFileSync(path.join(root, '.gitignore'),
    `artifacts/\napps/web/node_modules/\n${ignoreDist ? 'apps/web/dist/\n' : ''}.env.local\n.env.*.local\n`);
  writeFileSync(path.join(web, 'package.json'), JSON.stringify({
    name: 'web',
    private: true,
    version: '0.1.0',
    scripts: { build: 'vite build' },
  }, null, 2) + '\n');
  writeFileSync(path.join(web, 'package-lock.json'), JSON.stringify({
    name: 'web',
    version: '0.1.0',
    lockfileVersion: 3,
    packages: {
      '': { name: 'web', version: '0.1.0', dependencies: { vite: '8.0.0' } },
      'node_modules/vite': { version: '8.0.0', resolved: 'https://registry.invalid/vite.tgz', integrity: 'sha512-pinned' },
    },
  }, null, 2) + '\n');
  writeFileSync(path.join(web, 'src', 'main.ts'), 'export const app = "web";\n');
  writeFileSync(path.join(web, 'Dockerfile'), dockerfile ?? [
    `FROM ${baseImage} AS runtime`,
    'ARG IMAGE_TAG',
    'ARG GIT_SHA',
    'LABEL org.opencontainers.image.version="${IMAGE_TAG}"',
    'LABEL org.opencontainers.image.revision="${GIT_SHA}"',
    'COPY dist /srv/web',
    '',
  ].join('\n'));
  git(root, 'init', '-q');
  git(root, 'config', 'core.autocrlf', 'false');
  git(root, 'add', '.');
  git(root, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial web source');
  return root;
}

function runners(root, { inspectMismatch = false, calls = [], environmentCheck = () => {} } = {}) {
  const sourceSha = git(root, 'rev-parse', 'HEAD');
  return {
    calls,
    npm(args, cwd, environment) {
      calls.push(['npm', ...args]);
      environmentCheck(environment);
      if (args[0] === 'run') {
        mkdirSync(path.join(cwd, 'dist'), { recursive: true });
        writeFileSync(path.join(cwd, 'dist', 'index.html'), '<html>web</html>');
        writeFileSync(path.join(cwd, 'dist', 'assets.js'), 'source-bound build output');
      }
    },
    docker(args, cwd) {
      calls.push(['docker', ...args]);
      if (args[0] === 'image' && args[1] === 'inspect' && args.at(-1).includes('@sha256:')) {
        const digestRef = inspectMismatch
          ? `${baseImage.slice(0, baseImage.lastIndexOf('@'))}@sha256:${'c'.repeat(64)}`
          : baseImage;
        return JSON.stringify({
          Id: `sha256:${'d'.repeat(64)}`,
          RepoDigests: [digestRef],
        });
      }
      if (args[0] === 'image' && args[1] === 'inspect') {
        return JSON.stringify({
          Id: imageId,
          Os: 'linux',
          Architecture: 'amd64',
          Config: { Labels: {
            'org.opencontainers.image.revision': sourceSha,
            'org.opencontainers.image.version': '0.1.0',
          } },
        });
      }
      if (args[0] === 'save') {
        writeFileSync(args[args.indexOf('--output') + 1], 'local saved image archive');
      }
      return '';
    },
  };
}

test('locked web build creates a pinned local image archive and source-bound provenance', (t) => {
  const root = initRepo(t);
  const outDir = path.join(root, 'artifacts', 'release', 'web');
  const seenEnvironment = [];
  const runnersForPack = runners(root, {
    environmentCheck(environment) {
      seenEnvironment.push(environment);
      assert.equal(environment.VITE_API_URL, undefined);
      assert.equal(environment.vite_secret, undefined);
    },
  });
  const provenance = packComponents(manifest(), {
    root,
    outDir,
    ...runnersForPack,
    environment: { PATH: process.env.PATH, VITE_API_URL: 'https://untrusted.invalid', vite_secret: 'leak' },
  });
  assert.deepEqual(runnersForPack.calls.filter(([tool]) => tool === 'npm').map((call) => call.slice(1)), [
    ['ci', '--offline', '--no-audit', '--no-fund', '--no-progress'],
    ['run', 'build'],
  ]);
  const dockerBuild = runnersForPack.calls.find((call) => call[0] === 'docker' && call[1] === 'build');
  assert.ok(dockerBuild.includes('--pull=false'));
  assert.ok(dockerBuild.includes('--network=none'));
  assert.ok(dockerBuild.includes('--platform=linux/amd64'));
  assert.ok(!dockerBuild.some((argument) => argument.startsWith('--build-arg') && argument.includes('BASE')));
  assert.equal(seenEnvironment.length, 2);
  assert.equal(existsSync(path.join(root, 'apps', 'web', 'dist')), false);

  const component = provenance.components[0];
  assert.equal(component.lock.path, 'apps/web/package-lock.json');
  assert.match(component.lock.sha256, /^[a-f0-9]{64}$/);
  assert.equal(component.source.packageLockSha256, component.lock.sha256);
  assert.match(component.source.treeSha256, /^[a-f0-9]{64}$/);
  assert.equal(component.build.imageId, imageId);
  assert.equal(component.build.baseImages[0].reference, baseImage);
  assert.equal(component.build.baseImages[0].imageId, `sha256:${'d'.repeat(64)}`);
  assert.equal(provenance.artifacts.length, 1);
  const artifact = provenance.artifacts[0];
  assert.equal(artifact.kind, 'image');
  assert.equal(artifact.repository, 'agentweaver-web');
  assert.match(artifact.tag, /^0\.1\.0-[a-f0-9]{40}$/);
  assert.equal(artifact.imageId, imageId);
  assert.equal(artifact.buildOutput.files.length, 2);
  assert.equal(artifact.sha256, createHash('sha256')
    .update(readFileSync(path.join(outDir, artifact.path))).digest('hex'));
});

test('clean Vite builds exclude all ambient VITE-prefixed variables', () => {
  assert.deepEqual(cleanBuildEnvironment({
    PATH: 'path',
    VITE_API_URL: 'https://untrusted.invalid',
    vite_secret: 'untrusted',
    NODE_ENV: 'test',
  }), { PATH: 'path', NODE_ENV: 'test' });
});

test('rejects ignored local Vite env files and pre-existing dist output before invoking npm', (t) => {
  const root = initRepo(t);
  const calls = [];
  const run = runners(root, { calls });
  writeFileSync(path.join(root, 'apps', 'web', '.env.staging.local'), 'VITE_API_URL=https://local.invalid\n');
  assert.throws(() => packComponents(manifest(), {
    root, outDir: path.join(root, 'artifacts', 'release', 'env'), ...run,
  }), /local Vite environment files/);
  assert.equal(calls.length, 0);

  rmSync(path.join(root, 'apps', 'web', '.env.staging.local'));
  mkdirSync(path.join(root, 'apps', 'web', 'dist'));
  writeFileSync(path.join(root, 'apps', 'web', 'dist', 'index.html'), 'stale');
  assert.throws(() => packComponents(manifest(), {
    root, outDir: path.join(root, 'artifacts', 'release', 'stale'), ...run,
  }), /stale or prebuilt dist output/);
  assert.equal(calls.length, 0);
});

test('rejects mutable Dockerfile FROM references and locally mismatched pinned digests', (t) => {
  const tagRoot = initRepo(t, { dockerfile: 'FROM registry.example/web-static:latest\n' });
  const tagCalls = [];
  assert.throws(() => packComponents(manifest(), {
    root: tagRoot,
    outDir: path.join(tagRoot, 'artifacts', 'release', 'tag'),
    ...runners(tagRoot, { calls: tagCalls }),
  }), /pinned by an explicit immutable digest/);
  assert.equal(tagCalls.some(([tool]) => tool === 'npm'), false);

  const mismatchRoot = initRepo(t);
  const mismatchCalls = [];
  assert.throws(() => packComponents(manifest(), {
    root: mismatchRoot,
    outDir: path.join(mismatchRoot, 'artifacts', 'release', 'mismatch'),
    ...runners(mismatchRoot, { inspectMismatch: true, calls: mismatchCalls }),
  }), /does not have the required immutable RepoDigest/);
  assert.equal(mismatchCalls.some((call) => call[0] === 'docker' && call[1] === 'build'), false);
});

test('requires the npm lockfile in source HEAD and rejects the retired host Dockerfile input', (t) => {
  const uncommittedLockRoot = initRepo(t);
  writeFileSync(path.join(uncommittedLockRoot, '.gitignore'), 'artifacts/\napps/web/node_modules/\napps/web/dist/\napps/web/package-lock.json\n');
  git(uncommittedLockRoot, 'rm', '--cached', 'apps/web/package-lock.json');
  git(uncommittedLockRoot, 'add', '.gitignore');
  git(uncommittedLockRoot, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'ignore npm lockfile');
  assert.throws(() => packComponents(manifest(), {
    root: uncommittedLockRoot,
    outDir: path.join(uncommittedLockRoot, 'artifacts', 'release', 'missing-lock'),
    ...runners(uncommittedLockRoot),
  }), /package-lock\.json.*must be committed in source HEAD/);

  const retiredHostRoot = initRepo(t, {
    dockerfile: `FROM ${baseImage} AS runtime\nCOPY apps/Agentweaver.Web/ .\n`,
  });
  assert.throws(() => packComponents(manifest(), {
    root: retiredHostRoot,
    outDir: path.join(retiredHostRoot, 'artifacts', 'release', 'retired-host'),
    ...runners(retiredHostRoot),
  }), /must not depend on the retired apps\/Agentweaver\.Web host/);
});

test('rejects untracked dist output instead of accepting it as build input', (t) => {
  const root = initRepo(t, { ignoreDist: false });
  assert.throws(() => packComponents(manifest(), {
    root,
    outDir: path.join(root, 'artifacts', 'release', 'untracked'),
    ...runners(root),
  }), /tracked or untracked output/);
  assert.equal(existsSync(path.join(root, 'apps', 'web', 'dist')), false);
});
