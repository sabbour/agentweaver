import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

import {
  CHALLENGE_CATALOG_PATH,
  CHALLENGE_SCHEMA_PATH,
  getChallenge,
  listChallenges,
  loadChallengeCatalog,
  selectReleaseChallenges,
  validateChallengeCatalog,
} from '../challenge-catalog.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

test('checked-in challenge catalog is closed, valid, and contains the approved seed families', () => {
  const catalog = loadChallengeCatalog();
  const result = validateChallengeCatalog(catalog);
  assert.equal(result.ok, true, result.errors.join('\n'));
  assert.equal(catalog.entries.length, 44);
  for (const id of [
    'repository-map-reduce-error-index-v1',
    'workflow-conservative-fan-generation-v1',
    'product-management-full-lifecycle-v1',
    'release-lumenpath-launch-integration-v1',
    'homepage-marketing-launch-page-v1',
    'auth-session-refresh-before-disclosure-v1',
    'release-focused-feature-acceptance-v1',
    'release-repair-disposition-v1',
  ]) {
    assert.ok(getChallenge(catalog, id), `missing ${id}`);
  }
});

test('list and get are deterministic and filter by tier and surface', () => {
  const catalog = loadChallengeCatalog();
  const first = listChallenges(catalog, { tier: 'release-integration' });
  const second = listChallenges(catalog, { tier: 'release-integration' });
  assert.deepEqual(first, second);
  assert.deepEqual(first.map((entry) => entry.id), [...first.map((entry) => entry.id)].sort());
  assert.ok(first.some((entry) => entry.id === 'release-lumenpath-launch-integration-v1'));
  assert.ok(listChallenges(catalog, { surface: 'mcp' }).every((entry) =>
    [...entry.requiredSurfaces, ...getChallenge(catalog, entry.id).surfaces.optional].includes('mcp')));
});

test('validator fails closed on unknown fields and unsafe authority-bearing properties', () => {
  const catalog = structuredClone(loadChallengeCatalog());
  catalog.entries[0].surfaces.host = 'https://example.invalid';
  const result = validateChallengeCatalog(catalog);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /unsupported property "host"|authority-bearing property "host"/);
});

test('validator rejects dangling adapters and completion-required gate-stopping personas', () => {
  const catalog = structuredClone(loadChallengeCatalog());
  const challenge = getChallenge(catalog, 'release-lumenpath-launch-integration-v1');
  challenge.personaRefs = [{ id: 'jordan', surfaces: ['api', 'ui'] }];
  const result = validateChallengeCatalog(catalog);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /gate-stopping persona "jordan"/);

  challenge.personaRefs = [{ id: 'missing-persona', surfaces: ['api'] }];
  const dangling = validateChallengeCatalog(catalog);
  assert.match(dangling.errors.join('\n'), /unknown persona "missing-persona"/);
});

test('validator requires disposable provenance and independent validation for previews', () => {
  const catalog = structuredClone(loadChallengeCatalog());
  const challenge = getChallenge(catalog, 'homepage-marketing-launch-page-v1');
  challenge.preview.provenance = 'arbitrary-url';
  challenge.evidence.requiredTypes = challenge.evidence.requiredTypes.filter((item) => item !== 'preview-validation');
  const result = validateChallengeCatalog(catalog);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /harness-owned-disposable-project/);
  assert.match(result.errors.join('\n'), /preview-validation/);
});

test('JSON Schema closes the catalog, challenge, claim, and release policy objects', async () => {
  const schema = JSON.parse(await readFile(CHALLENGE_SCHEMA_PATH, 'utf8'));
  assert.equal(schema.additionalProperties, false);
  assert.equal(schema.properties.releasePolicy.additionalProperties, false);
  assert.equal(schema.$defs.challenge.additionalProperties, false);
  assert.equal(schema.$defs.claim.additionalProperties, false);
  assert.equal(schema.$defs.preview.properties.provenance.const, 'harness-owned-disposable-project');
});

