import { access, cp, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

function required(env, name) {
  const value = env[name];
  if (!value) throw new Error(`Missing ${name}.`);
  return value;
}

export function createArtifactMetadata(env) {
  const repository = required(env, 'DOCS_REPOSITORY');
  const branch = required(env, 'DOCS_BRANCH');
  const event = required(env, 'DOCS_EVENT');
  const runId = Number(required(env, 'DOCS_RUN_ID'));
  const runAttempt = Number(required(env, 'DOCS_RUN_ATTEMPT'));
  const sourceSha = required(env, 'DOCS_SOURCE_SHA');

  if (!/^[^/]+\/[^/]+$/.test(repository)) throw new Error('DOCS_REPOSITORY must be owner/name.');
  if (branch !== 'v1') throw new Error('DOCS_BRANCH must be v1.');
  if (event !== 'push') throw new Error('DOCS_EVENT must be push.');
  if (!Number.isSafeInteger(runId) || runId < 1) throw new Error('DOCS_RUN_ID must be a positive integer.');
  if (!Number.isSafeInteger(runAttempt) || runAttempt < 1) throw new Error('DOCS_RUN_ATTEMPT must be a positive integer.');
  if (!/^[0-9a-f]{40}$/.test(sourceSha)) throw new Error('DOCS_SOURCE_SHA must be a full lowercase commit SHA.');

  return {
    schemaVersion: 1,
    repository,
    branch,
    event,
    workflow: 'Agentweaver v1 Docs CI',
    workflowPath: '.github/workflows/v1-docs-ci.yml',
    runId,
    runAttempt,
    sourceSha,
    artifactName: `v1-docs-${sourceSha}-${runAttempt}`,
  };
}

async function stageArtifact(env = process.env, root = repoRoot) {
  const metadata = createArtifactMetadata(env);
  const distPath = path.join(root, 'docs', '.vitepress', 'dist');
  const outputPath = path.join(root, 'artifacts', 'docs');
  await access(distPath);
  await mkdir(outputPath, { recursive: true });
  await cp(distPath, path.join(outputPath, 'dist'), { recursive: true });
  await writeFile(
    path.join(outputPath, 'metadata.json'),
    `${JSON.stringify(metadata, null, 2)}\n`,
    'utf8',
  );
  console.log(`Staged ${metadata.artifactName} with metadata beside dist/.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await stageArtifact();
}
