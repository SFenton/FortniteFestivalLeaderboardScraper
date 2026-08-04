import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';
import { readFileSync } from 'fs';
import { sharedPackageBoundaryPlugin } from './scripts/shared-package-boundary-plugin.mjs';

const pkg = JSON.parse(readFileSync(path.resolve(__dirname, 'package.json'), 'utf-8'));
const corePkg = JSON.parse(readFileSync(path.resolve(__dirname, '../packages/core/package.json'), 'utf-8'));
const themePkg = JSON.parse(readFileSync(path.resolve(__dirname, '../packages/theme/package.json'), 'utf-8'));

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_BASE || 'http://localhost:8080';
  const buildOutDir = process.env.FST_WEB_OUT_DIR
    ? path.resolve(__dirname, process.env.FST_WEB_OUT_DIR)
    : path.resolve(__dirname, '../FSTService/wwwroot');

  return {
    base: '/',
    plugins: [
      react(),
      sharedPackageBoundaryPlugin({
        webRoot: __dirname,
        graphOutput: env.FST_BUNDLE_GRAPH_OUT,
      }),
    ],
    define: {
      __APP_VERSION__: JSON.stringify(pkg.version),
      __CORE_VERSION__: JSON.stringify(corePkg.version),
      __THEME_VERSION__: JSON.stringify(themePkg.version),
    },
    resolve: {
      alias: {
        '@festival/theme': path.resolve(__dirname, '../packages/theme/src'),
        '@festival/ui-utils': path.resolve(__dirname, '../packages/ui-utils/src'),
        'react-native': path.resolve(__dirname, 'src/stubs/react-native.ts'),
        'react-native-app-auth': path.resolve(__dirname, 'src/stubs/react-native-app-auth.ts'),
      },
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./__test__/setup.ts'],
      exclude: ['e2e/**', 'node_modules/**'],
      coverage: {
        provider: 'v8',
        reporter: ['text', 'json', 'lcov'],
        include: ['src/**/*.{ts,tsx}'],
        exclude: [
          '__test__/**',
          'src/vite-env.d.ts',
          'src/main.tsx',
          'src/stubs/**',
          'src/utils/platform.ts',
          'src/components/sort/reorderTypes.ts',
          'src/pages/player/helpers/playerPageTypes.ts',
        ],
        thresholds: {
          lines: 88,
          branches: 79,
          statements: 86,
          functions: 87,
        },
      },
    },
    build: {
      outDir: buildOutDir,
      emptyOutDir: true,
    },
    server: {
      host: true,
      port: 3000,
      fs: {
        allow: [
          path.resolve(__dirname),
          path.resolve(__dirname, '..'),
        ],
      },
      watch: {
        ignored: ['**/coverage/**', '**/TestResults/**', '**/__test__/**'],
      },
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          ws: true,
          headers: env.VITE_API_KEY
            ? { 'X-API-Key': env.VITE_API_KEY }
            : {},
        },
      },
    },
  };
});
