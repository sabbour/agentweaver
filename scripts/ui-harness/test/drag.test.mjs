import test from 'node:test';
import assert from 'node:assert/strict';
import { dragOptionsFromArgs, performPointerDrag } from '../lib/drag.mjs';

function locator(box, waitError) {
  return {
    async waitFor() {
      if (waitError) throw waitError;
    },
    async boundingBox() {
      return box;
    },
  };
}

function recordingPage({ failTargetMove = false } = {}) {
  const calls = [];
  let moves = 0;
  return {
    calls,
    mouse: {
      async move(x, y, options) {
        calls.push(['move', x, y, options]);
        moves += 1;
        if (failTargetMove && moves === 2) throw new Error('target move failed');
      },
      async down(options) {
        calls.push(['down', options]);
      },
      async up(options) {
        calls.push(['up', options]);
      },
    },
  };
}

test('drag-to-connect uses handle centers and a real pointer down/move/up sequence', async () => {
  const page = recordingPage();
  const result = await performPointerDrag({
    page,
    source: locator({ x: 10, y: 20, width: 10, height: 10 }),
    target: locator({ x: 100, y: 200, width: 10, height: 10 }),
    steps: 8,
  });

  assert.deepEqual(result, { from: { x: 15, y: 25 }, to: { x: 105, y: 205 }, steps: 8 });
  assert.deepEqual(page.calls, [
    ['move', 15, 25, undefined],
    ['down', { button: 'left' }],
    ['move', 105, 205, { steps: 8 }],
    ['up', { button: 'left' }],
  ]);
});

test('node repositioning accepts safe offsets within the node and canvas targets', async () => {
  const page = recordingPage();
  await performPointerDrag({
    page,
    source: locator({ x: 40, y: 50, width: 160, height: 60 }),
    target: locator({ x: 0, y: 0, width: 800, height: 600 }),
    sourceOffset: { x: 20, y: 15 },
    targetOffset: { x: 640, y: 420 },
    steps: 20,
  });

  assert.deepEqual(page.calls[0], ['move', 60, 65, undefined]);
  assert.deepEqual(page.calls[2], ['move', 640, 420, { steps: 20 }]);
});

test('invalid or missing targets fail before pointerdown', async () => {
  const page = recordingPage();
  await assert.rejects(
    performPointerDrag({
      page,
      source: locator({ x: 10, y: 20, width: 10, height: 10 }),
      target: locator(null),
    }),
    /drag target did not resolve/,
  );
  assert.deepEqual(page.calls, []);

  await assert.rejects(
    performPointerDrag({
      page,
      source: locator({ x: 10, y: 20, width: 10, height: 10 }),
      target: locator({ x: 100, y: 200, width: 10, height: 10 }),
      targetOffset: { x: 11, y: 5 },
    }),
    /coordinates must stay inside/,
  );
  assert.deepEqual(page.calls, []);
});

test('a failed drag releases the pressed pointer', async () => {
  const page = recordingPage({ failTargetMove: true });
  await assert.rejects(
    performPointerDrag({
      page,
      source: locator({ x: 10, y: 20, width: 10, height: 10 }),
      target: locator({ x: 100, y: 200, width: 10, height: 10 }),
    }),
    /target move failed/,
  );
  assert.deepEqual(page.calls.at(-1), ['up', { button: 'left' }]);
});

test('drag CLI options validate steps, timeout, and optional coordinates', () => {
  assert.deepEqual(dragOptionsFromArgs({
    steps: '24',
    timeout: '5000',
    'from-x': '2.5',
    'to-y': '9',
  }), {
    steps: 24,
    timeout: 5000,
    sourceOffset: { x: 2.5, y: undefined },
    targetOffset: { x: undefined, y: 9 },
  });
  assert.throws(() => dragOptionsFromArgs({ steps: '0' }), /steps/);
  assert.throws(() => dragOptionsFromArgs({ 'to-x': '-1' }), /to-x/);
});
