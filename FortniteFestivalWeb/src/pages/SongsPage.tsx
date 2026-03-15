import { useState, useMemo, useEffect, useRef, useCallback, Fragment, type CSSProperties } from 'react';
import { IoSwapVerticalSharp, IoFunnel, IoSearch } from 'react-icons/io5';
import { Link, useLocation, useNavigationType } from 'react-router-dom';
import { formatPercentileBucket } from '@festival/core';
import { staggerDelay, estimateVisibleCount } from '../utils/stagger';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { useSettings } from '../contexts/SettingsContext';
import { useIsMobile, useIsMobileChrome } from '../hooks/useIsMobile';
import { useFabSearch } from '../contexts/FabSearchContext';
import { useScrollMask } from '../hooks/useScrollMask';
import { useStaggerRush } from '../hooks/useStaggerRush';
import type { Song, PlayerScore, InstrumentKey } from '../models';
import { Colors, Font, Gap, Radius, Layout, Size, MaxWidth, goldFill, goldOutline, goldOutlineSkew, frostedCard } from '../theme';
import { InstrumentIcon, getInstrumentStatusVisual } from '../components/InstrumentIcons';
import SeasonPill from '../components/SeasonPill';
import AlbumArt from '../components/AlbumArt';
import { visibleInstruments } from '../contexts/SettingsContext';
import SortModal from '../components/SortModal';
import type { SortDraft } from '../components/SortModal';
import FilterModal from '../components/FilterModal';
import type { FilterDraft } from '../components/FilterModal';
import {
  type SongSortMode,
  type SongSettings,
  defaultSongSettings,
  defaultSongFilters,
  loadSongSettings,
  saveSongSettings,
  SONG_SETTINGS_CHANGED_EVENT,
  isFilterActive,
} from '../components/songSettings';

let _savedScrollTop = 0;
let _songsHasRendered = false;

