import fs from 'node:fs/promises';
import { loadSessionSeed } from '../lib/auth.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';
import { buildStableStagingInterimPlan } from '../plans/stable-staging-interim.mjs';

const baseUrl = process.env.AGENTWEAVER_DEMO_BASE_URL;
const projectId = process.env.AGENTWEAVER_DEMO_PROJECT_ID;
const outPath = process.env.AGENTWEAVER_DEMO_PLAN_OUT;
const sessionStoragePath = process.env.AGENTWEAVER_DEMO_SESSION_STORAGE_PATH;

if (!baseUrl || !projectId || !outPath) {
  throw new Error('Missing AGENTWEAVER_DEMO_BASE_URL, AGENTWEAVER_DEMO_PROJECT_ID, or AGENTWEAVER_DEMO_PLAN_OUT.');
}

const plan = buildStableStagingInterimPlan({ baseUrl, projectId });
if (sessionStoragePath) {
  const seed = await loadSessionSeed(sessionStoragePath);
  plan.auth = {
    origin: baseUrl,
    entries: seed.entries,
  };
}
await fs.writeFile(outPath, renderCaptureScript(plan), 'utf8');
console.log(JSON.stringify({ outPath, stepCount: plan.steps.length, authEmbedded: Boolean(plan.auth) }));
