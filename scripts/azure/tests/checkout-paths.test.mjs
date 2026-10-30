import assert from 'node:assert/strict';
import path from 'node:path';
import { test } from 'node:test';
import { isOutsideCheckout } from '../lib/checkout-paths.mjs';

test('isOutsideCheckout treats only parent-relative and other-root paths as outside', () => {
  const root = path.resolve('checkout-root');
  assert.equal(isOutsideCheckout(root, path.join(root, 'Temp')), false);
  assert.equal(isOutsideCheckout(root, path.join(root, '..receipts')), false);
  assert.equal(isOutsideCheckout(root, root), false);
  assert.equal(isOutsideCheckout(root, path.resolve(root, '..', 'receipts')), true);
  assert.equal(isOutsideCheckout(root, path.dirname(root)), true);
});