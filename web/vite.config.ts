import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

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
  test: {
    /* jsdom, not happy-dom: the two screens worth testing render a real table
     * and a real form, and jsdom is the environment whose gaps are documented.
     *
     * `globals` stays OFF. Importing describe/it/expect costs one line per file
     * and keeps tsconfig.app.json's `types` array honest — turning globals on
     * would mean adding "vitest/globals" there, which puts test types into the
     * type-check of every application file that has nothing to do with tests. */
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
