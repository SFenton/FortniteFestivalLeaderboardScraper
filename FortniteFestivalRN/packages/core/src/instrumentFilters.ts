/**
 * Instrument-visibility filter helpers shared across screens.
 *
 * These complement `isInstrumentVisible` / `InstrumentShowSettings` already
 * exported from `songListConfig.ts`.
 */
import type {InstrumentKey} from './instruments';
import type {InstrumentShowSettings} from './songListConfig';
import {isInstrumentVisible} from './songListConfig';
import type {SuggestionCategory} from './suggestions/types';

/**
 * Determine whether a suggestion / statistics category should be shown
 * based on its key string and the current instrument-visibility settings.
 *
 * The key is matched case-insensitively against known instrument fragments
 * (pro variants checked first to avoid false positives).
 */
export const shouldShowCategory = (
  categoryKey: string,
  settings: InstrumentShowSettings,
): boolean => {
  const key = categoryKey.toLowerCase();
  if (key.includes('pro_guitar') || key.includes('prolead') || key.includes('pro_lead')) return settings.showProLead;
  if (key.includes('pro_bass') || key.includes('probass')) return settings.showProBass;
  if (key.includes('guitar') || key.includes('lead')) return settings.showLead;
  if (key.includes('bass')) return settings.showBass;
  if (key.includes('drums')) return settings.showDrums;
  if (key.includes('vocals') || key.includes('vocal')) return settings.showVocals;
  return true;
};

/**
 * Filter songs within a category to remove items for hidden instruments,
 * then drop the entire category if no songs remain.
 *
 * Single-instrument categories are typically excluded earlier by
 * `shouldShowCategory`; this handles multi-instrument categories where
 * individual song items may belong to different instruments.
 */
export const filterCategoryForInstruments = (
  cat: SuggestionCategory,
  settings: InstrumentShowSettings,
): SuggestionCategory | null => {
  const filtered = cat.songs.filter(
    s => !s.instrumentKey || isInstrumentVisible(s.instrumentKey, settings),
  );
  if (filtered.length === 0) return null;
  if (filtered.length === cat.songs.length) return cat;
  return {...cat, songs: filtered};
};