test('CLI validate, list, and get return deterministic machine-readable output', () => {
  const cli = path.join(root, 'challenge-catalog.mjs');
  const validate = spawnSync(process.execPath, [cli, 'validate'], { encoding: 'utf8' });
  assert.equal(validate.status, 0, validate.stderr);
  assert.deepEqual(JSON.parse(validate.stdout), {
    valid: true,
    schemaVersion: 'agentweaver.challenge-catalog/v1',
    catalogVersion: 1,
    challengeCount: 44,
  });

  const list = spawnSync(process.execPath, [cli, 'list', '--tier', 'release-integration'], { encoding: 'utf8' });
  assert.equal(list.status, 0, list.stderr);
  assert.ok(JSON.parse(list.stdout).some((entry) => entry.id === 'release-lumenpath-launch-integration-v1'));

  const get = spawnSync(process.execPath, [cli, 'get', 'product-management-full-lifecycle-v1'], { encoding: 'utf8' });
  assert.equal(get.status, 0, get.stderr);
  assert.equal(JSON.parse(get.stdout).id, 'product-management-full-lifecycle-v1');
  assert.equal(CHALLENGE_CATALOG_PATH.endsWith('challenges.v1.json'), true);
});

test('release selector returns only the representative project and directly linked focused coverage', () => {
  const catalog = loadChallengeCatalog();
  const result = selectReleaseChallenges(catalog, {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [{
      id: 'issue-1519',
      refs: ['sabbour/agentweaver#1519'],
      shippedBehaviors: [{
        id: 'repair-contract',
        claimIds: ['release-repair-contract-deterministic-v1'],
        affectedSurfaces: ['api'],
      }],
    }],
  });
  assert.equal(result.ok, true, result.errors.join('\n'));
  assert.equal(result.representativeChallengeId, 'release-lumenpath-launch-integration-v1');
  assert.deepEqual(result.focusedChallenges, [{
    challengeId: 'release-repair-disposition-v1',
    claimIds: ['release-repair-contract-deterministic-v1'],
    requiredSurfaces: ['api'],
    featureIds: ['issue-1519'],
    behaviorIds: ['repair-contract'],
    coverage: [{
      featureId: 'issue-1519',
      behaviorId: 'repair-contract',
      claimId: 'release-repair-contract-deterministic-v1',
      featureRefs: ['sabbour/agentweaver#1519'],
      requiredSurfaces: ['api'],
    }],
  }]);
});

test('release selector links conservative fan generation PRs without substituting for issue 1418 runtime proof', () => {
  const catalog = loadChallengeCatalog();
  const result = selectReleaseChallenges(catalog, {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [{
      id: 'fan-generation-corrections',
      refs: ['sabbour/agentweaver#1591', 'sabbour/agentweaver#1592', 'sabbour/agentweaver#1593'],
      shippedBehaviors: [{
        id: 'conservative-fan-generation',
        claimIds: ['workflow-conservative-fan-generation-v1'],
        affectedSurfaces: ['api'],
      }],
    }],
  });
  assert.equal(result.ok, true, result.errors.join('\n'));
  assert.deepEqual(result.focusedChallenges, [{
    challengeId: 'workflow-conservative-fan-generation-v1',
    claimIds: ['workflow-conservative-fan-generation-v1'],
    requiredSurfaces: ['api'],
    featureIds: ['fan-generation-corrections'],
    behaviorIds: ['conservative-fan-generation'],
    coverage: [{
      featureId: 'fan-generation-corrections',
      behaviorId: 'conservative-fan-generation',
      claimId: 'workflow-conservative-fan-generation-v1',
      featureRefs: ['sabbour/agentweaver#1591', 'sabbour/agentweaver#1592', 'sabbour/agentweaver#1593'],
      requiredSurfaces: ['api'],
    }],
  }]);

  const runtimeIssue = structuredClone(result);
  assert.equal(
    selectReleaseChallenges(catalog, {
      schemaVersion: 'agentweaver.release-feature-manifest/v1',
      release: {
        version: '0.34.0',
        deployedRevision: 'revision-1',
        deploymentIdentity: 'staging-a',
      },
      features: [{
        id: 'issue-1418',
        refs: ['sabbour/agentweaver#1418'],
        shippedBehaviors: [{
          id: 'durable-fan-execution',
          claimIds: ['workflow-conservative-fan-generation-v1'],
          affectedSurfaces: ['api'],
        }],
      }],
    }).ok,
    false,
    JSON.stringify(runtimeIssue),
  );
});

