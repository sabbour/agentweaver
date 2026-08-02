import assert from 'node:assert/strict';
import test from 'node:test';
import {
  createCueManifest,
  joinCaptureConfig,
  validateCaptureConfig,
} from '../lib/capture-config.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';
import { browserDomCueBootstrapSource } from '../lib/dom-cues.mjs';

const beats = [
  { id: '1.1', title: 'Hook', startUrl: '/projects', freshNavigation: false },
  { id: '1.2', title: 'Topology', startUrl: '/projects/1', freshNavigation: false },
];

test('capture config joins exact markdown beat IDs and preserves continuity metadata', () => {
  const joined = joinCaptureConfig(beats, {
    schemaVersion: 1,
    requireAllBeats: true,
    beats: [
      { id: '1.1', steps: [{ type: 'pause', ms: 500 }] },
      {
        id: '1.2',
        cueWatchers: [{
          name: '1.2.topology-done',
          source: {
            kind: 'predicate',
            selector: '[data-node-status]',
            operator: 'all-attribute-in',
            attribute: 'data-node-status',
            values: ['done'],
            minCount: 1,
          },
          rect: { mode: 'union', selector: '[data-node-status]' },
        }],
      },
    ],
  });

  assert.equal(joined[0].beatId, '1.1');
  assert.equal(joined[0].startUrl, '/projects');
  assert.equal(joined[0].freshNavigation, false);
  assert.equal(joined[1].cueWatchers[0].name, '1.2.topology-done');
});

test('capture config rejects backend-coupled cue sources', () => {
  assert.throws(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{
      id: '1.2',
      cueWatchers: [{
        name: '1.2.running',
        source: { kind: 'run-event', selector: '#topology' },
      }],
    }],
  }), /DOM-only/);
  assert.throws(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{
      id: '1.2',
      cueWatchers: [{
        name: '1.2.done',
        source: { kind: 'topology-state', selector: '#topology' },
      }],
    }],
  }), /DOM-only/);
});

test('capture config validates approval watcher settings', () => {
  assert.doesNotThrow(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{ id: '1.1', disableApprovalWatcher: true, approvalWatcherGraceMs: 0 }],
  }));
  assert.throws(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{ id: '1.1', disableApprovalWatcher: 'true' }],
  }), /disableApprovalWatcher must be a boolean/);
  assert.throws(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{ id: '1.1', approvalWatcherGraceMs: -1 }],
  }), /approvalWatcherGraceMs must be a non-negative integer/);
});

test('capture config rejects unknown, missing, and duplicate IDs and cue names', () => {
  assert.throws(() => joinCaptureConfig(beats, {
    schemaVersion: 1,
    beats: [{ id: '9.9' }],
  }), /does not exist/);
  assert.throws(() => joinCaptureConfig(beats, {
    schemaVersion: 1,
    requireAllBeats: true,
    beats: [{ id: '1.1' }],
  }), /missing capture definitions: 1.2/);
  assert.throws(() => validateCaptureConfig({
    schemaVersion: 1,
    beats: [{
      id: '1.1',
      cueWatchers: [
        { name: 'shared', source: { kind: 'selector', selector: '#one' } },
        { name: 'shared', source: { kind: 'selector', selector: '#two' } },
      ],
    }],
  }), /duplicate semantic cue name/);
});

test('capture script installs DOM-only passive watchers and semantic blocking cues', () => {
  const src = renderCaptureScript({
    beatId: '1.2',
    startUrl: 'https://x/topology',
    videoPath: 'topology.webm',
    cueWatchers: [{
      name: '1.2.first-running',
      source: {
        kind: 'attribute',
        selector: '[data-node-status]',
        attribute: 'data-node-status',
        equals: 'running',
      },
      rect: { mode: 'union', selector: '[data-node-status]' },
    }],
    steps: [
      {
        type: 'waitFor',
        selector: "page.getByTestId('transaction-trace-panel')",
        cue: { name: '1.2.trace-visible', rect: { mode: 'matched-element' } },
      },
      {
        type: 'waitText',
        text: 'Completed',
        cue: { name: '1.2.completed-copy', rect: { mode: 'element', selector: 'main' } },
      },
      { type: 'goto', url: 'https://x/trace' },
    ],
  });

  assert.ok(src.includes("page.exposeBinding('__demoReportCue'"), 'expected a durable Node-side cue sink');
  assert.ok(src.includes('__demoConfigureDomCueWatchers'), 'expected passive watcher configuration');
  assert.ok(src.includes('new MutationObserver(scheduleEvaluation)'), 'expected one in-page MutationObserver');
  assert.ok(src.includes('all-attribute-in'), 'expected declarative DOM predicate support in the bootstrap');
  assert.ok(src.includes('waitLocator.evaluate((node, cue) => window.__demoEmitDomCue'), 'expected waitFor cue emission');
  assert.ok(src.includes('window.__demoEmitDomCue?.(cue, document.body)'), 'expected waitText cue emission');
  assert.ok(src.includes('rectNormalized'), 'expected normalized rectangle capture');
  assert.ok(src.includes('cueLog, captureStartedAtEpochMs'), 'expected cues returned with the capture result');

  const gotoIndex = src.indexOf('await page.goto("https://x/trace"');
  const rearmIndex = src.indexOf('__demoConfigureDomCueWatchers', gotoIndex);
  assert.ok(rearmIndex > gotoIndex, 'expected passive watchers to be re-armed after navigation');
  assert.ok(!src.includes('run-event'), 'must not contain backend run-event coupling');
  assert.ok(!src.includes('topology-state'), 'must not contain backend topology-state coupling');
});

test('DOM cue bootstrap is valid standalone browser JavaScript', () => {
  assert.doesNotThrow(() => new Function(browserDomCueBootstrapSource()));
});

test('cue manifest sorts immutable observations by capture-relative time', () => {
  const manifest = createCueManifest({
    takeId: 'take-7',
    videoPath: 'raw.webm',
    captureStartedAtEpochMs: 1000,
    cues: [
      { name: 'later', tMs: 20, sequence: 1 },
      { name: 'earlier', tMs: 10, sequence: 0 },
    ],
  });
  assert.equal(manifest.schemaVersion, 1);
  assert.deepEqual(manifest.cues.map((cue) => cue.name), ['earlier', 'later']);
});
