import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // The ASP.NET Core API (src/SellerPilot.Api, launchSettings "http" profile).
    proxy: {
      '/api': 'http://localhost:5080',
    },
  },
})
