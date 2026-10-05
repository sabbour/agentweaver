// Git-tree verification used before any deploy/acceptance tooling runs.
// Exact-SHA proof requires a clean working tree and a full 40-character
// commit SHA; anything else is rejected rather than silently normalized.
import { gitShowMatches, runGit } from './exec.mjs';
import { readFileSync, realpathSync } from 'node:fs';
import { resolve, relative, isAbsolute } from 'node:path';
import { createHash } from 'node:crypto';
import { validateFile } from '../../release/validate.mjs';
import { validateAppRoutingDnsZoneResourceIds } from './app-routing-dns.mjs';

const FULL_SHA_PATTERN = /^[0-9a-f]{40}$/;
const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const POSTGRES_ADMIN_TYPES = new Set(['User', 'Group', 'ServicePrincipal']);

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

export function resolveSource({ repoRoot, template, parametersFile, resourceGroup, subscriptionId, tenantId, expectedSha,
  operatorObjectId: rawOperatorObjectId },
  { execGit = runGit, compareGitShow = gitShowMatches, readFile = readFileSync,
    realpath = realpathSync, validateRelease = validateFile } = {}) {
  const { sha: headSha, branch } = resolveCleanHead(repoRoot, { execGit });
  const sha = expectedSha ?? headSha;
  if (!isFullSha(sha)) throw new Error('Selected source must be a full 40-character SHA.');
  const sourceTree = execGit(['rev-parse', `${sha}^{tree}`], { cwd: repoRoot }).stdout.trim();
  if (!isFullSha(sourceTree)) throw new Error('Source tree did not resolve to a full Git tree SHA.');
  const ancestor = execGit(['merge-base', '--is-ancestor', 'origin/v1', headSha], { cwd: repoRoot, check: false });
  if (ancestor.status !== 0) throw new Error('Source must contain the locally fetched current origin/v1 ancestry.');
  const admittedSha = execGit(['rev-parse', 'origin/v1'], { cwd: repoRoot }).stdout.trim();
  if (headSha !== admittedSha) throw new Error('Verifier HEAD must be the locally fetched admitted origin/v1 tip, not an unreviewed descendant.');
  const deploymentAncestor = execGit(['merge-base', '--is-ancestor', sha, headSha], { cwd: repoRoot, check: false });
  if (deploymentAncestor.status !== 0) {
    throw new Error('Selected deployment source must be an ancestor of the reviewed verifier HEAD.');
  }
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
  if (!['infra/bicep/main.bicep', 'infra/bicep/aks-redeploy.bicep'].includes(inputTemplate.local)) {
    throw new Error('Only the reviewed v1 foundation or AKS redeployment template is supported.');
  }
  const parameters = trackedPath(parametersFile);
  if (!/^infra\/bicep\/parameters\/[^/]+\.json$/.test(parameters.local)) {
    throw new Error('Use tracked Azure JSON parameters beneath infra/bicep/parameters.');
  }
  const extras = execGit(['ls-files', '--others', '--ignored', '--exclude-standard', '--', 'infra/bicep'],
    { cwd: root });
  if (extras.stdout.trim()) throw new Error('Ignored/untracked infrastructure inputs are not exact source.');
  const files = execGit(['ls-files', '--', 'infra/bicep'], { cwd: root }).stdout.trim().split(/\r?\n/).sort();
  const sourceFiles = execGit(['ls-tree', '-r', '--name-only', sha, '--', 'infra/bicep'], { cwd: root })
    .stdout.trim().split(/\r?\n/).sort();
  if (!sourceFiles.length || sourceFiles.join('\n') !== files.join('\n')) {
    throw new Error('Selected source has a different tracked infrastructure input set.');
  }
  const hash = createHash('sha256');
  for (const file of files) {
    const tracked = trackedPath(file);
    const content = readFile(tracked.absolute);
    // Git normalizes CRLF in text files. Compare content, then hash normalized bytes.
    const normalized = content.toString().replaceAll('\r\n', '\n');
    if (!compareGitShow(sha, file, content, { cwd: root })) throw new Error(`Changed source input: ${file}`);
    hash.update(file).update('\0').update(normalized).update('\0');
  }
  const document = JSON.parse(readFile(parameters.absolute, 'utf8'));
  const values = document.parameters;
  if (!GUID_PATTERN.test(rawOperatorObjectId ?? '')) {
    throw new Error('An explicit Kubernetes operator object ID is required.');
  }
  const operatorObjectId = rawOperatorObjectId.toLowerCase();
  const location = values?.location?.value;
  const namePrefix = values?.namePrefix?.value;
  const parameterTenantId = values?.tenantId?.value;
  const owner = values?.owner?.value;
  const costCenter = values?.costCenter?.value;
  if (namePrefix !== resourceGroup || parameterTenantId !== tenantId ||
      typeof owner !== 'string' || !owner.trim() ||
      typeof costCenter !== 'string' || !costCenter.trim() ||
      typeof location !== 'string' || !location.trim() ||
      /unassigned|CHANGEME|00000000-0000/i.test(JSON.stringify(document))) {
    throw new Error('Parameters must bind the dedicated target, tenant, region, owner and cost center without placeholders.');
  }
  const scope = inputTemplate.local === 'infra/bicep/main.bicep' ? 'infrastructure-only' : 'aks-only';
  let monitorLocation;
  let postgresEntraAdminObjectId;
  let postgresEntraAdminPrincipalName;
  let postgresEntraAdminPrincipalType;
  if (scope === 'infrastructure-only') {
    monitorLocation = values?.monitorLocation?.value;
    postgresEntraAdminObjectId = values?.postgresEntraAdminObjectId?.value;
    postgresEntraAdminPrincipalName = values?.postgresEntraAdminPrincipalName?.value;
    postgresEntraAdminPrincipalType = values?.postgresEntraAdminPrincipalType?.value;
    if (typeof monitorLocation !== 'string' || !monitorLocation.trim() ||
        location.toLowerCase() === monitorLocation.toLowerCase() ||
        !GUID_PATTERN.test(postgresEntraAdminObjectId ?? '') ||
        typeof postgresEntraAdminPrincipalName !== 'string' || !postgresEntraAdminPrincipalName.trim() ||
        !POSTGRES_ADMIN_TYPES.has(postgresEntraAdminPrincipalType)) {
      throw new Error('Foundation parameters must specify a separate Monitor region and supported PostgreSQL Entra administrator.');
    }
  } else {
    if (parameters.local !== 'infra/bicep/parameters/p0-aks-redeploy.approved.json') {
      throw new Error('AKS-only redeployment requires its exact reviewed P0 parameter file.');
    }
    const approvedFoundation = trackedPath('infra/bicep/parameters/p0-integration.approved.json');
    const foundationValues = JSON.parse(readFile(approvedFoundation.absolute, 'utf8')).parameters;
    postgresEntraAdminObjectId = foundationValues?.postgresEntraAdminObjectId?.value;
    postgresEntraAdminPrincipalName = foundationValues?.postgresEntraAdminPrincipalName?.value;
    postgresEntraAdminPrincipalType = foundationValues?.postgresEntraAdminPrincipalType?.value;
    if (foundationValues?.namePrefix?.value !== namePrefix ||
        foundationValues?.tenantId?.value !== parameterTenantId ||
        foundationValues?.owner?.value !== owner ||
        foundationValues?.costCenter?.value !== costCenter ||
        foundationValues?.location?.value !== location ||
        !GUID_PATTERN.test(postgresEntraAdminObjectId ?? '') ||
        typeof postgresEntraAdminPrincipalName !== 'string' || !postgresEntraAdminPrincipalName.trim() ||
        !POSTGRES_ADMIN_TYPES.has(postgresEntraAdminPrincipalType)) {
      throw new Error('AKS-only PostgreSQL bootstrap inputs do not match the approved full-foundation parameters.');
    }
  }
  const appRoutingDnsZoneResourceIds = validateAppRoutingDnsZoneResourceIds(
    values.appRoutingDnsZoneResourceIds?.value, subscriptionId,
  );
  validateRelease(resolve(root, 'releases/foundation.json'), { root });
  return { sha, verifierSha: headSha, branch, sourceTree, sourceHash: hash.digest('hex'), template: inputTemplate.absolute,
    parametersFile: parameters.absolute, owner, costCenter, operatorObjectId,
    location, monitorLocation, postgresEntraAdminObjectId, postgresEntraAdminPrincipalName,
    postgresEntraAdminPrincipalType,
    appRoutingDnsZoneResourceIds,
    scope };
}
