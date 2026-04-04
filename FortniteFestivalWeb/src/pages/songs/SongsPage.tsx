/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useMemo, useEffect, useRef, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigationType } from 'react-router-dom';
import { useVirtualizer } from '@tanstack/react-virtual';
import { staggerDelay, estimateVisibleCount } from '@festival/ui-utils';
import { useStaggerStyle, buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import { useContainerWidth } from '../../hooks/ui/useContainerWidth';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useSearchQuery } from '../../contexts/SearchQueryContext';
import { clearScrollCache } from '../../hooks/ui/useScrollRestore';
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { useShopState } from '../../hooks/data/useShopState';
import { useShop } from '../../contexts/ShopContext';
import { useModalState } from '../../hooks/ui/useModalState';
import { songSlides } from './firstRun';
import { type PlayerScore, type ServerInstrumentKey as InstrumentKey, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { LoadPhase } from '@festival/core';
import { Gap, Colors, Font, Layout, MaxWidth, BoxSizing, CssValue, flexCenter, padding } from '@festival/theme';
import { LoadGate } from '../../components/page/LoadGate';
import Page from '../Page';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import SyncBanner from '../../components/page/SyncBanner';
import EmptyState from '../../components/common/EmptyState';
import { parseApiError } from '../../utils/apiError';
import PageHeader from '../../components/common/PageHeader';
import { SongRow } from './components/SongRow';
import { SongsToolbar } from './components/SongsToolbar';
import { visibleInstruments } from '../../contexts/SettingsContext';
import SortModal from './modals/SortModal';
import type { SortDraft } from './modals/SortModal';
import FilterModal from './modals/FilterModal';
import type { FilterDraft } from './modals/FilterModal';
import {
  type SongSettings,
  defaultSongSettings,
  defaultSongFilters,
  loadSongSettings,
  saveSongSettings,
  SONG_SETTINGS_CHANGED_EVENT,
  isFilterActive,
} from '../../utils/songSettings';
import { resolveCompactRowMode } from './layoutMode';
import { hasVisitedPage, markPageVisited } from '../../hooks/ui/usePageTransition';

/**
 * Estimated minimum width (px) for each metadata element in desktop row layout.
 * Includes the element itself plus its share of the gap.
 * Used to predict whether all elements fit before rendering.
 */
const METADATA_MIN_WIDTH: Record<string, number> = {
  score: 94,        // ScorePill (78px) + gap share
  percentage: 72,   // AccuracyDisplay (~55px) + gap share
  percentile: 96,   // PercentilePill (~80px) + gap share
  stars: 120,       // 5 gold stars (~104px) + gap share
  seasonachieved: 48, // SeasonPill (~32px) + gap share
  intensity: 44,    // DifficultyBars (~28px) + gap share
  maxdistance: 76,  // PercentilePill with % (~60px) + gap share
};

/** Fixed overhead: row padding (32px) + SongInfo (albumArt 48 + gap 16 + min title 150) + gap to metadata (16). */
const ROW_FIXED_OVERHEAD = 262;

/** Safety buffer (px) so compact mode fires before any metadata could clip. */
const ROW_WIDTH_BUFFER = 60;

function getMinDesktopRowWidth(visibleKeys: string[], sortMode?: string): number {
  let width = ROW_FIXED_OVERHEAD;
  for (const key of visibleKeys) {
    if (key === 'score' && sortMode === 'maxdistance') {
      width += 192;  // dual score: 78 + 6 + ~8 + 6 + 78 = 176px + 16px gap share
    } else {
      width += METADATA_MIN_WIDTH[key] ?? 80;
    }
  }
  return width + ROW_WIDTH_BUFFER;
}

export default function SongsPage() {
  const { t } = useTranslation();
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const { settings: appSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const [settings, setSettings] = useState<SongSettings>(loadSongSettings);
  
  // Filter metadata keys by visibility settings (computed early for container-width detection)
  /* v8 ignore start — metadata visibility: settings-dependent presentation filter */
  const visibleMetadataOrder = useMemo(() => {
    const hidden = new Set<string>();
    if (!appSettings.metadataShowScore) hidden.add('score');
    if (!appSettings.metadataShowPercentage) hidden.add('percentage');
    if (!appSettings.metadataShowPercentile) hidden.add('percentile');
    if (!appSettings.metadataShowSeasonAchieved) hidden.add('seasonachieved');
    if (!appSettings.metadataShowDifficulty) hidden.add('intensity');
    if (!appSettings.metadataShowStars) hidden.add('stars');

    let order: string[];
    if (hidden.size === 0) {
      order = settings.metadataOrder;
    } else if (appSettings.songRowVisualOrderEnabled) {
      order = appSettings.songRowVisualOrder.filter(k => !hidden.has(k));
    } else {
      order = settings.metadataOrder.filter(k => !hidden.has(k));
    }

    // Auto-inject maxdistance only when max-score sort is active
    if (settings.sortMode === 'maxdistance' && !order.includes('maxdistance')) {
      order = [...order, 'maxdistance'];
    }
    return order;
  }, [
    settings.metadataOrder,
    settings.sortMode,
    appSettings.songRowVisualOrderEnabled,
    appSettings.songRowVisualOrder,
    appSettings.metadataShowScore,
    appSettings.metadataShowPercentage,
    appSettings.metadataShowPercentile,
    appSettings.metadataShowSeasonAchieved,
    appSettings.metadataShowDifficulty,
    appSettings.metadataShowStars,
  ]);
  /* v8 ignore stop */
  
  // Container-width-aware compact row detection.
  // Keep the breakpoint decision stable with hysteresis so the virtualized list
  // does not bounce between 68px and 122px rows while resizing near the cutoff.
  const containerRef = useRef<HTMLDivElement>(null);
  const containerWidth = useContainerWidth(containerRef);
  const minDesktopWidth = useMemo(
    () => getMinDesktopRowWidth(visibleMetadataOrder, settings.sortMode),
    [visibleMetadataOrder, settings.sortMode],
  );
  const compactRowRef = useRef(typeof window !== 'undefined' ? window.innerWidth <= 1100 : false);
  const effectiveRowWidth = containerWidth > 0
    ? containerWidth
    : (typeof window !== 'undefined' ? window.innerWidth : minDesktopWidth);
  const isCompactRow = resolveCompactRowMode(effectiveRowWidth, minDesktopWidth, compactRowRef.current);
  const songRowMobile = isMobile || isCompactRow;

  useEffect(() => {
    compactRowRef.current = isCompactRow;
  }, [isCompactRow]);

  const songsSlidesMemo = useMemo(() => songSlides(isMobileChrome), [isMobileChrome]);

  const fabSearch = useFabSearch();
  const searchQuery = useSearchQuery();
  const staggerRushRef = useRef<(() => void) | undefined>(undefined);
  const resetRush = useCallback(() => staggerRushRef.current?.(), []);
  const navType = useNavigationType();
  const location = useLocation();
  const forceRestagger = !!(location.state as Record<string, unknown> | null)?.restagger;
  const isBackNav = navType === 'POP' && !!(location.state as Record<string, unknown> | null);

  const [search, setSearchLocal] = useState(searchQuery.query);
  const setSearch = useCallback((q: string) => {
    setSearchLocal(q);
    searchQuery.setQuery(q);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- searchQuery is context, stable ref
  }, [fabSearch]);
  const effectiveSearch = isMobileChrome ? searchQuery.query : search;
  const [debouncedSearch, setDebouncedSearch] = useState(effectiveSearch);
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(effectiveSearch), 250);
    return () => clearTimeout(id);
  }, [effectiveSearch]);

  // Re-sync settings from localStorage when changed externally (e.g. PlayerPage card clicks)
  const savingRef = useRef(false);
  /* v8 ignore start — external settings sync event listener */
  useEffect(() => {
    const sync = () => {
      if (savingRef.current) return; // ignore self-triggered events
      const fresh = loadSongSettings();
      setSettings(fresh);
      setInstrument(fresh.instrument ?? DEFAULT_INSTRUMENT);
    };
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);
  /* v8 ignore stop */
  const [instrument, setInstrument] = useState<InstrumentKey>(
    () => settings.instrument ?? DEFAULT_INSTRUMENT,
  );

  // Sort/Filter modal state
  const sortModal = useModalState<SortDraft>(() => ({
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    metadataOrder: settings.metadataOrder,
    instrumentOrder: settings.instrumentOrder,
  }));
  const filterModal = useModalState<FilterDraft>(() => ({
    ...settings.filters,
    instrumentFilter: settings.instrument,
  }));

  // Persist settings on change
  useEffect(() => { savingRef.current = true; saveSongSettings(settings); savingRef.current = false; }, [settings]);

  const openSort = () => {
    sortModal.open({
      sortMode: settings.sortMode,
      sortAscending: settings.sortAscending,
      metadataOrder: settings.metadataOrder,
      instrumentOrder: settings.instrumentOrder,
    });
  };
  const applySort = () => {
    setSettings(s => ({ ...s, ...sortModal.draft }));
    sortModal.close();
  };
  const resetSort = () => {
    const d = defaultSongSettings();
    sortModal.setDraft({ sortMode: d.sortMode, sortAscending: d.sortAscending, metadataOrder: d.metadataOrder, instrumentOrder: d.instrumentOrder });
  };

  const openFilter = () => {
    filterModal.open({ ...settings.filters, instrumentFilter: settings.instrument });
  };
  const applyFilter = () => {
    const { instrumentFilter, ...filters } = filterModal.draft;
    setInstrument(instrumentFilter ?? DEFAULT_INSTRUMENT);
    setSettings(s => ({ ...s, filters, instrument: instrumentFilter }));
    filterModal.close();
  };
  const resetFilter = () => {
    filterModal.setDraft({ ...defaultSongFilters(), instrumentFilter: null });
  };

  const filtersActive = isFilterActive(settings.filters, settings.instrument) || settings.instrument != null;
  const sortActive = settings.sortMode !== 'title' || !settings.sortAscending;

  // Register sort/filter actions for FAB â€” uses refs so latest closures are always captured
  const openSortRef = useRef(openSort);
  const openFilterRef = useRef(openFilter);
  openSortRef.current = openSort;
  openFilterRef.current = openFilter;
  /* v8 ignore start — FAB action registration callbacks */
  useEffect(() => {
    fabSearch.registerActions({ openSort: () => openSortRef.current(), openFilter: () => openFilterRef.current() });
  }, [fabSearch]);
  /* v8 ignore stop */
  const { playerData, playerLoading, isSyncing, syncPhase, backfillProgress, historyProgress, rivalsProgress, entriesFound, itemsCompleted, totalItems, currentSongName, seasonsQueried, rivalsFound } = usePlayerData();
  const shopCtx = useShop();
  const { isShopHighlighted, isLeavingTomorrow, isShopVisible } = useShopState();
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: !!playerData, shopHighlightEnabled: isShopVisible && !appSettings.disableShopHighlighting }), [playerData, isShopVisible, appSettings.disableShopHighlighting]);

  const { isScoreValid, enabled: scoreFilterEnabled, leeway: userLeeway, getFilteredRank, getFilteredTotal } = useScoreFilter();

  // Apply invalid-score substitution/dropping (same logic as filterPlayerScores but
  // also builds an invalidity map for the UI indicator, and exempts instruments where
  // the overThreshold filter is active so the user can inspect the raw values).
  /* v8 ignore start — effectiveScores: invalid-score substitution */
  type InvalidReason = 'fallback' | 'no-fallback' | 'over-threshold';
  const { effectiveScores, invalidScoreMap } = useMemo(() => {
    const empty = { effectiveScores: [] as PlayerScore[], invalidScoreMap: new Map<string, Map<InstrumentKey, InvalidReason>>() };
    if (!playerData) return empty;
    if (!scoreFilterEnabled) return { effectiveScores: playerData.scores, invalidScoreMap: empty.invalidScoreMap };

    const overThreshold = settings.filters.overThreshold ?? {};
    const scores: PlayerScore[] = [];
    const invalids = new Map<string, Map<InstrumentKey, InvalidReason>>();

    for (const s of playerData.scores) {
      const inst = s.instrument as InstrumentKey;
      const scoreInvalid = s.minLeeway != null ? s.minLeeway > userLeeway : !isScoreValid(s.songId, inst, s.score);
      if (scoreInvalid) {
        // When overThreshold filter is active, pass through raw score but mark as over-threshold
        if (overThreshold[inst]) {
          scores.push(s);
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'over-threshold');
        } else if (s.validScores && s.validScores.length > 0) {
          // New path: find best fallback from validScores where minLeeway <= userLeeway
          const fallback = s.validScores.find(v => v.minLeeway <= userLeeway);
          if (fallback) {
            const filteredRank = getFilteredRank(fallback.rankTiers);
            const filteredTotal = getFilteredTotal(s.songId, inst, s.totalEntries);
            scores.push({
              ...s,
              score: fallback.score,
              accuracy: fallback.accuracy ?? s.accuracy,
              isFullCombo: fallback.fc ?? s.isFullCombo,
              stars: fallback.stars ?? s.stars,
              rank: filteredRank ?? s.rank,
              totalEntries: filteredTotal ?? s.totalEntries,
            });
            let byInst = invalids.get(s.songId);
            if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
            byInst.set(inst, 'fallback');
          } else {
            let byInst = invalids.get(s.songId);
            if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
            byInst.set(inst, 'no-fallback');
          }
        } else if (s.validScore != null) {
          // Legacy path: substitute with server-provided fallback values
          scores.push({
            ...s,
            score: s.validScore,
            rank: s.validRank ?? 0,
            accuracy: s.validAccuracy ?? s.accuracy,
            isFullCombo: s.validIsFullCombo ?? s.isFullCombo,
            stars: s.validStars ?? s.stars,
            totalEntries: s.validTotalEntries ?? s.totalEntries,
          });
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'fallback');
        } else {
          // Invalid with no fallback — drop from effective scores
          let byInst = invalids.get(s.songId);
          if (!byInst) { byInst = new Map(); invalids.set(s.songId, byInst); }
          byInst.set(inst, 'no-fallback');
        }
      } else {
        scores.push(s);
      }
    }
    return { effectiveScores: scores, invalidScoreMap: invalids };
  }, [playerData, scoreFilterEnabled, isScoreValid, userLeeway, getFilteredRank, getFilteredTotal, settings.filters.overThreshold]);
  /* v8 ignore stop */

  // Build lookup: songId → PlayerScore for the selected instrument
  /* v8 ignore start — scoreMap: instrument filter loop */
  const scoreMap = useMemo(() => {
    if (!playerData) return new Map<string, PlayerScore>();
    const map = new Map<string, PlayerScore>();
    for (const s of effectiveScores) {
      if (s.instrument === instrument) {
        map.set(s.songId, s);
      }
    }
    return map;
  }, [playerData, effectiveScores, instrument]);
  /* v8 ignore stop */

  /* v8 ignore start — allScoreMap: multi-instrument lookup */
  // Build a per-song, per-instrument lookup for filter logic
  const allScoreMap = useMemo(() => {
    if (!playerData) return new Map<string, Map<InstrumentKey, PlayerScore>>();
    const map = new Map<string, Map<InstrumentKey, PlayerScore>>();
    for (const sc of effectiveScores) {
      let byInst = map.get(sc.songId);
      if (!byInst) {
        byInst = new Map();
        map.set(sc.songId, byInst);
      }
      byInst.set(sc.instrument as InstrumentKey, sc);
    }
    return map;
  }, [playerData, effectiveScores]);
  /* v8 ignore stop */

  const filtered = useFilteredSongs({
    songs,
    search: debouncedSearch,
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    filters: settings.filters,
    instrument: settings.instrument,
    scoreMap,
    allScoreMap,
    shopSongIds: shopCtx.shopSongIds,
    isScoreValid,
    filterInvalidScoresEnabled: scoreFilterEnabled,
  });

  const hasPlayer = !!playerData;

  const enabledInstruments = useMemo(
    () => visibleInstruments(appSettings),
    [appSettings],
  );

  // Derive available seasons from player scores
  const availableSeasons = useMemo(() => {
    if (!playerData) return [];
    const set = new Set<number>();
    for (const s of playerData.scores) {
      if (s.season != null) set.add(s.season);
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [playerData]);

  // â”€â”€ Spinner â†’ staggered-content transition â”€â”€
  const dataReady = !isLoading && !playerLoading;
  // Capture "first visit this session" before marking as rendered
  const skipAnimRef = useRef((hasVisitedPage('songs') || isBackNav) && !forceRestagger);
  const skipAnim = skipAnimRef.current;
  markPageVisited('songs');
  // Skip spinner + stagger only when data is actually available
  const [loadPhase, setLoadPhase] = useState<LoadPhase>(
    dataReady ? LoadPhase.ContentIn : LoadPhase.Loading,
  );
  // Track whether the initial load phase was set via settings change (not mount)
  const isSettingsChangeRef = useRef(false);
  // Whether to run stagger animation on the current contentIn transition
  const [shouldStagger, setShouldStagger] = useState(!skipAnim);

  // Track whether the toolbar has been shown at least once (initial load complete)
  const toolbarShownRef = useRef(skipAnim);
  if (loadPhase === LoadPhase.ContentIn) toolbarShownRef.current = true;

  // Fingerprint of sort/filter/search settings â€” when it changes, re-stagger the list
  const settingsKey = `${settings.sortMode}|${settings.sortAscending}|${instrument}|${JSON.stringify(settings.filters)}|${debouncedSearch}`;
  const prevSettingsKeyRef = useRef(settingsKey);

  /* v8 ignore start — animation: stagger/re-stagger effects */
  useEffect(() => {
    if (prevSettingsKeyRef.current === settingsKey) return;
    prevSettingsKeyRef.current = settingsKey;
    // Only re-stagger if we were already showing content
    if (loadPhase === LoadPhase.ContentIn) {
      isSettingsChangeRef.current = true;
      resetRush();
      setShouldStagger(true);
      setLoadPhase(LoadPhase.SpinnerOut);
    }
  }, [settingsKey, loadPhase]);

  // Show spinner while the user is typing ahead of the debounce
  const searchLoadingRef = useRef(false);
  useEffect(() => {
    if (effectiveSearch !== debouncedSearch && (loadPhase === LoadPhase.ContentIn || loadPhase === LoadPhase.SpinnerOut)) {
      searchLoadingRef.current = true;
      setLoadPhase(LoadPhase.Loading);
    }
  }, [effectiveSearch, debouncedSearch, loadPhase]);

  useEffect(() => {
    if (!dataReady || loadPhase !== LoadPhase.Loading) return;
    // Hold the spinner briefly so it feels intentional after search debounce
    if (searchLoadingRef.current) {
      searchLoadingRef.current = false;
      const id = setTimeout(() => { resetRush(); setShouldStagger(true); setLoadPhase(LoadPhase.SpinnerOut); }, 300);
      return () => clearTimeout(id);
    }
    // On revisit (skipAnim) skip the spinner-out delay and jump straight to content
    if (skipAnimRef.current) {
      setLoadPhase(LoadPhase.ContentIn);
    } else {
      resetRush();
      setShouldStagger(true);
      setLoadPhase(LoadPhase.SpinnerOut);
    }
  }, [dataReady, loadPhase]);

  useEffect(() => {
    if (loadPhase !== LoadPhase.SpinnerOut) return;
    const id = setTimeout(() => {
      setLoadPhase(LoadPhase.ContentIn);
    }, 500);
    return () => clearTimeout(id);
  }, [loadPhase]);

  // Turn off stagger after all animations finish
  const maxVisibleSongs = useMemo(() => estimateVisibleCount(songRowMobile ? 120 : 72), [songRowMobile]);
  useEffect(() => {
    if (loadPhase !== LoadPhase.ContentIn || !shouldStagger) return;
    const totalAnimTime = (maxVisibleSongs + 1) * 125 + 400;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [loadPhase, shouldStagger, maxVisibleSongs]);
  /* v8 ignore stop */

  const scrollContainerRef = useScrollContainer();

  // Scroll to top when content transitions in after a settings change (not on initial mount or back nav)
  /* v8 ignore start — scroll reset on settings change */
  useEffect(() => {
    if (loadPhase === LoadPhase.ContentIn && isSettingsChangeRef.current) {
      isSettingsChangeRef.current = false;
      scrollContainerRef.current?.scrollTo(0, 0);
      clearScrollCache('songs');
    }
  }, [loadPhase, scrollContainerRef]);
  /* v8 ignore stop */

  // ── Virtual list ──
  const ROW_HEIGHT = songRowMobile ? 122 : 68; // row + 2px gap
  const listParentRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: loadPhase === LoadPhase.ContentIn ? filtered.length : 0,
    estimateSize: () => ROW_HEIGHT,
    overscan: 8,
    gap: 2,
    getScrollElement: () => scrollContainerRef.current,
    scrollMargin: listParentRef.current?.offsetTop ?? 0,
  });

  const emptyStagger = useStaggerStyle(200, { skip: !shouldStagger });
  const songsStyles = useSongsStyles();

  if (error) {
    const parsed = parseApiError(error);
    return <EmptyState fullPage title={parsed.title} subtitle={parsed.subtitle} style={buildStaggerStyle(200)} onAnimationEnd={clearStaggerStyle} />;
  }

  return (
    <Page
      scrollRestoreKey="songs"
      scrollDeps={[loadPhase, filtered]}
      staggerRushRef={staggerRushRef}
      firstRun={{ key: 'songs', label: t('nav.songs'), slides: songsSlidesMemo, gateContext: firstRunGateCtx }}
      fabSpacer={isMobileChrome ? 'fixed' : 'end'}
      before={<>
        <LoadGate phase={loadPhase} overlay>
        {!isMobileChrome && (
          <PageHeader
            title={
              <div style={{ visibility: (toolbarShownRef.current || loadPhase === LoadPhase.ContentIn) ? 'visible' : 'hidden' } as CSSProperties}>
                <SongsToolbar
                  search={search}
                  onSearchChange={setSearch}
                  instrument={settings.instrument}
                  sortActive={sortActive}
                  filtersActive={filtersActive}
                  hasSongs={songs.length > 0 && !isLoading}
                  hasPlayer={hasPlayer}
                  filteredCount={filtered.length}
                  totalCount={songs.length}
                  onOpenSort={openSort}
                  onOpenFilter={openFilter}
                />
              </div>
            }
          />
        )}
        </LoadGate>
      </>}
      after={<>
        <SortModal
          visible={sortModal.visible}
          draft={sortModal.draft}
          savedDraft={{
            sortMode: settings.sortMode,
            sortAscending: settings.sortAscending,
            metadataOrder: settings.metadataOrder,
            instrumentOrder: settings.instrumentOrder,
          }}
          instrumentFilter={instrument}
          hasPlayer={!!playerData}
          hideItemShop={!isShopVisible}
          metadataVisibility={{
            score: appSettings.metadataShowScore,
            percentage: appSettings.metadataShowPercentage,
            percentile: appSettings.metadataShowPercentile,
            seasonachieved: appSettings.metadataShowSeasonAchieved,
            intensity: appSettings.metadataShowDifficulty,
            stars: appSettings.metadataShowStars,
          }}
          onChange={sortModal.setDraft}
          onCancel={sortModal.close}
          onReset={resetSort}
          onApply={applySort}
        />
        <FilterModal
          visible={filterModal.visible}
          draft={filterModal.draft}
          savedDraft={{ ...settings.filters, instrumentFilter: settings.instrument }}
          availableSeasons={availableSeasons}
          onChange={filterModal.setDraft}
          onCancel={filterModal.close}
          onReset={resetFilter}
          onApply={applyFilter}
        />
      </>}
    >
      <div ref={containerRef} style={songsStyles.container}>
        {isSyncing && playerData && (
          <SyncBanner
            phase={syncPhase}
            backfillProgress={backfillProgress}
            historyProgress={historyProgress}
            rivalsProgress={rivalsProgress}
            itemsCompleted={itemsCompleted}
            totalItems={totalItems}
            entriesFound={entriesFound}
            currentSongName={currentSongName}
            seasonsQueried={seasonsQueried}
            rivalsFound={rivalsFound}
          />
        )}
        {loadPhase === LoadPhase.ContentIn && filtered.length === 0 ? (
          <EmptyState
            fullPage
            title={t('songs.noResults')}
            subtitle={filtersActive ? t('songs.noResultsSubtitle') : t('common.serviceDown')}
            style={emptyStagger.style}
            onAnimationEnd={emptyStagger.onAnimationEnd}
          />
        ) : (
          <div
            ref={listParentRef}
            style={{ ...songsStyles.list,
              height: virtualizer.getTotalSize(),
              position: 'relative',
            }}
          >
            {loadPhase === LoadPhase.ContentIn && virtualizer.getVirtualItems().map((virtualRow) => {
                const song = filtered[virtualRow.index]!;
                const i = virtualRow.index;
                return (
                  <div
                    key={song.songId}
                    style={{
                      position: 'absolute',
                      top: 0,
                      left: 0,
                      width: '100%',
                      transform: `translateY(${virtualRow.start - virtualizer.options.scrollMargin}px)`,
                    }}
                    ref={virtualizer.measureElement}
                    data-index={virtualRow.index}
                  >
                    <SongRow
                      song={song}
                      score={hasPlayer ? scoreMap.get(song.songId) : undefined}
                      instrument={instrument}
                      instrumentFilter={settings.instrument}
                      allScoreMap={hasPlayer ? allScoreMap.get(song.songId) : undefined}
                      showInstrumentIcons={hasPlayer && !appSettings.songsHideInstrumentIcons}
                      enabledInstruments={enabledInstruments}
                      metadataOrder={visibleMetadataOrder}
                      sortMode={settings.sortMode}
                      isMobile={songRowMobile}
                      /* v8 ignore start — stagger delay calculation */
                      staggerDelay={shouldStagger && i < maxVisibleSongs ? (staggerDelay(i, 125, maxVisibleSongs) ?? maxVisibleSongs * 125) : undefined}
                      /* v8 ignore stop */
                      shopHighlight={isShopHighlighted(song.songId)}
                      shopHighlightRed={isLeavingTomorrow(song.songId)}
                      invalidInstruments={invalidScoreMap.get(song.songId)}
                      containerWidth={containerWidth}
                    />
                  </div>
                );
            })}
          </div>
        )}
      </div>
    </Page>
  );
}

function useSongsStyles() {
  return useMemo(() => ({
    container: {
      width: CssValue.full,
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      padding: padding(Layout.paddingTop, Layout.paddingHorizontal),
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    list: {
      paddingTop: Gap.lg,
    } as CSSProperties,
    center: {
      ...flexCenter,
      minHeight: CssValue.viewportFull,
      color: Colors.textSecondary,
      backgroundColor: Colors.backgroundApp,
      fontSize: Font.lg,
    } as CSSProperties,
  }), []);
}

/** Render a single metadata element for the given key. Mirrors mobile renderMetadataElement. */

