import { appendFile, cp, lstat, readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const PUBLISHED_REPOSITORY = 'sabbour/agentweaver';
export const ADMITTED_BRANCH = 'v1';
export const V1_WORKFLOW_NAME = 'Agentweaver v1 Docs CI';
export const V1_WORKFLOW_PATH = '.github/workflows/v1-docs-ci.yml';
export const V1_BASE = '/agentweaver/v1/';
export const ROOT_BASE = '/agentweaver/';
const PUBLISHED_ORIGIN = 'https://sabbour.me';

const METADATA_KEYS = [
  'schemaVersion',
  'repository',
  'branch',
  'event',
  'workflow',
  'workflowPath',
  'runId',
  'runAttempt',
  'sourceSha',
  'artifactName',
];

function fail(message) {
  throw new Error(message);
}

function isSha(value) {
  return typeof value === 'string' && /^[0-9a-f]{40}$/i.test(value);
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 1) {
    fail(`${label} must be a positive integer.`);
  }
}

function artifactNameFor(sourceSha, runAttempt) {
  if (!isSha(sourceSha)) {
    fail('The v1 docs source SHA is not a full git SHA.');
  }
  requirePositiveInteger(runAttempt, 'The v1 docs run attempt');
  return `v1-docs-${sourceSha}-${runAttempt}`;
}

function matchesWorkflow(workflow, workflowId = workflow?.id) {
  return Boolean(
    workflow &&
    Number.isSafeInteger(workflowId) &&
    workflow.id === workflowId &&
    workflow.path === V1_WORKFLOW_PATH &&
    workflow.name === V1_WORKFLOW_NAME &&
    workflow.state === 'active',
  );
}

function validateWorkflow(workflow, workflowId = workflow?.id) {
  if (!matchesWorkflow(workflow, workflowId)) {
    fail(`The active workflow must match the exact filename ${V1_WORKFLOW_PATH}.`);
  }
  return workflow;
}

function validateRun(run, { workflowId, expectedSha }) {
  if (!run || run.workflow_id !== workflowId) {
    fail('The v1 docs run does not belong to the expected workflow.');
  }
  if (run.event !== 'push' || run.head_branch !== ADMITTED_BRANCH) {
    fail('The v1 docs run must be a push to the admitted v1 branch.');
  }
  if (run.head_repository?.full_name !== PUBLISHED_REPOSITORY) {
    fail('The v1 docs run must come from the Agentweaver repository, not a fork.');
  }
  if (run.status !== 'completed' || run.conclusion !== 'success') {
    fail('The v1 docs run must have completed successfully.');
  }
  if (!isSha(run.head_sha) || run.head_sha.toLowerCase() !== expectedSha.toLowerCase()) {
    fail('The v1 docs run SHA does not match the current v1 branch head.');
  }
  requirePositiveInteger(run.id, 'The v1 docs run ID');
  requirePositiveInteger(run.run_attempt, 'The v1 docs run attempt');
  if (typeof run.html_url !== 'string' || !run.html_url.startsWith('https://')) {
    fail('The v1 docs run URL is missing or invalid.');
  }
  return run;
}

function assertRunAttemptMatchesEvent(eventRun, currentRun) {
  if (
    eventRun.id !== currentRun.id ||
    eventRun.workflow_id !== currentRun.workflow_id ||
    eventRun.run_attempt !== currentRun.run_attempt ||
    eventRun.event !== currentRun.event ||
    eventRun.head_branch !== currentRun.head_branch ||
    eventRun.head_sha.toLowerCase() !== currentRun.head_sha.toLowerCase() ||
    eventRun.head_repository?.full_name !== currentRun.head_repository?.full_name
  ) {
    fail('The v1 docs workflow_run event no longer matches the live run attempt.');
  }
}

