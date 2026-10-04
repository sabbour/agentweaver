import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  ADMITTED_BRANCH,
  PUBLISHED_REPOSITORY,
  ROOT_BASE,
  V1_BASE,
  V1_WORKFLOW_PATH,
  composePagesDist,
  resolveV1DocsSource,
  validateV1Metadata,
} from './pages-publisher.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.join(HERE, '..', '..');
const SOURCE_SHA = 'a'.repeat(40);
const WORKFLOW_NAME = 'Agentweaver v1 Docs CI';
const WORKFLOW_ID = 42;
const RUN_ID = 1001;
const RUN_ATTEMPT = 2;
const ARTIFACT_ID = 9001;
const ARTIFACT_NAME = `v1-docs-${SOURCE_SHA}-${RUN_ATTEMPT}`;

function createRun(overrides = {}) {
  return {
    id: RUN_ID,
    run_number: 12,
    run_attempt: RUN_ATTEMPT,
    workflow_id: WORKFLOW_ID,
    name: WORKFLOW_NAME,
    event: 'push',
    status: 'completed',
    conclusion: 'success',
    head_branch: ADMITTED_BRANCH,
    head_sha: SOURCE_SHA,
    head_repository: { full_name: PUBLISHED_REPOSITORY },
    html_url: `https://github.com/${PUBLISHED_REPOSITORY}/actions/runs/${RUN_ID}`,
    ...overrides,
  };
}

function createArtifact(overrides = {}) {
  return {
    id: ARTIFACT_ID,
    name: ARTIFACT_NAME,
    expired: false,
    size_in_bytes: 1024,
    workflow_run: {
      id: RUN_ID,
      head_sha: SOURCE_SHA,
      head_branch: ADMITTED_BRANCH,
    },
    ...overrides,
  };
}

function createMetadata(overrides = {}) {
  return {
    schemaVersion: 1,
    repository: PUBLISHED_REPOSITORY,
    branch: ADMITTED_BRANCH,
    eventName: 'push',
    workflowName: WORKFLOW_NAME,
    workflowPath: V1_WORKFLOW_PATH,
    runId: RUN_ID,
    runAttempt: RUN_ATTEMPT,
    sourceSha: SOURCE_SHA,
    artifactName: ARTIFACT_NAME,
    ...overrides,
  };
}

function createApiRequest({
  runs = [createRun()],
  artifacts = [createArtifact()],
  liveRun = createRun(),
  currentSha = SOURCE_SHA,
  workflows = {},
  filenameUnavailable = false,
} = {}) {
  const requests = [];
  const request = async (endpoint) => {
    requests.push(endpoint);
    if (endpoint.endsWith('/actions/workflows/v1-docs-ci.yml')) {
      if (filenameUnavailable) {
        throw Object.assign(new Error('Not Found'), { status: 404 });
      }
      return workflows[WORKFLOW_ID] ?? {
        id: WORKFLOW_ID,
        name: WORKFLOW_NAME,
        path: V1_WORKFLOW_PATH,
        state: 'active',
      };
    }
    const workflowIdMatch = endpoint.match(/\/actions\/workflows\/(\d+)$/);
    if (workflowIdMatch) {
      const workflowId = Number(workflowIdMatch[1]);
      return workflows[workflowId] ?? {
        id: workflowId,
        name: WORKFLOW_NAME,
        path: workflowId === WORKFLOW_ID ? V1_WORKFLOW_PATH : '.github/workflows/other.yml',
        state: 'active',
      };
    }
    if (endpoint.includes(`/actions/workflows/${WORKFLOW_ID}/runs?`)) {
      return { workflow_runs: runs };
    }
    if (endpoint.includes('/actions/runs?')) {
      return { workflow_runs: runs };
    }
    if (endpoint.endsWith(`/actions/runs/${RUN_ID}`)) {
      return liveRun;
    }
    if (endpoint.endsWith('/git/ref/heads/v1')) {
      return { object: { sha: currentSha } };
    }
    if (endpoint.endsWith(`/actions/runs/${RUN_ID}/artifacts?per_page=100`)) {
      return { artifacts };
    }
    if (endpoint.endsWith(`/actions/runs/${RUN_ID + 1}/artifacts?per_page=100`)) {
      return {
        artifacts,
      };
    }
    throw new Error(`Unexpected API endpoint: ${endpoint}`);
  };
  return { request, requests };
}

