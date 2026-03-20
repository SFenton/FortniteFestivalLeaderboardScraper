/**
 * Pure helper functions extracted from SuggestionsPage for testability.
 */
import type { SuggestionsFilterDraft } from './modals/SuggestionsFilterModal';
import { defaultSuggestionsFilterDraft } from './modals/SuggestionsFilterModal';
import { globalKeyFor, getCategoryTypeId, getCategoryInstrument, perInstrumentKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';
import type { AppSettings } from '../../contexts/SettingsContext';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import { estimateVisibleCount } from '@festival/ui-utils';

export const FILTER_STORAGE_KEY = 'fst-suggestions-filter';

// ── localStorage helpers ──

export function loadSuggestionsFilter(): SuggestionsFilterDraft {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return { ...defaultSuggestionsFilterDraft(), ...parsed };
    }
  } catch { /* ignore corrupt data */ }
  return defaultSuggestionsFilterDraft();
}

export function saveSuggestionsFilter(draft: SuggestionsFilterDraft) {
  localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(draft));
}

// ── Instrument visibility ──

export type InstrumentShowSettings = {
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;
};

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
  };
}

// ── Category filter helpers ──

export function shouldShowCategoryType(
  categoryKey: string,
  filter: SuggestionsFilterDraft,
): boolean {
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

// ── Season fallback ──

export function computeEffectiveSeason(
  currentSeason: number,
  playerScores: Array<{ season?: number | null }> | null,
): number {
  if (currentSeason > 0) return currentSeason;
  if (!playerScores) return 0;
  let max = 0;
  for (const s of playerScores) {
    if (s.season != null && s.season > max) max = s.season;
  }
  return max;
}

// ── Card animation delay ──

export function getCardDelay(
  index: number,
  skipAnim: boolean,
  phase: string,
  revealedCount: number,
): number | null {
  if (skipAnim) return -1;
  if (phase !== 'contentIn') return null;
  if (index < revealedCount) return -1;
  const offset = index - revealedCount;
  const maxVisible = estimateVisibleCount(200);
  if (offset >= maxVisible) return -1;
  return offset * 125;
}

// ── Album art map builder ──

export function buildAlbumArtMap(
  songs: Array<{ songId: string; albumArt?: string }>,
): Map<string, string> {
  const m = new Map<string, string>();
  for (const s of songs) {
    if (s.albumArt) m.set(s.songId, s.albumArt);
  }
  return m;
}
