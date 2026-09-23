#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { loadCatalog as loadPersonaCatalog } from './find-similar.mjs';
import { validateJsonSchema } from './schema-validator.mjs';

export const PACKAGE_DIR = path.dirname(fileURLToPath(import.meta.url));
export const CHALLENGE_CATALOG_PATH = path.join(PACKAGE_DIR, 'challenges.v1.json');
export const CHALLENGE_SCHEMA_PATH = path.join(PACKAGE_DIR, 'challenge-catalog-v1.schema.json');
export const RELEASE_FEATURE_SCHEMA_PATH = path.join(
  PACKAGE_DIR,
  'release-feature-manifest-v1.schema.json',
);

const SURFACES = new Set(['api', 'ui', 'mcp']);
const EXECUTION_CONTRACTS = new Set(['dynamic-human-v1', 'structural-observability-v1']);
const TIERS = new Set(['fast-smoke', 'release-integration', 'nightly', 'manual']);
const PRIORITIES = new Set(['P0', 'P1']);
const RISK_CLASSES = new Set(['read-only', 'reversible-owned', 'destructive-owned', 'staging-only']);
const UNSUPPORTED = new Set([
  'capability-not-advertised',
  'surface-not-enabled',
  'provider-not-configured',
  'environment-not-eligible',
]);
const EVIDENCE_TYPES = new Set([
  'deployed-release-revision',
  'project-record',
  'run-record',
  'outcome-spec-revision',
  'work-plan',
  'topology-record',
  'event-query',
  'surface-transcript',
  'artifact-file',
  'artifact-hash',
  'repository-revision',
  'memory-record',
  'decision-record',
  'review-record',
  'preview-publication',
  'preview-validation',
  'source-manifest',
  'backlog-record',
  'usage-record',
  'cleanup-record',
  'deployment-record',
  'authorization-record',
  'structural-validation',
]);
const EVIDENCE_BINDINGS = new Set(['revision', 'project', 'run', 'artifact', 'surface']);
const FORBIDDEN_AUTHORITY_KEYS = new Set([
  'host',
  'hostname',
  'baseUrl',
  'credentials',
  'credential',
  'token',
  'projectId',
  'command',
  'commands',
  'approval',
  'approvals',
  'githubAction',
  'githubActions',
  'deploymentMutation',
]);
const CATALOG_KEYS = new Set([
  '$schema',
  'schemaVersion',
  'catalogVersion',
  'generatedNote',
  'authority',
  'tiers',
  'releasePolicy',
  'entries',
]);
const ENTRY_KEYS = new Set([
  'id',
  'version',
  'title',
  'summary',
  'family',
  'tags',
  'executionContract',
  'completionRequired',
  'personaRefs',
  'surfaces',
  'claims',
  'seams',
  'prerequisiteCapabilities',
  'completionRequirements',
  'riskClass',
  'evidence',
  'preview',
  'publication',
  'cadence',
  'featureRefs',
  'unsupportedClassifications',
  'lastVerifiedRelease',
]);

function isObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function nonEmpty(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function assertClosedObject(value, allowed, label, errors) {
  if (!isObject(value)) {
    errors.push(`${label} must be an object`);
    return false;
  }
  for (const key of Object.keys(value)) {
    if (!allowed.has(key)) errors.push(`${label} contains unsupported property "${key}"`);
  }
  return true;
}

function validateStringArray(value, label, errors, allowed = null, { min = 1 } = {}) {
  if (!Array.isArray(value) || value.length < min) {
    errors.push(`${label} must be an array with at least ${min} item(s)`);
    return;
  }
  const seen = new Set();
  for (const [index, item] of value.entries()) {
    if (!nonEmpty(item)) errors.push(`${label}[${index}] must be a non-empty string`);
    else if (seen.has(item)) errors.push(`${label} contains duplicate "${item}"`);
    else seen.add(item);
    if (allowed && nonEmpty(item) && !allowed.has(item)) {
      errors.push(`${label}[${index}] must be one of ${[...allowed].join(', ')}`);
    }
  }
}

function findForbiddenKeys(value, label, errors) {
  if (Array.isArray(value)) {
    value.forEach((item, index) => findForbiddenKeys(item, `${label}[${index}]`, errors));
    return;
  }
  if (!isObject(value)) return;
  for (const [key, child] of Object.entries(value)) {
    if (FORBIDDEN_AUTHORITY_KEYS.has(key)) {
      errors.push(`${label} contains authority-bearing property "${key}"`);
    }
    findForbiddenKeys(child, `${label}.${key}`, errors);
  }
}

function loadJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

export function loadChallengeCatalog(catalogPath = CHALLENGE_CATALOG_PATH) {
  return loadJson(catalogPath);
}

function validatePersonaRefs(entry, personas, errors) {
  if (!Array.isArray(entry.personaRefs)) {
    errors.push(`${entry.id}.personaRefs must be an array`);
    return;
  }
  for (const [index, ref] of entry.personaRefs.entries()) {
    const label = `${entry.id}.personaRefs[${index}]`;
    if (!assertClosedObject(ref, new Set(['id', 'surfaces']), label, errors)) continue;
    if (!nonEmpty(ref.id)) {
      errors.push(`${label}.id must be a non-empty string`);
      continue;
    }
    const persona = personas.get(ref.id);
    if (!persona) {
      errors.push(`${label}.id references unknown persona "${ref.id}"`);
      continue;
    }
    validateStringArray(ref.surfaces, `${label}.surfaces`, errors, SURFACES);
    for (const surface of ref.surfaces ?? []) {
      if (!(persona.surfaces ?? []).includes(surface)) {
        errors.push(`${label} references missing adapter "${ref.id}.${surface}"`);
      } else if (!fs.existsSync(path.join(PACKAGE_DIR, 'surfaces', `${ref.id}.${surface}.md`))) {
        errors.push(`${label} references adapter file that does not exist: "${ref.id}.${surface}.md"`);
      }
    }
    if (entry.completionRequired && persona.runsToCompletion !== true) {
      errors.push(`${label} uses gate-stopping persona "${ref.id}" for a completion-required challenge`);
    }
  }
}

function validateSurfaces(entry, errors) {
  const label = `${entry.id}.surfaces`;
  if (!assertClosedObject(entry.surfaces, new Set(['required', 'optional']), label, errors)) return;
  validateStringArray(entry.surfaces.required, `${label}.required`, errors, SURFACES);
  validateStringArray(entry.surfaces.optional, `${label}.optional`, errors, SURFACES, { min: 0 });
  const required = new Set(entry.surfaces.required ?? []);
  for (const surface of entry.surfaces.optional ?? []) {
    if (required.has(surface)) errors.push(`${label} repeats "${surface}" as required and optional`);
  }
}

function validateClaims(entry, errors) {
  if (!Array.isArray(entry.claims) || entry.claims.length === 0) {
    errors.push(`${entry.id}.claims must be a non-empty array`);
    return;
  }
  const ids = new Set();
  for (const [index, claim] of entry.claims.entries()) {
    const label = `${entry.id}.claims[${index}]`;
    if (!assertClosedObject(claim, new Set(['id', 'priority', 'statement', 'requiredEvidence']), label, errors)) continue;
    if (!nonEmpty(claim.id)) errors.push(`${label}.id must be a non-empty string`);
    else if (ids.has(claim.id)) errors.push(`${entry.id}.claims contains duplicate id "${claim.id}"`);
    else ids.add(claim.id);
    if (!PRIORITIES.has(claim.priority)) errors.push(`${label}.priority must be P0 or P1`);
    if (!nonEmpty(claim.statement)) errors.push(`${label}.statement must be a non-empty quoted data string`);
    validateStringArray(claim.requiredEvidence, `${label}.requiredEvidence`, errors, EVIDENCE_TYPES);
  }
}

function validateEvidence(entry, errors) {
  const label = `${entry.id}.evidence`;
  if (!assertClosedObject(
    entry.evidence,
    new Set(['requiredTypes', 'requiredBindings', 'structuralEvidenceCanSatisfy']),
    label,
    errors,
  )) return;
  validateStringArray(entry.evidence.requiredTypes, `${label}.requiredTypes`, errors, EVIDENCE_TYPES);
  validateStringArray(entry.evidence.requiredBindings, `${label}.requiredBindings`, errors, EVIDENCE_BINDINGS);
  if (typeof entry.evidence.structuralEvidenceCanSatisfy !== 'boolean') {
    errors.push(`${label}.structuralEvidenceCanSatisfy must be a boolean`);
  }
  if (entry.executionContract === 'dynamic-human-v1') {
    for (const binding of ['revision', 'project', 'run']) {
      if (!(entry.evidence.requiredBindings ?? []).includes(binding)) {
        errors.push(`${label}.requiredBindings must include "${binding}" for actual execution`);
      }
    }
    if (entry.evidence.structuralEvidenceCanSatisfy !== false) {
      errors.push(`${label}.structuralEvidenceCanSatisfy must be false for actual execution`);
    }
  }
}

function validatePreview(entry, errors) {
  if (!entry.preview) return;
  const label = `${entry.id}.preview`;
  if (!assertClosedObject(
    entry.preview,
    new Set(['required', 'provenance', 'independentValidation']),
    label,
    errors,
  )) return;
  if (entry.preview.required !== true) errors.push(`${label}.required must be true when preview is declared`);
  if (entry.preview.provenance !== 'harness-owned-disposable-project') {
    errors.push(`${label}.provenance must be "harness-owned-disposable-project"`);
  }
  if (entry.preview.independentValidation !== true) {
    errors.push(`${label}.independentValidation must be true`);
  }
  for (const type of ['preview-publication', 'preview-validation']) {
    if (!(entry.evidence?.requiredTypes ?? []).includes(type)) {
      errors.push(`${entry.id}.evidence.requiredTypes must include "${type}" for preview challenges`);
    }
  }
}

function validateCadence(entry, errors) {
  const label = `${entry.id}.cadence`;
  if (!assertClosedObject(
    entry.cadence,
    new Set(['eligibleTiers', 'defaultTier', 'requiredOnRelease', 'selectionTags']),
    label,
    errors,
  )) return;
  validateStringArray(entry.cadence.eligibleTiers, `${label}.eligibleTiers`, errors, TIERS);
  if (!TIERS.has(entry.cadence.defaultTier)) errors.push(`${label}.defaultTier is unsupported`);
  if (!(entry.cadence.eligibleTiers ?? []).includes(entry.cadence.defaultTier)) {
    errors.push(`${label}.defaultTier must be included in eligibleTiers`);
  }
  if (typeof entry.cadence.requiredOnRelease !== 'boolean') {
    errors.push(`${label}.requiredOnRelease must be a boolean`);
  }
  validateStringArray(entry.cadence.selectionTags, `${label}.selectionTags`, errors);
}

function validateEntry(entry, personas, errors) {
  if (!assertClosedObject(entry, ENTRY_KEYS, 'challenge entry', errors)) return;
  const label = nonEmpty(entry.id) ? entry.id : 'challenge entry';
  if (!/^[a-z0-9][a-z0-9-]*-v[0-9]+$/.test(entry.id ?? '')) errors.push(`${label}.id must end in a version suffix such as -v1`);
  if (entry.version !== 1) errors.push(`${label}.version must equal 1`);
  for (const field of ['title', 'summary', 'family']) {
    if (!nonEmpty(entry[field])) errors.push(`${label}.${field} must be a non-empty string`);
  }
  validateStringArray(entry.tags, `${label}.tags`, errors);
  if (!EXECUTION_CONTRACTS.has(entry.executionContract)) errors.push(`${label}.executionContract is unsupported`);
  if (typeof entry.completionRequired !== 'boolean') errors.push(`${label}.completionRequired must be a boolean`);
  validatePersonaRefs(entry, personas, errors);
  validateSurfaces(entry, errors);
  validateClaims(entry, errors);
  validateStringArray(entry.seams, `${label}.seams`, errors);
  validateStringArray(entry.prerequisiteCapabilities, `${label}.prerequisiteCapabilities`, errors);
  validateStringArray(entry.completionRequirements, `${label}.completionRequirements`, errors);
  if (!RISK_CLASSES.has(entry.riskClass)) errors.push(`${label}.riskClass is unsupported`);
  validateEvidence(entry, errors);
  validatePreview(entry, errors);
  if (!['none', 'internal-artifact-only'].includes(entry.publication)) {
    errors.push(`${label}.publication must be "none" or "internal-artifact-only"`);
  }
  validateCadence(entry, errors);
  validateStringArray(entry.featureRefs, `${label}.featureRefs`, errors, null, { min: 0 });
  validateStringArray(
    entry.unsupportedClassifications,
    `${label}.unsupportedClassifications`,
    errors,
    UNSUPPORTED,
  );
  if (entry.lastVerifiedRelease !== null && !nonEmpty(entry.lastVerifiedRelease)) {
    errors.push(`${label}.lastVerifiedRelease must be null or a non-empty string`);
  }
}

export function validateChallengeCatalog(catalog, { personaEntries = null } = {}) {
  const errors = [];
  if (!assertClosedObject(catalog, CATALOG_KEYS, 'catalog', errors)) return { ok: false, errors };
  findForbiddenKeys(catalog, 'catalog', errors);
  if (catalog.schemaVersion !== 'agentweaver.challenge-catalog/v1') {
    errors.push('catalog.schemaVersion must equal agentweaver.challenge-catalog/v1');
  }
  if (catalog.catalogVersion !== 1) errors.push('catalog.catalogVersion must equal 1');
  if (!nonEmpty(catalog.generatedNote)) errors.push('catalog.generatedNote must be a non-empty string');
  if (!isObject(catalog.authority) || catalog.authority.prose !== 'quoted-untrusted-data') {
    errors.push('catalog.authority.prose must equal quoted-untrusted-data');
  }
  if (catalog.authority?.environmentOwner !== 'harness-runtime') {
    errors.push('catalog.authority.environmentOwner must equal harness-runtime');
  }
  const forbiddenAuthority = catalog.authority?.forbiddenCatalogAuthority;
  if (!Array.isArray(forbiddenAuthority) || forbiddenAuthority.length !== 7) {
    errors.push('catalog.authority.forbiddenCatalogAuthority must declare all seven forbidden authority classes');
  }
  if (!Array.isArray(catalog.tiers) || catalog.tiers.length !== 4) {
    errors.push('catalog.tiers must declare exactly four execution tiers');
  } else {
    const tierIds = new Set(catalog.tiers.map((tier) => tier?.id));
    for (const tier of TIERS) if (!tierIds.has(tier)) errors.push(`catalog.tiers is missing "${tier}"`);
    for (const [index, tier] of catalog.tiers.entries()) {
      const label = `catalog.tiers[${index}]`;
      if (!assertClosedObject(
        tier,
        new Set([
          'id',
          'description',
          'fullCatalog',
          'wallClockMinutesMax',
          'aiCreditsMax',
          'maxConcurrentAgents',
          'maxChildRuns',
        ]),
        label,
        errors,
      )) continue;
      if (!TIERS.has(tier.id)) errors.push(`${label}.id is unsupported`);
      if (!nonEmpty(tier.description)) errors.push(`${label}.description must be non-empty`);
      if (tier.fullCatalog !== false) errors.push(`${label}.fullCatalog must be false`);
      for (const field of ['wallClockMinutesMax', 'aiCreditsMax', 'maxConcurrentAgents', 'maxChildRuns']) {
        if (typeof tier[field] !== 'number' || tier[field] <= 0) errors.push(`${label}.${field} must be positive`);
      }
    }
  }
  if (!assertClosedObject(
    catalog.authority,
    new Set(['prose', 'environmentOwner', 'forbiddenCatalogAuthority']),
    'catalog.authority',
    errors,
  )) {
    // The earlier authority checks report the required values.
  }
  if (!assertClosedObject(
    catalog.releasePolicy,
    new Set([
      'representativeChallengeId',
      'runFullCatalogPerRelease',
      'focusedFeatureAcceptanceRequired',
      'surfaceSelection',
      'abnormalResultContract',
    ]),
    'catalog.releasePolicy',
    errors,
  )) {
    errors.push('catalog.releasePolicy must be an object');
  }
  if (!Array.isArray(catalog.entries) || catalog.entries.length === 0) {
    errors.push('catalog.entries must be a non-empty array');
    return { ok: false, errors };
  }

  const personaList = personaEntries ?? loadPersonaCatalog();
  const personas = new Map(personaList.map((entry) => [entry.id, entry]));
  const ids = new Set();
  for (const entry of catalog.entries) {
    if (ids.has(entry?.id)) errors.push(`catalog.entries contains duplicate id "${entry.id}"`);
    else if (entry?.id) ids.add(entry.id);
    validateEntry(entry, personas, errors);
  }
  const representativeId = catalog.releasePolicy?.representativeChallengeId;
  const representative = catalog.entries.find((entry) => entry.id === representativeId);
  if (!representative) errors.push('catalog.releasePolicy.representativeChallengeId must reference an entry');
  else if (!representative.cadence.requiredOnRelease || representative.cadence.defaultTier !== 'release-integration') {
    errors.push('representative release challenge must be required on release and default to release-integration');
  }
  if (catalog.releasePolicy?.runFullCatalogPerRelease !== false) {
    errors.push('catalog.releasePolicy.runFullCatalogPerRelease must be false');
  }
  if (catalog.releasePolicy?.focusedFeatureAcceptanceRequired !== true) {
    errors.push('catalog.releasePolicy.focusedFeatureAcceptanceRequired must be true');
  }
  if (catalog.releasePolicy?.surfaceSelection !== 'affected-surfaces-fail-closed') {
    errors.push('catalog.releasePolicy.surfaceSelection must be affected-surfaces-fail-closed');
  }
  if (catalog.releasePolicy?.abnormalResultContract !== 'agentweaver.release-acceptance-result/v1') {
    errors.push('catalog.releasePolicy.abnormalResultContract is unsupported');
  }
  return { ok: errors.length === 0, errors };
}

export function listChallenges(catalog, { tier = null, surface = null } = {}) {
  if (tier && !TIERS.has(tier)) throw new Error(`Unsupported tier "${tier}"`);
  if (surface && !SURFACES.has(surface)) throw new Error(`Unsupported surface "${surface}"`);
  return catalog.entries
    .filter((entry) => !tier || entry.cadence.eligibleTiers.includes(tier))
    .filter((entry) => !surface || [...entry.surfaces.required, ...entry.surfaces.optional].includes(surface))
    .map((entry) => ({
      id: entry.id,
      title: entry.title,
      family: entry.family,
      executionContract: entry.executionContract,
      requiredSurfaces: entry.surfaces.required,
      defaultTier: entry.cadence.defaultTier,
      requiredOnRelease: entry.cadence.requiredOnRelease,
    }))
    .sort((a, b) => a.id.localeCompare(b.id));
}

export function getChallenge(catalog, id) {
  return catalog.entries.find((entry) => entry.id === id) ?? null;
}

export function validateReleaseFeatureManifest(manifest) {
  return validateJsonSchema(RELEASE_FEATURE_SCHEMA_PATH, manifest, 'release manifest');
}

export function selectReleaseChallenges(catalog, manifest) {
  const schemaValidation = validateReleaseFeatureManifest(manifest);
  if (!schemaValidation.ok) {
    return {
      ok: false,
      errors: schemaValidation.errors,
      release: manifest?.release ?? null,
      representativeChallengeId: null,
      focusedChallenges: [],
    };
  }
  const errors = [];

  const focused = new Map();
  for (const [featureIndex, feature] of (manifest?.features ?? []).entries()) {
    const label = `features[${featureIndex}]`;
    if (!nonEmpty(feature?.id)) errors.push(`${label}.id is required`);
    if (!Array.isArray(feature?.refs) || feature.refs.length === 0) errors.push(`${label}.refs must be non-empty`);
    if (!Array.isArray(feature?.shippedBehaviors) || feature.shippedBehaviors.length === 0) {
      errors.push(`${label}.shippedBehaviors must be non-empty`);
      continue;
    }
    for (const [behaviorIndex, behavior] of feature.shippedBehaviors.entries()) {
      const behaviorLabel = `${label}.shippedBehaviors[${behaviorIndex}]`;
      if (!nonEmpty(behavior?.id)) errors.push(`${behaviorLabel}.id is required`);
      validateStringArray(behavior?.claimIds, `${behaviorLabel}.claimIds`, errors);
      validateStringArray(behavior?.affectedSurfaces, `${behaviorLabel}.affectedSurfaces`, errors, SURFACES);
      for (const claimId of behavior?.claimIds ?? []) {
        const candidates = catalog.entries
          .filter((entry) => entry.claims.some((claim) => claim.id === claimId))
          .filter((entry) => entry.featureRefs.some((ref) => feature.refs.includes(ref)))
          .sort((a, b) => a.id.localeCompare(b.id));
        if (candidates.length === 0) {
          errors.push(`${behaviorLabel} has no challenge linking claim "${claimId}" to feature refs`);
          continue;
        }
        const challenge = candidates[0];
        const supported = new Set([...challenge.surfaces.required, ...challenge.surfaces.optional]);
        const requiredSurfaces = [...new Set([
          ...challenge.surfaces.required,
          ...(behavior.affectedSurfaces ?? []),
        ])].sort();
        const missing = requiredSurfaces.filter((surface) => !supported.has(surface));
        if (missing.length) {
          errors.push(`${behaviorLabel} claim "${claimId}" lacks required surface coverage: ${missing.join(', ')}`);
          continue;
        }
        const selected = focused.get(challenge.id) ?? {
          challengeId: challenge.id,
          claimIds: [],
          requiredSurfaces: [],
          featureIds: [],
          behaviorIds: [],
          coverage: [],
        };
        if (!selected.claimIds.includes(claimId)) selected.claimIds.push(claimId);
        for (const surface of requiredSurfaces) {
          if (!selected.requiredSurfaces.includes(surface)) selected.requiredSurfaces.push(surface);
        }
        if (!selected.featureIds.includes(feature.id)) selected.featureIds.push(feature.id);
        if (!selected.behaviorIds.includes(behavior.id)) selected.behaviorIds.push(behavior.id);
        selected.coverage.push({
          featureId: feature.id,
          behaviorId: behavior.id,
          claimId,
          requiredSurfaces,
        });
        focused.set(challenge.id, selected);
      }
    }
  }

  const representative = getChallenge(catalog, catalog.releasePolicy.representativeChallengeId);
  if (!representative) errors.push('representative release challenge is missing');
  const focusedChallenges = [...focused.values()]
    .map((item) => ({
      ...item,
      claimIds: item.claimIds.sort(),
      requiredSurfaces: item.requiredSurfaces.sort(),
      featureIds: item.featureIds.sort(),
      behaviorIds: item.behaviorIds.sort(),
      coverage: item.coverage.sort((a, b) =>
        `${a.featureId}/${a.behaviorId}/${a.claimId}`
          .localeCompare(`${b.featureId}/${b.behaviorId}/${b.claimId}`)),
    }))
    .sort((a, b) => a.challengeId.localeCompare(b.challengeId));
  return {
    ok: errors.length === 0,
    errors,
    release: manifest?.release ?? null,
    representativeChallengeId: representative?.id ?? null,
    focusedChallenges,
  };
}

function usage() {
  return [
    'usage:',
    '  node challenge-catalog.mjs validate [--catalog <path>]',
    '  node challenge-catalog.mjs list [--tier <tier>] [--surface <surface>] [--catalog <path>]',
    '  node challenge-catalog.mjs get <challenge-id> [--catalog <path>]',
    '  node challenge-catalog.mjs select-release --manifest <path> [--catalog <path>]',
  ].join('\n');
}

function take(args, flag) {
  const index = args.indexOf(flag);
  if (index < 0) return null;
  const value = args[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`${flag} requires a value`);
  args.splice(index, 2);
  return value;
}

async function main() {
  const args = process.argv.slice(2);
  const command = args.shift();
  const catalogPath = take(args, '--catalog') ?? CHALLENGE_CATALOG_PATH;
  const catalog = loadChallengeCatalog(catalogPath);
  if (command === 'validate') {
    if (args.length) throw new Error(usage());
    const result = validateChallengeCatalog(catalog);
    if (!result.ok) {
      process.stderr.write(`${result.errors.join('\n')}\n`);
      process.exitCode = 1;
      return;
    }
    process.stdout.write(`${JSON.stringify({
      valid: true,
      schemaVersion: catalog.schemaVersion,
      catalogVersion: catalog.catalogVersion,
      challengeCount: catalog.entries.length,
    })}\n`);
    return;
  }
  if (command === 'list') {
    const tier = take(args, '--tier');
    const surface = take(args, '--surface');
    if (args.length) throw new Error(usage());
    process.stdout.write(`${JSON.stringify(listChallenges(catalog, { tier, surface }), null, 2)}\n`);
    return;
  }
  if (command === 'get') {
    const id = args.shift();
    if (!id || args.length) throw new Error(usage());
    const challenge = getChallenge(catalog, id);
    if (!challenge) {
      process.stderr.write(`Challenge not found: ${id}\n`);
      process.exitCode = 1;
      return;
    }
    process.stdout.write(`${JSON.stringify(challenge, null, 2)}\n`);
    return;
  }
  if (command === 'select-release') {
    const manifestPath = take(args, '--manifest');
    if (!manifestPath || args.length) throw new Error(usage());
    const result = selectReleaseChallenges(catalog, loadJson(manifestPath));
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
    if (!result.ok) process.exitCode = 1;
    return;
  }
  throw new Error(usage());
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 2;
  });
}
