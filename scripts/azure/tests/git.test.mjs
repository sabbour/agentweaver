import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isFullSha, resolveCleanHead, resolveSource } from '../lib/git.mjs';
import { resolve, relative } from 'node:path';
import { fixture, source, ids } from './fixtures/target.mjs';

test('isFullSha accepts exactly 40 lowercase hex characters', () => {
  assert.ok(isFullSha('abcdef0123456789abcdef0123456789abcdef01'.slice(0, 40)));
  assert.ok(!isFullSha('e5093cd'));
  assert.ok(!isFullSha('ABCDEF0123456789ABCDEF0123456789ABCDEF01'));
  assert.ok(!isFullSha(''));
  assert.ok(!isFullSha(undefined));
});

function sourceFixture(overrides = {}) {
  const root = process.cwd();
  const selectedSha = overrides.config?.expectedSha ?? source.sha;
  const template = 'infra/bicep/main.bicep';
  const parameterPath = 'infra/bicep/parameters/approved.json';
  const parameterValues = { namePrefix: { value: 'aw-v1-p0' },
    tenantId: { value: ids.tenantId }, owner: { value: 'team' }, costCenter: { value: 'p0' },
    location: { value: source.location }, monitorLocation: { value: source.monitorLocation },
    postgresEntraAdminObjectId: { value: source.postgresEntraAdminObjectId },
    postgresEntraAdminPrincipalName: { value: source.postgresEntraAdminPrincipalName },
    postgresEntraAdminPrincipalType: { value: source.postgresEntraAdminPrincipalType } };
  Object.assign(parameterValues, overrides.parameterValues);
  if (!overrides.omitZones) parameterValues.appRoutingDnsZoneResourceIds = { value: overrides.zoneIds ?? [] };
  const parameters = JSON.stringify({ parameters: parameterValues });
  const files = { [template]: 'targetScope = \'resourceGroup\'\n', [parameterPath]: parameters };
  const config = { ...fixture, repoRoot: root, template, parametersFile: parameterPath, ...overrides.config };
  const deps = {
    realpath: path => resolve(path),
    readFile: path => files[relative(root, path).replaceAll('\\', '/')],
    validateRelease: () => ({}),
    compareGitShow: (revision, file, expected) => {
      assert.equal(revision, selectedSha);
      return !overrides.changed && Buffer.from(files[file]).equals(Buffer.from(expected));
    },
    execGit: args => {
      if (args[0] === 'status') return { status: 0, stdout: overrides.dirty ?? '' };
      if (args[0] === 'rev-parse') return { status: 0, stdout: args[1].endsWith('^{tree}') ?
        args[1].startsWith(`${selectedSha}^{tree}`) ? overrides.deploymentTree ?? overrides.sourceTree ?? source.sourceTree :
          source.sourceTree :
        args[1] === 'HEAD' ? source.sha :
        args[1] === 'origin/v1' ? overrides.unadmitted ? 'f'.repeat(40) : source.sha : 'candidate' };
      if (args[0] === 'merge-base') {
        return { status: args[2] === 'origin/v1' ? overrides.wrongAncestry ? 1 : 0 :
          overrides.wrongDeploymentAncestry ? 1 : 0, stdout: '' };
      }
      if (args[0] === 'ls-files' && args.includes('--error-unmatch')) return {
        status: overrides.untracked ? 1 : 0, stdout: args.at(-1),
      };
      if (args[0] === 'ls-files' && args.includes('--others')) return { status: 0, stdout: overrides.ignored ?? '' };
      if (args[0] === 'ls-files') return { status: 0, stdout: Object.keys(files).join('\n') };
      if (args[0] === 'ls-tree') return { status: 0, stdout: overrides.sourceFiles ?? Object.keys(files).join('\n') };
      throw new Error(`Unexpected git command ${args.join(' ')}`);
    },
    ...overrides.deps,
  };
  return { config, deps };
}

