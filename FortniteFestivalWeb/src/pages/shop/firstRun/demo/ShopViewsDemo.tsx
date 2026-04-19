/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Cycles between grid and list view of shop songs
 * every 5 seconds to demonstrate the view toggle feature.
 * Grid uses the same container-measurement sizing as ShopOverviewDemo.
 * Grid shows album art only (no title/artist); list shows SongInfo rows.
 */
import { useState, useEffect, useRef, useCallback, useMemo, type CSSProperties } from 'react';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useItemShopDemoSongs } from '../../../../hooks/data/useItemShopDemoSongs';
import {
  Colors, Gap, Radius, Layout, Border, Display, Align, Overflow, ObjectFit,
  CssValue, PointerEvents, Opacity, frostedCard, flexColumn, padding, border,
  FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

const GRID_GAP = Gap.sm;
const MIN_COLS = 2;
const MAX_COLS = 5;
const LIST_ROW_HEIGHT = Layout.demoRowHeight;

export default function ShopViewsDemo() {
  const h = useSlideHeight();
  const budget = h || 280;
  const [viewMode, setViewMode] = useState<'grid' | 'list'>('grid');
  const [visible, setVisible] = useState(true);
  const modeRef = useRef<'grid' | 'list'>('grid');

  // Measure container width for grid card sizing
  const [containerWidth, setContainerWidth] = useState(0);
  const measureRef = useCallback((node: HTMLDivElement | null) => {
    if (node) setContainerWidth(node.getBoundingClientRect().width);
  }, []);

  const rotate = useCallback(() => {
    setVisible(false);
    setTimeout(() => {
      modeRef.current = modeRef.current === 'grid' ? 'list' : 'grid';
      setViewMode(modeRef.current);
      requestAnimationFrame(() => setVisible(true));
    }, FADE_DURATION);
  }, []);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  useEffect(() => {
    const raf = requestAnimationFrame(() => setVisible(true));
    return () => cancelAnimationFrame(raf);
  }, []);

  // Grid layout: measure-based sizing (same approach as ShopOverviewDemo)
  const { gridCols, gridRows } = useMemo(() => {
    if (containerWidth === 0) return { gridCols: 3, gridRows: 2 };
    for (let c = MAX_COLS; c >= MIN_COLS; c--) {
      const cardW = (containerWidth - (c - 1) * GRID_GAP) / c;
      const r = Math.floor((budget + GRID_GAP) / (cardW + GRID_GAP));
      if (r >= 2) return { gridCols: c, gridRows: r };
    }
    return { gridCols: MIN_COLS, gridRows: 1 };
  }, [containerWidth, budget]);

  const gridCount = gridCols * gridRows;
  const listCount = Math.max(1, Math.floor(budget / (LIST_ROW_HEIGHT + Gap.sm)));
  const maxNeeded = Math.max(gridCount, listCount);

  const { songs } = useItemShopDemoSongs(maxNeeded);

  const gridVisible = songs.slice(0, Math.min(songs.length, gridCount));
  const listVisible = songs.slice(0, Math.min(songs.length, listCount));

  const s = useStyles(gridCols, budget);

  const transStyle: CSSProperties = {
    transition: `opacity ${FADE_DURATION}ms ease, transform ${FADE_DURATION}ms ease`,
    opacity: visible ? 1 : Opacity.none,
    transform: visible ? 'translateY(0)' : 'translateY(6px)',
  };

  return (
    <div ref={measureRef} style={{ ...s.wrapper, ...transStyle }}>
      {viewMode === 'grid' ? (
        <div style={s.grid}>
          {gridVisible.map(song => (
            song.albumArt ? (
              <img key={song.songId} src={song.albumArt} alt="" style={s.art} loading="lazy" />
            ) : (
              <div key={song.songId} style={s.placeholder} />
            )
          ))}
        </div>
      ) : (
        <div style={s.list}>
          {listVisible.map(song => (
            <div key={song.songId} style={s.listRow}>
              <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function useStyles(gridCols: number, budget: number) {
  return useMemo(() => ({
    wrapper: {
      width: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    grid: {
      display: Display.grid,
      gridTemplateColumns: `repeat(${gridCols}, 1fr)`,
      gap: GRID_GAP,
      maxHeight: budget,
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
    list: {
      ...flexColumn,
      gap: Gap.sm,
      maxHeight: budget,
      overflow: Overflow.hidden,
    } as CSSProperties,
    listRow: {
      ...frostedCard,
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xl,
      padding: padding(0, Gap.xl),
      height: Layout.demoRowHeight,
      borderRadius: Radius.md,
    } as CSSProperties,
  }), [gridCols, budget]);
}
