import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    /* Pinned, not left to chance. The API's Development CORS policy allows
     * exactly http://localhost:5173 (src/appsettings.Development.json), so if
     * Vite silently moved to 5174 because the port was busy, every request
     * would fail preflight and it would read like a React bug. strictPort
     * makes that a startup error instead.
     *
     * No proxy on purpose — see the note in src/lib/api.ts. */
    port: 5173,
    strictPort: true,
  },
});
