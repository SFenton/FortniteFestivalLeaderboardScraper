declare const __APP_VERSION__: string;
declare const __APP_COMMIT__: string;
declare const __CORE_VERSION__: string;
declare const __THEME_VERSION__: string;

/* v8 ignore start — build-time Vite define replacements; not available in test runner */
export const APP_VERSION: string = typeof __APP_VERSION__ !== 'undefined' ? __APP_VERSION__ : '0.0.0';
export const APP_COMMIT: string = typeof __APP_COMMIT__ !== 'undefined' ? __APP_COMMIT__ : '';
export const CORE_VERSION: string = typeof __CORE_VERSION__ !== 'undefined' ? __CORE_VERSION__ : '0.0.0';
export const THEME_VERSION: string = typeof __THEME_VERSION__ !== 'undefined' ? __THEME_VERSION__ : '0.0.0';
/* v8 ignore stop */

export function formatAppVersionLabel(version: string, commit: string): string {
  return commit ? `${version} · ${commit}` : version;
}

export const APP_VERSION_LABEL: string = formatAppVersionLabel(APP_VERSION, APP_COMMIT);
