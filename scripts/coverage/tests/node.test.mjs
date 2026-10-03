import assert from 'node:assert/strict';
import test from 'node:test';
import { hasValidatorCoverage } from '../node.mjs';

test('only an executed release validator counts as coverage', () => {
  assert.equal(hasValidatorCoverage(''), false);
  assert.equal(hasValidatorCoverage('SF:scripts/release/tests/validator.test.mjs\nDA:1,1\nend_of_record'), false);
  assert.equal(hasValidatorCoverage('SF:scripts/release/validate.mjs\nDA:1,0\nend_of_record'), false);
  assert.equal(hasValidatorCoverage('SF:scripts/release/validate.mjs\nDA:1,1\nend_of_record'), true);
});
