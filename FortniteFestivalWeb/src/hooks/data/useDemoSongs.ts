/**
 * Shared hook for first-run demo slides that display song rows.
 *
 * - Pulls real songs from FestivalContext, filtered to artist containing "Epic Games".
 * - Falls back to a hardcoded pool when the API hasn't loaded yet.
 * - Fits as many rows as the slide allows via SlideHeightContext.
 * - Rotates one row every SWAP_INTERVAL_MS: fade-out → swap → fade-in.
 * - Rotation order is deterministic (shuffled once, then cycled).
 */
import { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import { FADE_DURATION, DEMO_SWAP_INTERVAL_MS, STAGGER_INTERVAL, Layout } from '@festival/theme';
import type { SongDisplay as DemoSong } from '@festival/core/api/serverTypes';
import { useFestival } from '../../contexts/FestivalContext';
import { useSlideHeight } from '../../firstRun/SlideHeightContext';

export type { SongDisplay as DemoSong } from '@festival/core/api/serverTypes';

/** Re-export for consumers that need the fade timing. */
export const FADE_MS = FADE_DURATION;

/* ── Helpers ── */

export function shuffle<T>(arr: readonly T[]): T[] {
  const a = [...arr];
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j]!, a[i]!];
  }
  return a;
}

export function fitRows(containerHeight: number, rowHeight: number): number {
  if (containerHeight <= 0) return 1;
  return Math.max(1, Math.floor((containerHeight + Layout.demoRowGap) / (rowHeight + Layout.demoRowGap)));
}

/* ── Hook ── */

export interface UseDemoSongsOptions {
  rowHeight: number;
  mobileRowHeight: number;
  isMobile: boolean;
  /** When false, disables the auto-swap cycle so the consumer can manage its own. Default: true. */
  autoSwap?: boolean;
}

export interface UseDemoSongsResult {
  /** Current visible rows. Indices are stable (0..n-1). */
  rows: DemoSong[];
  /** Set of row indices currently fading out. */
  fadingIdx: ReadonlySet<number>;
  /** True once the initial fade-in animation is complete. */
  initialDone: boolean;
  /** The full shuffled song pool. */
  pool: DemoSong[];
}

const INITIAL_ROW_COUNT = 3;
const SMALL_POOL = 3;
const MEDIUM_POOL = 6;
const MAX_DEDUP_ATTEMPTS = 10;
const INITIAL_DONE_BUFFER_MS = 100;

export function useDemoSongs({ rowHeight, mobileRowHeight, isMobile, autoSwap = true }: UseDemoSongsOptions): UseDemoSongsResult {
  const { state: { songs: apiSongs } } = useFestival();
  const h = useSlideHeight();

  // Build song pool from real songs filtered by "Epic Games".
  const pool = useMemo(() => {
    const epicSongs: DemoSong[] = apiSongs
      .filter(s => s.artist.includes('Epic Games') && s.albumArt)
      .map(s => ({ title: s.title, artist: s.artist, year: s.year ?? 0, albumArt: s.albumArt! }));
    return shuffle(epicSongs);
  }, [apiSongs]);

  const [rows, setRows] = useState<DemoSong[]>(() => pool.slice(0, INITIAL_ROW_COUNT));
  const [fadingIdx, setFadingIdx] = useState<ReadonlySet<number>>(new Set());
  const lastSwappedRef = useRef<string>('');
  const [initialDone, setInitialDone] = useState(false);

  // Build rows from pool when height or pool changes.
  const buildRows = useCallback((count: number): DemoSong[] => {
    return pool.slice(0, count);
  }, [pool]);

  useEffect(() => {
    if (!h) return;
    const rh = isMobile ? mobileRowHeight : rowHeight;
    const count = Math.min(fitRows(h, rh), pool.length);
    setRows(prev => {
      if (prev.length === count) return prev;
      const next = buildRows(count);
      return next;
    });
  }, [h, isMobile, rowHeight, mobileRowHeight, pool.length, buildRows]);

  // Mark initial done — wait for the last row's stagger + animation to finish.
  useEffect(() => {
    const totalMs = Math.max(0, rows.length - 1) * STAGGER_INTERVAL + FADE_MS + INITIAL_DONE_BUFFER_MS;
    const timer = setTimeout(() => setInitialDone(true), totalMs);
    return () => clearTimeout(timer);
  }, [rows.length]);

  // Swap cycle (when autoSwap is enabled).
  useEffect(() => {
    if (!autoSwap || rows.length === 0) return;

    const timer = setInterval(() => {
      if (rows.length === 0) return;
      const swapCount = rows.length <= SMALL_POOL ? 1 : rows.length <= MEDIUM_POOL ? 2 : SMALL_POOL;
      const allIndices = Array.from({ length: rows.length }, (_, i) => i);

      // Pick N random unique indices, avoiding the exact previous combination.
      let indices: number[];
      let key: string;
      let attempts = 0;
      do {
        const shuffled = shuffle(allIndices);
        indices = shuffled.slice(0, swapCount);
        key = [...indices].sort().join(',');
        attempts++;
      } while (key === lastSwappedRef.current && attempts < MAX_DEDUP_ATTEMPTS);
      lastSwappedRef.current = key;

      setFadingIdx(new Set(indices));

      setTimeout(() => {
        setRows(prev => {
          const visibleTitles = new Set(prev.map(r => r.title));
          const next = [...prev];
          for (const idx of indices) {
            if (idx >= prev.length) continue;
            const available = pool.filter(s => !visibleTitles.has(s.title));
            const source = available.length > 0 ? available : pool;
            const newSong = source[Math.floor(Math.random() * source.length)]!;
            next[idx] = newSong;
            visibleTitles.delete(prev[idx]!.title);
            visibleTitles.add(newSong.title);
          }
          return next;
        });
        setFadingIdx(new Set());
      }, FADE_MS);
    }, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [autoSwap, rows.length, pool]);

  return { rows, fadingIdx, initialDone, pool };
}
