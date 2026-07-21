import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Headless-capture-only Vite app: no relation to the shipped apps/web build.
// See scripts/docs/capture-diagrams.mjs for how this is built + previewed.
export default defineConfig({
  plugins: [react()],
  base: './',
});
