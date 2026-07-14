import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './test',
  use: { browserName: 'chromium', headless: true, trace: 'retain-on-failure', video: 'off' },
});