function latestMatchingRun(runs, { workflowId, expectedSha }) {
  const candidates = runs.filter((run) =>
    run?.workflow_id === workflowId &&
    run.event === 'push' &&
    run.head_branch === ADMITTED_BRANCH &&
    run.head_repository?.full_name === PUBLISHED_REPOSITORY &&
    isSha(run.head_sha) &&
    run.head_sha.toLowerCase() === expectedSha.toLowerCase() &&
    Number.isSafeInteger(run.id),
  );
  candidates.sort((left, right) => {
    const runNumberDifference = (right.run_number ?? 0) - (left.run_number ?? 0);
    return runNumberDifference || right.id - left.id;
  });
  if (candidates.length === 0) {
    fail(`No successful v1 docs push run exists for current v1 commit ${expectedSha}.`);
  }
  return validateRun(candidates[0], {
    workflowId,
    expectedSha,
  });
}

async function workflowById(request, workflowId) {
  return validateWorkflow(
    await request(`/repos/${PUBLISHED_REPOSITORY}/actions/workflows/${workflowId}`),
  );
}

async function workflowByFilename(request) {
  try {
    return validateWorkflow(
      await request(`/repos/${PUBLISHED_REPOSITORY}/actions/workflows/v1-docs-ci.yml`),
    );
  } catch (error) {
    if (error.status === 404) {
      return null;
    }
    throw error;
  }
}

async function latestRunWithoutDefaultBranchWorkflow(runs, { expectedSha, request }) {
  const candidates = runs.filter((run) =>
    run?.name === V1_WORKFLOW_NAME &&
    run.event === 'push' &&
    run.head_branch === ADMITTED_BRANCH &&
    run.head_repository?.full_name === PUBLISHED_REPOSITORY &&
    isSha(run.head_sha) &&
    run.head_sha.toLowerCase() === expectedSha.toLowerCase() &&
    Number.isSafeInteger(run.workflow_id),
  );
  candidates.sort((left, right) => {
    const runNumberDifference = (right.run_number ?? 0) - (left.run_number ?? 0);
    return runNumberDifference || right.id - left.id;
  });

  for (const candidate of candidates) {
    const workflow = await request(
      `/repos/${PUBLISHED_REPOSITORY}/actions/workflows/${candidate.workflow_id}`,
    );
    if (!matchesWorkflow(workflow, candidate.workflow_id)) {
      continue;
    }
    const run = validateRun(candidate, {
      workflowId: candidate.workflow_id,
      expectedSha,
    });
    return { workflow, run };
  }
  fail(`No successful v1 docs push run exists for current v1 commit ${expectedSha}.`);
}

function validateArtifact(artifact, run, expectedName) {
  const linkedRun = artifact?.workflow_run;
  if (
    artifact?.name !== expectedName ||
    artifact.expired !== false ||
    !Number.isSafeInteger(artifact.id) ||
    artifact.id < 1 ||
    !Number.isSafeInteger(artifact.size_in_bytes) ||
    artifact.size_in_bytes <= 0 ||
    linkedRun?.id !== run.id ||
    !isSha(linkedRun?.head_sha) ||
    linkedRun.head_sha.toLowerCase() !== run.head_sha.toLowerCase() ||
    linkedRun?.head_branch !== ADMITTED_BRANCH
  ) {
    fail(`The v1 docs artifact ${expectedName} is missing, expired, or does not match its run.`);
  }
  return artifact;
}

async function getBranchHead(request) {
  const ref = await request(`/repos/${PUBLISHED_REPOSITORY}/git/ref/heads/${ADMITTED_BRANCH}`);
  const sha = ref?.object?.sha;
  if (!isSha(sha)) {
    fail('The admitted v1 branch does not have a valid head SHA.');
  }
  return sha;
}

