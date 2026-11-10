import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { INSTALL_TARGETS } from '../lib/targets.mjs';

const values = file => JSON.parse(readFileSync(file, 'utf8')).parameters;

test('every registered target binds its own foundation and AKS-only parameter files', () => {
  for (const [name, target] of Object.entries(INSTALL_TARGETS)) {
    const foundation = values(target.foundationParameters);
    const aksOnly = values(target.aksParameters);
    assert.equal(foundation.namePrefix.value, name);
    // git.mjs requires the AKS-only file to repeat these foundation values and nothing else.
    assert.deepEqual(Object.keys(aksOnly).sort(), ['costCenter', 'location', 'namePrefix', 'owner', 'tenantId']);
    for (const key of Object.keys(aksOnly)) assert.equal(aksOnly[key].value, foundation[key].value, `${name} ${key}`);
    if (name !== 'agentweaver') {
      // A separate group is created by the new-foundation path, which requires these values.
      assert.match(name, /^[a-z][a-z0-9]{5,11}$/);
      assert.deepEqual(foundation.appRoutingDnsZoneResourceIds.value, []);
      assert.ok(foundation.keyVaultName.value.startsWith(`${name}-`));
      assert.equal(foundation.location.value, foundation.monitorLocation.value);
    }
  }
});
