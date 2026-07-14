export const VERDICT_SCHEMA = 'agentweaver.persona-judge-verdict/v1';
export const META_AGGREGATE_SCHEMA = 'agentweaver.harness-meta-aggregate/v1';

export const SURFACES = Object.freeze(['api', 'ui', 'mcp']);
export const P0_VERDICTS = Object.freeze(['PASS', 'FAIL', 'CANNOT_DETERMINE']);
export const P1_VERDICTS = Object.freeze(['PASS', 'PARTIAL', 'FAIL', 'CANNOT_DETERMINE']);
export const FRUSTRATION_LEVELS = Object.freeze(['none', 'mild', 'moderate', 'severe', 'abandoned', 'not_assessed']);
export const FRUSTRATION_SCORES = Object.freeze({
  none: 0,
  mild: 1,
  moderate: 2,
  severe: 3,
  abandoned: 4,
  not_assessed: null,
});
export const REQUIRED_JOIN_KEY_FIELDS = Object.freeze([
  'batchId',
  'scenarioId',
  'inputSeed',
  'adapterVersion',
  'personaCoreVersion',
  'targetRevision',
  'surface',
  'runId',
  'timestamp',
]);

function isPlainObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function isNonEmptyString(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function validateFindings(findings, errors) {
  if (!Array.isArray(findings)) {
    errors.push('findings must be an array');
    return;
  }
  for (const [idx, finding] of findings.entries()) {
    if (!isPlainObject(finding)) {
      errors.push(`findings[${idx}] must be an object`);
      continue;
    }
    if (!isNonEmptyString(finding.title)) errors.push(`findings[${idx}].title must be a non-empty string`);
    if (!isNonEmptyString(finding.kind)) errors.push(`findings[${idx}].kind must be a non-empty string`);
    if (finding.evidence != null && !isNonEmptyString(finding.evidence)) {
      errors.push(`findings[${idx}].evidence must be a non-empty string when present`);
    }
  }
}

function validatePushback(pushback, errors) {
  if (!isPlainObject(pushback)) {
    errors.push('pushback must be an object');
    return;
  }
  if (typeof pushback.count !== 'number' || Number.isNaN(pushback.count) || pushback.count < 0) {
    errors.push('pushback.count must be a non-negative number');
  }
  if (typeof pushback.requirementMet !== 'boolean') {
    errors.push('pushback.requirementMet must be a boolean');
  }
  if (pushback.each != null && !Array.isArray(pushback.each)) {
    errors.push('pushback.each must be an array when present');
  }
}

function validateFrustration(frustration, errors) {
  if (!isPlainObject(frustration)) {
    errors.push('frustration must be an object');
    return;
  }
  const level = frustration.level;
  if (!FRUSTRATION_LEVELS.includes(level)) {
    errors.push(`frustration.level must be one of ${FRUSTRATION_LEVELS.join(', ')}`);
  }
  const expectedScore = FRUSTRATION_SCORES[level];
  if (level === 'not_assessed') {
    if (frustration.score !== null) errors.push('frustration.score must be null when level is not_assessed');
  } else if (frustration.score !== expectedScore) {
    errors.push(`frustration.score must equal ${expectedScore} when level is ${level}`);
  }
  if (!Array.isArray(frustration.signals)) {
    errors.push('frustration.signals must be an array');
  } else {
    for (const [idx, signal] of frustration.signals.entries()) {
      if (!isPlainObject(signal)) {
        errors.push(`frustration.signals[${idx}] must be an object`);
        continue;
      }
      if (!isNonEmptyString(signal.kind)) errors.push(`frustration.signals[${idx}].kind must be a non-empty string`);
      if (!isNonEmptyString(signal.evidence)) errors.push(`frustration.signals[${idx}].evidence must be a non-empty string`);
    }
  }
  if (!isNonEmptyString(frustration.rationale)) {
    errors.push('frustration.rationale must be a non-empty string');
  }
}

function validateJoinKey(verdict, errors, expectedMetadata) {
  for (const field of REQUIRED_JOIN_KEY_FIELDS) {
    if (!isNonEmptyString(verdict[field])) {
      errors.push(`${field} must be a non-empty string`);
      continue;
    }
    if (expectedMetadata && expectedMetadata[field] != null && verdict[field] !== expectedMetadata[field]) {
      errors.push(`${field} must equal expected metadata value ${JSON.stringify(expectedMetadata[field])}`);
    }
  }
  if (isNonEmptyString(verdict.surface) && !SURFACES.includes(verdict.surface)) {
    errors.push(`surface must be one of ${SURFACES.join(', ')}`);
  }
  if (isNonEmptyString(verdict.timestamp) && Number.isNaN(Date.parse(verdict.timestamp))) {
    errors.push('timestamp must be an ISO-8601 datetime string');
  }
}

export function extractJoinKey(verdict) {
  const out = {};
  for (const field of REQUIRED_JOIN_KEY_FIELDS) out[field] = verdict?.[field] ?? null;
  return out;
}

export function validateVerdict(verdict, opts = {}) {
  const errors = [];
  if (!isPlainObject(verdict)) {
    return { ok: false, errors: ['verdict must be a JSON object'] };
  }
  if (verdict.schema !== VERDICT_SCHEMA) {
    errors.push(`schema must equal ${VERDICT_SCHEMA}`);
  }

  validateJoinKey(verdict, errors, opts.expectedMetadata);

  if (verdict.persona != null && !isNonEmptyString(verdict.persona)) {
    errors.push('persona must be a non-empty string when present');
  }

  if (!isPlainObject(verdict.p0) || !P0_VERDICTS.includes(verdict.p0?.verdict)) {
    errors.push(`p0.verdict must be one of ${P0_VERDICTS.join(', ')}`);
  }
  if (verdict.p0?.evidence != null && !isNonEmptyString(verdict.p0.evidence)) {
    errors.push('p0.evidence must be a non-empty string when present');
  }

  if (!isPlainObject(verdict.p1) || !P1_VERDICTS.includes(verdict.p1?.verdict)) {
    errors.push(`p1.verdict must be one of ${P1_VERDICTS.join(', ')}`);
  }
  if (verdict.p1?.evidence != null && !isNonEmptyString(verdict.p1.evidence)) {
    errors.push('p1.evidence must be a non-empty string when present');
  }
  if (verdict.p1?.criteriaCoverage != null && !Array.isArray(verdict.p1.criteriaCoverage)) {
    errors.push('p1.criteriaCoverage must be an array when present');
  }

  validateFrustration(verdict.frustration, errors);
  validatePushback(verdict.pushback, errors);
  validateFindings(verdict.findings, errors);

  if (!Array.isArray(verdict.cannotDetermine)) {
    errors.push('cannotDetermine must be an array');
  } else {
    for (const [idx, item] of verdict.cannotDetermine.entries()) {
      if (!isNonEmptyString(item)) errors.push(`cannotDetermine[${idx}] must be a non-empty string`);
    }
  }

  if (verdict.judgeError != null) {
    if (!isPlainObject(verdict.judgeError)) {
      errors.push('judgeError must be an object when present');
    } else {
      if (!isNonEmptyString(verdict.judgeError.kind)) errors.push('judgeError.kind must be a non-empty string');
      if (!isNonEmptyString(verdict.judgeError.message)) errors.push('judgeError.message must be a non-empty string');
      if (verdict.judgeError.stderrTail != null && typeof verdict.judgeError.stderrTail !== 'string') {
        errors.push('judgeError.stderrTail must be a string when present');
      }
    }
  }

  return { ok: errors.length === 0, errors };
}
