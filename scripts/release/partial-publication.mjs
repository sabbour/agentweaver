/**
 * Continues an approved partial release publication after validating its original workflow evidence.
 * It publishes only planned, verified artifacts and records completion evidence for that same source.
 */
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createNugetConfig } from './nuget-config.mjs';
import { readDockerArchiveConfig } from './docker-archive.mjs';
import { PARTIAL_PUBLICATION as P } from './partial-publication-contract.mjs';

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const digestPattern = /^sha256:[a-f0-9]{64}$/;
const contentHashPattern = /^[a-f0-9]{64}$/;
const shaPattern = /^[a-f0-9]{40}$/;
const runIdPattern = /^[1-9]\d*$/;
const indexMediaTypes = new Set([
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
]);
const manifestMediaTypes = new Set([
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.v2+json',
]);
const readAfterWriteDelays = [2_000, 5_000, 10_000, 20_000];
const fail = message => { throw new Error(`partial publication: ${message}`); };
const canonical = value => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
};
const sameKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) &&
  canonical(Object.keys(value).sort()) === canonical([...keys].sort());

function exact(value, expected, message) {
  if (canonical(value) !== canonical(expected)) fail(message);
}

function imageRepositoryOf(planned) {
  const suffix = `:${planned.version}`;
  if (planned.kind !== 'image' || typeof planned.destination !== 'string' ||
      !planned.destination.startsWith(`${P.registry}/`) || !planned.destination.endsWith(suffix)) {
    fail(`the ${planned.id} image destination is not the approved versioned GHCR tag`);
  }
  return planned.destination.slice(0, -suffix.length);
}

/** Returns a copy of the immutable source, run, and receipt bindings expected for the original run. */
export function originalBinding() {
  return {
    claimSha: P.claimSha,
    resultSha: P.resultSha,
    receiptSha256: P.receiptSha256,
    provenanceSha256: P.provenanceSha256,
    runId: P.runId,
    packArtifactId: P.packArtifactId,
    packArtifactDigest: P.packArtifactDigest,
    receiptArtifactId: P.receiptArtifactId,
    receiptArtifactDigest: P.receiptArtifactDigest,
    publishedIds: [...P.publishedIds],
  };
}

function buildClaim({ helperSha, runId, runAttempt, planned }) {
  return {
    schemaVersion: 1,
    mode: 'partial-continuation',
    status: 'claimed',
    sourceSha: P.sourceSha,
    helperSha,
    runId,
    runAttempt,
    namespace: P.namespace,
    original: originalBinding(),
    planned,
    published: [],
  };
}

function artifactPlan(selected, imageRepositories, feed) {
  return selected.map(({ component, artifact }) => ({
    id: component.id,
    version: component.version,
    kind: artifact.kind,
    sha256: artifact.sha256,
    destination: artifact.kind === 'package' ? feed
      : `${imageRepositories.get(component.id)}:${component.version}`,
  }));
}

/** Verifies the original publication receipt bytes and plan against the pinned run and helper source. */
export function validateOriginalReceipt(receipt, receiptBytes, plan, helperSha) {
  if (!shaPattern.test(helperSha ?? '') || helperSha === P.sourceSha ||
      sha256(receiptBytes) !== P.receiptSha256 ||
      receipt?.schemaVersion !== 1 || receipt.sourceSha !== P.sourceSha ||
      receipt.status !== 'partial' || receipt.provenanceSha256 !== P.provenanceSha256 ||
      receipt.claimSha !== P.claimSha ||
      !Array.isArray(receipt.planned) || !Array.isArray(receipt.published) ||
      receipt.published.length !== P.publishedIds.length) {
    fail('the original receipt bytes or source-bound partial state do not match the approved incident');
  }
  exact(receipt.planned, plan, 'the original plan does not match every original prepared artifact and destination');
  exact(receipt.published.map(({ id }) => id), P.publishedIds,
    'the original published set contains an unexpected, missing, or reordered component');
  for (const item of receipt.published) {
    const planned = plan.find(({ id }) => id === item.id);
    let valid = Boolean(planned) && item.version === planned.version && item.kind === planned.kind;
    if (valid && item.kind === 'package') {
      valid = planned.destination === P.feed && item.feed === planned.destination && item.sha256 === planned.sha256;
    } else if (valid && item.kind === 'image') {
      const repository = imageRepositoryOf(planned);
      const digest = item.image?.slice(repository.length + 1);
      valid = item.archiveSha256 === planned.sha256 && item.image === `${repository}@${digest}` &&
        digestPattern.test(digest ?? '') &&
        (item.id !== 'Agentweaver.AgentHost' ||
          (digestPattern.test(item.imageConfigDigest ?? '') &&
            item.preparedOciMeasurement?.sourceSha === P.sourceSha &&
            item.preparedOciMeasurement?.image?.digest === digest &&
            item.preparedOciMeasurement?.image?.configDigest === item.imageConfigDigest));
    } else {
      valid = false;
    }
    if (!valid) fail(`the original ${item?.id ?? 'unknown'} publication record does not match its prepared bytes and destination`);
  }
  exact(P.missingIds, plan.map(({ id }) => id).filter(id => !P.publishedIds.includes(id)),
    'the expected missing set does not match the exact original release plan');
  return receipt;
}

