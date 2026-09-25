import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// /api is served by the ASCO .NET API (server/Asco.Api). Same-origin through this proxy, so the
// auth cookie stays SameSite=Strict and no CORS is needed. In production, serve the built app
// from the same host as the API (or put both behind one reverse proxy).
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': { target: process.env.ASCO_API ?? 'http://localhost:5080', changeOrigin: false } },
  },
})
