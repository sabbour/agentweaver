// Git-tree verification used before any deploy/acceptance tooling runs.
// Exact-SHA proof requires a clean working tree and a full 40-character
// commit SHA; anything else is rejected rather than silently normalized.
import { runGit } from './exec.mjs';
import { readFileSync, realpathSync } from 'node:fs';
import { resolve, relative, isAbsolute } from 'node:path';
import { createHash } from 'node:crypto';
import { validateFile } from '../../release/validate.mjs';
import { validateAppRoutingDnsZoneResourceIds } from './app-routing-dns.mjs';

const FULL_SHA_PATTERN = /^[0-9a-f]{40}$/;

export function isFullSha(value) {
  return typeof value === 'string' && FULL_SHA_PATTERN.test(value);
}

/**
 * Returns { sha, branch } for HEAD in `cwd`, or throws if the tree is dirty
 * or HEAD cannot be resolved to a full 40-character SHA.
 */
export function resolveCleanHead(cwd, { execGit = runGit } = {}) {
  const status = execGit(['status', '--porcelain'], { cwd });
  if (status.stdout.trim().length > 0) {
    throw new Error(
      'Working tree is not clean. Exact-SHA deployment tooling refuses a dirty tree; commit, stash, or discard changes first.',
    );
  }

  const revParse = execGit(['rev-parse', 'HEAD'], { cwd });
  const sha = revParse.stdout.trim();
  if (!isFullSha(sha)) {
    throw new Error(`HEAD did not resolve to a full 40-character SHA (got "${sha}").`);
  }

  const branch = execGit(['rev-parse', '--abbrev-ref', 'HEAD'], { cwd }).stdout.trim();
  return { sha, branch };
}

export function resolveSource({ repoRoot, template, parametersFile, resourceGroup, subscriptionId, tenantId },
  { execGit = runGit, readFile = readFileSync, realpath = realpathSync, validateRelease = validateFile } = {}) {
  const { sha, branch } = resolveCleanHead(repoRoot, { execGit });
  const sourceTree = execGit(['rev-parse', `${sha}^{tree}`], { cwd: repoRoot }).stdout.trim();
  if (!isFullSha(sourceTree)) throw new Error('Source tree did not resolve to a full Git tree SHA.');
  const ancestor = execGit(['merge-base', '--is-ancestor', 'origin/v1', sha], { cwd: repoRoot, check: false });
  if (ancestor.status !== 0) throw new Error('Source must contain the locally fetched current origin/v1 ancestry.');
  const admittedSha = execGit(['rev-parse', 'origin/v1'], { cwd: repoRoot }).stdout.trim();
  if (sha !== admittedSha) throw new Error('Deployment source must be the locally fetched admitted origin/v1 tip, not an unreviewed descendant.');
  const root = realpath(repoRoot);
  function trackedPath(file) {
    if (!file) throw new Error('Tracked template and JSON parameters are required.');
    const requested = resolve(root, file);
    const absolute = realpath(requested);
    if (absolute !== requested) throw new Error('Symlinked infrastructure inputs are not reviewed source paths.');
    const local = relative(root, absolute).replaceAll('\\', '/');
    if (isAbsolute(local) || local.startsWith('../') || !local.startsWith('infra/bicep/')) {
      throw new Error('Source input is outside tracked infra/bicep.');
    }
    const tracked = execGit(['ls-files', '--error-unmatch', '--', local], { cwd: root, check: false });
    if (tracked.status !== 0) throw new Error(`Untracked source input: ${local}`);
    return { absolute, local };
  }
  const inputTemplate = trackedPath(template);
  if (inputTemplate.local !== 'infra/bicep/main.bicep') throw new Error('Only the reviewed v1 main template is supported.');
  const parameters = trackedPath(parametersFile);
  if (!/^infra\/bicep\/parameters\/[^/]+\.json$/.test(parameters.local)) {
    throw new Error('Use tracked Azure JSON parameters beneath infra/bicep/parameters.');
  }
  const extras = execGit(['ls-files', '--others', '--ignored', '--exclude-standard', '--', 'infra/bicep'],
    { cwd: root });
  if (extras.stdout.trim()) throw new Error('Ignored/untracked infrastructure inputs are not exact source.');
  const files = execGit(['ls-files', '--', 'infra/bicep'], { cwd: root }).stdout.trim().split(/\r?\n/).sort();
  const hash = createHash('sha256');
  for (const file of files) {
    const tracked = trackedPath(file);
    const content = readFile(tracked.absolute);
    const committed = execGit(['show', `${sha}:${file}`], { cwd: root }).stdout;
    // Git normalizes CRLF in text files. Compare content, then hash normalized bytes.
    const normalized = content.toString().replaceAll('\r\n', '\n');
    if (normalized !== committed.replaceAll('\r\n', '\n')) throw new Error(`Changed source input: ${file}`);
    hash.update(file).update('\0').update(normalized).update('\0');
  }
  const document = JSON.parse(readFile(parameters.absolute, 'utf8'));
  const values = document.parameters;
  if (values?.namePrefix?.value !== resourceGroup || values?.tenantId?.value !== tenantId ||
      !values?.owner?.value || !values?.costCenter?.value ||
      /unassigned|CHANGEME|00000000-0000/i.test(JSON.stringify(document))) {
    throw new Error('Parameters do not bind the exact dedicated target or contain placeholders.');
  }
  const appRoutingDnsZoneResourceIds = validateAppRoutingDnsZoneResourceIds(
    values.appRoutingDnsZoneResourceIds?.value, subscriptionId,
  );
  validateRelease(resolve(root, 'releases/foundation.json'), { root });
  return { sha, branch, sourceTree, sourceHash: hash.digest('hex'), template: inputTemplate.absolute,
    parametersFile: parameters.absolute, owner: values.owner.value, costCenter: values.costCenter.value,
    postgresEntraAdminObjectId: values.postgresEntraAdminObjectId?.value,
    appRoutingDnsZoneResourceIds,
    scope: 'infrastructure-only' };
}
