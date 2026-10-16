import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { componentImageRepository } from './pack.mjs';
import { createNugetConfig } from './nuget-config.mjs';
import { PARTIAL_PUBLICATION } from './partial-publication-contract.mjs';
import { verifyAgentHostArchives, verifyLoadedAgentHostImage } from './agenthost-image.mjs';

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const digestPattern = /^sha256:[a-f0-9]{64}$/;
const shaPattern = /^[a-f0-9]{40}$/;
const fail = message => { throw new Error(`partial publication: ${message}`); };
const canonical = value => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
};

function exact(value, expected, message) {
  if (canonical(value) !== canonical(expected)) fail(message);
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

function validateOriginalReceipt(receipt, receiptBytes, plan, helperSha) {
  if (!shaPattern.test(helperSha ?? '') || helperSha === PARTIAL_PUBLICATION.sourceSha ||
      sha256(receiptBytes) !== PARTIAL_PUBLICATION.receiptSha256 ||
      receipt?.schemaVersion !== 1 || receipt.sourceSha !== PARTIAL_PUBLICATION.sourceSha ||
      receipt.status !== 'partial' || receipt.provenanceSha256 !== PARTIAL_PUBLICATION.provenanceSha256 ||
      receipt.claimSha !== PARTIAL_PUBLICATION.claimSha ||
      !Array.isArray(receipt.planned) || !Array.isArray(receipt.published) ||
      receipt.published.length !== PARTIAL_PUBLICATION.publishedIds.length) {
    fail('the original receipt bytes or source-bound partial state do not match the approved incident');
  }
  exact(receipt.planned, plan, 'the original plan does not match every original prepared artifact and destination');
  exact(receipt.published.map(({ id }) => id), PARTIAL_PUBLICATION.publishedIds,
    'the original published set contains an unexpected, missing, or reordered component');
  for (const item of receipt.published) {
    const planned = plan.find(({ id }) => id === item.id);
    if (!planned || item.version !== planned.version || item.kind !== planned.kind ||
        (item.kind === 'package' &&
          (item.feed !== planned.destination || item.sha256 !== planned.sha256)) ||
        (item.kind === 'image' &&
          (item.archiveSha256 !== planned.sha256 || !item.image?.startsWith(`${planned.destination.slice(0, -planned.version.length)}@`) ||
            !digestPattern.test(item.image.slice(item.image.lastIndexOf('@') + 1)))) {
      fail(`the original ${item?.id ?? 'unknown'} publication record does not match its prepared bytes and destination`);
    }
  }
  exact(PARTIAL_PUBLICATION.missingIds,
    plan.map(({ id }) => id).filter(id => !PARTIAL_PUBLICATION.publishedIds.includes(id)),
    'the expected missing set does not match the exact original release plan');
  return receipt;
}

function runGh(run, method, endpoint, body) {
  const args = ['api', '--method', method, `repos/sabbour/agentweaver/${endpoint}`];
  if (body !== undefined) args.push('--input', '-');
  return JSON.parse(run('gh', args, body === undefined ? undefined : JSON.stringify(body)));
}

function readTagRecord(run, name, expectedObjectSha) {
  const ref = runGh(run, 'GET', `git/ref/tags/${encodeURIComponent(name)}`);
  if (ref?.ref !== `refs/tags/${name}` || ref.object?.type !== 'tag' ||
      ref.object.sha !== expectedObjectSha) {
    fail(`the immutable ${name.endsWith('/claim') ? 'claim' : 'result'} ref differs from its approved object`);
  }
  const tag = runGh(run, 'GET', `git/tags/${expectedObjectSha}`);
  if (tag?.sha !== expectedObjectSha || tag.tag !== name || tag.object?.type !== 'commit' ||
      tag.object.sha !== PARTIAL_PUBLICATION.sourceSha) {
    fail(`the immutable ${name.endsWith('/claim') ? 'claim' : 'result'} object is not bound to the exact release source`);
  }
  try {
    return JSON.parse(tag.message);
  } catch {
    fail(`the immutable ${name.endsWith('/claim') ? 'claim' : 'result'} message is not valid JSON`);
  }
}

function verifyOriginalRecords(run, originalReceipt) {
  const namespace = `agentweaver-publication/${PARTIAL_PUBLICATION.sourceSha}`;
  const claim = readTagRecord(run, `${namespace}/claim`, PARTIAL_PUBLICATION.claimSha);
  const result = readTagRecord(run, `${namespace}/result`, PARTIAL_PUBLICATION.resultSha);
  const { claimSha, status, published, ...claimFields } = originalReceipt;
  exact(claim, { ...claimFields, status: 'claimed', published: [] },
    'the original immutable claim does not match the exact failed publication');
  exact(result, originalReceipt, 'the original immutable result differs from the retained partial receipt');
}

function verifyArtifactMetadata(run, id, name, size, digest) {
  const metadata = runGh(run, 'GET', `actions/artifacts/${id}`);
  const workflowRun = metadata?.workflow_run;
  if (String(metadata?.id) !== id || metadata.name !== name || metadata.size_in_bytes !== size ||
      metadata.expired !== false || (digest && metadata.digest !== digest) ||
      String(workflowRun?.id) !== PARTIAL_PUBLICATION.runId ||
      workflowRun.head_sha !== PARTIAL_PUBLICATION.sourceSha || workflowRun.head_branch !== 'v1') {
    fail(`the retained Actions artifact ${id} does not match the approved source and exact archive metadata`);
  }
  return metadata;
}

async function nugetBaseUrl(env, fetchImpl) {
  const feed = new URL(env.RELEASE_NUGET_SOURCE ?? '');
  if (feed.href !== 'https://nuget.pkg.github.com/sabbour/index.json' ||
      !env.RELEASE_NUGET_USERNAME || !env.RELEASE_NUGET_API_KEY) {
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

function imageDigest(repository, version, run) {
  const reference = `${repository}:${version}`;
  let descriptor;
  try {
    descriptor = JSON.parse(run('docker', [
      'buildx', 'imagetools', 'inspect', reference, '--format', '{{json .Manifest}}',
    ]));
  } catch (error) {
    if (error?.code === 'REGISTRY_NOT_FOUND') return null;
    throw error;
  }
  if (!digestPattern.test(descriptor?.digest ?? '')) {
    fail(`the GHCR manifest for ${reference} is malformed or ambiguous`);
  }
  return descriptor.digest;
}

async function inspectOutput(selected, plan, imageRepositories, nuget, fetchImpl, run) {
  if (selected.artifact.kind === 'package') {
    const bytes = await readPackageBytes(selected.component, nuget, fetchImpl);
    return bytes === null ? null : sha256(bytes);
  }
  return imageDigest(imageRepositories.get(selected.component.id), selected.component.version, run);
}

function compareRemote(item, plan, value) {
  if (item.kind === 'package') {
    if (value !== plan.sha256) fail(`the remote ${item.id} package bytes differ from the approved source artifact`);
    return;
  }
  const expected = item.image.slice(item.image.lastIndexOf('@') + 1);
  if (value !== expected) fail(`the remote ${item.id} image tag differs from its recorded immutable digest`);
}

function makeRecordFactory(run, sourceSha) {
  return (name, record) => {
    const tag = `${PARTIAL_PUBLICATION.namespace}/${name}`;
    const message = JSON.stringify(record);
    const object = runGh(run, 'POST', 'git/tags', { tag, message, object: sourceSha, type: 'commit' });
    if (!shaPattern.test(object?.sha ?? '') || object.tag !== tag || object.message !== message ||
        object.object?.sha !== sourceSha || object.object?.type !== 'commit') {
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

function verifyOriginalWorkflowRun(run) {
  const workflow = runGh(run, 'GET', `actions/runs/${PARTIAL_PUBLICATION.runId}`);
  if (String(workflow?.id) !== PARTIAL_PUBLICATION.runId ||
      workflow.path !== '.github/workflows/v1-release-pack.yml' ||
      workflow.head_sha !== PARTIAL_PUBLICATION.sourceSha || workflow.head_branch !== 'v1' ||
      workflow.event !== 'workflow_dispatch' || workflow.run_attempt !== 1 ||
      workflow.status !== 'completed' || workflow.conclusion !== 'failure') {
    fail('the original release workflow run is not the exact completed first-attempt v1 run');
  }
}

function publishPackage(item, { directory, env, run }) {
  const configDirectory = mkdtempSync(path.join(tmpdir(), 'agentweaver-continuation-nuget-'));
  chmodSync(configDirectory, 0o700);
  const configFile = path.join(configDirectory, 'NuGet.Config');
  writeFileSync(configFile, createNugetConfig(
    env.RELEASE_NUGET_SOURCE, env.RELEASE_NUGET_USERNAME, env.RELEASE_NUGET_API_KEY,
  ), { encoding: 'utf8', flag: 'wx', mode: 0o600 });
  try {
    run('dotnet', ['nuget', 'push', item.file, '--source', 'agentweaver-publication',
      '--api-key', env.RELEASE_NUGET_API_KEY, '--configfile', configFile]);
  } finally {
    rmSync(configDirectory, { recursive: true, force: true });
  }
}

async function verifyAllPublished(original, additions, planById, imageRepositories, nuget, fetchImpl, run) {
  const records = new Map([...original, ...additions].map(item => [item.id, item]));
  const unknown = [];
  for (const [id, record] of records) {
    try {
      const plan = planById.get(id);
      const selected = plan.selected;
      const value = await inspectOutput(selected, plan, imageRepositories, nuget, fetchImpl, run);
      if (value === null) {
        unknown.push(id);
      } else {
        compareRemote(record, plan, value);
      }
    } catch {
      unknown.push(id);
    }
  }
  return unknown;
}

export async function continuePartialPublication({
  sourceSha,
  helperSha,
  selected,
  imageRepositories,
  env,
  run,
  fetchImpl = globalThis.fetch,
  directory,
  originalReceiptPath,
  originalReceiptBytes,
  originalReceipt,
  runId,
  runAttempt,
}) {
  if (sourceSha !== PARTIAL_PUBLICATION.sourceSha || runId !== PARTIAL_PUBLICATION.runId ||
      runAttempt !== '1' || !shaPattern.test(helperSha ?? '') || !directory) {
    fail('the admitted helper, original release, run, and attempt must match the one approved recovery');
  }
  if (existsSync(path.join(directory, 'continuation.json'))) {
    fail('a continuation receipt already exists; inspect the permanent claim before stopping');
  }
  if (!originalReceiptBytes || !originalReceipt || !originalReceiptPath ||
      sha256(originalReceiptBytes) !== PARTIAL_PUBLICATION.receiptSha256 ||
      sha256(readFileSync(originalReceiptPath)) !== PARTIAL_PUBLICATION.receiptSha256) {
    fail('the original publication receipt bytes are missing or changed');
  }
  if (selected.length !== 23 ||
      selected.some(({ component }) => component.id === 'Agentweaver.FoundationProbe')) {
    fail('the original artifact set must be exactly the 23 product components');
  }
  const plan = artifactPlan(selected, imageRepositories, env.RELEASE_NUGET_SOURCE);
  const receipt = validateOriginalReceipt(originalReceipt, originalReceiptBytes, plan, helperSha);
  const planById = new Map(plan.map((entry, index) => [
    entry.id, { ...entry, selected: selected[index] },
  ]));
  const selectedById = new Map(selected.map(item => [item.component.id, item]));
  const existing = PARTIAL_PUBLICATION.publishedIds.map(id => receipt.published.find(item => item.id === id));
  const missing = PARTIAL_PUBLICATION.missingIds.map(id => selectedById.get(id));
  if (existing.some(item => !item) || missing.some(item => !item)) {
    fail('the original receipt or prepared artifact set does not match the exact 12-plus-11 recovery split');
  }
  if (!env.GH_TOKEN || env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' ||
      env.RELEASE_REGISTRY !== 'ghcr.io/sabbour' ||
      env.RELEASE_NUGET_SOURCE !== 'https://nuget.pkg.github.com/sabbour/index.json') {
    fail('the existing protected workflow token and exact approved destinations are required');
  }
  verifyArtifactMetadata(run, PARTIAL_PUBLICATION.packArtifactId, PARTIAL_PUBLICATION.packArtifactName,
    PARTIAL_PUBLICATION.packArtifactSize, PARTIAL_PUBLICATION.packArtifactDigest);
  verifyArtifactMetadata(run, PARTIAL_PUBLICATION.receiptArtifactId, PARTIAL_PUBLICATION.receiptArtifactName,
    PARTIAL_PUBLICATION.receiptSize);
  verifyOriginalRecords(run, receipt);
  const priorRefs = runGh(run, 'GET', `git/matching-refs/tags/${PARTIAL_PUBLICATION.namespace}/`);
  if (!Array.isArray(priorRefs) || priorRefs.length !== 0) {
    fail('the fixed continuation claim already exists or its state is ambiguous');
  }
  verifyOriginalWorkflowRun(run);

  run('docker', ['login', 'ghcr.io', '--username', env.RELEASE_REGISTRY_USER, '--password-stdin'],
    env.RELEASE_REGISTRY_PASSWORD);
  const nuget = await nugetBaseUrl(env, fetchImpl);
  for (const item of existing) {
    const planItem = planById.get(item.id);
    const observed = await inspectOutput(planItem.selected, planItem, imageRepositories, nuget, fetchImpl, run);
    if (observed === null) fail(`the previously published ${item.id} is missing`);
    compareRemote(item, planItem, observed);
  }
  for (const item of missing) {
    const planItem = planById.get(item.component.id);
    const observed = await inspectOutput(planItem.selected, planItem, imageRepositories, nuget, fetchImpl, run);
    if (observed !== null) fail(`the missing ${item.component.id} destination is already present; refusing overwrite`);
  }
  const loadedImages = new Map();
  for (const item of missing) {
    if (item.artifact.kind !== 'image') continue;
    const { component, artifact } = item;
    if (item.agentHost) {
      verifyAgentHostArchives(item.agentHost.archives, directory);
    }
    run('docker', ['load', '--input', item.file]);
    if (item.agentHost) {
      const loaded = verifyLoadedAgentHostImage(item.localImageReference, item.agentHost.measured,
        item.agentHost.source, component.version, args => run('docker', args));
      verifyAgentHostArchives(item.agentHost.archives, directory);
      loadedImages.set(component.id, loaded.loadedImageId);
      continue;
    }
    let image;
    try {
      image = JSON.parse(run('docker', [
        'image', 'inspect', '--format', '{{json .}}',
        item.localImageReference ?? `${componentImageRepository(component.id)}:${component.version}`,
      ]));
    } catch (error) {
      fail(`the loaded ${component.id} image cannot be inspected: ${error.message}`);
    }
    const labels = image?.Config?.Labels;
    if ((artifact.imageId !== undefined && image?.Id !== artifact.imageId) ||
        image?.Os !== 'linux' || image?.Architecture !== 'amd64' ||
        labels?.['org.opencontainers.image.revision'] !== sourceSha ||
        labels?.['org.opencontainers.image.version'] !== component.version) {
      fail(`the loaded ${component.id} image does not match its source, version, identity, or linux/amd64 provenance`);
    }
    loadedImages.set(component.id,
      item.localImageReference ?? `${componentImageRepository(component.id)}:${component.version}`);
  }
  const createRecord = makeRecordFactory(run, sourceSha);
  const claim = {
    schemaVersion: 1,
    mode: 'partial-continuation',
    status: 'claimed',
    sourceSha,
    helperSha,
    runId,
    runAttempt,
    namespace: PARTIAL_PUBLICATION.namespace,
    original: {
      claimSha: PARTIAL_PUBLICATION.claimSha,
      resultSha: PARTIAL_PUBLICATION.resultSha,
      receiptSha256: PARTIAL_PUBLICATION.receiptSha256,
      provenanceSha256: PARTIAL_PUBLICATION.provenanceSha256,
      runId: PARTIAL_PUBLICATION.runId,
      packArtifactId: PARTIAL_PUBLICATION.packArtifactId,
      packArtifactDigest: PARTIAL_PUBLICATION.packArtifactDigest,
      publishedIds: PARTIAL_PUBLICATION.publishedIds,
    },
    planned: missing.map(({ component }) => planById.get(component.id)),
    published: [],
  };
  claim.claimSha = createRecord('claim', claim);
  const additions = [];
  const unknown = [];
  let unattempted = missing.map(({ component }) => component.id);
  let publicationError;
  for (const item of missing) {
    const { component } = item;
    const planItem = planById.get(component.id);
    try {
      const before = await inspectOutput(item, planItem, imageRepositories, nuget, fetchImpl, run);
      if (before !== null) fail(`the ${component.id} destination appeared after preflight; refusing overwrite`);
      if (item.artifact.kind === 'package') {
        unattempted = unattempted.filter(id => id !== component.id);
        publishPackage(item, { directory, env, run });
        const packageBytes = await readPackageBytes(component, nuget, fetchImpl);
        if (!packageBytes || sha256(packageBytes) !== item.artifact.sha256) {
          unknown.push(component.id);
          fail(`the pushed ${component.id} package could not be confirmed byte-for-byte`);
        }
        additions.push({
          id: component.id, version: component.version, kind: 'package',
          feed: env.RELEASE_NUGET_SOURCE, sha256: item.artifact.sha256,
        });
      } else {
        const remote = `${imageRepositories.get(component.id)}:${component.version}`;
        const local = loadedImages.get(component.id);
        if (!local) fail(`the ${component.id} image was not loaded and verified before its continuation claim`);
        run('docker', ['tag', local, remote]);
        unattempted = unattempted.filter(id => id !== component.id);
        run('docker', ['push', remote]);
        const digest = imageDigest(imageRepositories.get(component.id), component.version, run);
        if (!digest) {
          unknown.push(component.id);
          fail(`the pushed ${component.id} image tag is not readable`);
        }
        const published = {
          id: component.id, version: component.version, kind: 'image',
          image: `${imageRepositories.get(component.id)}@${digest}`,
          archiveSha256: item.artifact.sha256,
        };
        compareRemote(published, planItem, digest);
        additions.push(published);
      }
    } catch (error) {
      if (!additions.some(({ id }) => id === component.id) && !unknown.includes(component.id)) {
        unknown.push(component.id);
      }
      unattempted = unattempted.filter(id => id !== component.id);
      publicationError = error;
      break;
    }
  }
  if (!publicationError) {
    unattempted = [];
    const readbackUnknown = await verifyAllPublished(
      receipt.published, additions, planById, imageRepositories, nuget, fetchImpl, run,
    );
    unknown.push(...readbackUnknown.filter(id => !unknown.includes(id)));
    if (readbackUnknown.length > 0) publicationError = new Error('partial publication: final registry read-back was missing, unknown, or mismatched');
  }
  const completed = !publicationError && additions.length === PARTIAL_PUBLICATION.missingIds.length &&
    unknown.length === 0 && unattempted.length === 0;
  const continuation = {
    namespace: PARTIAL_PUBLICATION.namespace,
    claimSha: claim.claimSha,
    helperSha,
    runId,
    runAttempt,
    original: claim.original,
    addedIds: PARTIAL_PUBLICATION.missingIds,
    originalReceiptText: originalReceiptBytes.toString('utf8'),
  };
  const finalReceipt = {
    schemaVersion: 1,
    status: completed ? 'published' : 'partial',
    sourceSha,
    provenanceSha256: receipt.provenanceSha256,
    planned: receipt.planned,
    published: [...receipt.published, ...additions],
    continuation,
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
  try {
    writeFileSync(path.join(directory, 'continuation.json'), `${JSON.stringify(finalReceipt, null, 2)}\n`,
      { encoding: 'utf8', flag: 'wx', mode: 0o600 });
  } catch (error) {
    failures.push(error);
  }
  if (completed) {
    try {
      writeFileSync(path.join(directory, 'publication.json'), `${JSON.stringify(finalReceipt, null, 2)}\n`,
        { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    } catch (error) {
      failures.push(error);
    }
  }
  if (failures.length) {
    throw new AggregateError([...(publicationError ? [publicationError] : []), ...failures],
      `partial publication finalization failed; permanent continuation claim blocks replay`);
  }
  if (publicationError) throw publicationError;
  return finalReceipt;
}

export function verifyPartialPublicationCompletion(completion, publication, sourceSha, run) {
  if (!completion || !publication || !isDeepStrictEqual(completion, publication) ||
      sourceSha !== PARTIAL_PUBLICATION.sourceSha ||
      completion.schemaVersion !== 1 || completion.status !== 'published' ||
      completion.sourceSha !== PARTIAL_PUBLICATION.sourceSha ||
      completion.provenanceSha256 !== PARTIAL_PUBLICATION.provenanceSha256 ||
      !Array.isArray(completion.planned) || !Array.isArray(completion.published) ||
      completion.unknown !== undefined || completion.unattempted !== undefined ||
      completion.failure !== undefined) {
    fail('the completion proof is missing, partial, changed, or bound to another release');
  }
  const continuation = completion.continuation;
  if (!continuation || continuation.namespace !== PARTIAL_PUBLICATION.namespace ||
      continuation.runAttempt !== '1' || continuation.runId === PARTIAL_PUBLICATION.runId ||
      !/^[1-9]\d*$/.test(continuation.runId ?? '') ||
      !shaPattern.test(continuation.helperSha ?? '') ||
      continuation.helperSha === PARTIAL_PUBLICATION.sourceSha ||
      !isDeepStrictEqual(continuation.addedIds, PARTIAL_PUBLICATION.missingIds) ||
      !isDeepStrictEqual(continuation.original, {
        claimSha: PARTIAL_PUBLICATION.claimSha,
        resultSha: PARTIAL_PUBLICATION.resultSha,
        receiptSha256: PARTIAL_PUBLICATION.receiptSha256,
        provenanceSha256: PARTIAL_PUBLICATION.provenanceSha256,
        runId: PARTIAL_PUBLICATION.runId,
        packArtifactId: PARTIAL_PUBLICATION.packArtifactId,
        packArtifactDigest: PARTIAL_PUBLICATION.packArtifactDigest,
        publishedIds: PARTIAL_PUBLICATION.publishedIds,
      }) || typeof continuation.originalReceiptText !== 'string') {
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
  if (!isDeepStrictEqual(completion.published.map(({ id }) => id), [
    ...PARTIAL_PUBLICATION.publishedIds, ...PARTIAL_PUBLICATION.missingIds,
  ])) {
    fail('the completed publication does not contain the exact approved 12-plus-11 component set');
  }
  verifyOriginalRecords(run, originalReceipt);

  const expectedClaim = {
    schemaVersion: 1,
    mode: 'partial-continuation',
    status: 'claimed',
    sourceSha: PARTIAL_PUBLICATION.sourceSha,
    helperSha: continuation.helperSha,
    runId: continuation.runId,
    runAttempt: continuation.runAttempt,
    namespace: PARTIAL_PUBLICATION.namespace,
    original: continuation.original,
    planned: PARTIAL_PUBLICATION.missingIds.map(id => plan.find(entry => entry.id === id)),
    published: [],
  };
  const namespaceRefs = runGh(run, 'GET', `git/matching-refs/tags/${PARTIAL_PUBLICATION.namespace}/`);
  const claimRefName = `refs/tags/${PARTIAL_PUBLICATION.namespace}/claim`;
  const resultRefName = `refs/tags/${PARTIAL_PUBLICATION.namespace}/result`;
  if (!Array.isArray(namespaceRefs) || namespaceRefs.length !== 2 ||
      !namespaceRefs.some(ref => ref.ref === claimRefName) ||
      !namespaceRefs.some(ref => ref.ref === resultRefName)) {
    fail('the continuation namespace does not contain exactly its one-use claim and result');
  }
  const claimRef = namespaceRefs.find(ref => ref.ref === claimRefName);
  const resultRef = namespaceRefs.find(ref => ref.ref === resultRefName);
  const claim = readTagRecord(run, `${PARTIAL_PUBLICATION.namespace}/claim`, claimRef.object?.sha);
  exact(claim, expectedClaim, 'the continuation claim does not match the exact successful recovery');
  const result = readTagRecord(run, `${PARTIAL_PUBLICATION.namespace}/result`, resultRef.object?.sha);
  exact(result, completion, 'the immutable continuation result differs from the supplied completion receipt');

  const continuationRun = runGh(run, 'GET', `actions/runs/${continuation.runId}`);
  if (String(continuationRun?.id) !== continuation.runId ||
      continuationRun.path !== PARTIAL_PUBLICATION.workflowPath ||
      continuationRun.head_sha !== continuation.helperSha ||
      continuationRun.head_branch !== 'v1' || continuationRun.event !== 'workflow_dispatch' ||
      continuationRun.run_attempt !== 1 || continuationRun.status !== 'completed' ||
      continuationRun.conclusion !== 'success') {
    fail('the continuation receipt is not backed by the exact successful first-attempt v1 workflow run');
  }
  return completion;
}
