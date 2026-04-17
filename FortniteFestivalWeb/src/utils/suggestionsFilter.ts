/**
 * Pure helper functions for the suggestions filter logic.
 * Extracted from SuggestionsPage for testability.
 */
import type { SuggestionsFilterDraft } from '../pages/suggestions/modals/SuggestionsFilterModal';
import type { AppSettings } from '../contexts/SettingsContext';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import { globalKeyFor, getCategoryTypeId, getCategoryInstrument, perInstrumentKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';

const FILTER_STORAGE_KEY = 'fst-suggestions-filter';

export type InstrumentShowSettings = {
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;
  showPeripheralVocals: boolean;
  showPeripheralCymbals: boolean;
  showPeripheralDrums: boolean;
};

export function loadSuggestionsFilter(defaultDraft: () => SuggestionsFilterDraft): SuggestionsFilterDraft {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return { ...defaultDraft(), ...parsed };
    }
  } catch { /* ignore */ }
  return defaultDraft();
}

export function saveSuggestionsFilter(draft: SuggestionsFilterDraft) {
  localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(draft));
}

export function buildEffectiveInstrumentSettings(
  filter: SuggestionsFilterDraft,
  appSettings: AppSettings,
): InstrumentShowSettings {
  return {
    showLead: appSettings.showLead && filter.suggestionsLeadFilter,
    showBass: appSettings.showBass && filter.suggestionsBassFilter,
    showDrums: appSettings.showDrums && filter.suggestionsDrumsFilter,
    showVocals: appSettings.showVocals && filter.suggestionsVocalsFilter,
    showProLead: appSettings.showProLead && filter.suggestionsProLeadFilter,
    showProBass: appSettings.showProBass && filter.suggestionsProBassFilter,
    showPeripheralVocals: appSettings.showPeripheralVocals && filter.suggestionsPeripheralVocalsFilter,
    showPeripheralCymbals: appSettings.showPeripheralCymbals && filter.suggestionsPeripheralCymbalsFilter,
    showPeripheralDrums: appSettings.showPeripheralDrums && filter.suggestionsPeripheralDrumsFilter,
  };
}

export function shouldShowCategoryType(categoryKey: string, filter: SuggestionsFilterDraft): boolean {
  const typeId = getCategoryTypeId(categoryKey);
  if (!typeId) return true;
  return filter[globalKeyFor(typeId)] ?? true;
}

export function filterCategoryForInstrumentTypes(
  cat: SuggestionCategory,
  filter: SuggestionsFilterDraft,
): SuggestionCategory | null {
  const typeId = getCategoryTypeId(cat.key);
  if (!typeId) return cat;
  const catInstrument = getCategoryInstrument(cat.key);
  if (catInstrument) {
    const pk = perInstrumentKeyFor(catInstrument, typeId);
    return (filter[pk] ?? true) ? cat : null;
  }
  const filtered = cat.songs.filter(s => {
    if (!s.instrumentKey) return true;
    const pk = perInstrumentKeyFor(s.instrumentKey, typeId);
    return filter[pk] ?? true;
  });
  if (filtered.length === 0) return null;
  if (filtered.length === cat.songs.length) return cat;
  return { ...cat, songs: filtered };
}
