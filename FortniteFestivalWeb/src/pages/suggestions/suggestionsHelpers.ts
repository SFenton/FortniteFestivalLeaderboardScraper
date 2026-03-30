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

// ── Category i18n key resolver ──

/** Decade regex for keys like `near_fc_any_decade_80`. */
const DECADE_RE = /_decade_(\d{2})$/;

function decadeLabel(twoDigit: string): string {
  return twoDigit === '00' ? "00's" : `${twoDigit}'s`;
}

/**
 * Maps a suggestion category key to an i18n key + interpolation params.
 * The returned `i18nKey` is under the `"suggestionCategory"` namespace.
 * Returns `null` for unknown keys — the caller should fall back to the
 * raw title/description from the generator.
 */
export function resolveCategoryI18n(
  categoryKey: string,
  rawTitle: string,
  _rawDesc: string,
): { titleKey: string; descKey: string; params: Record<string, string> } | null {
  const k = categoryKey.toLowerCase();

  // Extract decade suffix if present
  const decadeMatch = DECADE_RE.exec(k);
  const decade = decadeMatch ? decadeLabel(decadeMatch[1]!) : undefined;
  const baseKey = decadeMatch ? k.replace(DECADE_RE, '') : k;

  // Extract rival name from raw title for rival categories
  const rivalName = extractRivalParam(k, rawTitle);

  // Extract artist from raw title for artist categories
  const artistName = extractArtistParam(baseKey, rawTitle);

  // Extract instrument from key
  const instrument = extractInstrumentParam(baseKey);

  // Build params
  const params: Record<string, string> = {};
  if (decade) params.decade = decade;
  if (rivalName) params.rival = rivalName;
  if (artistName) params.artist = artistName;
  if (instrument) params.instrument = instrument;

  // Resolve base i18n key
  const i18nBase = resolveBaseKey(baseKey, k, params);
  if (!i18nBase) return null;

  return {
    titleKey: `suggestionCategory.${i18nBase}.title`,
    descKey: `suggestionCategory.${i18nBase}.desc`,
    params,
  };
}

const INSTRUMENT_LABELS: Record<string, string> = {
  guitar: 'Guitar', bass: 'Bass', drums: 'Drums', vocals: 'Vocals',
  pro_guitar: 'Pro Guitar', pro_bass: 'Pro Bass',
};

const INSTRUMENT_KEYS = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'] as const;

function extractRivalParam(key: string, title: string): string | undefined {
  // Rival categories embed the rival name in the title
  if (!key.startsWith('song_rival_') && !key.startsWith('lb_rival_')) return undefined;
  // Title patterns: "Close the Gap vs RivalName", "Rival Spotlight: RivalName", etc.
  // The generator embeds the display name — extract from raw title
  const vsMatch = /vs\s+(.+)$/.exec(title);
  if (vsMatch) return vsMatch[1]!.trim();
  const colonMatch = /:\s+(.+)$/.exec(title);
  if (colonMatch) return colonMatch[1]!.trim();
  const beatMatch = /Beat\s+(.+?)!?$/.exec(title);
  if (beatMatch) return beatMatch[1]!.trim();
  const dominateMatch = /^Dominate\s+(.+)$/.exec(title);
  if (dominateMatch) return dominateMatch[1]!.trim();
  const pullingMatch = /^(.+)\s+is\s+Pulling/.exec(title);
  if (pullingMatch) return pullingMatch[1]!.trim();
  const crushMatch = /crushing\s+(.+)\s+on/.exec(title);
  if (crushMatch) return crushMatch[1]!.trim();
  const starsMatch = /Stars\s+&\s+Beat\s+(.+)$/.exec(title);
  if (starsMatch) return starsMatch[1]!.trim();
  const pastMatch = /Past\s+(.+)$/.exec(title);
  if (pastMatch) return pastMatch[1]!.trim();
  return undefined;
}

function extractArtistParam(key: string, title: string): string | undefined {
  if (key.startsWith('artist_sampler_')) {
    // Title: "ArtistName Essentials"
    return title.replace(/\s+Essentials$/, '');
  }
  if (key.startsWith('artist_unplayed_')) {
    // Title: "Discover ArtistName"
    return title.replace(/^Discover\s+/, '');
  }
  return undefined;
}

function extractInstrumentParam(key: string): string | undefined {
  for (const ins of INSTRUMENT_KEYS) {
    if (key.endsWith(`_${ins}`)) return INSTRUMENT_LABELS[ins];
  }
  return undefined;
}

function stripInstrumentSuffix(key: string): string {
  for (const ins of INSTRUMENT_KEYS) {
    if (key.endsWith(`_${ins}`)) return key.slice(0, -(ins.length + 1));
  }
  return key;
}