export async function resolveV1DocsSource({
  eventName,
  eventPayload,
  repository,
  request,
}) {
  if (repository !== PUBLISHED_REPOSITORY || eventPayload?.repository?.full_name !== PUBLISHED_REPOSITORY) {
    fail(`The docs publisher only accepts events from ${PUBLISHED_REPOSITORY}.`);
  }

  const basePath = `/repos/${PUBLISHED_REPOSITORY}`;
  let workflow = await workflowByFilename(request);
  if (
    eventName === 'workflow_run' &&
    workflow &&
    eventPayload.workflow_run?.workflow_id !== workflow.id
  ) {
    return { publish: false };
  }

  const currentHeadSha = await getBranchHead(request);
  let run;

  if (eventName === 'workflow_run') {
    const eventRun = eventPayload.workflow_run;
    if (!workflow) {
      if (!Number.isSafeInteger(eventRun?.workflow_id)) {
        fail('The v1 docs workflow_run event has no workflow ID.');
      }
      workflow = await workflowById(request, eventRun.workflow_id);
    }
    validateRun(eventRun, {
      workflowId: workflow.id,
      expectedSha: currentHeadSha,
    });
    const currentRun = validateRun(
      await request(`${basePath}/actions/runs/${eventRun.id}`),
      {
        workflowId: workflow.id,
        expectedSha: currentHeadSha,
      },
    );
    assertRunAttemptMatchesEvent(eventRun, currentRun);
    run = currentRun;
  } else if (eventName === 'push' || eventName === 'workflow_dispatch') {
    const query = new URLSearchParams({
      branch: ADMITTED_BRANCH,
      event: 'push',
      head_sha: currentHeadSha,
      per_page: '100',
    });
    if (workflow) {
      const result = await request(
        `${basePath}/actions/workflows/${workflow.id}/runs?${query}`,
      );
      run = latestMatchingRun(result?.workflow_runs ?? [], {
        workflowId: workflow.id,
        expectedSha: currentHeadSha,
      });
    } else {
      const result = await request(`${basePath}/actions/runs?${query}`);
      const selected = await latestRunWithoutDefaultBranchWorkflow(
        result?.workflow_runs ?? [],
        {
          expectedSha: currentHeadSha,
          request,
        },
      );
      workflow = selected.workflow;
      run = selected.run;
    }
  } else {
    fail(`The docs publisher does not accept the ${eventName} event.`);
  }

  const expectedArtifactName = artifactNameFor(run.head_sha, run.run_attempt);
  const result = await request(
    `${basePath}/actions/runs/${run.id}/artifacts?per_page=100`,
  );
  const matches = (result?.artifacts ?? []).filter(
    (artifact) => artifact.name === expectedArtifactName,
  );
  if (matches.length !== 1) {
    fail(`Expected one unexpired v1 docs artifact named ${expectedArtifactName}.`);
  }
  const artifact = validateArtifact(matches[0], run, expectedArtifactName);

  return {
    publish: true,
    repository: PUBLISHED_REPOSITORY,
    branch: ADMITTED_BRANCH,
    eventName: 'push',
    workflowId: run.workflow_id,
    workflowName: workflow.name,
    workflowPath: V1_WORKFLOW_PATH,
    runId: run.id,
    runAttempt: run.run_attempt,
    sourceSha: run.head_sha,
    runUrl: run.html_url,
    artifactId: artifact.id,
    artifactName: artifact.name,
  };
}

export function validateV1Metadata(metadata, source) {
  if (!metadata || typeof metadata !== 'object' || Array.isArray(metadata)) {
    fail('The v1 docs artifact metadata must be a JSON object.');
  }
  const actualKeys = Object.keys(metadata).sort();
  const expectedKeys = [...METADATA_KEYS].sort();
  if (
    actualKeys.length !== expectedKeys.length ||
    actualKeys.some((key, index) => key !== expectedKeys[index])
  ) {
    fail('The v1 docs artifact metadata does not match schema version 1.');
  }

  const expected = {
    schemaVersion: 1,
    repository: source.repository,
    branch: source.branch,
    event: 'push',
    workflow: source.workflowName,
    workflowPath: source.workflowPath,
    runId: source.runId,
    runAttempt: source.runAttempt,
    sourceSha: source.sourceSha,
    artifactName: source.artifactName,
  };
  for (const key of METADATA_KEYS) {
    if (metadata[key] !== expected[key]) {
      fail(`The v1 docs artifact metadata field ${key} does not match the selected run.`);
    }
  }
  if (!isSha(metadata.sourceSha)) {
    fail('The v1 docs artifact metadata has an invalid source SHA.');
  }
  artifactNameFor(metadata.sourceSha, metadata.runAttempt);
  return metadata;
}

