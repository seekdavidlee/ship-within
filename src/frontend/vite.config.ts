import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: Number(process.env.SHIP_WITHIN_FRONTEND_PORT || 5173),
    strictPort: true,
    proxy: { '/api': { target: `http://127.0.0.1:${process.env.SHIP_WITHIN_API_PORT || 4174}`, changeOrigin: true } },
  },
});