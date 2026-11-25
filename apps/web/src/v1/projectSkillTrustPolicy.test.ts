import { describe, expect, it } from 'vitest';
import {
  createPlatformPolicyTextBuffer,
  serializePlatformPolicyTextBuffer,
} from './platformPolicyContracts';
import {
  applyProjectSkillTrustScoreUpdate,
  findProjectSkillTrustTarget,
  parseProjectSkillTrustData,
  validateNextSkillTrustScore,
} from './projectSkillTrustPolicy';

describe('projectSkillTrustPolicy', () => {
  it('parses the exact three-field schema in any key order with optional BOM and preserves bytes', () => {
    const raw =
      '\uFEFF{\r\n' +
      '  "requireCompletedEvidence": false,\n' +
      '  "minimumScore": 500,\r' +
      '  "schemaVersion": 1\r\n' +
      '}\n';
    const buffer = createPlatformPolicyTextBuffer(raw);
    const target = findProjectSkillTrustTarget([
      { id: 1, relativePath: 'policy/actions.rego', content: createPlatformPolicyTextBuffer('package x\r\n') },
      { id: 2, relativePath: 'policy/skills/data.json', content: buffer },
    ]);

    expect(target).not.toBeNull();
    expect(target?.fileId).toBe(2);
    expect(target?.minimumScore).toBe(500);
    expect(target?.requireCompletedEvidence).toBe(false);

    const updated = applyProjectSkillTrustScoreUpdate(buffer, target!, '750');
    expect(serializePlatformPolicyTextBuffer(updated)).toBe(
      '\uFEFF{\r\n' +
      '  "requireCompletedEvidence": false,\n' +
      '  "minimumScore": 750,\r' +
      '  "schemaVersion": 1\r\n' +
      '}\n',
    );
  });

  it('rejects duplicate keys, ambiguous number tokens, out-of-range scores, and unsupported layouts', () => {
    const invalidInputs = [
      '{"schemaVersion":1,"minimumScore":500,"minimumScore":600,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":500,"\\u006dinimumScore":600,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":-0,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":-1,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":1001,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":500.0,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":5e2,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":0500,"requireCompletedEvidence":true}',
      '{"schemaVersion":1.0,"minimumScore":500,"requireCompletedEvidence":true}',
      '{"schemaVersion":2,"minimumScore":500,"requireCompletedEvidence":true}',
      '{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":"true"}',
      '{"schemaVersion":1,"minimumScore":500}',
      '{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true,"extra":1}',
      '{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true,} ',
      ' {"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}\uFEFF',
    ];

    for (const input of invalidInputs)
      expect(parseProjectSkillTrustData(input)).toBeNull();

    expect(findProjectSkillTrustTarget([
      {
        id: 1,
        relativePath: 'policy/skills/data.json',
        content: createPlatformPolicyTextBuffer('{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}'),
      },
      {
        id: 2,
        relativePath: 'policy/skills/data.json',
        content: createPlatformPolicyTextBuffer('{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}'),
      },
    ])).toBeNull();
  });

  it('validates whole-number score increases within 0..1000', () => {
    expect(validateNextSkillTrustScore('1', 0)).toEqual({ ok: true, score: 1, token: '1' });
    expect(validateNextSkillTrustScore(' 1000 ', 999)).toEqual({ ok: true, score: 1000, token: '1000' });

    for (const bad of ['', '500', '499', '1001', '-0', '-1', '600.0', '6e2', '0600', 'abc']) {
      expect(validateNextSkillTrustScore(bad, 500).ok).toBe(false);
    }
    expect(validateNextSkillTrustScore('1000', 1000)).toEqual({
      ok: false,
      message: 'The minimum trust score is already 1000.',
    });
  });
});
