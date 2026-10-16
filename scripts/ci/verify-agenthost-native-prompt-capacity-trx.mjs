import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const expectedTests = [
  'ByokNativeRuntimeCompactsRemovableHistoryWithinTheAcceptedCapacity',
  'ByokNativeRuntimeReportsCapacityAttributedQueryErrorForIrreduciblePrompt',
];

/** Require one passing TRX result for each pinned native prompt-capacity test and no extras. */
export function verifyPromptCapacityResults(xml, runnerExitCode) {
  if (typeof xml !== 'string') throw new TypeError('TRX content must be a string.');

  const results = [...xml.matchAll(/<UnitTestResult\b([^>]*)\/?>/g)].map(([, source]) => {
    const attributes = new Map();
    for (const [, name, value] of source.matchAll(/([A-Za-z_][\w:.-]*)="([^"]*)"/g)) {
      attributes.set(name, value);
    }
    return {
      testName: attributes.get('testName') ?? '',
      outcome: attributes.get('outcome') ?? '',
    };
  });
  const matched = expectedTests.map(name => ({
    name,
    results: results.filter(result =>
      result.testName.endsWith(`RuntimeNativePromptCapacityTests.${name}`)),
  }));
  const unexpected = results.filter(result =>
    !matched.some(test => test.results.includes(result)));
  const problems = [];
  for (const test of matched) {
    if (test.results.length !== 1) {
      problems.push(`${test.name}: expected exactly one result, found ${test.results.length}`);
    } else if (test.results[0].outcome.toLowerCase() !== 'passed') {
      problems.push(`${test.name}: expected Passed, got ${test.results[0].outcome || '(missing outcome)'}`);
    }
  }
  if (unexpected.length) {
    problems.push(`Unexpected test results: ${unexpected.map(result =>
      `${result.testName || '(missing name)'}=${result.outcome || '(missing outcome)'}`).join(', ')}`);
  }

  if (runnerExitCode !== 0) {
    problems.push(`Native test runner exited with code ${runnerExitCode ?? '(missing)'}`);
  }
  if (problems.length) {
    throw new Error(`Pinned native prompt-capacity result verification failed:\n${problems.join('\n')}`);
  }
  return matched.map(test => ({
    name: test.name,
    testName: test.results[0].testName,
    outcome: test.results[0].outcome,
  }));
}

/** Verify the requested TRX and attach its test and file-hash receipts to the identity record. */
function main() {
  const [trxArgument, receiptArgument, outputArgument] = process.argv.slice(2);
  if (!trxArgument || !receiptArgument || !outputArgument) {
    throw new Error('Usage: verify-agenthost-native-prompt-capacity-trx.mjs <trx> <identity.json> <output.log>');
  }
  const exitCodeText = process.env.NATIVE_TEST_EXIT_CODE;
  const runnerExitCode = exitCodeText && /^\d+$/.test(exitCodeText) ? Number(exitCodeText) : undefined;
  const trxPath = resolve(trxArgument);
  const receiptPath = resolve(receiptArgument);
  const outputPath = resolve(outputArgument);
  const verifiedResults = verifyPromptCapacityResults(readFileSync(trxPath, 'utf8'), runnerExitCode);
  const receipt = JSON.parse(readFileSync(receiptPath, 'utf8'));
  const sha256 = file => createHash('sha256').update(readFileSync(file)).digest('hex');
  receipt.nativeTestExitCode = runnerExitCode;
  receipt.nativePromptCapacityTests = verifiedResults;
  receipt.trxSha256 = sha256(trxPath);
  receipt.outputSha256 = sha256(outputPath);
  writeFileSync(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`);
  console.log(`Verified both pinned native prompt-capacity tests passed: ${expectedTests.join(', ')}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(error);
    process.exitCode = 1;
  }
}