function runGh(run, method, endpoint, body) {
  const args = ['api', '--method', method, `repos/${P.repository}/${endpoint}`];
  if (body !== undefined) args.push('--input', '-');
  return JSON.parse(run('gh', args, body === undefined ? undefined : JSON.stringify(body)));
}

function readTagRecord(run, name, expectedObjectSha) {
  const kind = name.endsWith('/claim') ? 'claim' : 'result';
  if (!shaPattern.test(expectedObjectSha ?? '')) fail(`the immutable ${kind} ref has no exact object`);
  const ref = runGh(run, 'GET', `git/ref/tags/${encodeURIComponent(name)}`);
  if (ref?.ref !== `refs/tags/${name}` || ref.object?.type !== 'tag' || ref.object.sha !== expectedObjectSha) {
    fail(`the immutable ${kind} ref differs from its approved object`);
  }
  const tag = runGh(run, 'GET', `git/tags/${expectedObjectSha}`);
  if (tag?.sha !== expectedObjectSha || tag.tag !== name || tag.object?.type !== 'commit' ||
      tag.object.sha !== P.sourceSha) {
    fail(`the immutable ${kind} object is not bound to the exact release source`);
  }
  try {
    return JSON.parse(tag.message);
  } catch {
    fail(`the immutable ${kind} message is not valid JSON`);
  }
}

function verifyOriginalRecords(run, originalReceipt) {
  const namespace = `agentweaver-publication/${P.sourceSha}`;
  const claim = readTagRecord(run, `${namespace}/claim`, P.claimSha);
  const result = readTagRecord(run, `${namespace}/result`, P.resultSha);
  const { claimSha, status, published, ...claimFields } = originalReceipt;
  exact(claim, { ...claimFields, status: 'claimed', published: [] },
    'the original immutable claim does not match the exact failed publication');
  exact(result, originalReceipt, 'the original immutable result differs from the retained partial receipt');
}

function verifyArtifactMetadata(run, id, name, size, digest) {
  const metadata = runGh(run, 'GET', `actions/artifacts/${id}`);
  const workflowRun = metadata?.workflow_run;
  if (String(metadata?.id) !== id || metadata.name !== name || metadata.size_in_bytes !== size ||
      metadata.expired !== false || metadata.digest !== digest ||
      String(workflowRun?.id) !== P.runId ||
      workflowRun.head_sha !== P.sourceSha || workflowRun.head_branch !== P.branch) {
    fail(`the retained Actions artifact ${id} does not match the approved source and exact archive metadata`);
  }
  return metadata;
}

function verifyOriginalWorkflowRun(run) {
  const workflow = runGh(run, 'GET', `actions/runs/${P.runId}`);
  if (String(workflow?.id) !== P.runId || workflow.path !== P.originalWorkflowPath ||
      workflow.head_sha !== P.sourceSha || workflow.head_branch !== P.branch ||
      workflow.event !== 'workflow_dispatch' || workflow.run_attempt !== 1 ||
      workflow.status !== 'completed' || workflow.conclusion !== 'failure') {
    fail('the original release workflow run is not the exact completed first-attempt v1 run');
  }
}

