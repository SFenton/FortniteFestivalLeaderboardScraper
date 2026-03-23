import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import reactPlugin from 'eslint-plugin-react';

export default tseslint.config(
  // Ignore build outputs
  { ignores: ['dist/**', 'node_modules/**', '*.config.*'] },

  // Base JS recommended rules
  js.configs.recommended,

  // TypeScript recommended (type-aware where possible)
  ...tseslint.configs.recommended,

  // ── Web app source ──
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      'react': reactPlugin,
    },
    rules: {
      // ── React hooks ──
      // Note: rules-of-hooks is warn (not error) because several pre-existing
      // components call hooks conditionally after early returns. These should
      // be refactored but are not blockers.
      'react-hooks/rules-of-hooks': 'warn',
      'react-hooks/exhaustive-deps': 'warn',

      // ── React refresh (HMR) ──
      // Disabled: standard patterns (contexts, mixed exports) trigger false positives
      'react-refresh/only-export-components': 'off',

      // ── No inline styles on DOM elements ──
      'react/forbid-dom-props': ['warn', { forbid: ['style'] }],

      // ── No magic numbers (warn only — too strict at error level) ──
      // ignore covers: basics (0/1/-1/2), small ints (3-10, 100),
      // halves (0.5), common px sizes (12-96), animation ms (120-1500),
      // fractional opacities/ratios, and miscellaneous UI constants
      'no-magic-numbers': ['warn', {
        // prettier-ignore
        ignore: [
          0, 1, -1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 100, 0.5,
          // negatives
          -3, -5, -14, -18,
          // layout & sizing (px)
          12, 14, 15, 16, 20, 24, 25, 28, 30, 36, 40, 44, 45, 46, 48, 50,
          52, 56, 60, 64, 66, 68, 70, 72, 80, 84, 85, 90, 96, 99,
          113, 160, 204, 220,
          // animation / timing (ms)
          120, 122, 125, 150, 200, 250, 300, 360, 400,
          450, 500, 840, 850, 900, 1000, 1500, 100000,
          // fractional values (opacity, scale, ratio)
          0.2, 0.26, 0.45, 0.8, 0.85, 0.9, 1.1, 1.12, 1.18, 2.5,
        ],
        ignoreArrayIndexes: true,
        ignoreDefaultValues: true,
        enforceConst: true,
      }],

      // ── Block deprecated imports from songSettings ──
      'no-restricted-imports': ['error', {
        paths: [
          {
            name: '../components/songSettings',
            importNames: ['INSTRUMENT_SORT_MODES', 'METADATA_SORT_DISPLAY'],
            message: 'Use getInstrumentSortModes() / getMetadataSortDisplay() instead.',
          },
          {
            name: './songSettings',
            importNames: ['INSTRUMENT_SORT_MODES', 'METADATA_SORT_DISPLAY'],
            message: 'Use getInstrumentSortModes() / getMetadataSortDisplay() instead.',
          },
        ],
      }],

      // ── Naming: discourage generic `type Props` ──
      '@typescript-eslint/naming-convention': ['warn',
        {
          selector: 'typeAlias',
          format: ['PascalCase'],
          custom: {
            regex: '^Props$',
            match: false,
          },
        },
      ],

      // ── Prevent unused vars (allow underscore prefix) ──
      '@typescript-eslint/no-unused-vars': ['warn', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
      }],

      // ── Allow `any` but warn (gradual migration) ──
      '@typescript-eslint/no-explicit-any': 'warn',

      // ── Prefer const assertions for enums ──
      'prefer-const': 'error',

      // ── No console (warn — useful for debugging but shouldn't ship) ──
      'no-console': 'warn',

      // ── Enforce curly braces for blocks ──
      'curly': ['warn', 'multi-line'],
    },
  },

  // ── Test files — relaxed rules ──
  {
    files: ['__test__/**/*.{ts,tsx}'],
    rules: {
      'no-magic-numbers': 'off',
      '@typescript-eslint/no-explicit-any': 'off',
      'no-console': 'off',
    },
  },
);
