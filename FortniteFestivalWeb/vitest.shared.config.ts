import { defineConfig } from 'vitest/config';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const configDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(configDir, '..');

export default defineConfig({
  root: repoRoot,
  resolve: {
    alias: [
      {
        find: '@festival/core',
        replacement: path.resolve(repoRoot, 'packages/core/src'),
      },
      {
        find: '@festival/ui-utils',
        replacement: path.resolve(repoRoot, 'packages/ui-utils/src'),
      },
      {
        find: '@festival/theme',
        replacement: path.resolve(repoRoot, 'packages/theme/src'),
      },
      {
        find: '@vitest/coverage-v8',
        replacement: require.resolve('@vitest/coverage-v8'),
      },
    ],
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['FortniteFestivalWeb/scripts/shared-test-setup.ts'],
    include: [
      'packages/core/src/**/*.test.ts',
      'packages/theme/src/**/*.test.ts',
      'packages/ui-utils/src/**/*.test.ts',
      'FortniteFestivalWeb/__test__/utils/platform.test.ts',
    ],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'lcov'],
      reportsDirectory: './FortniteFestivalWeb/coverage-shared',
      include: [
        'packages/core/src/**/*.ts',
        'packages/theme/src/colorHelpers.ts',
        'packages/ui-utils/src/**/*.ts',
      ],
      exclude: [
        'packages/**/__tests__/**',
        'packages/**/*.test.ts',
        'packages/**/*.d.ts',
        'packages/**/index.ts',
        'packages/core/src/types.ts',
        'packages/core/src/suggestions/types.ts',
      ],
      thresholds: {
        lines: 81,
        branches: 73,
        statements: 79,
        functions: 85,
        'packages/core/src/**': {
          lines: 81,
          branches: 73,
          statements: 79,
          functions: 85,
        },
        'packages/ui-utils/src/**': {
          lines: 100,
          branches: 59,
          statements: 100,
          functions: 100,
        },
        'packages/theme/src/**': {
          lines: 100,
          branches: 100,
          statements: 100,
          functions: 100,
        },
      },
    },
  },
});