export default function SongsPage() {
  const {
    state: { songs, isLoading, error },
  } = useFestival();
  const { settings: appSettings } = useSettings();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const fabSearch = useFabSearch();
  const scrollRef = useRef<HTMLDivElement>(null);
  const navType = useNavigationType();
  const location = useLocation();
  const forceRestagger = !!(location.state as any)?.restagger;
  // POP fires for both back-nav and fresh page load/refresh;
  // only treat it as back-nav if we have a saved scroll position from a prior visit.
  const isBackNav = navType === 'POP' && _savedScrollTop > 0;

  // Restore scroll position on back navigation
  useEffect(() => {
    if (!isBackNav) return;
    const el = scrollRef.current;
    if (el && _savedScrollTop > 0) {
      el.scrollTop = _savedScrollTop;
    }
  }, []);

  const [search, setSearchLocal] = useState(fabSearch.query);
  const setSearch = useCallback((q: string) => {
    setSearchLocal(q);
    fabSearch.setQuery(q);
  }, [fabSearch]);
  const effectiveSearch = isMobileChrome ? fabSearch.query : search;
  const [debouncedSearch, setDebouncedSearch] = useState(effectiveSearch);
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(effectiveSearch), 250);
    return () => clearTimeout(id);
  }, [effectiveSearch]);
  const [settings, setSettings] = useState<SongSettings>(loadSongSettings);

  // Re-sync settings from localStorage when changed externally (e.g. PlayerPage card clicks)
  const savingRef = useRef(false);
  useEffect(() => {
    const sync = () => {
      if (savingRef.current) return; // ignore self-triggered events
      const fresh = loadSongSettings();
      setSettings(fresh);
      setInstrument(fresh.instrument ?? 'Solo_Guitar');
    };
    window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
    return () => window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, sync);
  }, []);
  const [instrument, setInstrument] = useState<InstrumentKey>(
    () => settings.instrument ?? 'Solo_Guitar',
  );

  // Sort/Filter modal visibility + drafts
  const [showSort, setShowSort] = useState(false);
  const [showFilter, setShowFilter] = useState(false);
  const [sortDraft, setSortDraft] = useState<SortDraft>(() => ({
    sortMode: settings.sortMode,
    sortAscending: settings.sortAscending,
    metadataOrder: settings.metadataOrder,
    instrumentOrder: settings.instrumentOrder,
  }));
  const [filterDraft, setFilterDraft] = useState<FilterDraft>(() => ({
    ...settings.filters,
    instrumentFilter: settings.instrument,
  }));

  // Persist settings on change
  useEffect(() => { savingRef.current = true; saveSongSettings(settings); savingRef.current = false; }, [settings]);

  const openSort = () => {
    setSortDraft({
      sortMode: settings.sortMode,
      sortAscending: settings.sortAscending,
      metadataOrder: settings.metadataOrder,
      instrumentOrder: settings.instrumentOrder,
    });
    setShowSort(true);
  };
  const applySort = () => {
    setSettings(s => ({ ...s, ...sortDraft }));
    setShowSort(false);
  };
  const resetSort = () => {
    const d = defaultSongSettings();
    setSortDraft({ sortMode: d.sortMode, sortAscending: d.sortAscending, metadataOrder: d.metadataOrder, instrumentOrder: d.instrumentOrder });
  };

  const openFilter = () => {
    setFilterDraft({ ...settings.filters, instrumentFilter: settings.instrument });
    setShowFilter(true);
  };
  const applyFilter = () => {
    const { instrumentFilter, ...filters } = filterDraft;
    setInstrument(instrumentFilter ?? 'Solo_Guitar');
    setSettings(s => ({ ...s, filters, instrument: instrumentFilter }));
    setShowFilter(false);
  };
  const resetFilter = () => {
    setFilterDraft({ ...defaultSongFilters(), instrumentFilter: null });
  };

  const filtersActive = isFilterActive(settings.filters) || settings.instrument != null;

  // Register sort/filter actions for FAB — uses refs so latest closures are always captured
  const openSortRef = useRef(openSort);
  const openFilterRef = useRef(openFilter);
  openSortRef.current = openSort;
  openFilterRef.current = openFilter;
  useEffect(() => {
    fabSearch.registerActions({ openSort: () => openSortRef.current(), openFilter: () => openFilterRef.current() });
  }, [fabSearch]);
  const { playerData, playerLoading, isSyncing, syncPhase, backfillProgress, historyProgress } = usePlayerData();

  // Build lookup: songId → PlayerScore for the selected instrument
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

  // Build a per-song, per-instrument lookup for filter logic
  const allScoreMap = useMemo(() => {
    if (!playerData) return new Map<string, Map<string, PlayerScore>>();
    const map = new Map<string, Map<string, PlayerScore>>();
    for (const s of playerData.scores) {
      let byInst = map.get(s.songId);
      if (!byInst) {
        byInst = new Map();
        map.set(s.songId, byInst);
      }
      byInst.set(s.instrument, s);
    }
    return map;
  }, [playerData]);

  const filtered = useMemo(() => {
    const q = debouncedSearch.toLowerCase();
    const f = settings.filters;
    const hasPlayerData = allScoreMap.size > 0;

    // Pre-compute which instruments have any missing/has filter active
    const inst = settings.instrument;
    const filterInstruments = new Set<InstrumentKey>();
    for (const [k, v] of Object.entries(f.missingScores)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.missingFCs)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.hasScores)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.hasFCs)) { if (v) filterInstruments.add(k as InstrumentKey); }
    // When an instrument is selected, only that instrument's filters apply
    const activeFilterInstruments = inst
      ? (filterInstruments.has(inst) ? [inst] : [])
      : [...filterInstruments];

    // Pre-compute which filter checks are active
    const seasonKeys = Object.keys(f.seasonFilter);
    const checkSeason = hasPlayerData && seasonKeys.length > 0 && seasonKeys.some(k => f.seasonFilter[Number(k)] === false);
    const pctKeys = Object.keys(f.percentileFilter);
    const checkPct = hasPlayerData && pctKeys.length > 0 && pctKeys.some(k => f.percentileFilter[Number(k)] === false);
    const starKeys = Object.keys(f.starsFilter);
    const checkStars = hasPlayerData && starKeys.length > 0 && starKeys.some(k => f.starsFilter[Number(k)] === false);
    const diffKeys = Object.keys(f.difficultyFilter);
    const checkDiff = hasPlayerData && diffKeys.length > 0 && diffKeys.some(k => f.difficultyFilter[Number(k)] === false);
    const pctThresholds = [1,2,3,4,5,10,15,20,25,30,40,50,60,70,80,90,100];

    // Single-pass filter: combine all filter conditions into one loop
    let list = songs.filter(s => {
      // Text search
      if (q && !s.title.toLowerCase().includes(q) && !s.artist.toLowerCase().includes(q)) return false;

      if (!hasPlayerData) return true;

      const byInst = allScoreMap.get(s.songId);

      // Per-instrument filters: (MS OR HS) AND (MF OR HF) per instrument, OR'd across instruments
      if (activeFilterInstruments.length > 0) {
        let anyInstrumentPassed = false;
        for (const key of activeFilterInstruments) {
          const ps = byInst?.get(key);
          const hasScore = !!ps?.score;
          const hasFC = !!ps?.isFullCombo;

          const ms = f.missingScores[key];
          const hs = f.hasScores[key];
          const mf = f.missingFCs[key];
          const hf = f.hasFCs[key];

          let passed = true;
          // Score axis: (MS OR HS) — skip if neither is active for this instrument
          if (ms || hs) {
            const passedMS = ms && !hasScore;
            const passedHS = hs && hasScore;
            if (!passedMS && !passedHS) passed = false;
          }
          // FC axis: (MF OR HF) — skip if neither is active for this instrument
          if (passed && (mf || hf)) {
            const passedMF = mf && !hasFC;
            const passedHF = hf && hasFC;
            if (!passedMF && !passedHF) passed = false;
          }

          if (passed) { anyInstrumentPassed = true; break; }
        }
        if (!anyInstrumentPassed) return false;
      }

      const score = scoreMap.get(s.songId);

      // Season filter
      if (checkSeason) {
        const season = score?.season ?? 0;
        if (f.seasonFilter[season] === false) return false;
      }

      // Percentile filter
      if (checkPct) {
        if (!score) {
          if (f.percentileFilter[0] === false) return false;
        } else {
          const pct = score.rank > 0 && (score.totalEntries ?? 0) > 0
            ? Math.min((score.rank / score.totalEntries!) * 100, 100)
            : undefined;
          if (pct == null) {
            if (f.percentileFilter[0] === false) return false;
          } else {
            const bracket = pctThresholds.find(t => pct <= t) ?? 100;
            if (f.percentileFilter[bracket] === false) return false;
          }
        }
      }

      // Stars filter
      if (checkStars) {
        const stars = score?.stars ?? 0;
        if (f.starsFilter[stars] === false) return false;
      }

      // Difficulty filter
      if (checkDiff) {
        const diff = (s as any).difficulty ?? 0;
        if (f.difficultyFilter[diff] === false) return false;
      }

      return true;
    });

    const dir = settings.sortAscending ? 1 : -1;
    return list.slice().sort((a, b) => {
      const mode = settings.sortMode;
      let cmp = 0;
      switch (mode) {
        case 'title':
          cmp = a.title.localeCompare(b.title);
          break;
        case 'artist':
          cmp = a.artist.localeCompare(b.artist);
          break;
        case 'year':
          cmp = (a.year ?? 0) - (b.year ?? 0);
          break;
        default:
          // For instrument-specific modes we need player data
          if (scoreMap.size > 0) {
            const sa = scoreMap.get(a.songId);
            const sb = scoreMap.get(b.songId);
            cmp = compareByMode(mode, sa, sb);
          } else {
            cmp = a.title.localeCompare(b.title);
          }
      }
      return cmp === 0 ? a.title.localeCompare(b.title) * dir : cmp * dir;
    });
  }, [songs, debouncedSearch, settings.sortMode, settings.sortAscending, settings.filters, scoreMap, allScoreMap]);

  const hasPlayer = !!playerData;

  const enabledInstruments = useMemo(
    () => visibleInstruments(appSettings),
    [appSettings],
  );

  // Filter metadata keys by visibility settings (mirrors mobile visibleMetadataKeys)
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

  // Derive available seasons from player scores
  const availableSeasons = useMemo(() => {
    if (!playerData) return [];
    const set = new Set<number>();
    for (const s of playerData.scores) {
      if (s.season != null) set.add(s.season);
    }
    return Array.from(set).sort((a, b) => a - b);
  }, [playerData]);

  // ── Spinner → staggered-content transition ──
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

  // Fingerprint of sort/filter/search settings — when it changes, re-stagger the list
  const settingsKey = `${settings.sortMode}|${settings.sortAscending}|${instrument}|${JSON.stringify(settings.filters)}|${debouncedSearch}`;
  const prevSettingsKeyRef = useRef(settingsKey);

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

  useEffect(() => {
    if (!dataReady || loadPhase !== 'loading') return;
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

  // Scroll to top when content transitions in after a settings change (not on initial mount or back nav)
  useEffect(() => {
    if (loadPhase === 'contentIn' && isSettingsChangeRef.current && scrollRef.current) {
      isSettingsChangeRef.current = false;
      scrollRef.current.scrollTop = 0;
      _savedScrollTop = 0;
    }
  }, [loadPhase]);

  // Container-level scroll fade (works because frostedCard avoids backdrop-filter)
  const updateScrollMask = useScrollMask(scrollRef, [loadPhase, filtered]);

  // Save scroll position continuously + update fade
  const rushOnScroll = useStaggerRush(scrollRef);
  const handleScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    _savedScrollTop = el.scrollTop;
    updateScrollMask();
    rushOnScroll();
  }, [updateScrollMask, rushOnScroll]);

  if (error) {
    return <div style={styles.center}>{error}</div>;
  }

  return (
    <div style={styles.page}>
      {/* Spinner overlay — visible during loading & spinnerOut */}
      {loadPhase !== 'contentIn' && (
        <div
          style={{
            ...styles.spinnerOverlay,
            ...(loadPhase === 'spinnerOut'
              ? { animation: 'fadeOut 500ms ease-out forwards' }
              : {}),
          }}
        >
          <div style={styles.arcSpinner} />
        </div>
      )}
      {!isMobileChrome && (
      <div style={styles.header}>
        <div style={styles.container}>
          <div style={{ visibility: (toolbarShownRef.current || loadPhase === 'contentIn') ? 'visible' : 'hidden' } as CSSProperties}>
          <div style={styles.toolbar}>
            <div style={styles.searchWrap} onClick={e => { const input = e.currentTarget.querySelector('input'); input?.focus(); }}>
              <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
              <input
                style={styles.searchInput}
                placeholder="Search songs or artists…"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>
            {settings.instrument && (
              <InstrumentIcon instrument={settings.instrument} size={32} />
            )}
            <div style={styles.sortGroup}>
              <ToolbarIconButton icon={<IoSwapVerticalSharp size={18} />} label="Sort" onClick={openSort} />
              {hasPlayer && (
                <ToolbarIconButton
                  icon={<IoFunnel size={18} />}
                  label="Filter"
                  onClick={openFilter}
                  active={filtersActive}
                />
              )}
            </div>
          </div>
          {filtersActive && filtered.length !== songs.length && (
            <div style={styles.count}>{filtered.length} of {songs.length} songs</div>
          )}
          </div>
        </div>
      </div>
      )}
      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
        <div style={{ ...styles.container, paddingTop: isMobile ? Gap.sm : Gap.md }}>
        {isSyncing && (
          <div style={styles.syncBanner}>
            <div style={styles.syncSpinner} />
            <div style={{ flex: 1 }}>
              <div style={styles.syncTitle}>
                {syncPhase === 'backfill' ? 'Syncing Data' : 'Building Score History'}
              </div>
              <div style={styles.syncSubtitle}>
                {syncPhase === 'backfill'
                  ? 'Fetching scores from leaderboards…'
                  : 'Reconstructing score history across seasons…'}
              </div>
              {syncPhase === 'backfill' && backfillProgress > 0 && (
                <div style={{ marginTop: Gap.md }}>
                  <div style={styles.syncProgressLabel}>
                    <span>Syncing scores</span>
                    <span>{(backfillProgress * 100).toFixed(1)}%</span>
                  </div>
                  <div style={styles.syncProgressOuter}>
                    <div
                      style={{
                        ...styles.syncProgressInner,
                        width: `${Math.round(backfillProgress * 100)}%`,
                      }}
                    />
                  </div>
                </div>
              )}
              {syncPhase === 'history' && (
                <>
                  <div style={{ marginTop: Gap.md }}>
                    <div style={styles.syncProgressLabel}>
                      <span>Syncing scores</span>
                      <span>100.0%</span>
                    </div>
                    <div style={styles.syncProgressOuter}>
                      <div style={{ ...styles.syncProgressInner, width: '100%' }} />
                    </div>
                  </div>
                  {historyProgress > 0 && (
                    <div style={{ marginTop: Gap.sm }}>
                      <div style={styles.syncProgressLabel}>
                        <span>Building history</span>
                        <span>{(historyProgress * 100).toFixed(1)}%</span>
                      </div>
                      <div style={styles.syncProgressOuter}>
                        <div
                          style={{
                            ...styles.syncProgressInner,
                            width: `${Math.round(historyProgress * 100)}%`,
                          }}
                        />
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
          </div>
        )}
        {loadPhase === 'contentIn' && filtered.length === 0 ? (
          <div style={styles.emptyState}>
            <div style={styles.emptyTitle}>No Songs Found</div>
            <div style={styles.emptySubtitle}>
              {filtersActive
                ? 'Try changing your filters to see more songs.'
                : 'The service may be down unexpectedly. Please refresh to try again.'}
            </div>
          </div>
        ) : (
          <div style={styles.list}>
            {loadPhase === 'contentIn' && filtered.map((song, i) => (
                <SongRow
                  key={song.songId}
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
                  staggerDelay={shouldStagger ? (staggerDelay(i, 125, maxVisibleSongs) ?? maxVisibleSongs * 125) : undefined}
                />
            ))}
          </div>
        )}
        </div>
      </div>

      {isMobileChrome && <div style={styles.fabSpacer} />}

      <SortModal
        visible={showSort}
        draft={sortDraft}
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
        onChange={setSortDraft}
        onCancel={() => setShowSort(false)}
        onReset={resetSort}
        onApply={applySort}
      />
      <FilterModal
        visible={showFilter}
        draft={filterDraft}
        savedDraft={{ ...settings.filters, instrumentFilter: settings.instrument }}
        availableSeasons={availableSeasons}
        onChange={setFilterDraft}
        onCancel={() => setShowFilter(false)}
        onReset={resetFilter}
        onApply={applyFilter}
      />
    </div>
  );
}

/** SVG parallelogram difficulty bars matching mobile DifficultyBars. */
function DifficultyBars({ raw }: { raw: number }) {
  const display = Math.max(0, Math.min(6, Math.trunc(raw))) + 1;
  const barW = 8;
  const barH = 20;
  const offset = Math.min(Math.max(Math.round(barW * 0.26), 1), Math.floor(barW * 0.45));
  return (
    <svg
      width={barW * 7 + 6}
      height={barH}
      style={{ display: 'inline-block', verticalAlign: 'middle' }}
      aria-label={`Difficulty ${display} of 7`}
    >
      {Array.from({ length: 7 }).map((_, i) => {
        const filled = i + 1 <= display;
        const x = i * (barW + 1);
        return (
          <polygon
            key={i}
            points={`${x + offset},0 ${x + barW},0 ${x + barW - offset},${barH} ${x},${barH}`}
            fill={filled ? '#FFFFFF' : '#666666'}
          />
        );
      })}
    </svg>
  );
}

/** Star images matching mobile MiniStars component. */
function MiniStars({ starsCount, isFullCombo }: { starsCount: number; isFullCombo: boolean }) {
  const allGold = starsCount >= 6;
  const displayCount = allGold ? 5 : Math.max(1, starsCount);
  const src = allGold ? `${import.meta.env.BASE_URL}star_gold.png` : `${import.meta.env.BASE_URL}star_white.png`;
  const outline = (isFullCombo || allGold) ? Colors.gold : 'transparent';
  return (
    <span style={styles.miniStarRow}>
      {Array.from({ length: displayCount }).map((_, i) => (
        <span key={i} style={{ ...styles.miniStarCircle, borderColor: outline }}>
          <img src={src} alt="★" style={styles.miniStarIcon} />
        </span>
      ))}
    </span>
  );
}

/** Render a single metadata element for the given key. Mirrors mobile renderMetadataElement. */
function renderMetadataElement(
  key: string,
  score: PlayerScore,
  allKeys: string[],
  songIntensityRaw?: number,
): React.ReactNode | null {
  const rawAcc = score.accuracy ?? 0;
  const pct = rawAcc > 0 ? rawAcc / 10000 : 0;
  const accuracy = pct > 0
    ? (pct % 1 === 0 ? `${Math.round(pct)}%` : `${pct.toFixed(1)}%`)
    : undefined;
  const is100FC = score.isFullCombo && pct >= 100;
  const stars = score.stars ?? 0;

  switch (key) {
    case 'score':
      return score.score > 0 ? (
        <span style={styles.scoreValue}>{score.score.toLocaleString()}</span>
      ) : null;

    case 'percentage':
      return accuracy ? (
        <span
          style={score.isFullCombo ? styles.accuracyBadgeFC : styles.accuracyPill}
        >
          {accuracy}
        </span>
      ) : null;

    case 'stars':
      return stars > 0 ? (
        <MiniStars starsCount={stars} isFullCombo={!!score.isFullCombo} />
      ) : null;

    case 'seasonachieved':
      return score.season != null && score.season > 0 ? (
        <SeasonPill season={score.season} />
      ) : null;

    case 'percentile': {
      const pct =
        score.rank > 0 && (score.totalEntries ?? 0) > 0
          ? Math.min((score.rank / score.totalEntries!) * 100, 100)
          : undefined;
      if (pct == null) return null;
      const display = formatPercentileBucket(pct);
      const isTop1 = pct <= 1;
      const isTop5 = pct <= 5;
      const pctStyle = isTop1
        ? styles.percentileBadgeTop1
        : isTop5
          ? styles.percentileBadgeTop5
          : styles.percentilePill;
      return <span style={pctStyle}>{display}</span>;
    }

    case 'intensity':
      return songIntensityRaw != null ? <DifficultyBars raw={songIntensityRaw} /> : null;

    default:
      return null;
  }
}

type MetadataEntry = { key: string; el: React.ReactNode };

/** Bottom metadata grid matching mobile MetadataBottomRow layout rules. */
function MetadataBottomRow({ entries }: { entries: MetadataEntry[] }) {
  if (entries.length === 0) return null;
  return (
    <div style={styles.metadataWrap}>
      {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
    </div>
  );
}

const INSTRUMENT_DIFFICULTY_KEY: Record<string, keyof import('../models').SongDifficulty> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'proGuitar',
  Solo_PeripheralBass: 'proBass',
};

function SongRow({
  song,
  score,
  instrument,
  instrumentFilter,
  allScoreMap,
  showInstrumentIcons,
  enabledInstruments,
  metadataOrder,
  sortMode,
  isMobile,
  staggerDelay,
}: {
  song: Song;
  score?: PlayerScore;
  instrument: InstrumentKey;
  instrumentFilter?: InstrumentKey | null;
  allScoreMap?: Map<string, PlayerScore>;
  showInstrumentIcons: boolean;
  enabledInstruments: InstrumentKey[];
  metadataOrder: string[];
  sortMode: SongSortMode;
  isMobile: boolean;
  staggerDelay?: number;
}) {
  const instrumentChips = useMemo(() => {
    if (!showInstrumentIcons || instrumentFilter != null) return null;
    return enabledInstruments.map(key => {
      const ps = allScoreMap?.get(key);
      const hasScore = !!ps && ps.score > 0;
      const isFC = !!ps?.isFullCombo;
      const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
      return { key, fill, stroke };
    });
  }, [showInstrumentIcons, instrumentFilter, allScoreMap, enabledInstruments]);
  const linkRef = useRef<HTMLAnchorElement>(null);
  const location = useLocation();
  const handleAnimEnd = useCallback(() => {
    const el = linkRef.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  const animStyle: CSSProperties | undefined = staggerDelay != null
    ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${staggerDelay}ms forwards` }
    : undefined;
  // Promote current sort mode to position 0 for inline display (matches mobile)
  const displayOrder = useMemo(() => {
    const order = [...metadataOrder];
    const generalModes = ['title', 'artist', 'year', 'hasfc'];
    if (!generalModes.includes(sortMode) && order.includes(sortMode)) {
      return [sortMode, ...order.filter(k => k !== sortMode)];
    }
    return order;
  }, [metadataOrder, sortMode]);

  const diffKey = INSTRUMENT_DIFFICULTY_KEY[instrument];
  const songIntensityRaw = diffKey != null ? song.difficulty?.[diffKey] : undefined;

  const entries = useMemo(() => {
    if (!score || instrumentChips) return [];
    const result: { key: string; el: React.ReactNode }[] = [];
    for (const key of displayOrder) {
      const el = renderMetadataElement(key, score, displayOrder, songIntensityRaw);
      if (el) result.push({ key, el });
    }
    return result;
  }, [score, displayOrder, songIntensityRaw, instrumentChips]);

  const thumb = <AlbumArt src={song.albumArt} size={Size.thumb} />;

  const chipRow = instrumentChips && instrumentChips.length > 0 ? (
    <div style={styles.instrumentStatusRow}>
      {instrumentChips.map(c => (
        <div key={c.key} style={{ ...styles.instrumentStatusChip, backgroundColor: c.fill, borderColor: c.stroke }}>
          <InstrumentIcon instrument={c.key} size={24} />
        </div>
      ))}
    </div>
  ) : null;

  if (isMobile && entries.length > 0) {
    // Promoted sort attribute goes top-right; rest wraps below
    const primaryKey = entries[0]?.key;
    const scoreEntry = primaryKey ? entries.find(e => e.key === primaryKey) : null;
    const bottomEntries = primaryKey ? entries.filter(e => e.key !== primaryKey) : entries;
    return (
      <Link ref={linkRef} to={`/songs/${song.songId}${instrumentFilter != null ? `?instrument=${encodeURIComponent(instrument)}` : ''}`} state={{ backTo: location.pathname }} style={{ ...styles.rowMobile, ...animStyle }} onAnimationEnd={handleAnimEnd}>
        <div style={styles.mobileTopRow}>
          {thumb}
          <div style={styles.rowText}>
            <span style={styles.rowTitle}>{song.title}</span>
            <span style={styles.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
          </div>
          {scoreEntry && (
            <div style={styles.detailStrip}>
              {scoreEntry.el}
            </div>
          )}
        </div>
        {bottomEntries.length > 0 && (
          <MetadataBottomRow entries={bottomEntries} />
        )}
      </Link>
    );
  }

  if (isMobile && chipRow) {
    return (
      <Link ref={linkRef} to={`/songs/${song.songId}`} state={{ backTo: location.pathname }} style={{ ...styles.rowMobile, ...animStyle }} onAnimationEnd={handleAnimEnd}>
        <div style={styles.mobileTopRow}>
          {thumb}
          <div style={styles.rowText}>
            <span style={styles.rowTitle}>{song.title}</span>
            <span style={styles.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
          </div>
        </div>
        <div style={{ ...styles.instrumentStatusRow, justifyContent: 'center' }}>
          {instrumentChips!.map(c => (
            <div key={c.key} style={{ ...styles.instrumentStatusChip, backgroundColor: c.fill, borderColor: c.stroke }}>
              <InstrumentIcon instrument={c.key} size={24} />
            </div>
          ))}
        </div>
      </Link>
    );
  }

  return (
    <Link ref={linkRef} to={`/songs/${song.songId}${instrumentFilter != null ? `?instrument=${encodeURIComponent(instrument)}` : ''}`} state={{ backTo: location.pathname }} style={{ ...styles.row, ...animStyle }} onAnimationEnd={handleAnimEnd}>
      {thumb}
      <div style={styles.rowText}>
        <span style={styles.rowTitle}>{song.title}</span>
        <span style={styles.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
      </div>
      {chipRow}
      {entries.length > 0 && (
        <div style={styles.scoreMeta}>
          {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
        </div>
      )}
    </Link>
  );
}

/** Compare two PlayerScores by a given sort mode; undefined scores sort last. */
function compareByMode(mode: SongSortMode, a?: PlayerScore, b?: PlayerScore): number {
  if (!a && !b) return 0;
  if (!a) return 1;
  if (!b) return -1;

  switch (mode) {
    case 'score':
      return a.score - b.score;
    case 'percentage': {
      const pa = a.accuracy ?? 0;
      const pb = b.accuracy ?? 0;
      if (pa !== pb) return pa - pb;
      // Tiebreak: FC takes priority over non-FC
      const fa = a.isFullCombo ? 1 : 0;
      const fb = b.isFullCombo ? 1 : 0;
      return fa - fb;
    }
    case 'percentile': {
      // Compute percentile from rank/totalEntries; lower = better
      const pa = a.rank > 0 && (a.totalEntries ?? 0) > 0 ? a.rank / a.totalEntries! : Infinity;
      const pb = b.rank > 0 && (b.totalEntries ?? 0) > 0 ? b.rank / b.totalEntries! : Infinity;
      return pa - pb;
    }
    case 'stars':
      return (a.stars ?? 0) - (b.stars ?? 0);
    case 'seasonachieved':
      return (a.season ?? 0) - (b.season ?? 0);
    case 'hasfc':
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    default:
      return 0;
  }
}

function ToolbarIconButton({ icon, label, onClick, active, dot }: {
  icon: React.ReactNode;
  label: string;
  onClick: () => void;
  active?: boolean;
  dot?: boolean;
}) {
  return (
    <button
      style={{
        ...styles.iconBtn,
        ...(active ? styles.iconBtnActive : {}),
        width: 'auto',
        paddingLeft: Gap.xl,
        paddingRight: Gap.xl,
        gap: Gap.md,
      }}
      onClick={onClick}
      title={label}
      aria-label={label}
    >
      {icon}
      <span style={{ fontSize: Font.sm, fontWeight: 600, whiteSpace: 'nowrap' }}>{label}</span>
      {dot && <span style={styles.filterDot} />}
    </button>
  );
}

const styles: Record<string, React.CSSProperties> = {
  page: {
    height: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  header: {
    flexShrink: 0,
    zIndex: 10,
  },
  headerMobile: {
    flexShrink: 0,
    zIndex: 10,
  },
  bottomToolbar: {
    flexShrink: 0,
    zIndex: 10,
  },
  fabSpacer: {
    height: 80,
    flexShrink: 0,
  },
  bottomToolbarInner: {
    width: '100%',
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Gap.md}px ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
  },
  bottomCount: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    marginBottom: Gap.sm,
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
  },
  container: {
    width: '100%',
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
  },
  heading: {
    fontSize: Font.title,
    fontWeight: 700,
    marginBottom: Gap.xl,
  },
  toolbar: {
    display: 'flex',
    gap: Gap.xl,
    alignItems: 'center',
    flexWrap: 'wrap' as const,
    marginBottom: Gap.md,
  },
  searchWrap: {
    flex: 1,
    minWidth: 200,
    height: 48,
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    padding: `0 ${Gap.xl}px`,
    boxSizing: 'border-box' as const,
    borderRadius: Radius.full,
    ...frostedCard,
    cursor: 'text',
  },
  searchInput: {
    flex: 1,
    background: 'none',
    border: 'none',
    outline: 'none',
    color: Colors.textPrimary,
    fontSize: Font.md,
  },
  searchInputMobile: {
    padding: `${Gap.xl}px ${Gap.section}px`,
    fontSize: Font.lg,
  },
  sortGroup: {
    display: 'flex',
    gap: Gap.sm,
  },
  iconBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    position: 'relative' as const,
    width: 48,
    height: 48,
    borderRadius: Radius.full,
    ...frostedCard,
    color: Colors.textTertiary,
    cursor: 'pointer',
  },
  iconBtnMobile: {
    width: 44,
    height: 44,
  },
  iconBtnActive: {
    border: 'none',
    color: '#FFFFFF',
    backgroundColor: Colors.accentBlue,
  },
  filterDot: {
    position: 'absolute' as const,
    top: 4,
    right: 4,
    width: 6,
    height: 6,
    borderRadius: '50%',
    backgroundColor: Colors.accentBlue,
  },
  count: {
    fontSize: Font.sm,
    color: Colors.textTertiary,
    marginBottom: Gap.md,
  },
  syncBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.xl}px ${Gap.section}px`,
    backgroundColor: Colors.accentPurpleDark,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.lg,
    marginBottom: Gap.md,
  },
  syncSpinner: {
    width: 24,
    height: 24,
    border: '3px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    flexShrink: 0,
  },
  syncTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
  },
  syncSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  syncProgressOuter: {
    marginTop: Gap.xs,
    height: 6,
    backgroundColor: 'rgba(255,255,255,0.1)',
    borderRadius: 3,
    overflow: 'hidden',
  },
  syncProgressInner: {
    height: '100%',
    backgroundColor: Colors.accentPurple,
    borderRadius: 3,
    transition: 'width 0.3s ease',
  },
  syncProgressLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginBottom: Gap.xs,
  },
  list: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
    paddingTop: Gap.lg,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 64,
    borderRadius: Radius.md,
    ...frostedCard,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
  },
  rowMobile: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.md,
    padding: `${Gap.lg}px ${Gap.xl}px`,
    borderRadius: Radius.md,
    ...frostedCard,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
  },
  mobileTopRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
  },
  detailStrip: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    flexShrink: 0,
    marginLeft: 'auto',
  },
  metadataWrap: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: Gap.lg,
  },
  instrumentStatusRow: {
    display: 'flex',
    gap: Gap.sm,
    alignItems: 'center',
    flexShrink: 0,
  },
  instrumentStatusChip: {
    width: 34,
    height: 34,
    borderRadius: 17,
    borderWidth: 2,
    borderStyle: 'solid',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  thumb: {
    width: Size.thumb,
    height: Size.thumb,
    borderRadius: Radius.xs,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  thumbPlaceholder: {
    backgroundColor: Colors.purplePlaceholder,
  },
  rowText: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: Gap.xs,
    minWidth: 0,
    flex: 1,
  },
  rowTitle: {
    fontSize: Font.md,
    fontWeight: 600,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  rowArtist: {
    fontSize: Font.sm,
    color: Colors.textSubtle,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  rowBpm: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    flexShrink: 0,
  },
  instrumentBar: {
    display: 'flex',
    gap: Gap.sm,
    alignItems: 'center',
    flexWrap: 'wrap' as const,
    marginBottom: Gap.md,
  },
  instrumentChip: {
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.xs,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.transparent,
    color: Colors.textTertiary,
    fontSize: Font.sm,
    cursor: 'pointer',
  },
  instrumentChipActive: {
    backgroundColor: Colors.chipSelectedBg,
    color: Colors.accentBlue,
    borderColor: Colors.accentBlue,
  },
  loadingDot: {
    fontSize: Font.xs,
    color: Colors.textMuted,
    marginLeft: Gap.sm,
  },
  scoreMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    flexShrink: 0,
  },
  scoreValue: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    fontVariantNumeric: 'tabular-nums',
    width: 78,
    textAlign: 'right' as const,
    display: 'inline-block',
  },
  miniStarRow: {
    display: 'inline-flex',
    gap: 3,
    alignItems: 'center',
    justifyContent: 'flex-end' as const,
    width: 132,
  },
  miniStarCircle: {
    width: Size.iconSm,
    height: Size.iconSm,
    borderRadius: '50%',
    border: '1.5px solid transparent',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    overflow: 'hidden',
  },
  miniStarIcon: {
    width: 20,
    height: 20,
    objectFit: 'contain' as const,
  },
  accuracyPill: {
    fontSize: Font.lg,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: '2px solid transparent',
    minWidth: 58,
    textAlign: 'center' as const,
    display: 'inline-block',
    boxSizing: 'border-box' as const,
  },
  accuracyBadgeFC: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    minWidth: 58,
    textAlign: 'center' as const,
    boxSizing: 'border-box' as const,
  },
  percentilePill: {
    fontSize: Font.lg,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.sm}px`,
    borderRadius: Radius.xs,
    border: '2px solid transparent',
    minWidth: 82,
    textAlign: 'center' as const,
    display: 'inline-block',
    boxSizing: 'border-box' as const,
  },
  percentileBadgeTop1: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    minWidth: 82,
    textAlign: 'center' as const,
    boxSizing: 'border-box' as const,
  },
  percentileBadgeTop5: {
    ...goldOutline,
    fontSize: Font.lg,
    minWidth: 82,
    textAlign: 'center' as const,
    boxSizing: 'border-box' as const,
  },
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    color: Colors.textSecondary,
    backgroundColor: Colors.backgroundApp,
    fontSize: Font.lg,
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  spinnerOverlay: {
    position: 'fixed' as const,
    inset: 0,
    zIndex: 2,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    justifyContent: 'center',
    height: 'calc(100dvh - 250px)',
    textAlign: 'center' as const,
  },
  emptyTitle: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.md,
  },
  emptySubtitle: {
    fontSize: Font.md,
    color: Colors.textMuted,
  },
};
