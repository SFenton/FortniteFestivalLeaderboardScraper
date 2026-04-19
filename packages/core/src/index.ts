export * from './instruments';
export * from './combos';
export * from './enums';
export * from './keys';
export * from './stars';
export * from './settings';
export * from './models';
export * from './httpErrorHelper';
export * from './concurrency';
export * from './persistence';
export * from './songListConfig';
export * from './instrumentFilters';
export * from './api/serverTypes';
export * from './suggestions/types';
export * from './suggestions/suggestionGenerator';
export * from './suggestions/suggestionFilterConfig';

// ── App-level pure logic (view-model / filtering / formatting) ──
export * from './app/formatters';

/** Current app version. Keep in sync with package.json. */
export const APP_VERSION = '0.0.3';

/** Current @festival/core package version. Keep in sync with packages/core/package.json. */
export const CORE_VERSION = '0.0.15';

/** Current @festival/theme package version. Keep in sync with packages/theme/package.json. */
export const THEME_VERSION = '0.0.9';
export * from './app/scoreRows';
export * from './app/songInfo';
export * from './app/songFiltering';
export * from './app/statistics';
export * from './app/logBuffer';
export * from './app/progress';
export * from './app/findIndexBy';
