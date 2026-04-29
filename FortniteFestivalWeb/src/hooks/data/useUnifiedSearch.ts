import { useEffect, useRef, useState } from 'react';
import { DEBOUNCE_MS } from '@festival/theme';
import type { AccountSearchResult, BandSearchResult, ServerSong } from '@festival/core/api/serverTypes';
import { api } from '../../api/client';
import { useFestival } from '../../contexts/FestivalContext';
import type { SearchTarget } from '../../types/search';
import { songMatchesSearch } from '../../utils/songSearch';

type SearchTargetFlags = Record<SearchTarget, boolean>;

const EMPTY_FLAGS: SearchTargetFlags = { songs: false, players: false, bands: false };

export interface UnifiedSearchState {
  debouncedQuery: string;
  songResults: ServerSong[];
  playerResults: AccountSearchResult[];
  bandResults: BandSearchResult[];
  loading: SearchTargetFlags;
  errors: SearchTargetFlags;
  debouncing: boolean;
}


interface UnifiedSearchOptions {
  debounceMs?: number;
  songLimit?: number;
  playerLimit?: number;
  bandLimit?: number;
}

function cloneFlags(overrides?: Partial<SearchTargetFlags>): SearchTargetFlags {
  return { ...EMPTY_FLAGS, ...overrides };
}

function filterSongs(songs: ServerSong[], query: string, limit: number): ServerSong[] {
  return songs
    .filter(song => songMatchesSearch(song, query))
    .slice(0, limit);
}

export function useUnifiedSearch(query: string, options?: UnifiedSearchOptions): UnifiedSearchState {
  const { state: { songs } } = useFestival();
  const debounceMs = options?.debounceMs ?? DEBOUNCE_MS;
  const songLimit = options?.songLimit ?? 20;
  const playerLimit = options?.playerLimit ?? 10;
  const bandLimit = options?.bandLimit ?? 10;

  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [songResults, setSongResults] = useState<ServerSong[]>([]);
  const [playerResults, setPlayerResults] = useState<AccountSearchResult[]>([]);
  const [bandResults, setBandResults] = useState<BandSearchResult[]>([]);
  const [loading, setLoading] = useState<SearchTargetFlags>(() => cloneFlags());
  const [errors, setErrors] = useState<SearchTargetFlags>(() => cloneFlags());
  const [debouncing, setDebouncing] = useState(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const requestSeqRef = useRef(0);

  const trimmedQuery = query.trim();

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    requestSeqRef.current += 1;

    if (trimmedQuery.length < 2) {
      setDebouncedQuery('');
      setDebouncing(false);
      setSongResults([]);
      setPlayerResults([]);
      setBandResults([]);
      setLoading(cloneFlags());
      setErrors(cloneFlags());
      return undefined;
    }

    setDebouncing(true);
    debounceRef.current = setTimeout(() => {
      setDebouncedQuery(trimmedQuery);
      setDebouncing(false);
    }, debounceMs);

    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [debounceMs, trimmedQuery]);

  useEffect(() => {
    if (debouncedQuery.length < 2) return;
    setLoading(prev => ({ ...prev, songs: true }));
    setErrors(prev => ({ ...prev, songs: false }));
    setSongResults(filterSongs(songs, debouncedQuery, songLimit));
    setLoading(prev => ({ ...prev, songs: false }));
  }, [debouncedQuery, songLimit, songs]);

  useEffect(() => {
    if (debouncedQuery.length < 2) return undefined;

    const requestSeq = ++requestSeqRef.current;
    let cancelled = false;
    setLoading(prev => ({ ...prev, players: true, bands: true }));
    setErrors(prev => ({ ...prev, players: false, bands: false }));
    setPlayerResults([]);
    setBandResults([]);

    void api.searchAccounts(debouncedQuery, playerLimit)
      .then(response => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setPlayerResults(response.results);
      })
      .catch(() => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setPlayerResults([]);
        setErrors(prev => ({ ...prev, players: true }));
      })
      .finally(() => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setLoading(prev => ({ ...prev, players: false }));
      });

    void api.searchBands({ q: debouncedQuery, page: 1, pageSize: bandLimit })
      .then(response => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setBandResults(response.results);
      })
      .catch(() => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setBandResults([]);
        setErrors(prev => ({ ...prev, bands: true }));
      })
      .finally(() => {
        if (cancelled || requestSeq !== requestSeqRef.current) return;
        setLoading(prev => ({ ...prev, bands: false }));
      });

    return () => { cancelled = true; };
  }, [bandLimit, debouncedQuery, playerLimit]);

  return {
    debouncedQuery,
    songResults,
    playerResults,
    bandResults,
    loading,
    errors,
    debouncing,
  };
}
