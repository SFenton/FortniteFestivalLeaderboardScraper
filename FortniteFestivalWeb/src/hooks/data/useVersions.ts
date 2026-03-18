declare const __APP_VERSION__: string;
declare const __CORE_VERSION__: string;

/* v8 ignore start — build-time Vite define replacements; not available in test runner */
export const APP_VERSION: string = typeof __APP_VERSION__ !== 'undefined' ? __APP_VERSION__ : '0.0.0';
export const CORE_VERSION: string = typeof __CORE_VERSION__ !== 'undefined' ? __CORE_VERSION__ : '0.0.0';
/* v8 ignore stop */