async function resolveSource(options = {}) {
  const { request, requests } = createApiRequest(options);
  const source = await resolveV1DocsSource({
    eventName: 'push',
    eventPayload: { repository: { full_name: PUBLISHED_REPOSITORY } },
    repository: PUBLISHED_REPOSITORY,
    request,
  });
  return { source, requests };
}

test('selects the successful current v1 push run and binds its attempt-specific artifact', async () => {
  const invalidRuns = [
    createRun({ id: 2000, event: 'pull_request', run_number: 99 }),
    createRun({ id: 2003, head_repository: { full_name: 'contributor/agentweaver' }, run_number: 96 }),
    createRun({ id: 2004, head_sha: '0'.repeat(40), run_number: 95 }),
    createRun({ id: 2005, workflow_id: WORKFLOW_ID + 1, run_number: 94 }),
  ];
  const { source, requests } = await resolveSource({
    runs: [...invalidRuns, createRun()],
  });

  test('resolves the exact workflow ID and path when filename lookup is absent from the default branch', async () => {
    const { source, requests } = await resolveSource({
      filenameUnavailable: true,
      runs: [
        createRun({ id: RUN_ID + 1, workflow_id: WORKFLOW_ID + 1, run_number: 13 }),
        createRun(),
      ],
    });
    assert.equal(source.publish, true);
    assert.equal(source.workflowId, WORKFLOW_ID);
    assert.equal(source.sourceSha, SOURCE_SHA);
    assert.ok(requests.some((endpoint) => endpoint.endsWith('/actions/runs?branch=v1&event=push&head_sha=' + SOURCE_SHA + '&per_page=100')));
    assert.ok(requests.some((endpoint) => endpoint.endsWith(`/actions/workflows/${WORKFLOW_ID}`)));
  });

  assert.equal(source.repository, PUBLISHED_REPOSITORY);
  assert.equal(source.publish, true);
  assert.equal(source.branch, ADMITTED_BRANCH);
  assert.equal(source.eventName, 'push');
  assert.equal(source.workflowId, WORKFLOW_ID);
  assert.equal(source.workflowName, WORKFLOW_NAME);
  assert.equal(source.runId, RUN_ID);
  assert.equal(source.runAttempt, RUN_ATTEMPT);
  assert.equal(source.sourceSha, SOURCE_SHA);
  assert.equal(source.artifactId, ARTIFACT_ID);
  assert.equal(source.artifactName, ARTIFACT_NAME);
  assert.ok(requests.some((endpoint) => endpoint.endsWith('/actions/workflows/v1-docs-ci.yml')));
  assert.ok(requests.some((endpoint) => endpoint.includes(`/actions/workflows/${WORKFLOW_ID}/runs?`)));
});

test('rejects repositories and workflow definitions that do not match the publisher contract', async () => {
  const { request } = createApiRequest({
    workflows: {
      [WORKFLOW_ID]: {
        id: WORKFLOW_ID,
        name: 'Unexpected workflow',
        path: '.github/workflows/other.yml',
        state: 'active',
      },
    },
  });
  await assert.rejects(
    resolveV1DocsSource({
      eventName: 'push',
      eventPayload: { repository: { full_name: 'fork/agentweaver' } },
      repository: 'fork/agentweaver',
      request,
    }),
    /only accepts events from sabbour\/agentweaver/,
  );

  await assert.rejects(
    resolveV1DocsSource({
      eventName: 'push',
      eventPayload: { repository: { full_name: PUBLISHED_REPOSITORY } },
      repository: PUBLISHED_REPOSITORY,
      request,
    }),
    /must match the exact filename/,
  );
});

