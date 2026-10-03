import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildWhatIfArgs, plan } from '../plan.mjs';
import { fixture, source, fakeAzure, ids } from './fixtures/target.mjs';

test('what-if uses identical source and account/ownership guards without mutation', () => {
  const calls = [];
  assert.equal(plan(fixture, { sourceResolver: () => source, execAz: fakeAzure({}, calls) }).status, 0);
  assert.ok(calls.at(-1).includes('what-if'));
  assert.ok(!calls.some(args => args.includes('create')));
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('plan fails closed for unauthorized, missing or untagged target', () => {
  for (const overrides of [
    { groupResult: { status: 1, stderr: '(AuthorizationFailed)', stdout: '' } },
    { groupResult: { status: 1, stderr: '(ResourceGroupNotFound)', stdout: '' } },
    { group: { tags: null } },
    { accountResult: { status: 0, stdout: 'malformed', stderr: '' } },
  ]) {
    const calls = [];
    assert.throws(() => plan(fixture, { sourceResolver: () => source, execAz: fakeAzure(overrides, calls) }));
    assert.ok(!calls.some(args => args.includes('what-if')));
  }
});

test('what-if rejects caller template without source receipt', () => {
  assert.throws(() => buildWhatIfArgs({ resourceGroup: 'shared', template: 'anything' }));
  assert.throws(() => buildWhatIfArgs({ resourceGroup: 'aw-v1-p0', template: 'anything' }));
});