test('full PM lifecycle requires dependency, concurrency, conflict, and validation evidence', () => {
  const catalog = loadChallengeCatalog();
  const challenge = getChallenge(catalog, 'product-management-full-lifecycle-v1');
  const claim = challenge.claims.find((item) => item.id === 'pm-dependent-concurrent-delivery-v1');
  assert.ok(claim);
  assert.deepEqual(
    ['work-plan', 'topology-record', 'repository-revision', 'review-record', 'event-query']
      .filter((type) => !claim.requiredEvidence.includes(type)),
    [],
  );
  assert.ok(challenge.completionRequirements.some((item) => item.includes('concurrent branches')));
  assert.ok(challenge.completionRequirements.some((item) => item.includes('shared-contract conflict')));
  assert.ok(challenge.completionRequirements.some((item) => item.includes('Validate the assembled revision')));
});

test('release selector fails closed on unknown claims, feature-link mismatches, and absent surface support', () => {
  const catalog = loadChallengeCatalog();
  const base = {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [{
      id: 'issue-1519',
      refs: ['sabbour/agentweaver#1519'],
      shippedBehaviors: [{
        id: 'repair-contract',
        claimIds: ['missing-claim-v1'],
        affectedSurfaces: ['api'],
      }],
    }],
  };
  assert.match(selectReleaseChallenges(catalog, base).errors.join('\n'), /no challenge linking claim/);

  const noLink = structuredClone(base);
  noLink.features[0].refs = ['sabbour/agentweaver#9999'];
  noLink.features[0].shippedBehaviors[0].claimIds = ['release-repair-contract-deterministic-v1'];
  assert.match(selectReleaseChallenges(catalog, noLink).errors.join('\n'), /no challenge linking claim/);

  const missingSurface = structuredClone(base);
  missingSurface.features[0].shippedBehaviors[0].claimIds = ['homepage-blog-topic-and-source-binding-v1'];
  missingSurface.features[0].shippedBehaviors[0].affectedSurfaces = ['mcp'];
  assert.match(selectReleaseChallenges(catalog, missingSurface).errors.join('\n'), /lacks required surface coverage: mcp/);
});

test('release selector enforces every nested feature-manifest schema requirement', () => {
  const catalog = loadChallengeCatalog();
  const value = {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [{
      id: 'issue-1519',
      refs: ['sabbour/agentweaver#1519'],
      shippedBehaviors: [{
        id: 'repair-contract',
        claimIds: ['release-repair-contract-deterministic-v1'],
        affectedSurfaces: ['api'],
      }],
    }],
  };
  for (const [target, field] of [
    [value.release, 'deploymentIdentity'],
    [value.features[0], 'refs'],
    [value.features[0].shippedBehaviors[0], 'claimIds'],
    [value.features[0].shippedBehaviors[0], 'affectedSurfaces'],
  ]) {
    const invalid = structuredClone(value);
    const clonedTarget = target === value.release
      ? invalid.release
      : target === value.features[0]
        ? invalid.features[0]
        : invalid.features[0].shippedBehaviors[0];
    delete clonedTarget[field];
    assert.equal(selectReleaseChallenges(catalog, invalid).ok, false, field);
  }
});

test('release selector rejects duplicate feature and behavior identities', () => {
  const catalog = loadChallengeCatalog();
  const feature = {
    id: 'issue-1519',
    refs: ['sabbour/agentweaver#1519'],
    shippedBehaviors: [{
      id: 'repair-contract',
      claimIds: ['release-repair-contract-deterministic-v1'],
      affectedSurfaces: ['api'],
    }],
  };
  const duplicateFeature = selectReleaseChallenges(catalog, {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [feature, structuredClone(feature)],
  });
  assert.match(duplicateFeature.errors.join('\n'), /duplicates feature/);

  const duplicateBehaviorFeature = structuredClone(feature);
  duplicateBehaviorFeature.shippedBehaviors.push(structuredClone(feature.shippedBehaviors[0]));
  const duplicateBehavior = selectReleaseChallenges(catalog, {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: 'revision-1',
      deploymentIdentity: 'staging-a',
    },
    features: [duplicateBehaviorFeature],
  });
  assert.match(duplicateBehavior.errors.join('\n'), /duplicates behavior/);
});