test('a workflow_run event must be a successful admitted push for the current v1 head', async () => {
  const { request, requests } = createApiRequest();
  const payload = {
    repository: { full_name: PUBLISHED_REPOSITORY },
    workflow_run: createRun(),
  };
  const source = await resolveV1DocsSource({
    eventName: 'workflow_run',
    eventPayload: payload,
    repository: PUBLISHED_REPOSITORY,
    request,
  });
  assert.equal(source.runId, RUN_ID);

  for (const invalid of [
    createRun({ event: 'pull_request' }),
    createRun({ conclusion: 'failure' }),
    createRun({ conclusion: 'cancelled' }),
    createRun({ head_branch: 'feature/docs' }),
    createRun({ head_repository: { full_name: 'contributor/agentweaver' } }),
  ]) {
    await assert.rejects(
      resolveV1DocsSource({
        eventName: 'workflow_run',
        eventPayload: { ...payload, workflow_run: invalid },
        repository: PUBLISHED_REPOSITORY,
        request,
      }),
    );
  }

  const artifactRequestCount = requests.filter((endpoint) => endpoint.includes('/artifacts?')).length;
  const unrelatedRun = await resolveV1DocsSource({
    eventName: 'workflow_run',
    eventPayload: { ...payload, workflow_run: createRun({ workflow_id: WORKFLOW_ID + 1 }) },
    repository: PUBLISHED_REPOSITORY,
    request,
  });
  assert.deepEqual(unrelatedRun, { publish: false });
  assert.equal(
    requests.filter((endpoint) => endpoint.includes('/artifacts?')).length,
    artifactRequestCount,
  );
});

test('workflow_run uses the event workflow ID and verifies its exact path when filename lookup returns 404', async () => {
  const { request, requests } = createApiRequest({ filenameUnavailable: true });
  const source = await resolveV1DocsSource({
    eventName: 'workflow_run',
    eventPayload: {
      repository: { full_name: PUBLISHED_REPOSITORY },
      workflow_run: createRun(),
    },
    repository: PUBLISHED_REPOSITORY,
    request,
  });
  assert.equal(source.publish, true);
  assert.ok(requests.some((endpoint) => endpoint.endsWith(`/actions/workflows/${WORKFLOW_ID}`)));
});

test('rejects a queued workflow_run event after its run attempt changes', async () => {
  const { request, requests } = createApiRequest({
    liveRun: createRun({ run_attempt: RUN_ATTEMPT + 1, conclusion: 'failure' }),
  });
  await assert.rejects(
    resolveV1DocsSource({
      eventName: 'workflow_run',
      eventPayload: {
        repository: { full_name: PUBLISHED_REPOSITORY },
        workflow_run: createRun(),
      },
      repository: PUBLISHED_REPOSITORY,
      request,
    }),
    /completed successfully/,
  );
  assert.equal(requests.some((endpoint) => endpoint.includes('/artifacts?')), false);
});

test('does not fall back to an older success after a failed or canceled run for the same source SHA', async () => {
  for (const conclusion of ['failure', 'cancelled']) {
    const { request } = createApiRequest({
      runs: [
        createRun({ id: RUN_ID + 1, run_number: 13, conclusion }),
        createRun({ run_number: 12 }),
      ],
    });
    await assert.rejects(
      resolveV1DocsSource({
        eventName: 'push',
        eventPayload: { repository: { full_name: PUBLISHED_REPOSITORY } },
        repository: PUBLISHED_REPOSITORY,
        request,
      }),
      /completed successfully/,
    );
  }
});

test('does not fall back to a previous successful artifact when the v1 branch head changed', async () => {
  const { request, requests } = createApiRequest({ currentSha: '0'.repeat(40) });
  await assert.rejects(
    resolveV1DocsSource({
      eventName: 'push',
      eventPayload: { repository: { full_name: PUBLISHED_REPOSITORY } },
      repository: PUBLISHED_REPOSITORY,
      request,
    }),
    /No successful v1 docs push run exists for current v1 commit/,
  );
  assert.equal(requests.some((endpoint) => endpoint.includes('/artifacts?')), false);
});

