import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyPromptCapacityResults } from '../verify-agenthost-native-prompt-capacity-trx.mjs';

const expected = [
  'ByokNativeRuntimeCompactsRemovableHistoryWithinTheAcceptedCapacity',
  'ByokNativeRuntimeReportsCapacityAttributedQueryErrorForIrreduciblePrompt',
];

function result(name, outcome) {
  return `<UnitTestResult testName="Agentweaver.Identity.Broker.Tests.RuntimeNativePromptCapacityTests.${name}" outcome="${outcome}" />`;
}

function trx(...results) {
  return `<TestRun><Results>${results.join('')}</Results></TestRun>`;
}

test('accepts exactly the two expected Passed results with a successful runner', () => {
  const content = trx(...expected.map(name => result(name, 'Passed')));
  assert.deepEqual(verifyPromptCapacityResults(content, 0), expected.map(name => ({
    name,
    testName: `Agentweaver.Identity.Broker.Tests.RuntimeNativePromptCapacityTests.${name}`,
    outcome: 'Passed',
  })));
});

test('rejects a missing expected test result', () => {
  assert.throws(
    () => verifyPromptCapacityResults(trx(result(expected[0], 'Passed')), 0),
    /expected exactly one result, found 0/,
  );
});

test('rejects a skipped expected test result', () => {
  assert.throws(
    () => verifyPromptCapacityResults(
      trx(result(expected[0], 'Passed'), result(expected[1], 'NotExecuted')), 0),
    /expected Passed, got NotExecuted/,
  );
});

test('rejects duplicate expected test results', () => {
  assert.throws(
    () => verifyPromptCapacityResults(
      trx(result(expected[0], 'Passed'), result(expected[0], 'Passed'), result(expected[1], 'Passed')), 0),
    /expected exactly one result, found 2/,
  );
});

test('rejects unexpected test results', () => {
  assert.throws(
    () => verifyPromptCapacityResults(
      trx(...expected.map(name => result(name, 'Passed')), result('UnexpectedNativeTest', 'Passed')), 0),
    /Unexpected test results/,
  );
});

test('rejects a nonzero runner exit code', () => {
  assert.throws(
    () => verifyPromptCapacityResults(trx(...expected.map(name => result(name, 'Passed'))), 1),
    /Native test runner exited with code 1/,
  );
});

test('rejects a missing runner exit code', () => {
  assert.throws(
    () => verifyPromptCapacityResults(trx(...expected.map(name => result(name, 'Passed'))), undefined),
    /Native test runner exited with code \(missing\)/,
  );
});
