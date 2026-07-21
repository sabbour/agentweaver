import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Standalone Vite app used only by scripts/docs/render-diagrams.mjs — never
// shipped, never linked from the product. It exists purely so Playwright can
// load a real React Flow + dagre page headlessly and capture a static SVG.
export default defineConfig({
  plugins: [react()],
  server: {
    strictPort: true,
  },
});
