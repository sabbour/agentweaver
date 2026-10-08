import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

function injectRuntimeConfigScript() {
  return {
    name: 'inject-runtime-config-script',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler() {
        return [
          {
            tag: 'script',
            attrs: { src: '/env-config.js' },
            injectTo: 'head-prepend',
          },
        ]
      },
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), injectRuntimeConfigScript()],
  cacheDir: '.vite',
  build: {
    chunkSizeWarningLimit: 5000,
  },
  server: {
    port: 5173,
    strictPort: true,
  },
})