// The helper commit is admitted by a merged pull request into v1; the protected
// environment approval on the continuation run is the human admission of that exact commit.
/** Confirms the helper commit was admitted by the expected protected workflow before publication. */
export function verifyHelperAdmission(run, helperSha) {
  if (!shaPattern.test(helperSha ?? '')) fail('the helper commit must be an exact SHA');
  for (let page = 1; ; page += 1) {
    const pulls = runGh(run, 'GET',
      `pulls?state=closed&base=${encodeURIComponent(P.branch)}&sort=updated&direction=desc&per_page=100&page=${page}`);
    if (!Array.isArray(pulls)) fail('the merged helper pull request lookup returned an invalid response');
    if (pulls.some(pull => pull?.state === 'closed' &&
        typeof pull.merged_at === 'string' && pull.merged_at &&
        pull.merge_commit_sha === helperSha &&
        pull.base?.ref === P.branch && pull.base?.repo?.full_name === P.repository)) return;
    if (pulls.length < 100) break;
  }
  fail('the helper commit is not the exact result of a pull request merged into v1');
}

/** Checks that the original workflow run received the required protected-environment approval. */
export function verifyProtectedAdmission(run, runId) {
  const approvals = runGh(run, 'GET', `actions/runs/${runId}/approvals`);
  if (!Array.isArray(approvals) || !approvals.some(approval => approval?.state === 'approved' &&
      Array.isArray(approval.environments) && approval.environments.some(environment =>
        environment?.id === P.environmentId && environment.name === P.environmentName))) {
    fail(`run ${runId} has no recorded approval of the protected ${P.environmentName} environment`);
  }
}

async function nugetBaseUrl(env, fetchImpl) {
  const feed = new URL(env.RELEASE_NUGET_SOURCE ?? '');
  if (feed.href !== P.feed || !env.RELEASE_NUGET_USERNAME || !env.RELEASE_NUGET_API_KEY) {
    fail('the exact GitHub Packages feed and its existing workflow token are required');
  }
  const authorization = `Basic ${Buffer.from(`${env.RELEASE_NUGET_USERNAME}:${env.RELEASE_NUGET_API_KEY}`).toString('base64')}`;
  let response;
  try {
    response = await fetchImpl(feed, {
      headers: { authorization, accept: 'application/json' },
      cache: 'no-store',
      redirect: 'manual',
    });
  } catch {
    fail('the GitHub Packages service index could not be read');
  }
  if (!response.ok) fail(`the GitHub Packages service index returned HTTP ${response.status}`);
  let index;
  try {
    index = await response.json();
  } catch {
    fail('the GitHub Packages service index is malformed');
  }
  const resource = index?.resources?.find(entry =>
    (Array.isArray(entry['@type']) ? entry['@type'] : [entry['@type']])
      .some(type => String(type).startsWith('PackageBaseAddress/3.0.0')) &&
    typeof entry['@id'] === 'string');
  if (!resource) fail('the GitHub Packages service index has no flat-container endpoint');
  const base = new URL(`${resource['@id'].replace(/\/+$/, '')}/`);
  if (base.protocol !== 'https:' || base.origin !== feed.origin || base.username || base.password) {
    fail('the GitHub Packages flat-container endpoint is outside the approved feed origin');
  }
  return { base, authorization };
}

async function readPackageBytes(component, { base, authorization }, fetchImpl) {
  const id = encodeURIComponent(component.id.toLowerCase());
  const version = encodeURIComponent(component.version.toLowerCase());
  const url = new URL(`${id}/${version}/${id}.${version}.nupkg`, base);
  if (url.origin !== base.origin) fail('the NuGet package read escaped the approved feed origin');
  let response;
  try {
    response = await fetchImpl(url, {
      headers: { authorization, accept: 'application/octet-stream' },
      cache: 'no-store',
      redirect: 'manual',
    });
  } catch {
    fail(`the exact ${component.id} package version could not be read`);
  }
  if (response.status === 404) return null;
  if (!response.ok) fail(`the exact ${component.id} package version returned HTTP ${response.status}`);
  return Buffer.from(await response.arrayBuffer());
}

// Only an explicit registry "missing manifest" answer proves absence. Authorization,
// transport, rate-limit, or other failures are unknown and stop the continuation.
/** Distinguishes a genuine missing image from registry authentication, rate-limit, or transport failures. */
export function isRegistryAbsent(error, reference) {
  const diagnostic = error?.nativeDiagnostic;
  return error?.operation === 'docker.manifest-read' && typeof diagnostic === 'string' &&
    !/unauthori[sz]ed|denied|forbidden|authenticat|token|toomanyrequests|rate limit|timeout|refused|unreachable/i.test(diagnostic) &&
    (/\bmanifest unknown\b|\bname unknown\b/i.test(diagnostic) || diagnostic.includes(`${reference}: not found`));
}

