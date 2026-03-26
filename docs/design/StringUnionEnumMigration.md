# String Union → Const Enum Migration

## Overview

Replace string union types used for discriminated mode/state switching (`SongSortMode`, `MetadataSortKey`, `SongRowVisualKey`, `SyncPhase`) with `const enum` values. This eliminates magic string comparisons, enables IDE rename support, guarantees exhaustive switch coverage, and minifies to inlined numeric values.

## Current State

### SongSortMode (defined twice — duplication)

**`@festival/core/src/songListConfig.ts`:**
```ts
export type SongSortMode = 'title' | 'artist' | 'year' | 'hasfc' | 'isfc' | 'score' 
  | 'percentage' | 'percentile' | 'stars' | 'seasonachieved' | 'intensity';
```

**`FortniteFestivalWeb/src/utils/songSettings.ts`:**
```ts
export type SongSortMode = 'title' | 'artist' | 'year' | 'shop' | 'hasfc' | 'score'
  | 'percentage' | 'percentile' | 'stars' | 'seasonachieved' | 'intensity';
```

Note: Core has `'isfc'` which web doesn't have; web has `'shop'` which core doesn't. Unification needed.

### MetadataSortKey

```ts
export type MetadataSortKey = 'title' | 'artist' | 'year' | 'score' | 'percentage'
  | 'percentile' | 'isfc' | 'stars' | 'seasonachieved' | 'intensity';
```

### SongRowVisualKey

```ts
export type SongRowVisualKey = 'score' | 'percentage' | 'percentile' | 'stars'
  | 'seasonachieved' | 'intensity';
```

### SyncPhase

```ts
export type SyncPhase = 'idle' | 'backfill' | 'history';
```

## Usage Patterns

### Switch statements (`case 'score':`)
- `SongRow.tsx` — `renderMetadataElement()` (6 cases) + `compareByMode()` (7 cases)
- `useFilteredSongs.ts` — sort comparison
- `SortModal.tsx` — mode selection

### Direct comparisons (`mode === 'title'`)
- `songSettings.ts` — `isInstrumentSortMode()`
- `SongRow.tsx` — `displayOrder` logic
- `SettingsPage.tsx` — song row visual order

### `as const` casts (`sortMode: 'title' as const`)
- `InstrumentStatsSection.tsx` — 5 occurrences
- `OverallSummarySection.tsx` — 2 occurrences
- `SongRow.test.tsx` — 4 occurrences

### localStorage persistence
- `saveSongSettings()` / `loadSongSettings()` in `songSettings.ts`

## Proposed Design

### Enum Definition (`@festival/core/src/songListConfig.ts`)

```ts
/** Sort modes for the song list. */
export const enum SongSortMode {
  Title = 0,
  Artist = 1,
  Year = 2,
  Shop = 3,
  HasFC = 4,
  IsFC = 5,
  Score = 6,
  Percentage = 7,
  Percentile = 8,
  Stars = 9,
  SeasonAchieved = 10,
  Intensity = 11,
}

/** Metadata sort keys (subset of SongSortMode used for visual order). */
export const enum MetadataSortKey {
  Title = SongSortMode.Title,
  Artist = SongSortMode.Artist,
  Year = SongSortMode.Year,
  Score = SongSortMode.Score,
  Percentage = SongSortMode.Percentage,
  Percentile = SongSortMode.Percentile,
  IsFC = SongSortMode.IsFC,
  Stars = SongSortMode.Stars,
  SeasonAchieved = SongSortMode.SeasonAchieved,
  Intensity = SongSortMode.Intensity,
}

/** Song row visual order keys. */
export const enum SongRowVisualKey {
  Score = SongSortMode.Score,
  Percentage = SongSortMode.Percentage,
  Percentile = SongSortMode.Percentile,
  Stars = SongSortMode.Stars,
  SeasonAchieved = SongSortMode.SeasonAchieved,
  Intensity = SongSortMode.Intensity,
}

/** Sync/backfill phase. */
export const enum SyncPhase {
  Idle = 0,
  Backfill = 1,
  History = 2,
}
```

### Serialization Adapter (`songSettings.ts`)

```ts
const SORT_MODE_STRINGS: Record<string, SongSortMode> = {
  title: SongSortMode.Title,
  artist: SongSortMode.Artist,
  year: SongSortMode.Year,
  shop: SongSortMode.Shop,
  hasfc: SongSortMode.HasFC,
  isfc: SongSortMode.IsFC,
  score: SongSortMode.Score,
  percentage: SongSortMode.Percentage,
  percentile: SongSortMode.Percentile,
  stars: SongSortMode.Stars,
  seasonachieved: SongSortMode.SeasonAchieved,
  intensity: SongSortMode.Intensity,
};

const SORT_MODE_TO_STRING = Object.fromEntries(
  Object.entries(SORT_MODE_STRINGS).map(([k, v]) => [v, k])
) as Record<SongSortMode, string>;

/** Deserialize a string sort mode from localStorage. */
export function parseSortMode(raw: string): SongSortMode {
  return SORT_MODE_STRINGS[raw] ?? SongSortMode.Title;
}

/** Serialize a sort mode enum to string for localStorage. */
export function serializeSortMode(mode: SongSortMode): string {
  return SORT_MODE_TO_STRING[mode] ?? 'title';
}
```

### Caller Migration Examples

**Before:**
```ts
case 'score':
  return a.score - b.score;
```

**After:**
```ts
case SongSortMode.Score:
  return a.score - b.score;
```

**Before:**
```ts
return { ...s, instrument: inst, sortMode: 'title' as const, sortAscending: true, ... };
```

**After:**
```ts
return { ...s, instrument: inst, sortMode: SongSortMode.Title, sortAscending: true, ... };
```

## Migration Steps

1. Define enums in `@festival/core/src/songListConfig.ts`
2. Add serialization adapters to `songSettings.ts`
3. Update `loadSongSettings()` / `saveSongSettings()` to use adapters
4. Update all switch/case sites (estimated 30+ files)
5. Update all comparison sites
6. Remove `as const` casts on sort mode literals
7. Delete the duplicate `SongSortMode` type from `FortniteFestivalWeb/src/utils/songSettings.ts`
8. Update tests

## Risks

- **localStorage backward compat**: Existing users have string values stored. The `parseSortMode()` adapter handles this transparently.
- **Cross-package alignment**: Core and web have slightly different enum variants (`isfc` vs `hasfc`, `shop` only in web). The unified enum includes all variants; each consumer uses the subset it supports.
- **i18n display names**: `INSTRUMENT_SORT_MODES` currently uses `{ mode: 'score', label: 'Score' }`. After migration: `{ mode: SongSortMode.Score, label: t('metadata.score') }` — also fixes hardcoded labels.

## Verification

- `grep -rn "as const" src/ | grep -i "sort\|mode"` returns zero matches
- All switch statements use `SongSortMode.Xxx` enum constants
- TypeScript reports error for any unhandled enum case in switch
- localStorage settings round-trip correctly after upgrade
- Existing settings migrate transparently without user action
