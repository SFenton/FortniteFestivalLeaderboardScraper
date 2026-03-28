/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useCallback, useRef, useMemo, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigationType, useLocation } from 'react-router-dom';
import { IoGrid, IoList } from 'react-icons/io5';
import { LoadPhase } from '@festival/core';
import { useShopState } from '../../hooks/data/useShopState';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../hooks/ui/useMediaQuery';
import { useViewTransition } from '../../hooks/ui/useViewTransition';
import { ActionPill } from '../../components/common/ActionPill';
import { Size, QUERY_NARROW_GRID, Colors, Font, Gap, flexColumn } from '@festival/theme';
import { staggerDelay as calcStagger, estimateVisibleCount } from '@festival/ui-utils';
import { useStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { SongRow } from '../songs/components/SongRow';
import { visibleInstruments } from '../../contexts/SettingsContext';
import { DEFAULT_INSTRUMENT, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { loadSongSettings } from '../../utils/songSettings';
import { clearScrollCache } from '../../hooks/ui/useScrollRestore';
import ShopCard from './components/ShopCard';
import fx from '../../styles/effects.module.css';
import type { CSSProperties } from 'react';
import Page from '../Page';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';
import { hasVisitedPage, markPageVisited } from '../../hooks/ui/usePageTransition';

/* v8 ignore start -- page component with multiple context/hook dependencies */
const STORAGE_KEY = 'fst:shopView';

function loadViewMode(): 'grid' | 'list' {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'list' ? 'list' : 'grid';
  } catch { return 'grid'; }
}

export default function ShopPage() {
  const { t } = useTranslation();
  const { shopSongs } = useShopState();
  const { settings } = useSettings();
  const isMobileChrome = useIsMobileChrome();
  const navType = useNavigationType();
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);

  const [viewMode, setViewMode] = useState<'grid' | 'list'>(loadViewMode);
  const isNarrow = useMediaQuery(QUERY_NARROW_GRID);
  const effectiveView = isNarrow ? 'list' : viewMode;
  const [staggerGen, setStaggerGen] = useState(0);
  const shopStyles = useShopPageStyles();
  const scrollContainerRef = useScrollContainer();
  const transition = useViewTransition();
  const isViewToggleRef = useRef(false);

  const toggleView = useCallback(() => {
    isViewToggleRef.current = true;
    setViewMode(prev => {
      const next = prev === 'grid' ? 'list' : 'grid';
      localStorage.setItem(STORAGE_KEY, next);
      return next;
    });
    setStaggerGen(g => g + 1);
    resetRush();
    transition.trigger();
  }, [resetRush, transition]);

  // Scroll to top after spinner fades and new view is about to render
  /* v8 ignore start — scroll reset after view toggle */
  useEffect(() => {
    if (transition.phase === LoadPhase.ContentIn && isViewToggleRef.current) {
      isViewToggleRef.current = false;
      scrollContainerRef.current?.scrollTo(0, 0);
      clearScrollCache('shop');
    }
  }, [transition.phase, scrollContainerRef]);
  /* v8 ignore stop */

  // Register toggle action for FAB and sync view mode
  const fabSearch = useFabSearch();
  const toggleViewRef = useRef(toggleView);
  toggleViewRef.current = toggleView;
  /* v8 ignore start — FAB registration callback */
  useEffect(() => {
    fabSearch.registerShopActions({ toggleView: () => toggleViewRef.current() });
  }, [fabSearch]);
  useEffect(() => {
    fabSearch.setShopViewMode(effectiveView);
  }, [fabSearch, effectiveView]);
  /* v8 ignore stop */

  const enabledInstruments = useMemo(() => visibleInstruments(settings), [settings]);
  const instrument = useMemo(() => loadSongSettings().instrument ?? DEFAULT_INSTRUMENT, []);

  const sorted = useMemo(() => {
    return [...shopSongs].sort((a, b) => a.title.localeCompare(b.title));
  }, [shopSongs]);

  // Stagger: animate items on the first render that has data.
  // Skip when the page has been rendered before this session (layout remount, back-nav, etc.).
  const location = useLocation();
  const isRealBackNav = navType === 'POP' && !!location.state;
  const skipShopAnim = hasVisitedPage('shop') || isRealBackNav;
  markPageVisited('shop');
  const shouldStaggerInitRef = useRef(false);
  // Grid: estimate visible items as cols × visible rows (cards are square).
  // Computed every render (cheap) so it stays correct after resize.
  const w = typeof window !== 'undefined' ? window.innerWidth : 1200;
  const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
  const gridCols = w >= 1100 ? 5 : w >= 860 ? 4 : w >= 600 ? 3 : 2;
  const contentWidth = Math.min(w, 1080) - 40;
  const gridGap = 10;
  const cardHeight = (contentWidth - gridGap * (gridCols - 1)) / gridCols;
  const gridVisibleRows = Math.ceil(vh / (cardHeight + gridGap)) + 1;
  const maxVisibleGrid = gridCols * gridVisibleRows;
  const maxVisibleRows = estimateVisibleCount(68);

  // Adaptive stagger intervals: cap total cascade to a max duration
  const MAX_GRID_CASCADE_MS = 1200;
  const MAX_ROW_CASCADE_MS = 2000;
  const gridInterval = Math.max(20, Math.floor(MAX_GRID_CASCADE_MS / Math.max(maxVisibleGrid, 1)));
  const rowInterval = Math.max(40, Math.floor(MAX_ROW_CASCADE_MS / Math.max(maxVisibleRows, 1)));

  if (sorted.length > 0 && !shouldStaggerInitRef.current && !skipShopAnim) {
    shouldStaggerInitRef.current = true;
  }
  // Initial load uses shouldStaggerInitRef; view toggles use transition.shouldStagger
  const shouldStagger = shouldStaggerInitRef.current || transition.shouldStagger;
  const emptyStagger = useStaggerStyle(200, { skip: !shouldStagger });

  // Combine phases: transition.phase drives spinner during view toggle;
  // on initial load the phase is already ContentIn (no spinner needed).
  const loadPhase = transition.phase;

  return (
    <Page
      scrollRestoreKey="shop"
      staggerRushRef={staggerRushRef}
      scrollDeps={[loadPhase, shopSongs]}
      containerStyle={shopStyles.contentArea}
      loadPhase={loadPhase}
      before={
        <PageHeader
          title={t('nav.shop')}
          actions={<>
            <span style={shopStyles.count}>{t('format.songCount', { count: sorted.length })}</span>
            {!isNarrow && !isMobileChrome && (
              <ActionPill
                icon={viewMode === 'grid' ? <IoList size={Size.iconAction} /> : <IoGrid size={Size.iconAction} />}
                label={viewMode === 'grid' ? t('shop.viewList', 'List') : t('shop.viewGrid', 'Grid')}
                onClick={toggleView}
              />
            )}
          </>}
        />
      }
    >
          {loadPhase === LoadPhase.ContentIn && (sorted.length === 0 ? (
            <EmptyState
              fullPage
              title={t('shop.empty', 'No songs in the Item Shop')}
              subtitle={t('shop.emptyHint', 'Check back later — the shop updates regularly.')}              style={emptyStagger.style}
              onAnimationEnd={emptyStagger.onAnimationEnd}            />
          ) : effectiveView === 'grid' ? (
            <div className={fx.shopGrid} key={`grid-${staggerGen}`}>
              {sorted.map((song, i) => (
                <ShopCard key={song.songId} song={song}
                  staggerDelay={shouldStagger ? calcStagger(Math.min(i, maxVisibleGrid - 1), gridInterval, maxVisibleGrid) : undefined}
                />
              ))}
            </div>
          ) : (
            <div style={shopStyles.list} key={`list-${staggerGen}`}>
              {sorted.map((song, i) => (
                <SongRow
                  key={song.songId}
                  song={song}
                  instrument={instrument as InstrumentKey}
                  showInstrumentIcons={false}
                  enabledInstruments={enabledInstruments}
                  metadataOrder={[]}
                  sortMode="title"
                  isMobile={false}
                  externalHref={song.shopUrl}
                  staggerDelay={shouldStagger ? calcStagger(Math.min(i, maxVisibleRows - 1), rowInterval, maxVisibleRows) : undefined}
                />
              ))}
            </div>
          ))}
    </Page>
  );
}
/* v8 ignore stop */

function useShopPageStyles() {
  return useMemo(() => ({
    contentArea: {
      paddingTop: Gap.md,
    } as CSSProperties,
    count: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
    } as CSSProperties,
    list: {
      ...flexColumn,
      gap: Gap.xs,
      paddingBottom: Gap.xl,
    } as CSSProperties,
  }), []);
}
