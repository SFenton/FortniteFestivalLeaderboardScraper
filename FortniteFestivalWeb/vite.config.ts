import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';
import { readFileSync } from 'fs';

const pkg = JSON.parse(readFileSync(path.resolve(__dirname, 'package.json'), 'utf-8'));
const corePkg = JSON.parse(readFileSync(path.resolve(__dirname, '../packages/core/package.json'), 'utf-8'));
const themePkg = JSON.parse(readFileSync(path.resolve(__dirname, '../packages/theme/package.json'), 'utf-8'));

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_BASE || 'http://localhost:8080';

  return {
    base: '/',
    plugins: [react()],
    define: {
      __APP_VERSION__: JSON.stringify(pkg.version),
      __CORE_VERSION__: JSON.stringify(corePkg.version),
      __THEME_VERSION__: JSON.stringify(themePkg.version),
    },
    resolve: {
      alias: {
        '@festival/core': path.resolve(__dirname, '../packages/core/src'),
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
          'src/components/songs/rows/**',
          'src/components/suggestions/CategoryCard.tsx',
          'src/components/common/LoadGate.tsx',
          'src/pages/SongsPage.tsx',
          'src/theme/index.ts',
          'src/models/index.ts',
          'src/utils/platform.ts',
          'src/components/sort/reorderTypes.ts',
          'src/pages/player/helpers/playerPageTypes.ts',
        ],
        thresholds: {
          perFile: true,
          lines: 95,
          branches: 95,
          statements: 95,
          functions: 95,
        },
      },
    },
    build: {
      outDir: path.resolve(__dirname, '../FSTService/wwwroot'),
      emptyOutDir: true,
    },
    server: {
      host: true,
      port: 3000,
      watch: {
        ignored: ['**/coverage/**', '**/TestResults/**', '**/__test__/**'],
      },
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
