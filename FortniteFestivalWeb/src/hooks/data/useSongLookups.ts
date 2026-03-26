import { useMemo } from 'react';
import { useFestival } from '../../contexts/FestivalContext';

/**
 * Derived lookup maps built from the song catalog.
 * Memoized per-component — doesn't add to FestivalContext re-render surface.
 */
export function useSongLookups() {
  const { state: { songs } } = useFestival();

  const albumArtMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const song of songs) {
      if (song.albumArt) map.set(song.songId, song.albumArt);
    }
    return map;
  }, [songs]);

  const yearMap = useMemo(() => {
    const map = new Map<string, number>();
    for (const song of songs) {
      if (song.year) map.set(song.songId, song.year);
    }
    return map;
  }, [songs]);

  return { albumArtMap, yearMap };
}