test('fails when the current run has no artifact, an expired artifact, or mismatched artifact provenance', async () => {
  for (const artifacts of [
    [],
    [createArtifact({ expired: true })],
    [createArtifact({ workflow_run: { id: RUN_ID + 1, head_sha: SOURCE_SHA, head_branch: ADMITTED_BRANCH } })],
    [createArtifact({ name: 'v1-docs-old-attempt' })],
    [createArtifact(), createArtifact({ id: ARTIFACT_ID + 1 })],
  ]) {
    await assert.rejects(resolveSource({ artifacts }), /v1 docs artifact/);
  }
});

test('pins metadata to the repository, push branch, workflow, run, attempt, SHA, and artifact name', () => {
  const expected = {
    repository: PUBLISHED_REPOSITORY,
    branch: ADMITTED_BRANCH,
    workflowName: WORKFLOW_NAME,
    workflowPath: V1_WORKFLOW_PATH,
    runId: RUN_ID,
    runAttempt: RUN_ATTEMPT,
    sourceSha: SOURCE_SHA,
    artifactName: ARTIFACT_NAME,
  };
  assert.deepEqual(validateV1Metadata(createMetadata(), expected), createMetadata());

  for (const override of [
    { repository: 'fork/agentweaver' },
    { branch: 'feature/docs' },
    { eventName: 'pull_request' },
    { workflowName: 'Other CI' },
    { workflowPath: '.github/workflows/other.yml' },
    { runId: RUN_ID + 1 },
    { runAttempt: RUN_ATTEMPT + 1 },
    { sourceSha: '0'.repeat(40) },
    { artifactName: `../${ARTIFACT_NAME}` },
    { distPath: '../../outside' },
  ]) {
    assert.throws(
      () => validateV1Metadata(createMetadata(override), expected),
    );
  }
});

