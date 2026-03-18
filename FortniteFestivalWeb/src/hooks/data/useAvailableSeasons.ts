/**
 * Returns the list of available seasons (1..currentSeason).
 * Sources the current season from the songs API via FestivalContext.
 */
import { useMemo } from 'react';
import { useFestival } from '../../contexts/FestivalContext';

export function useAvailableSeasons(): number[] {
  const { state: { currentSeason } } = useFestival();
  return useMemo(() => {
    if (currentSeason <= 0) return [];
    return Array.from({ length: currentSeason }, (_, i) => i + 1);
  }, [currentSeason]);
}
