import test from 'node:test';
import assert from 'node:assert/strict';
import {
  TEST_SHARDS,
  matrix,
} from '../dotnet-test-shards.mjs';

test('test shard definitions produce a stable CI matrix', () => {
  assert.equal(new Set(TEST_SHARDS.map((shard) => shard.id)).size, TEST_SHARDS.length);
  assert.equal(TEST_SHARDS.filter((shard) => shard.requiresBubblewrap).length, 1);
  assert.equal(TEST_SHARDS.find((shard) => shard.id === 'postgres').filter, 'Category=PostgresIntegration');
  assert.match(
    TEST_SHARDS.find((shard) => shard.id === 'process-environment').filter,
    /^Category=ProcessEnvironment&Category!=KataRuntime&Category!=PostgresIntegration$/,
  );
  assert.match(
    TEST_SHARDS.find((shard) => shard.id === 'runtime').filter,
    /FullyQualifiedName~Agentweaver\.Tests\.RunActiveClaimGuard/,
  );
  assert.deepEqual(
    JSON.parse(matrix()).include.map((shard) => shard.id),
    TEST_SHARDS.map((shard) => shard.id),
  );
});
