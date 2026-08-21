import { readFileSync, writeFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dir = dirname(fileURLToPath(import.meta.url));
const planPath = join(__dir, 'plans', 'sabbour-aks-demo.capture.json');
const plan = JSON.parse(readFileSync(planPath, 'utf8'));

// ── Beat 2.2: content creation workflow prompt ──────────────────────────────
const b22 = plan.beats.find(b => b.id === '2.2');
const typeStep = b22.steps.find(
  s => s.type === 'type' && s.selector?.includes('Describe the workflow you need')
);
if (typeStep) {
  console.log('2.2 old prompt:', typeStep.text.slice(0, 60));
  typeStep.text =
    'Weekly content pipeline: monitor merged pull requests and release commits in sabbour/AKS from the past week, draft a concise engineering blog post summarising notable changes and their impact for the engineering blog, and open a pull request with the draft for team review — do not publish without explicit approval.';
  console.log('2.2 new prompt:', typeStep.text.slice(0, 60));
}

// ── Beat 2.2: dwell on visual editor nodes ──────────────────────────────────
const nodeWaitIdx = b22.steps.findIndex(
  s => s.type === 'waitFor' && s.selector?.includes('react-flow__node')
);
if (nodeWaitIdx > -1) {
  b22.steps.splice(nodeWaitIdx + 1, 0,
    { type: 'hover', selector: "page.locator('.react-flow__node').first()", scale: 1.25, hold: 1200 },
    { type: 'pause', ms: 1500 },
    { type: 'hover', selector: "page.locator('.react-flow__node').nth(1)", scale: 1.25, hold: 1000 },
    { type: 'pause', ms: 800 }
  );
  console.log('2.2: inserted node-hover dwell steps');
}

// ── Beat 3.1: wait for plan generated + topology populated ──────────────────
const b31 = plan.beats.find(b => b.id === '3.1');
const topoIdx = b31.steps.findIndex(s => s.cue?.name === 's2.3.1.topology-rendered');
if (topoIdx > -1) {
  b31.steps[topoIdx].cue.stableForMs = 6000;
  // Remove any previously-inserted post-topo steps (from prior patch run)
  const afterTopo = b31.steps.slice(topoIdx + 1);
  const nextNonInserted = afterTopo.findIndex(s =>
    s.type === 'hover' && s.selector?.includes('topology-graph-canvas')
  );
  if (nextNonInserted > 0) {
    b31.steps.splice(topoIdx + 1, nextNonInserted);
  }
  // Just dwell 8s after canvas is stable — don't block on node count
  // (topology populates asynchronously; agent nodes may appear slowly)
  const alreadyPatched31 = b31.steps.some(s => s.type === 'pause' && s.ms === 8000);
  if (!alreadyPatched31) {
    b31.steps.splice(topoIdx + 1, 0, { type: 'pause', ms: 8000 });
    console.log('3.1: 8s dwell after topology canvas stable (removed node-count waitFor)');
  } else {
    console.log('3.1: 8s dwell already present, skipping');
  }
}

// ── Beat 4.4: cluster page (not observability) + auth guard ─────────────────
const b44 = plan.beats.find(b => b.id === '4.4');
const gotoStep44 = b44.steps.find(s => s.type === 'gotoFromSessionStorage');
if (gotoStep44 && gotoStep44.suffix !== '/cluster') {
  gotoStep44.suffix = '/cluster';
  console.log('4.4: suffix changed to /cluster');
} else if (gotoStep44) {
  console.log('4.4: suffix already /cluster, skipping');
}
// Replace observability-specific waitText steps with cluster page content
b44.steps = b44.steps.filter(s =>
  !['Telemetry for model performance, token usage, and invocation trends.',
    'Performance summary', 'Performance data', 'Traces'].includes(s.text)
);
// Remove the Traces tab click and trace summary waitFor
b44.steps = b44.steps.filter(s =>
  !(s.type === 'click' && s.selector?.includes('Traces')) &&
  !(s.type === 'waitFor' && s.selector?.includes('Trace summary'))
);
// Insert auth guard + cluster page waits after gotoFromSessionStorage (idempotent)
const gotoIdx44 = b44.steps.findIndex(s => s.type === 'gotoFromSessionStorage');
const alreadyPatched44 = b44.steps.some(s => s.cue?.name === 's2.4.4.cluster-page');
if (!alreadyPatched44) {
  b44.steps.splice(gotoIdx44 + 1, 0,
    { type: 'waitFor', selector: "page.locator('[aria-label=\"Agents\"]')", timeout: 60000 },
    { type: 'waitText', text: 'Cluster', timeout: 30000, cue: {
        name: 's2.4.4.cluster-page',
        stableForMs: 800,
        deadlineMs: 30000,
        source: { kind: 'text', selector: 'main', includes: 'Cluster' },
        rect: { mode: 'element', selector: 'main' }
      }
    },
    { type: 'hover', selector: 'page.locator(\'main\')', scale: 1.0, hold: 3000 }
  );
  console.log('4.4: navigates to /cluster with auth guard and cluster waitText');
} else {
  console.log('4.4: cluster-page cue already present, skipping insert');
}

// ── Beat 5.1: click Account settings (mcp-server-url is account-level /settings) ──
const b51 = plan.beats.find(b => b.id === '5.1');
// mcp-server-url lives at /settings (global Account settings), not project /settings.
const alreadyPatched51 = b51.steps.some(s => s.selector?.includes('Account settings'));
if (!alreadyPatched51) {
  // Remove gotoFromSessionStorage + any following waitFor/click Settings steps
  const gotoIdx51 = b51.steps.findIndex(s => s.type === 'gotoFromSessionStorage');
  if (gotoIdx51 > -1) {
    let removeCount = 1;
    if (b51.steps[gotoIdx51+1]?.selector?.includes('Settings')) removeCount++;
    if (b51.steps[gotoIdx51+2]?.selector?.includes('Settings')) removeCount++;
    b51.steps.splice(gotoIdx51, removeCount,
      { type: 'click', selector: "page.locator('[aria-label=\"Account settings\"]')", scale: 1.3, after: 1200 }
    );
    console.log('5.1: replaced project nav with Account settings global nav click');
  }
} else {
  console.log('5.1: Account settings click already present, skipping');
}
// Remove any leftover scrollIntoView eval
b51.steps = b51.steps.filter(s => !(s.type === 'eval' && s.code?.includes('scrollIntoView')));
// Fix any lingering broken selector
for (const s of b51.steps) {
  if (s.selector?.includes('project-settings-copilot-model')) {
    s.selector = "page.getByTestId('mcp-server-url')";
    console.log('5.1: selector patched → mcp-server-url');
  }
}
const mcpWait = b51.steps.find(s => s.selector?.includes('mcp-server-url') && s.type === 'waitFor');
if (mcpWait && mcpWait.timeout !== 30000) { mcpWait.timeout = 30000; console.log('5.1: mcp waitFor timeout → 30000'); }

writeFileSync(planPath, JSON.stringify(plan, null, 2) + '\n');
console.log('\n✅ All patches applied to sabbour-aks-demo.capture.json');

