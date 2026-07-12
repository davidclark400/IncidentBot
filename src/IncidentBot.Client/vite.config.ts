import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../IncidentBot.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5073',
      '/health': 'http://localhost:5073',
      '/hubs': {
        target: 'http://localhost:5073',
        ws: true,
      },
    },
  },
})
