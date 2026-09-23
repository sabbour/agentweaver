import test from 'node:test';
import assert from 'node:assert/strict';
import { MOBILE_VIEWPORT, viewportOptions, zoomPercent } from '../lib/ui-actions.mjs';

test('zoomPercent accepts useful page zoom values', () => {
  assert.equal(zoomPercent({ percent: '75' }), 75);
  assert.equal(zoomPercent({ percent: 100 }), 100);
});

test('zoomPercent rejects missing or unreasonable values', () => {
  assert.throws(() => zoomPercent({}), /between 25 and 200/);
  assert.throws(() => zoomPercent({ percent: 10 }), /between 25 and 200/);
  assert.throws(() => zoomPercent({ percent: 250 }), /between 25 and 200/);
});

test('viewportOptions supports explicit and maintained mobile viewport sizes', () => {
  assert.deepEqual(viewportOptions({ width: '1280', height: '720' }), {
    size: { width: 1280, height: 720 },
    mode: 'custom',
    responsiveTargets: {
      navigationTestId: 'app-navigation-menu',
      focusTestId: 'run-focus-toggle',
      contentTestId: 'run-operator-console',
      focusMode: 'available',
    },
  });
  assert.deepEqual(viewportOptions({ mobile: true }).size, MOBILE_VIEWPORT);
  assert.deepEqual(viewportOptions({ preset: 'mobile' }).size, MOBILE_VIEWPORT);
});

test('viewportOptions rejects ambiguous, invalid, and unsupported viewport requests', () => {
  assert.throws(() => viewportOptions({ width: '1280' }), /--height/);
  assert.throws(() => viewportOptions({ width: 'narrow', height: '720' }), /--width/);
  assert.throws(() => viewportOptions({ mobile: true, width: '390' }), /cannot be combined/);
  assert.throws(() => viewportOptions({ preset: 'tablet' }), /supports only "mobile"/);
  assert.throws(() => viewportOptions({ width: '390', height: '844', 'focus-mode': 'immersive' }), /available, standard, or focused/);
});