function readRawManifest(run, repository, digest) {
  const raw = run('docker', ['buildx', 'imagetools', 'inspect', `${repository}@${digest}`, '--raw']);
  if (typeof raw !== 'string' || `sha256:${sha256(raw)}` !== digest) {
    fail(`the ${repository}@${digest} manifest bytes do not match their digest`);
  }
  try {
    return JSON.parse(raw);
  } catch {
    fail(`the ${repository}@${digest} manifest is malformed`);
  }
}

/** Reads and validates a remote image manifest, preserving non-absence errors for the caller. */
export function readRemoteImage(run, repository, tag) {
  const reference = `${repository}:${tag}`;
  let descriptor;
  try {
    descriptor = JSON.parse(run('docker', [
      'buildx', 'imagetools', 'inspect', reference, '--format', '{{json .Manifest}}',
    ]));
  } catch (error) {
    if (isRegistryAbsent(error, reference)) return null;
    throw error;
  }
  if (!digestPattern.test(descriptor?.digest ?? '')) fail(`the GHCR manifest for ${reference} is malformed or ambiguous`);
  let platformDigest = descriptor.digest;
  let manifest = readRawManifest(run, repository, platformDigest);
  if (indexMediaTypes.has(manifest?.mediaType)) {
    const images = (Array.isArray(manifest.manifests) ? manifest.manifests : []).filter(entry =>
      entry?.annotations?.['vnd.docker.reference.type'] !== 'attestation-manifest');
    if (images.length !== 1 || images[0].platform?.os !== 'linux' || images[0].platform?.architecture !== 'amd64' ||
        !digestPattern.test(images[0].digest ?? '')) {
      fail(`the GHCR index for ${reference} must name exactly one linux/amd64 image`);
    }
    platformDigest = images[0].digest;
    manifest = readRawManifest(run, repository, platformDigest);
  }
  if (manifest?.schemaVersion !== 2 || !manifestMediaTypes.has(manifest.mediaType) ||
      !digestPattern.test(manifest.config?.digest ?? '') ||
      !Array.isArray(manifest.layers) || manifest.layers.length === 0) {
    fail(`the GHCR manifest for ${reference} is not one exact single-platform image`);
  }
  return {
    digest: descriptor.digest,
    platformDigest,
    configDigest: manifest.config.digest,
    layerCount: manifest.layers.length,
  };
}

async function readAfterWrite(read, sleep) {
  let value = await read();
  for (const delay of readAfterWriteDelays) {
    if (value !== null) break;
    await sleep(delay);
    value = await read();
  }
  return value;
}

function archiveReference(item) {
  return item.localImageReference ?? item.artifact.imageReference ??
    `${item.artifact.repository}:${item.artifact.tag}`;
}

function preparedImage(item, readArchiveConfig) {
  const { component, artifact } = item;
  const reference = archiveReference(item);
  let prepared;
  try {
    prepared = readArchiveConfig(item.file, reference);
  } catch (error) {
    fail(`the prepared ${component.id} archive config cannot be verified: ${error.message}`);
  }
  const config = prepared?.config;
  const labels = config?.config?.Labels;
  const diffIds = config?.rootfs?.diff_ids;
  if (!digestPattern.test(prepared?.configDigest ?? '') || config.os !== 'linux' || config.architecture !== 'amd64' ||
      labels?.['org.opencontainers.image.revision'] !== P.sourceSha ||
      labels?.['org.opencontainers.image.version'] !== component.version ||
      !Array.isArray(diffIds) || diffIds.length === 0 || diffIds.some(id => !digestPattern.test(id ?? '')) ||
      (artifact.imageConfigDigest !== undefined && artifact.imageConfigDigest !== prepared.configDigest)) {
    fail(`the prepared ${component.id} archive is not the exact source-bound linux/amd64 image`);
  }
  return { reference, configDigest: prepared.configDigest, diffIds };
}

function verifyLoadedImage(item, expected, run) {
  const { component, artifact } = item;
  let image;
  try {
    image = JSON.parse(run('docker', ['image', 'inspect', '--format', '{{json .}}', expected.reference]));
  } catch (error) {
    fail(`the loaded ${component.id} image cannot be inspected: ${error.message}`);
  }
  const labels = image?.Config?.Labels;
  if ((artifact.imageId !== undefined && image?.Id !== artifact.imageId) ||
      image?.Os !== 'linux' || image?.Architecture !== 'amd64' ||
      labels?.['org.opencontainers.image.revision'] !== P.sourceSha ||
      labels?.['org.opencontainers.image.version'] !== component.version ||
      canonical(image?.RootFS?.Layers) !== canonical(expected.diffIds)) {
    fail(`the loaded ${component.id} image does not match its archive layers, source, version, identity, or platform`);
  }
}

