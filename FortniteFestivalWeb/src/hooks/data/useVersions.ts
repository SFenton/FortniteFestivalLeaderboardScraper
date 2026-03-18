declare const __APP_VERSION__: string;
declare const __CORE_VERSION__: string;

export const APP_VERSION: string = typeof __APP_VERSION__ !== 'undefined' ? __APP_VERSION__ : '0.0.0';
export const CORE_VERSION: string = typeof __CORE_VERSION__ !== 'undefined' ? __CORE_VERSION__ : '0.0.0';
