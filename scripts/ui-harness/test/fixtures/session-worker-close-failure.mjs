import { appendFile } from 'node:fs/promises';
import { closeBrowserResources } from '../../lib/browser.mjs';
import { runSessionWorker } from '../../agent-driver-ui/session-worker.mjs';

const mode = process.env.AGENTWEAVER_CLOSE_FAILURE;
const marker = process.env.AGENTWEAVER_CLOSE_MARKER;
const mark = (resource) => appendFile(marker, `${resource}\n`, 'utf8');

const context = {
  close: async () => {
    await mark('context');
    if (mode === 'context') throw new Error('fixture context close failed');
  },
};
const browser = {
  close: async () => {
    await mark('browser');
    if (mode === 'browser') throw new Error('fixture browser close failed');
  },
};
const page = {
  on: () => {},
  close: async () => { await mark('page'); },
};

runSessionWorker({
  openBrowserSessionImpl: async () => ({
    context,
    browser,
    page,
    close: () => closeBrowserResources(context, browser, page),
  }),
}).catch((error) => {
  console.error(String(error?.message ?? error));
  process.exitCode = 2;
});