function resolveBaseKey(baseKey: string, _fullKey: string, params: Record<string, string>): string | null {
  const hasDecade = !!params.decade;
  const hasInstrument = !!params.instrument;
  const keyWithoutInstrument = stripInstrumentSuffix(baseKey);

  // Song rival strategies
  if (baseKey.startsWith('song_rival_gap_')) return 'song_rival_gap';
  if (baseKey.startsWith('song_rival_protect_')) return 'song_rival_protect';
  if (baseKey === 'song_rival_battleground') return 'song_rival_battleground';
  if (baseKey.startsWith('song_rival_spotlight_')) return 'song_rival_spotlight';
  if (baseKey.startsWith('song_rival_slipping_')) return 'song_rival_slipping';
  if (baseKey.startsWith('song_rival_dominate_')) return 'song_rival_dominate';
  if (baseKey === 'song_rival_near_fc') return 'song_rival_near_fc';
  if (baseKey === 'song_rival_stale') return 'song_rival_stale';
  if (baseKey === 'song_rival_star_gains') return 'song_rival_star_gains';
  if (baseKey === 'song_rival_pct_push') return 'song_rival_pct_push';

  // Leaderboard rival strategies
  if (baseKey.startsWith('lb_rival_')) return baseKey.replace(/_[a-f0-9]{20,}/, '');

  // Near FC
  if (baseKey === 'near_fc_any') return hasDecade ? 'near_fc_any_decade' : 'near_fc_any';
  if (baseKey === 'near_fc_relaxed') return hasDecade ? 'near_fc_relaxed_decade' : 'near_fc_relaxed';

  // Star progress
  if (baseKey === 'almost_six_star') return hasDecade ? 'almost_six_star_decade' : 'almost_six_star';
  if (baseKey === 'star_gains') return hasDecade ? 'star_gains_decade' : 'star_gains';
  if (baseKey === 'more_stars') return hasDecade ? 'more_stars_decade' : 'more_stars';

  // Unfc
  if (keyWithoutInstrument === 'unfc') return hasDecade ? 'unfc_instrument_decade' : 'unfc_instrument';

  // Unplayed
  if (baseKey === 'unplayed_any') return hasDecade ? 'unplayed_any_decade' : 'unplayed_any';
  if (keyWithoutInstrument === 'unplayed' && hasInstrument) return hasDecade ? 'unplayed_instrument_decade' : 'unplayed_instrument';

  // Artist
  if (baseKey.startsWith('artist_sampler_')) return 'artist_sampler';
  if (baseKey.startsWith('artist_unplayed_')) return 'artist_unplayed';
  if (baseKey === 'variety_pack' || baseKey.startsWith('variety_pack')) return 'variety_pack';

  // First plays mixed
  if (baseKey === 'first_plays_mixed') return hasDecade ? 'first_plays_mixed_decade' : 'first_plays_mixed';

  // Same name
  if (baseKey.startsWith('samename_nearfc_')) return 'samename_nearfc';
  if (baseKey.startsWith('samename_')) return 'samename';

  // Almost elite
  if (baseKey === 'almost_elite') return hasDecade ? 'almost_elite_decade' : 'almost_elite';
  if (keyWithoutInstrument === 'almost_elite' && hasInstrument) return hasDecade ? 'almost_elite_instrument_decade' : 'almost_elite_instrument';

  // Percentile push
  if (baseKey === 'pct_push') return hasDecade ? 'pct_push_decade' : 'pct_push';
  if (keyWithoutInstrument === 'pct_push' && hasInstrument) return hasDecade ? 'pct_push_instrument_decade' : 'pct_push_instrument';

  // Stale
  if (baseKey.startsWith('stale_global_') || baseKey === 'stale_global') {
    const seasons = baseKey.match(/\d+/)?.[0];
    if (seasons) params.seasons = seasons;
    return 'stale_global';
  }
  if (baseKey.startsWith('stale_') && hasInstrument) {
    const seasons = baseKey.match(/\d+/)?.[0];
    if (seasons) params.seasons = seasons;
    return 'stale_instrument';
  }

  // Percentile improvements
  if (baseKey === 'same_pct_improve') return 'same_pct_improve';
  if (baseKey.startsWith('same_pct_bucket_')) {
    const bucket = baseKey.match(/\d+/)?.[0];
    if (bucket) { params.bucket = bucket; params.target = `Top ${Number(bucket) - 1}%`; }
    return 'same_pct_bucket';
  }
  if (keyWithoutInstrument.startsWith('pct_improve_') && hasInstrument) {
    const bucket = keyWithoutInstrument.match(/\d+/)?.[0];
    if (bucket) params.bucket = bucket;
    return 'pct_improve_instrument_bucket';
  }
  if (baseKey.startsWith('pct_improve_')) {
    const bucket = baseKey.match(/\d+/)?.[0];
    if (bucket) params.bucket = bucket;
    return 'pct_improve_bucket';
  }
  if (keyWithoutInstrument === 'improve_rankings' && hasInstrument) return 'improve_rankings_instrument';

  // Near max score
  if (baseKey.startsWith('near_max_')) {
    // Extract tier from key: near_max_5k, near_max_10k, near_max_15k
    const tier = baseKey.match(/near_max_(\d+k)/)?.[1];
    if (tier) return hasDecade ? `near_max_${tier}_decade` : `near_max_${tier}`;
  }

  return null;
}