test('exact source hashes tracked reviewed inputs and binds JSON parameters to target', () => {
  const { config, deps } = sourceFixture();
  const receipt = resolveSource(config, deps);
  assert.equal(receipt.sha, source.sha);
  assert.equal(receipt.sourceTree, source.sourceTree);
  assert.notEqual(receipt.sourceTree, receipt.sourceHash);
  assert.match(receipt.sourceHash, /^[0-9a-f]{64}$/);
  assert.equal(receipt.scope, 'infrastructure-only');
  assert.equal(receipt.owner, 'team');
  assert.equal(receipt.location, 'eastus2euap');
  assert.equal(receipt.monitorLocation, 'eastus2');
  assert.equal(receipt.postgresEntraAdminObjectId, source.postgresEntraAdminObjectId);
  assert.equal(receipt.postgresEntraAdminPrincipalName, source.postgresEntraAdminPrincipalName);
  assert.equal(receipt.postgresEntraAdminPrincipalType, 'User');
  assert.deepEqual(receipt.appRoutingDnsZoneResourceIds, []);
});

test('acceptance pins an ancestor deployment source separately from the reviewed verifier HEAD', () => {
  const deployedSha = 'd'.repeat(40);
  const deployedTree = 'e'.repeat(40);
  const current = sourceFixture();
  const currentReceipt = resolveSource(current.config, current.deps);
  const { config, deps } = sourceFixture({ config: { expectedSha: deployedSha }, deploymentTree: deployedTree });
  const receipt = resolveSource(config, deps);
  assert.equal(receipt.sha, deployedSha);
  assert.equal(receipt.verifierSha, currentReceipt.sha);
  assert.equal(receipt.sourceTree, deployedTree);
  assert.equal(receipt.sourceHash, currentReceipt.sourceHash);

  for (const overrides of [
    { config: { expectedSha: 'short' } },
    { config: { expectedSha: 'f'.repeat(40) }, wrongDeploymentAncestry: true },
    { config: { expectedSha: deployedSha }, changed: true },
    { config: { expectedSha: deployedSha }, sourceFiles: 'infra/bicep/removed-from-verifier.bicep' },
  ]) {
    const invalid = sourceFixture({ ...overrides, deploymentTree: deployedTree });
    assert.throws(() => resolveSource(invalid.config, invalid.deps));
  }
});

test('source parameters require a supported Entra administrator and separate Monitor region', () => {
  for (const principalType of ['User', 'Group', 'ServicePrincipal']) {
    const { config, deps } = sourceFixture({
      parameterValues: { postgresEntraAdminPrincipalType: { value: principalType } },
    });
    assert.equal(resolveSource(config, deps).postgresEntraAdminPrincipalType, principalType);
  }
  for (const parameterValues of [
    { postgresEntraAdminPrincipalType: { value: 'Unknown' } },
    { postgresEntraAdminObjectId: { value: 'not-a-guid' } },
    { postgresEntraAdminPrincipalName: { value: '' } },
    { location: { value: 'eastus2' }, monitorLocation: { value: 'EASTUS2' } },
    { monitorLocation: undefined },
  ]) {
    const { config, deps } = sourceFixture({ parameterValues });
    assert.throws(() => resolveSource(config, deps));
  }
});

test('custom App Routing DNS zones are optional and bind approved public or private zones', () => {
  const zoneIds = [
    `/subscriptions/${ids.subscriptionId}/resourceGroups/public-dns/providers/Microsoft.Network/dnsZones/apps.example.com`,
    `/subscriptions/${ids.subscriptionId}/resourceGroups/private-dns/providers/Microsoft.Network/privateDnsZones/apps.internal.example`,
  ];
  const { config, deps } = sourceFixture({ zoneIds });
  assert.deepEqual(resolveSource(config, deps).appRoutingDnsZoneResourceIds, zoneIds);

  const omitted = sourceFixture({ omitZones: true });
  assert.deepEqual(resolveSource(omitted.config, omitted.deps).appRoutingDnsZoneResourceIds, []);
});

