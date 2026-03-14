import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_BASE || 'http://localhost:8080';

  return {
    base: '/',
    plugins: [react()],
    resolve: {
      alias: {
        '@festival/core': path.resolve(__dirname, '../packages/core/src'),
        'react-native': path.resolve(__dirname, 'src/stubs/react-native.ts'),
      },
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
    },
    build: {
      outDir: path.resolve(__dirname, '../FSTService/wwwroot'),
      emptyOutDir: true,
    },
    server: {
      host: true,
      port: 3000,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          headers: env.VITE_API_KEY
            ? { 'X-API-Key': env.VITE_API_KEY }
            : {},
        },
      },
    },
  };
});
