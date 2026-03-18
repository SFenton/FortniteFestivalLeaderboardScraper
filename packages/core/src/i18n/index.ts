/**
 * i18n translation function registry for @festival/core.
 *
 * The host app (web or RN) must call `setTranslationFunction()` during
 * initialization to wire up the real i18next.t() instance. Until then,
 * `t()` returns the key unchanged (safe fallback).
 */

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

let _t: TranslateFn = (key: string) => key;

/** Set the translation function. Call once during app init (after i18next.init). */
export function setTranslationFunction(fn: TranslateFn): void {
  _t = fn;
}

/** Translate a key using the registered translation function. */
export const t = (key: string, options?: Record<string, unknown>): string => _t(key, options);