async function readOutput(plan, component, reads) {
  if (plan.kind === 'package') {
    const value = await reads.packageSha256(component);
    return value === null ? null : { sha256: value };
  }
  return reads.image(imageRepositoryOf(plan), plan.version);
}

function matchesRecord(record, plan, observed) {
  if (observed === null) return false;
  if (plan.kind === 'package') return observed.sha256 === plan.sha256 && record.sha256 === plan.sha256;
  const configDigest = record.imageConfigDigest ?? record.configDigest;
  return record.image === `${imageRepositoryOf(plan)}@${observed.digest}` &&
    digestPattern.test(configDigest ?? '') && observed.configDigest === configDigest;
}

function makeRecordFactory(run) {
  return (name, record) => {
    const tag = `${P.namespace}/${name}`;
    const message = JSON.stringify(record);
    const object = runGh(run, 'POST', 'git/tags', { tag, message, object: P.sourceSha, type: 'commit' });
    if (!shaPattern.test(object?.sha ?? '') || object.tag !== tag || object.message !== message ||
        object.object?.sha !== P.sourceSha || object.object?.type !== 'commit') {
      fail(`the new ${name} tag response does not match its exact source-bound record`);
    }
    const refName = `refs/tags/${tag}`;
    const created = runGh(run, 'POST', 'git/refs', { ref: refName, sha: object.sha });
    if (created?.ref !== refName || created.object?.type !== 'tag' || created.object.sha !== object.sha) {
      fail(`the new ${name} ref response is ambiguous; inspect it before any further action`);
    }
    return object.sha;
  };
}

