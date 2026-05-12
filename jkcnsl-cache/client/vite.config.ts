import { defineConfig } from 'vite'

export default defineConfig({
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', ws: true },
      '/comment': { target: 'ws://localhost:5000', ws: true },
      '/watch':   { target: 'ws://localhost:5000', ws: true },
    },
  },
})
