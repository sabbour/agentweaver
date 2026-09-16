import test from 'node:test';
import assert from 'node:assert/strict';
import { zoomPercent } from '../lib/ui-actions.mjs';

test('zoomPercent accepts useful page zoom values', () => {
  assert.equal(zoomPercent({ percent: '75' }), 75);
  assert.equal(zoomPercent({ percent: 100 }), 100);
});

test('zoomPercent rejects missing or unreasonable values', () => {
  assert.throws(() => zoomPercent({}), /between 25 and 200/);
  assert.throws(() => zoomPercent({ percent: 10 }), /between 25 and 200/);
  assert.throws(() => zoomPercent({ percent: 250 }), /between 25 and 200/);
});