function isPathInside(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

async function statIfPresent(filePath, statFunction = stat) {
  try {
    return await statFunction(filePath);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
}

async function assertNoSymlinks(root) {
  const rootRealPath = path.resolve(root);
  const rootDetails = await statIfPresent(rootRealPath, lstat);
  if (!rootDetails?.isDirectory() || rootDetails.isSymbolicLink()) {
    fail('The v1 docs artifact does not contain a real dist directory.');
  }
  const visit = async (directory) => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const entryPath = path.resolve(directory, entry.name);
      if (!isPathInside(rootRealPath, entryPath)) {
        fail('The v1 docs artifact contains a path outside its dist directory.');
      }
      if (entry.name === 'metadata.json') {
        fail('The v1 source metadata.json must remain outside the public dist directory.');
      }
      const details = await lstat(entryPath);
      if (details.isSymbolicLink()) {
        fail('The v1 docs artifact cannot contain symbolic links.');
      }
      if (details.isDirectory()) {
        await visit(entryPath);
      }
    }
  };
  await visit(rootRealPath);
}

async function assertBaseAssets(siteRoot, html, base) {
  const localAssets = [];
  for (const match of html.matchAll(/\b(?:href|src)=["']([^"'#]+)["']/g)) {
    const reference = match[1];
    const url = new URL(reference, PUBLISHED_ORIGIN);
    if (url.origin !== PUBLISHED_ORIGIN) {
      continue;
    }
    const rawPath = reference.split(/[?#]/, 1)[0];
    const rawPathname = rawPath
      .replace(/^https?:\/\/[^/]+/i, '')
      .replace(/^\/\/[^/]+/, '');
    const decodedPath = decodeURIComponent(rawPathname);
    if (!decodedPath.includes('/assets/') && !decodedPath.startsWith('assets/')) {
      continue;
    }
    const relativeSegments = decodedPath.split('/');
    if (
      decodedPath.includes('\\') ||
      relativeSegments.some((segment) => segment === '.' || segment === '..')
    ) {
      fail(`The site contains an asset path outside ${base}.`);
    }
    if (!url.pathname.startsWith(base)) {
      fail(`The site contains an asset path outside ${base}.`);
    }
    const relativePath = decodeURIComponent(url.pathname.slice(base.length));
    const assetPath = path.resolve(siteRoot, ...relativePath.split('/'));
    if (!isPathInside(siteRoot, assetPath)) {
      fail(`The site contains an asset path outside ${base}.`);
    }
    localAssets.push({ assetPath, reference });
  }
  if (localAssets.length === 0) {
    fail(`The site index does not contain any ${base} asset links.`);
  }

  for (const { assetPath, reference } of localAssets) {
    const details = await statIfPresent(assetPath);
    if (!details?.isFile()) {
      fail(`The site asset ${reference} does not exist in the composed output.`);
    }
  }
}

function expectedSourceFromEnvironment(environment) {
  const source = {
    repository: PUBLISHED_REPOSITORY,
    branch: ADMITTED_BRANCH,
    workflowName: environment.V1_WORKFLOW_NAME,
    workflowPath: V1_WORKFLOW_PATH,
    runId: Number(environment.V1_RUN_ID),
    runAttempt: Number(environment.V1_RUN_ATTEMPT),
    sourceSha: environment.V1_SOURCE_SHA,
    artifactId: Number(environment.V1_ARTIFACT_ID),
    artifactName: environment.V1_ARTIFACT_NAME,
    workflowId: Number(environment.V1_WORKFLOW_ID),
    runUrl: environment.V1_RUN_URL,
  };
  requirePositiveInteger(source.runId, 'The selected v1 docs run ID');
  requirePositiveInteger(source.runAttempt, 'The selected v1 docs run attempt');
  requirePositiveInteger(source.artifactId, 'The selected v1 docs artifact ID');
  requirePositiveInteger(source.workflowId, 'The selected v1 docs workflow ID');
  if (
    typeof source.workflowName !== 'string' ||
    source.workflowName.length === 0 ||
    typeof source.runUrl !== 'string' ||
    !source.runUrl.startsWith('https://') ||
    !isSha(source.sourceSha) ||
    source.artifactName !== artifactNameFor(source.sourceSha, source.runAttempt)
  ) {
    fail('The selected v1 docs artifact identity is invalid.');
  }
  return source;
}

export async function composePagesDist({ rootDist, artifactDir, source, summaryPath }) {
  const resolvedRootDist = path.resolve(rootDist);
  let resolvedArtifactDir = path.resolve(artifactDir);
  const rootIndexPath = path.join(resolvedRootDist, 'index.html');
  const expectedArtifactName = artifactNameFor(source.sourceSha, source.runAttempt);
  if (source.artifactName !== expectedArtifactName) {
    fail('The selected v1 docs artifact identity is invalid.');
  }

  const artifactRootDetails = await statIfPresent(resolvedArtifactDir, lstat);
  if (!artifactRootDetails?.isDirectory() || artifactRootDetails.isSymbolicLink()) {
    fail('The v1 docs artifact directory is missing or unsafe.');
  }
  let metadataPath = path.join(resolvedArtifactDir, 'metadata.json');
  let metadataDetails = await statIfPresent(metadataPath, lstat);
  if (!metadataDetails) {
    resolvedArtifactDir = path.join(resolvedArtifactDir, expectedArtifactName);
    const namedArtifactDetails = await statIfPresent(resolvedArtifactDir, lstat);
    if (!namedArtifactDetails?.isDirectory() || namedArtifactDetails.isSymbolicLink()) {
      fail('The v1 docs artifact directory is missing or unsafe.');
    }
    metadataPath = path.join(resolvedArtifactDir, 'metadata.json');
    metadataDetails = await statIfPresent(metadataPath, lstat);
  }
  if (
    !metadataDetails?.isFile() ||
    metadataDetails.isSymbolicLink() ||
    metadataDetails.size > 16 * 1024
  ) {
    fail('The v1 docs artifact does not contain safe metadata.json.');
  }
  const v1Dist = path.join(resolvedArtifactDir, 'dist');
  const v1IndexPath = path.join(v1Dist, 'index.html');
  await assertNoSymlinks(v1Dist);

  const [rootDetails, v1Details] = await Promise.all([
    statIfPresent(rootIndexPath),
    statIfPresent(v1IndexPath),
  ]);
  if (!rootDetails?.isFile()) {
    fail('The 0.x docs build does not contain index.html.');
  }
  if (!v1Details?.isFile()) {
    fail('The v1 docs artifact does not contain dist/index.html.');
  }

  const metadata = JSON.parse(await readFile(metadataPath, 'utf8'));
  validateV1Metadata(metadata, source);

  const [rootHtml, v1Html] = await Promise.all([
    readFile(rootIndexPath, 'utf8'),
    readFile(v1IndexPath, 'utf8'),
  ]);
  await assertBaseAssets(resolvedRootDist, rootHtml, ROOT_BASE);
  await assertBaseAssets(v1Dist, v1Html, V1_BASE);

  const targetDir = path.join(resolvedRootDist, 'v1');
  const existingTarget = await statIfPresent(targetDir, lstat);
  if (existingTarget) {
    fail('The 0.x docs output already contains a v1 directory.');
  }
  await cp(v1Dist, targetDir, { recursive: true, errorOnExist: true, force: false });
  await assertBaseAssets(targetDir, v1Html, V1_BASE);

  if (summaryPath) {
    const summary = [
      '## Documentation publication source',
      '',
      `- v1 source commit: \`${source.sourceSha}\``,
      `- v1 workflow run: [${source.runId}, attempt ${source.runAttempt}](${source.runUrl})`,
      `- v1 artifact: \`${source.artifactName}\` (ID ${source.artifactId})`,
      '',
    ].join('\n');
    await appendFile(summaryPath, summary, 'utf8');
  }
}

async function githubRequest(apiUrl, token, endpoint) {
  const response = await fetch(new URL(endpoint, apiUrl), {
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'X-GitHub-Api-Version': '2022-11-28',
    },
  });
  if (!response.ok) {
    const error = new Error(
      `GitHub Actions API request failed with status ${response.status}: ${endpoint}`,
    );
    error.status = response.status;
    throw error;
  }
  return response.json();
}

async function resolveCommand() {
  const { GITHUB_API_URL, GITHUB_EVENT_NAME, GITHUB_EVENT_PATH, GITHUB_OUTPUT, GITHUB_REPOSITORY, PUBLISHER_GITHUB_TOKEN } = process.env;
  if (!GITHUB_API_URL || !GITHUB_EVENT_NAME || !GITHUB_EVENT_PATH || !GITHUB_OUTPUT || !PUBLISHER_GITHUB_TOKEN) {
    fail('The docs source resolver is missing required GitHub Actions environment values.');
  }
  const eventPayload = JSON.parse(await readFile(GITHUB_EVENT_PATH, 'utf8'));
  const request = (endpoint) => githubRequest(GITHUB_API_URL, PUBLISHER_GITHUB_TOKEN, endpoint);
  const source = await resolveV1DocsSource({
    eventName: GITHUB_EVENT_NAME,
    eventPayload,
    repository: GITHUB_REPOSITORY,
    request,
  });
  if (!source.publish) {
    await appendFile(GITHUB_OUTPUT, 'publish=false\n', 'utf8');
    return;
  }
  const outputs = [
    ['publish', true],
    ['run_id', source.runId],
    ['run_attempt', source.runAttempt],
    ['source_sha', source.sourceSha],
    ['artifact_id', source.artifactId],
    ['artifact_name', source.artifactName],
    ['workflow_id', source.workflowId],
    ['workflow_name', source.workflowName],
    ['run_url', source.runUrl],
  ];
  for (const [name, value] of outputs) {
    if (String(value).includes('\n') || String(value).includes('\r')) {
      fail(`The v1 docs source output ${name} contains an invalid line break.`);
    }
  }
  await appendFile(GITHUB_OUTPUT, outputs.map(([name, value]) => `${name}=${value}`).join('\n') + '\n', 'utf8');
}

async function composeCommand(args) {
  const rootIndex = args.indexOf('--root-dist');
  const artifactIndex = args.indexOf('--artifact-dir');
  if (rootIndex < 0 || artifactIndex < 0 || !args[rootIndex + 1] || !args[artifactIndex + 1]) {
    fail('Usage: pages-publisher.mjs compose --root-dist <path> --artifact-dir <path>');
  }
  await composePagesDist({
    rootDist: args[rootIndex + 1],
    artifactDir: args[artifactIndex + 1],
    source: expectedSourceFromEnvironment(process.env),
    summaryPath: process.env.GITHUB_STEP_SUMMARY,
  });
}

async function runCli() {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'resolve') {
    await resolveCommand();
  } else if (command === 'compose') {
    await composeCommand(args);
  } else {
    fail('Usage: pages-publisher.mjs <resolve|compose>');
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runCli().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
