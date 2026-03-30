import { Keys } from '@festival/core';
import { useState, useCallback, useEffect, useRef } from 'react';
import { api } from '../../api/client';
import { type AccountSearchResult } from '@festival/core/api/serverTypes';
import { DEBOUNCE_MS } from '@festival/theme';

export interface AccountSearchState {
  query: string;
  setQuery: (q: string) => void;
  results: AccountSearchResult[];
  isOpen: boolean;
  activeIndex: number;
  setActiveIndex: (i: number) => void;
  loading: boolean;
  debouncing: boolean;
  resultSeq: number;
  handleChange: (value: string) => void;
  handleKeyDown: (e: React.KeyboardEvent) => void;
  selectResult: (r: AccountSearchResult) => void;
  close: () => void;
  containerRef: React.RefObject<HTMLDivElement | null>;
}

/**
 * Shared hook for account search with debounce, keyboard navigation,
 * and click-outside-to-close. Used by PlayerSearch, HeaderSearch,
 * FAB search, and MobilePlayerSearchModal.
 *
 * @param onSelect  Called when a user selects a search result
 * @param opts.debounceMs  Debounce interval (default: DEBOUNCE_MS from theme)
 * @param opts.limit  Max results to fetch (default: 10)
 */
export function useAccountSearch(
  onSelect: (result: AccountSearchResult) => void,
  opts?: { debounceMs?: number; limit?: number },
): AccountSearchState {
  const debounceMs = opts?.debounceMs ?? DEBOUNCE_MS;
  const limit = opts?.limit ?? 10;

  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [isOpen, setIsOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [debouncing, setDebouncing] = useState(false);
  const [resultSeq, setResultSeq] = useState(0);
  const [activeIndex, setActiveIndex] = useState(-1);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  const search = useCallback(async (q: string) => {
    if (q.length < 2) {
      setResults([]);
      setIsOpen(false);
      return;
    }
    setDebouncing(false);
    setLoading(true);
    try {
      const res = await api.searchAccounts(q, limit);
      setResults(res.results);
      setResultSeq(s => s + 1);
      setIsOpen(res.results.length > 0);
      setActiveIndex(-1);
    } catch {
      setResults([]);
      setIsOpen(false);
    } finally {
      setLoading(false);
    }
  }, [limit]);

  const handleChange = useCallback((value: string) => {
    setQuery(value);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    if (value.trim().length < 2) {
      setDebouncing(false);
      setResults([]);
      setIsOpen(false);
      return;
    }
    setDebouncing(true);
    debounceRef.current = setTimeout(() => {
      void search(value.trim());
    }, debounceMs);
  }, [search, debounceMs]);

  const selectResult = useCallback((r: AccountSearchResult) => {
    onSelect(r);
    setQuery('');
    setResults([]);
    setIsOpen(false);
  }, [onSelect]);

  const close = useCallback(() => setIsOpen(false), []);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (!isOpen || results.length === 0) return;
    if (e.key === Keys.ArrowDown) {
      e.preventDefault();
      setActiveIndex(p => (p < results.length - 1 ? p + 1 : 0));
    } else if (e.key === Keys.ArrowUp) {
      e.preventDefault();
      setActiveIndex(p => (p > 0 ? p - 1 : results.length - 1));
    } else if (e.key === Keys.Enter && activeIndex >= 0) {
      e.preventDefault();
      const selected = results[activeIndex];
      if (selected) selectResult(selected);
    } else if (e.key === Keys.Escape) {
      setIsOpen(false);
    }
  }, [isOpen, results, activeIndex, selectResult]);

  // Click-outside handling
  /* v8 ignore start — DOM contains() check not reliably testable in jsdom */
  useEffect(() => {
    const handleClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);
  /* v8 ignore stop */

  return {
    query, setQuery, results, isOpen, activeIndex, setActiveIndex,
    loading, debouncing, resultSeq, handleChange, handleKeyDown, selectResult, close, containerRef,
  };
}