test('composes only v1 dist and preserves the 0.x root, base assets, and CNAME', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-pages-'));
  try {
    const rootDist = path.join(temp, 'root');
    const artifactDir = path.join(temp, 'artifact');
    const rootAssets = path.join(rootDist, 'assets');
    const v1Dist = path.join(artifactDir, 'dist');
    const v1Assets = path.join(v1Dist, 'assets');
    await Promise.all([
      mkdir(rootAssets, { recursive: true }),
      mkdir(v1Assets, { recursive: true }),
    ]);
    const rootIndex = '<link href="/agentweaver/assets/root.css"><a href="https://sabbour.me/agentweaver/v1/">v1 docs</a>';
    const v1Index = '<link href="/agentweaver/v1/assets/v1.css">';
    await Promise.all([
      writeFile(path.join(rootDist, 'index.html'), rootIndex),
      writeFile(path.join(rootDist, 'CNAME'), 'sabbour.me\n'),
      writeFile(path.join(rootAssets, 'root.css'), 'root site'),
      writeFile(path.join(artifactDir, 'metadata.json'), JSON.stringify(createMetadata())),
      writeFile(path.join(v1Dist, 'index.html'), v1Index),
      writeFile(path.join(v1Assets, 'v1.css'), 'v1 site'),
      writeFile(path.join(v1Dist, 'guide.html'), 'v1 guide'),
    ]);

    await composePagesDist({ rootDist, artifactDir, source: createMetadata() });

    assert.equal(await readFile(path.join(rootDist, 'index.html'), 'utf8'), rootIndex);
    assert.equal(await readFile(path.join(rootDist, 'CNAME'), 'utf8'), 'sabbour.me\n');
    assert.equal(await readFile(path.join(rootDist, 'assets', 'root.css'), 'utf8'), 'root site');
    assert.equal(await readFile(path.join(rootDist, 'v1', 'index.html'), 'utf8'), v1Index);
    assert.equal(await readFile(path.join(rootDist, 'v1', 'assets', 'v1.css'), 'utf8'), 'v1 site');
    assert.equal(await readFile(path.join(rootDist, 'v1', 'guide.html'), 'utf8'), 'v1 guide');
    await assert.rejects(readFile(path.join(rootDist, 'metadata.json')));
    await assert.rejects(readFile(path.join(rootDist, 'v1', 'metadata.json')));
    await assert.rejects(readFile(path.join(rootDist, 'v1', 'assets', 'root.css')));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('rejects a v1 artifact with the wrong base or an asset link outside its dist', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-pages-base-'));
  try {
    const rootDist = path.join(temp, 'root');
    const artifactDir = path.join(temp, 'artifact');
    await mkdir(path.join(rootDist, 'assets'), { recursive: true });
    await mkdir(path.join(artifactDir, 'dist', 'assets'), { recursive: true });
    await Promise.all([
      writeFile(path.join(rootDist, 'index.html'), '<script src="/agentweaver/assets/root.js"></script>'),
      writeFile(path.join(rootDist, 'assets', 'root.js'), ''),
      writeFile(path.join(artifactDir, 'metadata.json'), JSON.stringify(createMetadata())),
      writeFile(path.join(artifactDir, 'dist', 'index.html'), '<script src="/agentweaver/v1/assets/v1.js"></script><script src="/agentweaver/assets/missing-v1.js"></script>'),
      writeFile(path.join(artifactDir, 'dist', 'assets', 'v1.js'), ''),
    ]);
    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /outside \/agentweaver\/v1\//,
    );

    await writeFile(path.join(artifactDir, 'dist', 'index.html'), '<script src="/agentweaver/v1/assets/../../outside.js"></script>');
    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /outside \/agentweaver\/v1\//,
    );

    await writeFile(path.join(artifactDir, 'dist', 'index.html'), '<script src="/agentweaver/v1/assets/missing.js"></script>');
    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /does not exist in the composed output/,
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('rejects source metadata copied inside public v1 dist', async () => {
  const temp = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-pages-metadata-'));
  try {
    const rootDist = path.join(temp, 'root');
    const artifactDir = path.join(temp, 'artifact');
    const v1Dist = path.join(artifactDir, 'dist');
    await Promise.all([
      mkdir(path.join(rootDist, 'assets'), { recursive: true }),
      mkdir(path.join(v1Dist, 'assets'), { recursive: true }),
    ]);
    await Promise.all([
      writeFile(path.join(rootDist, 'index.html'), '<script src="/agentweaver/assets/root.js"></script>'),
      writeFile(path.join(rootDist, 'assets', 'root.js'), ''),
      writeFile(path.join(artifactDir, 'metadata.json'), JSON.stringify(createMetadata())),
      writeFile(path.join(v1Dist, 'index.html'), '<script src="/agentweaver/v1/assets/v1.js"></script>'),
      writeFile(path.join(v1Dist, 'assets', 'v1.js'), ''),
      writeFile(path.join(v1Dist, 'metadata.json'), JSON.stringify(createMetadata())),
    ]);

    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /must remain outside the public dist directory/,
    );
    await assert.rejects(readFile(path.join(rootDist, 'v1', 'metadata.json')));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('rejects artifact symlinks before copying files into the public site', async (context) => {
  if (process.platform === 'win32') {
    context.skip('GitHub Pages builds run on Linux, where artifact symlink checks are exercised.');
    return;
  }
  const temp = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-pages-symlink-'));
  try {
    const rootDist = path.join(temp, 'root');
    const artifactDir = path.join(temp, 'artifact');
    const v1Dist = path.join(artifactDir, 'dist');
    await Promise.all([
      mkdir(path.join(rootDist, 'assets'), { recursive: true }),
      mkdir(path.join(v1Dist, 'assets'), { recursive: true }),
    ]);
    await Promise.all([
      writeFile(path.join(rootDist, 'index.html'), '<script src="/agentweaver/assets/root.js"></script>'),
      writeFile(path.join(rootDist, 'assets', 'root.js'), ''),
      writeFile(path.join(artifactDir, 'metadata.json'), JSON.stringify(createMetadata())),
      writeFile(path.join(v1Dist, 'index.html'), '<script src="/agentweaver/v1/assets/v1.js"></script>'),
      writeFile(path.join(v1Dist, 'assets', 'v1.js'), ''),
      writeFile(path.join(temp, 'outside.txt'), 'outside'),
    ]);
    await symlink(path.join(temp, 'outside.txt'), path.join(v1Dist, 'assets', 'escape.txt'));
    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /cannot contain symbolic links/,
    );
    await rm(path.join(artifactDir, 'metadata.json'));
    await symlink(path.join(temp, 'outside.txt'), path.join(artifactDir, 'metadata.json'));
    await assert.rejects(
      composePagesDist({ rootDist, artifactDir, source: createMetadata() }),
      /safe metadata\.json/,
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test('keeps the incumbent root base and provides a full-page v1 navigation link', async () => {
  const config = await readFile(path.join(REPO_ROOT, 'docs', '.vitepress', 'config.ts'), 'utf8');
  assert.match(config, /base:\s*['"]\/agentweaver\/['"]/);
  assert.match(
    config,
    /\{\s*text:\s*['"]v1 docs['"],\s*link:\s*['"]https:\/\/sabbour\.me\/agentweaver\/v1\/['"],\s*target:\s*['"]_self['"]\s*\}/,
  );
  assert.equal(ROOT_BASE, '/agentweaver/');
  assert.equal(V1_BASE, '/agentweaver/v1/');
});

test('keeps one Pages publisher and uploads only the composed root artifact', async () => {
  const workflow = await readFile(path.join(REPO_ROOT, '.github', 'workflows', 'deploy-docs.yml'), 'utf8');
  const pagesUploads = workflow.match(/uses:\s*actions\/upload-pages-artifact@/g) ?? [];
  const pagesDeployments = workflow.match(/uses:\s*actions\/deploy-pages@/g) ?? [];

  assert.equal(pagesUploads.length, 1);
  assert.equal(pagesDeployments.length, 1);
  assert.match(workflow, /group: pages\n\s+cancel-in-progress: false/);
  assert.match(
    workflow,
    /workflow_run:\n(?:\s+#.*\n)?\s+workflows: \['Agentweaver v1 Docs CI'\]\n\s+branches: \[v1\]\n\s+types: \[completed\]/,
  );
  assert.match(workflow, /actions: read/);
  assert.doesNotMatch(workflow, /pull-requests:\s*write/);
  assert.match(workflow, /path: docs\/\.vitepress\/dist/);
  assert.match(workflow, /artifact-ids: \$\{\{ steps\.v1_source\.outputs\.artifact_id \}\}/);
  assert.match(workflow, /run-id: \$\{\{ steps\.v1_source\.outputs\.run_id \}\}/);
  assert.match(workflow, /repository: sabbour\/agentweaver/);
  assert.match(workflow, /--artifact-dir "\$RUNNER_TEMP\/v1-docs"/);
  assert.doesNotMatch(workflow, /name: \$\{\{ steps\.v1_source\.outputs\.artifact_name \}\}/);
  assert.match(workflow, /if: needs\.build\.outputs\.publish == 'true'/);
  assert.ok(
    workflow.indexOf('run: node scripts/docs/pages-publisher.mjs resolve') <
      workflow.indexOf('name: Install docs dependencies'),
    'source selection must finish before the 0.x build starts',
  );
  const publisher = await readFile(path.join(REPO_ROOT, 'scripts', 'docs', 'pages-publisher.mjs'), 'utf8');
  assert.match(publisher, /actions\/workflows\/v1-docs-ci\.yml/);
});

test('runs the skipped area checks when a draft pull request becomes ready', async () => {
  const workflow = await readFile(path.join(REPO_ROOT, '.github', 'workflows', 'ci.yml'), 'utf8');
  assert.match(
    workflow,
    /pull_request:\n\s+types: \[opened, synchronize, reopened, ready_for_review\]/,
  );
});

test('documents the single-publisher and fail-before-deploy contract', async () => {
  const contributing = await readFile(path.join(REPO_ROOT, 'CONTRIBUTING.md'), 'utf8');
  assert.match(contributing, /\.github\/workflows\/deploy-docs\.yml` is the only GitHub Pages publisher/);
  assert.match(contributing, /If no matching artifact exists, the build stops before deployment\./);
});
