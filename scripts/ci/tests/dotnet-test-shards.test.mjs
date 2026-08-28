import test from 'node:test';
import assert from 'node:assert/strict';
import {
  TEST_SHARDS,
  parseTestList,
  validatePartition,
} from '../dotnet-test-shards.mjs';

test('test shard definitions have stable unique identifiers and filters', () => {
  assert.equal(new Set(TEST_SHARDS.map((shard) => shard.id)).size, TEST_SHARDS.length);
  assert.equal(TEST_SHARDS.filter((shard) => shard.requiresBubblewrap).length, 1);
  assert.equal(TEST_SHARDS.find((shard) => shard.id === 'kata-runtime').minimumTests, 34);
  assert.equal(TEST_SHARDS.find((shard) => shard.id === 'postgres').filter, 'Category=PostgresIntegration');
  assert.match(
    TEST_SHARDS.find((shard) => shard.id === 'process-environment').filter,
    /^Category=ProcessEnvironment&Category!=KataRuntime&Category!=PostgresIntegration$/,
  );
});

test('parses VSTest list output without treating runner messages as tests', () => {
  assert.deepEqual(
    parseTestList('The following Tests are available:\n    Agentweaver.Tests.Api.First\nwarning\n\tAgentweaver.Tests.Auth.Second\n'),
    ['Agentweaver.Tests.Api.First', 'Agentweaver.Tests.Auth.Second'],
  );
});

test('accepts an exact test partition', () => {
  assert.doesNotThrow(() => validatePartition(
    ['Agentweaver.Tests.Api.First', 'Agentweaver.Tests.Auth.Second'],
    [
      { id: 'application', tests: ['Agentweaver.Tests.Api.First'] },
      { id: 'environment', tests: ['Agentweaver.Tests.Auth.Second'] },
    ],
  ));
});

test('rejects test partition gaps and overlaps', () => {
  assert.throws(
    () => validatePartition(
      ['Agentweaver.Tests.Api.First', 'Agentweaver.Tests.Auth.Second'],
      [
        { id: 'one', tests: ['Agentweaver.Tests.Api.First'] },
        { id: 'two', tests: ['Agentweaver.Tests.Api.First'] },
      ],
    ),
    /gaps \(1\).*overlaps \(1\)/,
  );
});
