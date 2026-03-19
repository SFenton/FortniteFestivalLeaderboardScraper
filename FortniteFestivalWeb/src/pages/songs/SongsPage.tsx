import { useState, useMemo, useEffect, useRef, useCallback, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigationType } from 'react-router-dom';
import { useVirtualizer } from '@tanstack/react-virtual';
import { staggerDelay, estimateVisibleCount } from '@festival/ui-utils';
import { useFestival } from '../../contexts/FestivalContext';
import { usePlayerData } from '../../contexts/PlayerDataContext';
import { useSettings } from '../../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { useSearchQuery } from '../../contexts/SearchQueryContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useScrollRestore, clearScrollCache } from '../../hooks/ui/useScrollRestore';
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import { useModalState } from '../../hooks/ui/useModalState';
import { type PlayerScore, type ServerInstrumentKey as InstrumentKey, DEFAULT_INSTRUMENT } from '@festival/core/api/serverTypes';
import { Gap } from '@festival/theme';
import { LoadGate } from '../../components/page/LoadGate';
import SyncBanner from '../../components/page/SyncBanner';
import s from './SongsPage.module.css';
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

let _songsHasRendered = false;

export default function SongsPage() {
  const { t } = useTranslation();
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const { settings: appSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const fabSearch = useFabSearch();
  const searchQuery = useSearchQuery();
  const scrollRef = useRef<HTMLDivElement>(null);
  const navType = useNavigationType();
  const location = useLocation();
  const forceRestagger = !!(location.state as any)?.restagger;
  const isBackNav = navType === 'POP';

  // Unified scroll position save/restore
  const saveScroll = useScrollRestore(scrollRef, 'songs', navType);

  const [search, setSearchLocal] = useState(searchQuery.query);
  const setSearch = useCallback((q: string) => {
    setSearchLocal(q);
    searchQuery.setQuery(q);
  }, [fabSearch]);
  const effectiveSearch = isMobileChrome ? searchQuery.query : search;
  const [debouncedSearch, setDebouncedSearch] = useState(effectiveSearch);
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(effectiveSearch), 250);
    return () => clearTimeout(id);
  }, [effectiveSearch]);
  const [settings, setSettings] = useState<SongSettings>(loadSongSettings);

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

  const filtersActive = isFilterActive(settings.filters) || settings.instrument != null;
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
  const { playerData, playerLoading, isSyncing, syncPhase, backfillProgress, historyProgress } = usePlayerData();

  // Build lookup: songId â†’ PlayerScore for the selected instrument
  /* v8 ignore start — scoreMap: instrument filter loop */
  const scoreMap = useMemo(() => {
    if (!playerData) return new Map<string, PlayerScore>();
    const map = new Map<string, PlayerScore>();
    for (const s of playerData.scores) {
      if (s.instrument === instrument) {
        map.set(s.songId, s);
      }
    }
    return map;
  }, [playerData, instrument]);
  /* v8 ignore stop */

  /* v8 ignore start — allScoreMap: multi-instrument lookup */
  // Build a per-song, per-instrument lookup for filter logic
  const allScoreMap = useMemo(() => {
    if (!playerData) return new Map<string, Map<InstrumentKey, PlayerScore>>();
    const map = new Map<string, Map<InstrumentKey, PlayerScore>>();
    for (const sc of playerData.scores) {
      let byInst = map.get(sc.songId);
      if (!byInst) {
        byInst = new Map();
        map.set(sc.songId, byInst);
      }
      byInst.set(sc.instrument as InstrumentKey, sc);
    }
    return map;
  }, [playerData]);
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
  });

  const hasPlayer = !!playerData;

  const enabledInstruments = useMemo(
    () => visibleInstruments(appSettings),
    [appSettings],
  );

  // Filter metadata keys by visibility settings (mirrors mobile visibleMetadataKeys)
  /* v8 ignore start — metadata visibility: settings-dependent presentation filter */
  const visibleMetadataOrder = useMemo(() => {
    const hidden = new Set<string>();
    if (!appSettings.metadataShowScore) hidden.add('score');
    if (!appSettings.metadataShowPercentage) hidden.add('percentage');
    if (!appSettings.metadataShowPercentile) hidden.add('percentile');
    if (!appSettings.metadataShowSeasonAchieved) hidden.add('seasonachieved');
    if (!appSettings.metadataShowDifficulty) hidden.add('intensity');
    if (!appSettings.metadataShowStars) hidden.add('stars');
    if (hidden.size === 0) return settings.metadataOrder;

    if (appSettings.songRowVisualOrderEnabled) {
      return appSettings.songRowVisualOrder.filter(k => !hidden.has(k));
    }
    return settings.metadataOrder.filter(k => !hidden.has(k));
  }, [
    settings.metadataOrder,
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
  const dataReady = !isLoading && songs.length > 0 && !playerLoading;
  // Capture "first visit this session" before marking as rendered
  const skipAnimRef = useRef((_songsHasRendered || isBackNav) && !forceRestagger);
  const skipAnim = skipAnimRef.current;
  _songsHasRendered = true;
  // Skip spinner if data already available OR already visited; skip stagger only if already visited
  const [loadPhase, setLoadPhase] = useState<'loading' | 'spinnerOut' | 'contentIn'>(
    (skipAnim || dataReady) ? 'contentIn' : 'loading',
  );
  // Track whether the initial load phase was set via settings change (not mount)
  const isSettingsChangeRef = useRef(false);
  // Whether to run stagger animation on the current contentIn transition
  const [shouldStagger, setShouldStagger] = useState(!skipAnim);

  // Track whether the toolbar has been shown at least once (initial load complete)
  const toolbarShownRef = useRef(skipAnim);
  if (loadPhase === 'contentIn') toolbarShownRef.current = true;

  // Fingerprint of sort/filter/search settings â€” when it changes, re-stagger the list
  const settingsKey = `${settings.sortMode}|${settings.sortAscending}|${instrument}|${JSON.stringify(settings.filters)}|${debouncedSearch}`;
  const prevSettingsKeyRef = useRef(settingsKey);

  /* v8 ignore start — animation: stagger/re-stagger effects */
  useEffect(() => {
    if (prevSettingsKeyRef.current === settingsKey) return;
    prevSettingsKeyRef.current = settingsKey;
    // Only re-stagger if we were already showing content
    if (loadPhase === 'contentIn') {
      isSettingsChangeRef.current = true;
      setShouldStagger(true);
      setLoadPhase('spinnerOut');
    }
  }, [settingsKey, loadPhase]);

  // Show spinner while the user is typing ahead of the debounce
  const searchLoadingRef = useRef(false);
  useEffect(() => {
    if (effectiveSearch !== debouncedSearch && (loadPhase === 'contentIn' || loadPhase === 'spinnerOut')) {
      searchLoadingRef.current = true;
      setLoadPhase('loading');
    }
  }, [effectiveSearch, debouncedSearch, loadPhase]);

  useEffect(() => {
    if (!dataReady || loadPhase !== 'loading') return;
    // Hold the spinner briefly so it feels intentional after search debounce
    if (searchLoadingRef.current) {
      searchLoadingRef.current = false;
      const id = setTimeout(() => { setShouldStagger(true); setLoadPhase('spinnerOut'); }, 300);
      return () => clearTimeout(id);
    }
    setShouldStagger(true);
    setLoadPhase('spinnerOut');
  }, [dataReady, loadPhase]);

  useEffect(() => {
    if (loadPhase !== 'spinnerOut') return;
    const id = setTimeout(() => {
      setLoadPhase('contentIn');
    }, 500);
    return () => clearTimeout(id);
  }, [loadPhase]);

  // Turn off stagger after all animations finish
  const maxVisibleSongs = useMemo(() => estimateVisibleCount(isMobile ? 120 : 72), [isMobile]);
  useEffect(() => {
    if (loadPhase !== 'contentIn' || !shouldStagger) return;
    const totalAnimTime = (maxVisibleSongs + 1) * 125 + 400;
    const id = setTimeout(() => setShouldStagger(false), totalAnimTime);
    return () => clearTimeout(id);
  }, [loadPhase, shouldStagger, maxVisibleSongs]);
  /* v8 ignore stop */

  // Scroll to top when content transitions in after a settings change (not on initial mount or back nav)
  /* v8 ignore start — scroll reset on settings change */
  useEffect(() => {
    if (loadPhase === 'contentIn' && isSettingsChangeRef.current && scrollRef.current) {
      isSettingsChangeRef.current = false;
      scrollRef.current.scrollTop = 0;
      clearScrollCache('songs');
    }
  }, [loadPhase]);
  /* v8 ignore stop */

  // Container-level scroll fade (works because frostedCard avoids backdrop-filter)
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, filtered]);

  // Save scroll position continuously + update fade
  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    saveScroll();
    updateScrollMask();
    rushOnScroll();
  }, [saveScroll, updateScrollMask, rushOnScroll]);

  // â”€â”€ Virtual list â”€â”€
  const ROW_HEIGHT = isMobile ? 122 : 68; // row + 2px gap
  const listParentRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: loadPhase === 'contentIn' ? filtered.length : 0,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 8,
    gap: 2,
  });

  if (error) {
    return <div className={s.center}>{error}</div>;
  }

  return (
    <div className={s.page}>
      <LoadGate phase={loadPhase} overlay spinnerClassName={s.spinnerOverlay}>
      {!isMobileChrome && (
      <div className={s.header}>
        <div className={s.container}>
          <div style={{ visibility: (toolbarShownRef.current || loadPhase === 'contentIn') ? 'visible' : 'hidden' } as CSSProperties}>
          <SongsToolbar
            search={search}
            onSearchChange={setSearch}
            instrument={settings.instrument}
            sortActive={sortActive}
            filtersActive={filtersActive}
            hasPlayer={hasPlayer}
            filteredCount={filtered.length}
            totalCount={songs.length}
            onOpenSort={openSort}
            onOpenFilter={openFilter}
          />
          </div>
        </div>
      </div>
      )}
      <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
        <div className={s.container} style={{ paddingTop: isMobile ? Gap.sm : Gap.md }}>
        {isSyncing && playerData && (
          <SyncBanner
            displayName={playerData.displayName}
            phase={syncPhase}
            backfillProgress={backfillProgress}
            historyProgress={historyProgress}
          />
        )}
        {loadPhase === 'contentIn' && filtered.length === 0 ? (
          <div className={s.emptyState}>
            <div className={s.emptyTitle}>{t('songs.noResults')}</div>
            <div className={s.emptySubtitle}>
              {filtersActive
                ? t('songs.noResultsSubtitle')
                : t('common.serviceDown')}
            </div>
          </div>
        ) : (
          <div
            ref={listParentRef}
            className={s.list} style={{
              height: virtualizer.getTotalSize(),
              position: 'relative',
            }}
          >
            {loadPhase === 'contentIn' && virtualizer.getVirtualItems().map((virtualRow) => {
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
                      transform: `translateY(${virtualRow.start}px)`,
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
                      isMobile={isMobile}
                      /* v8 ignore start — stagger delay calculation */
                      staggerDelay={shouldStagger && i < maxVisibleSongs ? (staggerDelay(i, 125, maxVisibleSongs) ?? maxVisibleSongs * 125) : undefined}
                      /* v8 ignore stop */
                    />
                  </div>
                );
            })}
          </div>
        )}
        </div>
      </div>

      {isMobileChrome && <div className={s.fabSpacer} />}
      </LoadGate>

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
    </div>
  );
}

/** Render a single metadata element for the given key. Mirrors mobile renderMetadataElement. */

