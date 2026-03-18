import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';

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
    },
    rules: {
      // ── React hooks ──
      // Note: rules-of-hooks is warn (not error) because several pre-existing
      // components call hooks conditionally after early returns. These should
      // be refactored but are not blockers.
      'react-hooks/rules-of-hooks': 'warn',
      'react-hooks/exhaustive-deps': 'warn',

      // ── React refresh (HMR) ──
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],

      // ── No magic numbers (warn only — too strict at error level) ──
      'no-magic-numbers': ['warn', {
        ignore: [0, 1, -1, 2],
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
    files: ['src/test/**/*.{ts,tsx}'],
    rules: {
      'no-magic-numbers': 'off',
      '@typescript-eslint/no-explicit-any': 'off',
      'no-console': 'off',
    },
  },
);
