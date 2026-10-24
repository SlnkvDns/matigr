import react from '@vitejs/plugin-react';
import { defineConfig, loadEnv } from 'vite';
import { fileURLToPath } from 'node:url';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const checkingTarget = env.CHECKING_API_TARGET ?? 'http://127.0.0.1:5101';
  const resultsTarget = env.RESULTS_API_TARGET ?? 'http://127.0.0.1:5102';
  const appealsTarget = env.APPEALS_API_TARGET ?? 'http://127.0.0.1:5103';
  return {
    plugins: [react()],
    server: {
      fs: {
        strict: true,
        allow: [fileURLToPath(new URL('.', import.meta.url)), fileURLToPath(new URL('../../../../node_modules/', import.meta.url))],
        deny: ['.env', '.env.*', '**/*.{crt,pem}', '**/.git/**', '**/.local/**', '**/infra/fixtures/**'],
      },
      proxy: {
        '/api/v1/checking': { target: checkingTarget },
        '/api/v1/results': { target: resultsTarget },
        '/api/v1/appeals': { target: appealsTarget },
        '/dev/v1/session': { target: checkingTarget },
      },
    },
  };
});