test('custom App Routing DNS zones reject invalid, duplicate, cross-subscription and ungrouped inputs', () => {
  const validPublic = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/dns-a/providers/Microsoft.Network/dnsZones/apps.example.com';
  const validPrivate = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/dns-a/providers/Microsoft.Network/privateDnsZones/apps.internal.example';
  const anotherPublicGroup = validPublic.replace('/dns-a/', '/dns-b/');
  const anotherPrivateGroup = validPrivate.replace('/dns-a/', '/dns-b/');
  const invalidSets = [
    'not-an-array',
    ['not-an-arm-id'],
    [validPublic.replace('11111111-1111-1111-1111-111111111111', '99999999-9999-9999-9999-999999999999')],
    [validPublic.replace('apps.example.com', 'privatelink.example.com')],
    [validPublic.replace('apps.example.com', 'apps_bad.example.com')],
    [validPublic, validPublic.toUpperCase()],
    [validPublic, anotherPublicGroup],
    [validPrivate, anotherPrivateGroup],
    Array.from({ length: 6 }, (_, i) => validPublic.replace('apps.example.com', `app${i}.example.com`)),
  ];
  for (const zoneIds of invalidSets) {
    const { config, deps } = sourceFixture({ zoneIds });
    assert.throws(() => resolveSource(config, deps));
  }
  const { config, deps } = sourceFixture({ zoneIds: [validPublic] });
  assert.throws(() => resolveSource({ ...config, subscriptionId: undefined }, deps), /authorized subscription/);
});

test('clean full HEAD alone cannot authorize untracked/outside/0.x/changed/ignored inputs', () => {
  for (const overrides of [
    { dirty: '?? infra/bicep/parameters/untracked.json' }, { untracked: true }, { wrongAncestry: true },
    { changed: true }, { unadmitted: true }, { ignored: 'infra/bicep/hidden.bicep' },
    { sourceTree: source.sourceHash }, { sourceTree: 'HEAD' },
    { config: { template: '..\\external.bicep' } }, { config: { template: 'infra/bicep/other.bicep' } },
    { config: { parametersFile: 'infra/bicep/parameters/unreviewed.bicepparam' } },
    { config: { tenantId: 'other' } },
    { deps: { validateRelease: () => { throw new Error('incompatible manifest'); } } },
  ]) {
    const { config, deps } = sourceFixture(overrides);
    assert.throws(() => resolveSource(config, deps));
  }
});

test('resolveCleanHead throws when the tree is dirty', () => {
  const execGit = (args) => {
    if (args[0] === 'status') return { stdout: ' M some/file.cs\n', stderr: '' };
    return { stdout: '', stderr: '' };
  };
  assert.throws(() => resolveCleanHead('/repo', { execGit }), /not clean/);
});

test('resolveCleanHead throws when HEAD is not a full SHA', () => {
  const execGit = (args) => {
    if (args[0] === 'status') return { stdout: '', stderr: '' };
    if (args[0] === 'rev-parse' && args[1] === 'HEAD') return { stdout: 'short123\n', stderr: '' };
    return { stdout: '', stderr: '' };
  };
  assert.throws(() => resolveCleanHead('/repo', { execGit }), /full 40-character SHA/);
});

test('resolveCleanHead returns sha and branch for a clean tree', () => {
  const fullSha = 'e5093cd81e6302c6812ec866a8f3a62bd77b6e8a';
  const execGit = (args) => {
    if (args[0] === 'status') return { stdout: '', stderr: '' };
    if (args[0] === 'rev-parse' && args[1] === 'HEAD') return { stdout: `${fullSha}\n`, stderr: '' };
    if (args[0] === 'rev-parse' && args[1] === '--abbrev-ref') return { stdout: 'sabbour-automatic-fiesta\n', stderr: '' };
    throw new Error(`unexpected git args ${JSON.stringify(args)}`);
  };
  const { sha, branch } = resolveCleanHead('/repo', { execGit });
  assert.equal(sha, fullSha);
  assert.equal(branch, 'sabbour-automatic-fiesta');
});
