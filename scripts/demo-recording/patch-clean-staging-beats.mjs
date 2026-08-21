// Patches beats 0.1 and 1.1 to work on clean staging (no pre-existing projects)
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const planPath = path.join(__dirname, 'plans', 'azure-aks-demo.capture.json');
const plan = JSON.parse(fs.readFileSync(planPath, 'utf-8'));

// --- Beat 0.1: work on clean staging (overview page, no pre-existing project) ---
const b01 = plan.beats.find(b => b.id === '0.1');
const alreadyPatched01 = b01.steps.some(s => s.type === 'pause' && s.ms === 3500);
if (!alreadyPatched01) {
  b01.startUrl = 'https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io/overview';
  b01.freshNavigation = true;
  b01.steps = [
    { type: 'badge', label: 'Beat 0.1', title: 'Introduce the repo and the job', duration: 1800 },
    {
      type: 'waitFor',
      selector: 'page.locator("main")',
      timeout: 15000,
      cue: {
        name: 's2.0.1.repo-context',
        stableForMs: 600,
        deadlineMs: 15000,
        source: { kind: 'selector', selector: 'main', state: 'visible' },
        rect: { mode: 'element', selector: 'main' },
      },
    },
    { type: 'pause', ms: 3500 },
  ];
  b01.expectedCues = ['s2.0.1.repo-context'];
  b01.cueOrder = ['s2.0.1.repo-context'];
  console.log('Beat 0.1: patched to overview page (clean staging compatible)');
} else {
  console.log('Beat 0.1: already patched, skipping');
}

// --- Beat 1.1: increase project-created timeout 180s → 300s ---
const b11 = plan.beats.find(b => b.id === '1.1');
const alreadyPatched11 = b11.steps.some(
  s => s.timeout === 300000 && s.cue && s.cue.name === 's2.1.1.project-created',
);
if (!alreadyPatched11) {
  const createdStep = b11.steps.find(
    s => s.cue && s.cue.name === 's2.1.1.project-created',
  );
  if (createdStep) {
    createdStep.timeout = 300000;
    if (createdStep.cue.deadlineMs) createdStep.cue.deadlineMs = 300000;
    console.log('Beat 1.1: extended project-created timeout to 300s');
  } else {
    console.warn('Beat 1.1: could not find project-created step');
  }
} else {
  console.log('Beat 1.1: already patched, skipping');
}

fs.writeFileSync(planPath, JSON.stringify(plan, null, 2));
console.log('Patch complete.');
