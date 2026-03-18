/**
 * Test setup for @festival/core tests.
 *
 * Registers the core en.json translations with the core i18n registry
 * so that functions like formatScoreCompact, formatPercentileBucket, etc.
 * return resolved English strings instead of raw i18n keys.
 */
import {setTranslationFunction} from '../i18n';
import en from '../i18n/en.json';

// Flatten nested translation object into dot-delimited keys:
// { format: { na: "N/A" } } → { "format.na": "N/A" }
function flatten(obj: Record<string, unknown>, prefix = ''): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(obj)) {
    const fullKey = prefix ? `${prefix}.${key}` : key;
    if (typeof value === 'object' && value !== null && !Array.isArray(value)) {
      Object.assign(result, flatten(value as Record<string, unknown>, fullKey));
    } else {
      result[fullKey] = String(value);
    }
  }
  return result;
}

const translations = flatten(en as Record<string, unknown>);

// Simple interpolation: replace {{key}} with options[key]
function interpolate(template: string, options?: Record<string, unknown>): string {
  if (!options) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (_, key) => {
    const val = options[key];
    return val != null ? String(val) : `{{${key}}}`;
  });
}

setTranslationFunction((key: string, options?: Record<string, unknown>): string => {
  const template = translations[key];
  if (template == null) return key;
  return interpolate(template, options);
});
