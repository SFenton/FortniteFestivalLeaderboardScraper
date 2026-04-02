import type { PlayerScore } from '@festival/core/api/serverTypes';
import type { SongSortMode } from './songSettings';

/** Compare two PlayerScores by a given sort mode; undefined scores sort last. */
export function compareByMode(mode: SongSortMode, a?: PlayerScore, b?: PlayerScore): number {
  if (!a && !b) return 0;
  if (!a) return 1;
  if (!b) return -1;
  switch (mode) {
    case 'score':
      return a.score - b.score;
    case 'percentage': {
      const pa = a.accuracy ?? 0;
      const pb = b.accuracy ?? 0;
      if (pa !== pb) return pa - pb;
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    }
    case 'percentile': {
      const pa = a.rank > 0 && (a.totalEntries ?? 0) > 0 ? a.rank / a.totalEntries! : Infinity;
      const pb = b.rank > 0 && (b.totalEntries ?? 0) > 0 ? b.rank / b.totalEntries! : Infinity;
      return pa - pb;
    }
    case 'stars':
      return (a.stars ?? 0) - (b.stars ?? 0);
    case 'seasonachieved':
      return (a.season ?? 0) - (b.season ?? 0);
    case 'hasfc':
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    /* v8 ignore start -- exhaustive guard: all valid sort modes handled above */
    default:
      return 0;
    /* v8 ignore stop */
  }
}