function publishPackage(item, { env, run }) {
  const configDirectory = mkdtempSync(path.join(tmpdir(), 'agentweaver-continuation-nuget-'));
  try {
    chmodSync(configDirectory, 0o700);
    const configFile = path.join(configDirectory, 'NuGet.Config');
    writeFileSync(configFile, createNugetConfig(
      env.RELEASE_NUGET_SOURCE, env.RELEASE_NUGET_USERNAME, env.RELEASE_NUGET_API_KEY,
    ), { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    run('dotnet', ['nuget', 'push', item.file, '--source', 'agentweaver-publication',
      '--api-key', env.RELEASE_NUGET_API_KEY, '--configfile', configFile]);
  } finally {
    rmSync(configDirectory, { recursive: true, force: true });
  }
}

function readNamespace(run) {
  const refs = runGh(run, 'GET', `git/matching-refs/tags/${P.namespace}/`);
  if (!Array.isArray(refs)) fail('the continuation namespace state is ambiguous');
  return refs;
}

/**
 * Verifies original-run evidence, then publishes only artifacts selected by the validated plan.
 * Any mismatch fails closed; registry writes and completion-receipt writes occur only after admission.
 */
export async function continuePartialPublication({
  sourceSha,
  helperSha,
  selected,
  imageRepositories,
  env,
  run,
  fetchImpl = globalThis.fetch,
  readArchiveConfig = readDockerArchiveConfig,
  readPackageSha256,
  readImage,
  sleep = delay => new Promise(resolve => setTimeout(resolve, delay)),
  directory,
  originalReceiptPath,
  originalReceiptBytes,
  originalReceipt,
  provenanceSha256,
  runId,
  runAttempt,
}) {
  if (sourceSha !== P.sourceSha || !runIdPattern.test(runId ?? '') || runId === P.runId ||
      runAttempt !== '1' || !shaPattern.test(helperSha ?? '') || helperSha === P.sourceSha || !directory) {
    fail('the admitted helper, original release, new run, and first attempt must match the one approved recovery');
  }
  if (existsSync(path.join(directory, 'continuation.json')) || existsSync(path.join(directory, 'publication.json'))) {
    fail('a local continuation or publication receipt already exists; inspect the permanent claim before stopping');
  }
  if (!originalReceiptBytes || !originalReceipt || !originalReceiptPath ||
      sha256(originalReceiptBytes) !== P.receiptSha256 ||
      sha256(readFileSync(originalReceiptPath)) !== P.receiptSha256) {
    fail('the original publication receipt bytes are missing or changed');
  }
  if (provenanceSha256 !== P.provenanceSha256) {
    fail('the retained provenance bytes differ from the original publication provenance');
  }
  if (!Array.isArray(selected) || selected.length !== P.publishedIds.length + P.missingIds.length ||
      selected.some(({ component }) => component.id === 'Agentweaver.FoundationProbe')) {
    fail('the original artifact set must be exactly the 23 product components');
  }
  if (!env.GH_TOKEN || env.GITHUB_REPOSITORY !== P.repository || env.RELEASE_REGISTRY !== P.registry ||
      env.RELEASE_NUGET_SOURCE !== P.feed || !env.RELEASE_REGISTRY_USER || !env.RELEASE_REGISTRY_PASSWORD) {
    fail('the existing protected workflow token and exact approved destinations are required');
  }
  const plan = artifactPlan(selected, imageRepositories, env.RELEASE_NUGET_SOURCE);
  const receipt = validateOriginalReceipt(originalReceipt, originalReceiptBytes, plan, helperSha);
  const planById = new Map(plan.map(entry => [entry.id, entry]));
  const selectedById = new Map(selected.map(item => [item.component.id, item]));
  const existing = P.publishedIds.map(id => receipt.published.find(item => item.id === id));
  const missing = P.missingIds.map(id => selectedById.get(id));
  if (existing.some(item => !item) || missing.some(item => !item || item.agentHost)) {
    fail('the original receipt or prepared artifact set does not match the exact 12-plus-11 recovery split');
  }

  verifyArtifactMetadata(run, P.packArtifactId, P.packArtifactName, P.packArtifactSize, P.packArtifactDigest);
  verifyArtifactMetadata(run, P.receiptArtifactId, P.receiptArtifactName, P.receiptSize, P.receiptArtifactDigest);
  verifyOriginalRecords(run, receipt);
  verifyOriginalWorkflowRun(run);
  if (readNamespace(run).length !== 0) fail('the fixed continuation claim already exists; this recovery is consumed');
  verifyHelperAdmission(run, helperSha);
  verifyProtectedAdmission(run, runId);

  run('docker', ['login', 'ghcr.io', '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'],
    env.RELEASE_REGISTRY_PASSWORD);
  const nuget = readPackageSha256 ? undefined : await nugetBaseUrl(env, fetchImpl);
  const reads = {
    packageSha256: readPackageSha256 ?? (async component => {
      const bytes = await readPackageBytes(component, nuget, fetchImpl);
      return bytes === null ? null : sha256(bytes);
    }),
    image: readImage ?? ((repository, tag) => readRemoteImage(run, repository, tag)),
  };
  for (const record of existing) {
    const planItem = planById.get(record.id);
    const observed = await readOutput(planItem, selectedById.get(record.id).component, reads);
    if (!matchesRecord(record, planItem, observed)) {
      fail(`the previously published ${record.id} is missing or differs from its permanent record`);
    }
  }
  for (const item of missing) {
    const planItem = planById.get(item.component.id);
    if (await readOutput(planItem, item.component, reads) !== null) {
      fail(`the missing ${item.component.id} destination is already present; refusing overwrite`);
    }
  }
  const preparedImages = new Map();
  for (const item of missing) {
    if (item.artifact.kind !== 'image') continue;
    const expected = preparedImage(item, readArchiveConfig);
    run('docker', ['load', '--input', item.file]);
    verifyLoadedImage(item, expected, run);
    preparedImages.set(item.component.id, expected);
  }

  const createRecord = makeRecordFactory(run);
  const claim = buildClaim({
    helperSha, runId, runAttempt, planned: missing.map(({ component }) => planById.get(component.id)),
  });
  claim.claimSha = createRecord('claim', claim);

  const additions = [];
  const unknown = [];
  const unattempted = missing.map(({ component }) => component.id);
  let publicationError;
  for (const item of missing) {
    const { component, artifact } = item;
    const planItem = planById.get(component.id);
    let mutated = false;
    try {
      if (artifact.kind === 'package') {
        if (await readOutput(planItem, component, reads) !== null) {
          fail(`the ${component.id} destination appeared after preflight; refusing overwrite`);
        }
        unattempted.shift();
        mutated = true;
        publishPackage(item, { env, run });
        const published = await readAfterWrite(() => reads.packageSha256(component), sleep);
        if (published !== artifact.sha256) {
          fail(`the pushed ${component.id} package could not be confirmed byte-for-byte`);
        }
        additions.push({
          id: component.id, version: component.version, kind: 'package',
          feed: env.RELEASE_NUGET_SOURCE, sha256: artifact.sha256,
        });
      } else {
        const expected = preparedImages.get(component.id);
        const repository = imageRepositoryOf(planItem);
        const remote = `${repository}:${component.version}`;
        if (!expected) fail(`the ${component.id} image was not loaded and verified before its continuation claim`);
        run('docker', ['tag', expected.reference, remote]);
        if (await reads.image(repository, component.version) !== null) {
          fail(`the ${component.id} destination appeared after preflight; refusing overwrite`);
        }
        unattempted.shift();
        mutated = true;
        const pushed = /\bdigest: (sha256:[a-f0-9]{64})\b/.exec(run('docker', ['push', remote]) ?? '')?.[1];
        const observed = await readAfterWrite(() => reads.image(repository, component.version), sleep);
        if (!pushed || !observed || observed.digest !== pushed || observed.configDigest !== expected.configDigest ||
            observed.layerCount !== expected.diffIds.length) {
          fail(`the pushed ${component.id} image does not read back as the exact pushed archive image`);
        }
        additions.push({
          id: component.id, version: component.version, kind: 'image',
          image: `${repository}@${observed.digest}`, archiveSha256: artifact.sha256,
          configDigest: observed.configDigest,
        });
      }
    } catch (error) {
      if (mutated) unknown.push(component.id);
      publicationError = error;
      break;
    }
  }
  if (!publicationError) {
    const records = [...receipt.published, ...additions];
    for (const record of records) {
      const planItem = planById.get(record.id);
      try {
        const observed = await readOutput(planItem, selectedById.get(record.id).component, reads);
        if (!matchesRecord(record, planItem, observed)) unknown.push(record.id);
      } catch {
        unknown.push(record.id);
      }
    }
    if (unknown.length > 0) {
      publicationError = new Error('partial publication: final registry read-back was missing, unknown, or mismatched');
    }
  }
  const completed = !publicationError && additions.length === P.missingIds.length &&
    unknown.length === 0 && unattempted.length === 0;
  const finalReceipt = {
    schemaVersion: 1,
    status: completed ? 'published' : 'partial',
    sourceSha,
    provenanceSha256: receipt.provenanceSha256,
    planned: receipt.planned,
    published: [...receipt.published, ...additions],
    continuation: {
      namespace: P.namespace,
      claimSha: claim.claimSha,
      helperSha,
      runId,
      runAttempt,
      original: claim.original,
      addedIds: [...P.missingIds],
      originalReceiptText: originalReceiptBytes.toString('utf8'),
    },
    ...(unknown.length ? { unknown: [...new Set(unknown)] } : {}),
    ...(unattempted.length ? { unattempted } : {}),
    ...(publicationError ? {
      failure: {
        operation: publicationError.operation ?? 'partial-continuation',
        code: publicationError.code ?? 'OPERATION_FAILED',
      },
    } : {}),
  };
  const failures = [];
  try {
    createRecord('result', finalReceipt);
  } catch (error) {
    failures.push(error);
  }
  const text = `${JSON.stringify(finalReceipt, null, 2)}\n`;
  const deployable = completed && failures.length === 0;
  for (const name of deployable ? ['continuation.json', 'publication.json'] : ['continuation.json']) {
    try {
      writeFileSync(path.join(directory, name), text, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    } catch (error) {
      failures.push(error);
    }
  }
  if (failures.length) {
    throw new AggregateError([...(publicationError ? [publicationError] : []), ...failures],
      'partial publication: finalization failed; the permanent continuation claim blocks replay');
  }
  if (publicationError) throw publicationError;
  return finalReceipt;
}

/** Confirms the completion document exactly matches the expected publication result and source. */
export function verifyPartialPublicationCompletion(completion, publication, sourceSha, run) {
  if (!completion || !publication || canonical(completion) !== canonical(publication) ||
      sourceSha !== P.sourceSha ||
      !sameKeys(completion, ['schemaVersion', 'status', 'sourceSha', 'provenanceSha256', 'planned', 'published', 'continuation']) ||
      completion.schemaVersion !== 1 || completion.status !== 'published' ||
      completion.sourceSha !== P.sourceSha || completion.provenanceSha256 !== P.provenanceSha256 ||
      !Array.isArray(completion.planned) || !Array.isArray(completion.published)) {
    fail('the completion proof is missing, partial, changed, or bound to another release');
  }
  const continuation = completion.continuation;
  if (!sameKeys(continuation, ['namespace', 'claimSha', 'helperSha', 'runId', 'runAttempt', 'original', 'addedIds', 'originalReceiptText']) ||
      continuation.namespace !== P.namespace || continuation.runAttempt !== '1' ||
      !runIdPattern.test(continuation.runId ?? '') || continuation.runId === P.runId ||
      !shaPattern.test(continuation.helperSha ?? '') || continuation.helperSha === P.sourceSha ||
      !shaPattern.test(continuation.claimSha ?? '') ||
      canonical(continuation.addedIds) !== canonical(P.missingIds) ||
      canonical(continuation.original) !== canonical(originalBinding()) ||
      typeof continuation.originalReceiptText !== 'string') {
    fail('the completion metadata does not match the one approved continuation run');
  }
  const originalReceiptBytes = Buffer.from(continuation.originalReceiptText, 'utf8');
  let originalReceipt;
  try {
    originalReceipt = JSON.parse(continuation.originalReceiptText);
  } catch {
    fail('the embedded original partial receipt is malformed');
  }
  const plan = completion.planned;
  validateOriginalReceipt(originalReceipt, originalReceiptBytes, plan, continuation.helperSha);
  if (completion.published.length !== P.publishedIds.length + P.missingIds.length) {
    fail('the completed publication does not contain the exact approved 12-plus-11 component set');
  }
  exact(completion.published.slice(0, P.publishedIds.length), originalReceipt.published,
    'the completed publication changed or reordered the original permanent records');
  completion.published.slice(P.publishedIds.length).forEach((record, index) => {
    const planned = plan.find(entry => entry.id === P.missingIds[index]);
    let valid = Boolean(planned) && record?.id === planned.id && record.version === planned.version &&
      record.kind === planned.kind;
    if (valid && record.kind === 'package') {
      valid = sameKeys(record, ['id', 'version', 'kind', 'feed', 'sha256']) &&
        record.feed === planned.destination && record.sha256 === planned.sha256 && contentHashPattern.test(record.sha256);
    } else if (valid && record.kind === 'image') {
      const repository = imageRepositoryOf(planned);
      const digest = record.image?.slice(repository.length + 1);
      valid = sameKeys(record, ['id', 'version', 'kind', 'image', 'archiveSha256', 'configDigest']) &&
        record.image === `${repository}@${digest}` && digestPattern.test(digest ?? '') &&
        record.archiveSha256 === planned.sha256 && digestPattern.test(record.configDigest ?? '');
    } else {
      valid = false;
    }
    if (!valid) fail(`the continued ${P.missingIds[index]} record does not match its exact original plan`);
  });
  verifyOriginalRecords(run, originalReceipt);

  const namespaceRefs = readNamespace(run);
  const claimRefName = `refs/tags/${P.namespace}/claim`;
  const resultRefName = `refs/tags/${P.namespace}/result`;
  const claimRef = namespaceRefs.find(ref => ref?.ref === claimRefName);
  const resultRef = namespaceRefs.find(ref => ref?.ref === resultRefName);
  if (namespaceRefs.length !== 2 || !claimRef || !resultRef || claimRef.object?.sha !== continuation.claimSha) {
    fail('the continuation namespace does not contain exactly its one-use claim and result');
  }
  const claim = readTagRecord(run, `${P.namespace}/claim`, claimRef.object.sha);
  exact(claim, buildClaim({
    helperSha: continuation.helperSha,
    runId: continuation.runId,
    runAttempt: continuation.runAttempt,
    planned: P.missingIds.map(id => plan.find(entry => entry.id === id)),
  }), 'the continuation claim does not match the exact successful recovery');
  const result = readTagRecord(run, `${P.namespace}/result`, resultRef.object?.sha);
  exact(result, completion, 'the immutable continuation result differs from the supplied completion receipt');

  const continuationRun = runGh(run, 'GET', `actions/runs/${continuation.runId}`);
  if (String(continuationRun?.id) !== continuation.runId ||
      continuationRun.path !== P.workflowPath ||
      continuationRun.head_sha !== continuation.helperSha ||
      continuationRun.head_branch !== P.branch || continuationRun.event !== 'workflow_dispatch' ||
      continuationRun.run_attempt !== 1 || continuationRun.status !== 'completed' ||
      continuationRun.conclusion !== 'success') {
    fail('the continuation receipt is not backed by the exact successful first-attempt v1 workflow run');
  }
  verifyProtectedAdmission(run, continuation.runId);
  verifyHelperAdmission(run, continuation.helperSha);
  return completion;
}
