/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Grid of ShopCards showing real item shop album art.
 * Measures the container width to compute actual card size (aspect-ratio 1:1),
 * then fits as many rows as the slide height allows: min 2×2, max 5 cols.
 */
import { useState, useCallback, useMemo, type CSSProperties } from 'react';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useItemShopDemoSongs } from '../../../../hooks/data/useItemShopDemoSongs';
import {
  Colors, Gap, Radius, Border, CssValue, PointerEvents, Display, Overflow, ObjectFit, border,
} from '@festival/theme';

const GRID_GAP = Gap.sm;
const MIN_COLS = 2;
const MAX_COLS = 5;

export default function ShopOverviewDemo() {
  const h = useSlideHeight();
  const budget = h || 280;

  // Measure actual container width so we know the real card height (aspect-ratio 1:1)
  const [containerWidth, setContainerWidth] = useState(0);
  const measureRef = useCallback((node: HTMLDivElement | null) => {
    if (node) setContainerWidth(node.getBoundingClientRect().width);
  }, []);

  // Work out cols → card width → card height → rows that fit
  const { cols, rows, visibleCount } = useMemo(() => {
    if (containerWidth === 0) {
      // Before measurement, pick safe defaults
      return { cols: 3, rows: 2, visibleCount: 6 };
    }
    // Try from MAX_COLS down to MIN_COLS, pick the first that gives ≥ 2 rows
    for (let c = MAX_COLS; c >= MIN_COLS; c--) {
      const cardW = (containerWidth - (c - 1) * GRID_GAP) / c;
      const cardH = cardW; // aspect-ratio 1:1
      const r = Math.floor((budget + GRID_GAP) / (cardH + GRID_GAP));
      if (r >= 2) return { cols: c, rows: r, visibleCount: c * r };
    }
    // Fallback: MIN_COLS × 1 row
    return { cols: MIN_COLS, rows: 1, visibleCount: MIN_COLS };
  }, [containerWidth, budget]);

  const { songs } = useItemShopDemoSongs(visibleCount);

  // Shrink cols if fewer songs than a full grid
  const effectiveCols = Math.max(MIN_COLS, Math.min(cols, Math.ceil(songs.length / Math.max(1, rows))));
  const visible = songs.slice(0, Math.min(songs.length, effectiveCols * rows));

  const s = useStyles(effectiveCols, budget);

  return (
    <div ref={measureRef} style={s.grid}>
      {visible.map((song, i) => (
        <FadeIn key={song.songId} delay={i * 60}>
          {song.albumArt ? (
            <img src={song.albumArt} alt="" style={s.art} loading="lazy" />
          ) : (
            <div style={s.placeholder} />
          )}
        </FadeIn>
      ))}
    </div>
  );
}

function useStyles(cols: number, budget: number) {
  return useMemo(() => ({
    grid: {
      display: Display.grid,
      gridTemplateColumns: `repeat(${cols}, 1fr)`,
      gap: GRID_GAP,
      width: CssValue.full,
      maxHeight: budget,
      pointerEvents: PointerEvents.none,
      overflow: Overflow.hidden,
    } as CSSProperties,
    art: {
      width: CssValue.full,
      aspectRatio: '1',
      objectFit: ObjectFit.cover,
      borderRadius: Radius.md,
      display: Display.block,
      border: border(Border.thin, Colors.glassBorder),
    } as CSSProperties,
    placeholder: {
      width: CssValue.full,
      aspectRatio: '1',
      borderRadius: Radius.md,
      backgroundColor: Colors.accentPurpleDark,
      border: border(Border.thin, Colors.glassBorder),
    } as CSSProperties,
  }), [cols, budget]);
}
