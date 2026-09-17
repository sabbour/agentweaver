#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const KIND = 'agentweaver.ponytail-review/v1';
const SHA = /^[0-9a-f]{40}$/iu;

function requiredString(value, field) {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new Error(`${field} must be a non-empty string`);
  }
  return value.trim();
}

function requiredStringArray(value, field) {
  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`${field} must be a non-empty array`);
  }
  return value.map((item, index) => requiredString(item, `${field}[${index}]`));
}

export function validatePonytailGate(gate, expectedTip) {
  if (!gate || typeof gate !== 'object' || Array.isArray(gate)) {
    throw new Error('gate must be a JSON object');
  }
  if (gate.kind !== KIND) {
    throw new Error(`kind must be ${KIND}`);
  }

  const headSha = requiredString(gate.head_sha, 'head_sha');
  if (!SHA.test(headSha)) throw new Error('head_sha must be a 40-character commit SHA');
  if (expectedTip && headSha.toLowerCase() !== expectedTip.toLowerCase()) {
    throw new Error(`head_sha ${headSha} does not match expected tip ${expectedTip}`);
  }

  const implementer = requiredString(gate.implementer, 'implementer');
  const reviewer = requiredString(gate.reviewer, 'reviewer');
  if (implementer.localeCompare(reviewer, undefined, { sensitivity: 'accent' }) === 0) {
    throw new Error('reviewer must be independent from implementer');
  }

  requiredStringArray(gate.implementer_validation?.commands, 'implementer_validation.commands');

  requiredStringArray(gate.rubber_duck?.assumptions, 'rubber_duck.assumptions');
  requiredStringArray(gate.rubber_duck?.simplifications, 'rubber_duck.simplifications');
  requiredString(gate.rubber_duck?.flaw, 'rubber_duck.flaw');

  if (!Array.isArray(gate.findings)) throw new Error('findings must be an array');
  const findingIds = new Set();
  const unwaivedHighConfidence = [];
  for (const [index, finding] of gate.findings.entries()) {
    const prefix = `findings[${index}]`;
    const id = requiredString(finding?.id, `${prefix}.id`);
    if (findingIds.has(id)) throw new Error(`duplicate finding id: ${id}`);
    findingIds.add(id);
    if (!['low', 'medium', 'high'].includes(finding.confidence)) {
      throw new Error(`${prefix}.confidence must be low, medium, or high`);
    }
    requiredString(finding.summary, `${prefix}.summary`);
    requiredString(finding.location, `${prefix}.location`);

    if (finding.waiver != null) {
      requiredString(finding.waiver.justification, `${prefix}.waiver.justification`);
      requiredString(finding.waiver.approved_by, `${prefix}.waiver.approved_by`);
    } else if (finding.confidence === 'high') {
      unwaivedHighConfidence.push(id);
    }
  }

  return {
    admitted: unwaivedHighConfidence.length === 0,
    headSha,
    unwaivedHighConfidence,
  };
}

function parseArgs(argv) {
  const file = argv[0];
  const expectedIndex = argv.indexOf('--expect-tip');
  const expectedTip = expectedIndex >= 0 ? argv[expectedIndex + 1] : null;
  if (!file) throw new Error('usage: ponytail-gate.mjs <gate.json> --expect-tip <sha>');
  if (!expectedTip) throw new Error('--expect-tip requires a commit SHA');
  return { file, expectedTip };
}

function main() {
  const { file, expectedTip } = parseArgs(process.argv.slice(2));
  const gate = JSON.parse(readFileSync(file, 'utf8'));
  const result = validatePonytailGate(gate, expectedTip);
  if (!result.admitted) {
    throw new Error(`Ponytail admission blocked by: ${result.unwaivedHighConfidence.join(', ')}`);
  }
  console.log(`Ponytail admission passed for ${result.headSha}.`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
