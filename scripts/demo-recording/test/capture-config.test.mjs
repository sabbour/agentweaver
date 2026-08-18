import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import test from 'node:test';
import {
  createCueManifest,
  joinCaptureConfig,
  validateCaptureConfig,
} from '../lib/capture-config.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';
import { browserDomCueBootstrapSource } from '../lib/dom-cues.mjs';
import { resolveCapturePreflight } from '../lib/preflight.mjs';

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

test('Bug Fix capture resolves one verified current pull-request identity before approval', async () => {
  const config = {
    schemaVersion: 1,
    preflight: {
      pullRequest: {
        beats: ['4.6', '4.7'],
        repositoryEnvironment: 'BUGFIX_REPOSITORY',
        numberEnvironment: 'BUGFIX_NUMBER',
        urlEnvironment: 'BUGFIX_URL',
        instruction: 'Use the current Bug Fix run pull request.',
      },
    },
    beats: [{
      id: '4.6',
      steps: [{ type: 'goto', url: '{{BUGFIX_URL}}' }],
    }],
  };
  const environment = {
    BUGFIX_REPOSITORY: 'example/demo',
    BUGFIX_NUMBER: '42',
  };

  await assert.rejects(
    () => resolveCapturePreflight(config, ['4.6'], { BUGFIX_NUMBER: '42' }),
    /BUGFIX_REPOSITORY is required/,
  );
  await assert.rejects(
    () => resolveCapturePreflight(config, ['4.6'], { ...environment, BUGFIX_NUMBER: '0' }),
    /positive pull-request number/,
  );
  await assert.rejects(
    () => resolveCapturePreflight(config, ['4.6'], environment, {
      fetchImpl: async () => ({ ok: false }),
    }),
    /does not exist or is not readable/,
  );
  await assert.rejects(
    () => resolveCapturePreflight(config, ['4.6'], environment, {
      fetchImpl: async () => ({ ok: true, json: async () => ({ number: 42, html_url: 'https://github.com/example/other/pull/42' }) }),
    }),
    /mismatched pull-request identity/,
  );

  const calls = [];
  const resolved = await resolveCapturePreflight(config, ['4.6'], environment, {
    fetchImpl: async (url) => {
      calls.push(url);
      return { ok: true, json: async () => ({ number: 42, html_url: 'https://github.com/example/demo/pull/42' }) };
    },
  });
  assert.deepEqual(calls, ['https://api.github.com/repos/example/demo/pulls/42']);
  assert.equal(resolved.beats[0].steps[0].url, 'https://github.com/example/demo/pull/42');
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

test('Blueprint plan keeps promotion, review, trace, and decision evidence continuous', async () => {
  const plan = JSON.parse(await fs.readFile(
    new URL('../plans/blueprint-demo.capture.json', import.meta.url),
    'utf8',
  ));
  assert.doesNotThrow(() => validateCaptureConfig(plan));
  const byId = (id) => plan.beats.find((beat) => beat.id === id);
  const confirm = byId('2.2');
  const board = byId('2.4');
  const review = byId('2.6');
  const traces = byId('2.7');
  const decisions = byId('2.8');

  const promotionCheckbox = "page.getByRole('checkbox', { name: 'Allow standalone backlog tasks for independent deliverables', exact: true })";
  const promotionWait = confirm.steps.findIndex((step) => step.type === 'waitFor'
    && step.selector === promotionCheckbox);
  assert.ok(promotionWait >= 0, 'expected the actual promotion checkbox to be ready before confirmation');
  assert.deepEqual(
    confirm.steps.slice(promotionWait, promotionWait + 3).map(({ type, selector }) => ({ type, selector })),
    [
      { type: 'waitFor', selector: promotionCheckbox },
      { type: 'click', selector: promotionCheckbox },
      { type: 'click', selector: "page.getByRole('button', { name: 'Confirm plan' }).first()" },
    ],
    'the promotion checkbox must be waited for, selected, then immediately confirmed',
  );
  assert.ok(board.steps.some((step) => step.cue?.name === '2.4.promoted-task'));
  assert.equal(board.steps.some((step) => step.selector?.includes('New task title')), false);
  assert.ok(review.cueWatchers.some((cue) => cue.source?.selector === "[data-testid='coordinator-review-changes']"));
  assert.equal(review.freshNavigation, false);
  assert.equal(traces.freshNavigation, false);
  assert.ok(traces.steps.some((step) => step.selector?.includes('Preview trace')));
  assert.equal(decisions.freshNavigation, false);
  assert.equal(
    decisions.steps.find((step) => step.cue?.name === '2.8.accepted-decision').selector,
    "page.getByTestId('accepted-decision').first()",
  );
  const approval = byId('4.6');
  const closeLoop = byId('4.7');
  const pullRequest = plan.preflight.pullRequest;
  assert.deepEqual(pullRequest.beats, ['4.6', '4.7']);
  assert.equal(pullRequest.repositoryEnvironment, 'AGENTWEAVER_DEMO_GITHUB_BUGFIX_PR_REPOSITORY');
  assert.equal(pullRequest.numberEnvironment, 'AGENTWEAVER_DEMO_GITHUB_BUGFIX_PR_NUMBER');
  assert.equal(
    closeLoop.steps.find((step) => step.type === 'goto').url,
    '{{AGENTWEAVER_DEMO_GITHUB_BUGFIX_PR_URL}}',
  );
  assert.ok(
    approval.steps.some((step) => step.type === 'click' && step.selector.includes('Approve.*merge')),
    'the identity preflight must complete before Beat 4.6 can click approval',
  );
});
