import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  cacheDir: '.vite/vitest',
  test: {
    environment: 'happy-dom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    // vitest 4's default 5000ms is occasionally too tight for the heavier
    // copilot-fluent-system showcase render test on a cold transform cache.
    testTimeout: 15000,
  },
});
