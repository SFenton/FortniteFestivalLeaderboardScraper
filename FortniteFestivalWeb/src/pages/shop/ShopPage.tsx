/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useCallback, useRef, useMemo, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigationType, useLocation } from 'react-router-dom';
import { IoGrid, IoList } from 'react-icons/io5';
import { useShopState } from '../../hooks/data/useShopState';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../hooks/ui/useMediaQuery';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { ActionPill } from '../../components/common/ActionPill';
import { Size, QUERY_NARROW_GRID } from '@festival/theme';
import { staggerDelay as calcStagger, estimateVisibleCount } from '@festival/ui-utils';
import { SongRow } from '../songs/components/SongRow';
import { visibleInstruments } from '../../contexts/SettingsContext';
import { DEFAULT_INSTRUMENT, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Gap } from '@festival/theme';
import { loadSongSettings } from '../../utils/songSettings';
import ShopCard from './components/ShopCard';
import css from './ShopPage.module.css';

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
  const scrollRef = useRef<HTMLDivElement>(null);
  const saveScroll = useScrollRestore(scrollRef, 'shop', navType);
  const updateScrollMask = useScrollMask(scrollRef, [shopSongs]);
  const { rushOnScroll, resetRush } = useStaggerRush(scrollRef);
  const programmaticScrollRef = useRef(false);
  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => { saveScroll(); updateScrollMask(); if (!programmaticScrollRef.current) rushOnScroll(); programmaticScrollRef.current = false; }, [saveScroll, updateScrollMask, rushOnScroll]);
  /* v8 ignore stop */

  const [viewMode, setViewMode] = useState<'grid' | 'list'>(loadViewMode);
  const isNarrow = useMediaQuery(QUERY_NARROW_GRID);
  const effectiveView = isNarrow ? 'list' : viewMode;
  const [staggerGen, setStaggerGen] = useState(0);

  const toggleView = useCallback(() => {
    programmaticScrollRef.current = true;
    scrollRef.current?.scrollTo({ top: 0 });
    setViewMode(prev => {
      const next = prev === 'grid' ? 'list' : 'grid';
      localStorage.setItem(STORAGE_KEY, next);
      return next;
    });
    setStaggerGen(g => g + 1);
    setStaggerDone(false);
    resetRush();
  }, [resetRush]);

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
  // Skip only on true back-navigation (POP with location.state from internal links),
  // not on initial page load which is also POP under HashRouter.
  const location = useLocation();
  const isRealBackNav = navType === 'POP' && !!location.state;
  const staggerDecidedRef = useRef(false);
  const shouldStaggerRef = useRef(false);
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

  if (sorted.length > 0 && !staggerDecidedRef.current) {
    staggerDecidedRef.current = true;
    shouldStaggerRef.current = !isRealBackNav;
  }
  // Track stagger generation — bumped on view toggle to re-trigger animation
  const [staggerDone, setStaggerDone] = useState(false);
  const shouldStagger = (shouldStaggerRef.current || staggerGen > 0) && !staggerDone;

  /* v8 ignore start — turn off stagger after animations complete */
  useEffect(() => {
    if (staggerGen === 0) return; // initial stagger has no timer; toggles do
    const cascadeMs = effectiveView === 'grid' ? MAX_GRID_CASCADE_MS : MAX_ROW_CASCADE_MS;
    const id = setTimeout(() => setStaggerDone(true), cascadeMs + 400);
    return () => clearTimeout(id);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- staggerGen is the trigger
  }, [staggerGen]);
  /* v8 ignore stop */

  return (
    <div className={css.page}>
      <div className={css.header}>
        <div className={css.container}>
          <div className={css.toolbar}>
            <h1 className={css.title}>{t('nav.shop', 'Item Shop')}</h1>
            <div className={css.toolbarRight}>
              <span className={css.count}>{sorted.length} {sorted.length === 1 ? 'song' : 'songs'}</span>
              {!isNarrow && !isMobileChrome && (
                <ActionPill
                  icon={viewMode === 'grid' ? <IoList size={Size.iconAction} /> : <IoGrid size={Size.iconAction} />}
                  label={viewMode === 'grid' ? 'List' : 'Grid'}
                  onClick={toggleView}
                />
              )}
            </div>
          </div>
        </div>
      </div>
      <div ref={scrollRef} onScroll={handleScroll} className={css.scrollArea}>
        <div className={css.container} style={{ paddingTop: Gap.md }}>
          {sorted.length === 0 ? (
            <div className={css.emptyState}>
              <div className={css.emptyTitle}>{t('shop.empty', 'No songs in the Item Shop')}</div>
              <div className={css.emptySubtitle}>{t('shop.emptyHint', 'Check back later — the shop updates regularly.')}</div>
            </div>
          ) : effectiveView === 'grid' ? (
            <div className={css.grid} key={`grid-${staggerGen}`}>
              {sorted.map((song, i) => (
                <ShopCard key={song.songId} song={song}
                  staggerDelay={shouldStagger ? calcStagger(Math.min(i, maxVisibleGrid - 1), gridInterval, maxVisibleGrid) : undefined}
                />
              ))}
            </div>
          ) : (
            <div className={css.list} key={`list-${staggerGen}`}>
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
          )}
        </div>
        {isMobileChrome && <div className={css.fabSpacer} />}
      </div>
    </div>
  );
}
/* v8 ignore stop */
