import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  cacheDir: '.vite/vitest',
  test: {
    environment: 'happy-dom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/**/__tests__/**',
        'src/**/__fixtures__/**',
        'src/**/fixtures/**',
        'src/test/**',
        'src/**/*.d.ts',
        'src/**/*.generated.{ts,tsx}',
        'src/**/generated/**',
      ],
      reporter: ['json', 'json-summary', 'lcov', 'text', 'html'],
    },
    // vitest 4's default 5000ms is occasionally too tight for the heavier
    // copilot-fluent-system showcase render test on a cold transform cache.
    testTimeout: 15000,
  },
});
